using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace CodeUsageMonit {
    // One session (one conversation) of one agent: its identity, first/last activity and
    // the per-model totals. Field meanings follow Bucket (I fresh input, C cache read,
    // W cache write, O output, X other tokens, R requests, D USD, U unpriced tokens), so
    // a session row can reuse every formatter the hourly views use.
    public sealed class SessionRow {
        public string Id = "", First = "", Last = "";
        public Dictionary<string, Bucket> Models = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        public Bucket Total() { var total = new Bucket(); foreach (Bucket b in Models.Values) total.Add(b, 1); return total; }
    }
    // What one owner file claimed for one booked event: the give-back bookkeeping that
    // lets LogReader.Reset subtract exactly what a truncated / rewritten file had added
    // (the session-side mirror of the Seen owner + LogFile.Hours double layer).
    public sealed class SessionClaim {
        public string Session = "", Model = "";
        public double I, C, W, O, X, R, D, U;
        public static SessionClaim Of(Bucket b, string session, string model) { return new SessionClaim { Session = session ?? "", Model = model ?? "", I = b.I, C = b.C, W = b.W, O = b.O, X = b.X, R = b.R, D = b.D, U = b.U }; }
        public Bucket Amounts() { return new Bucket { I = I, C = C, W = W, O = O, X = X, R = R, D = D, U = U }; }
    }
    // D 期 Sessions 会话视图的存储侧：每个 agent 一份 data/sessions-<agent>.json，独立于
    // *-logs.json（LogIndex.CurrentVersion 不动，避免全量重扫主索引）。各扫描器的 emit 点
    // 在通过既有去重（index.Seen）之后调用 Emit，因此这里记的每一笔都已被主口径去重。
    //
    // 一致性模型（照抄小时桶的双层结构）：
    //   · 文件型扫描器（codex/claude/grok/kimi/pi/deepseek）每笔事件按 owner file 记一条
    //     claim；LogReader.Reset（文件截断 / 改写回读）经 GiveBack 把该文件的 claims 逐笔
    //     从会话桶回扣。Claude 的「更大副本替换」路径经 Rebook 把 claim 从旧 owner 挪到新
    //     owner，会话桶与小时桶同步换账。
    //   · 全量重建型扫描器（zcode/copilot/opencode/antigravity）每轮整库重建，直接以空账本
    //     开始（rebuild: true），不记 claims——没有增量就没有 Reset，回扣无处需要。
    //   · 落盘挂载在 UsageScanner.Persist：主索引 Store.Write 成功之后才写 sessions-*.json，
    //     两份文件同进同退；损坏的 sessions-*.json 只隔离删除自身并触发下轮 backfill 重建，
    //     绝不影响主索引加载。
    //   · 首次启用（sessions-<agent>.json 不存在）时把该 agent 的文件状态整体 Reset 一次
    //     （BackfillDue），既有日志经同一条「回扣 + 重读」路径补录会话；小时桶总量在数学上
    //     保持不变（先回扣再重记），只多花一轮解析。
    //   · 保留期与 LogReader.RetainDays（33 天）对齐，SaveIfActive 前裁剪过期会话。
    //   · 账本随 Pricing.Version 盖戳：价目表更新会让 LogReader.Fresh 丢弃整个主索引并
    //     全量重读（不走 Reset），与主索引同进同退——Load 遇戳不符按 rebuild 处理
    //     （隔离旧文件 + 解锁 BackfillDue），旧 claims 不复存在，重读不会双计。
    public sealed class SessionLedger {
        public const int CurrentVersion = 1;
        public int Version = CurrentVersion;
        public string Agent = "";
        // 记账时的 Pricing.Version：主索引在价目表更新后整体重建，账本必须同步重建，
        // 否则全量重读会把既有 claims 上的事件再记一遍（会话 R/Token/费用翻倍且无自愈）。
        public string PricingStamp = "";
        public Dictionary<string, SessionRow> Sessions = new Dictionary<string, SessionRow>(StringComparer.Ordinal);
        // owner file → claim key → claim。claim key 与该事件在 index.Seen 里的键同形
        //（claude 用解析别名后的 canonical 键；无 id 的 claude 行没有去重键，由账本铸造唯一键）。
        public Dictionary<string, Dictionary<string, SessionClaim>> Claims = new Dictionary<string, Dictionary<string, SessionClaim>>(StringComparer.OrdinalIgnoreCase);
        private long minted;   // 无键事件的铸造序号；runNamespace 保证跨进程重载后不与旧键冲突
        private readonly string runNamespace = Guid.NewGuid().ToString("N");

        private static readonly ConcurrentDictionary<string, SessionLedger> active = new ConcurrentDictionary<string, SessionLedger>(StringComparer.Ordinal);
        // 正在扫描的 agent（AsyncLocal 随执行流进入 LogReader.Reset）：并发扫描多个 agent 时
        // Reset 只回扣自己的账本，不触碰别的扫描线程正在写的字典。
        private static readonly AsyncLocal<string> scanningAgent = new AsyncLocal<string>();
        private static readonly HashSet<string> backfillTried = new HashSet<string>(StringComparer.Ordinal);

        public static string FileOf(string agent) { return "sessions-" + agent + ".json"; }

        // Attach the agent's ledger for the duration of one scan. rebuild = 全量重建型扫描器
        //（或损坏隔离后）：一律空账本起算，落盘时整体替换。
        public static void Begin(string agent, bool rebuild) {
            var ledger = new SessionLedger { Agent = agent };
            if (!rebuild) LoadInto(ledger, enforceStamp: true);   // ScanAll 已先 Pricing.EnsureLoaded
            active[agent] = ledger;
            scanningAgent.Value = agent;
        }
        public static void End(string agent) {
            SessionLedger ignored;
            active.TryRemove(agent, out ignored);
            if (scanningAgent.Value == agent) scanningAgent.Value = null;
        }
        // 自测 / 窄路径用：不经过 ScanAll 的直接扫描也能记账。落盘仍只发生在
        // UsageScanner.Persist（SaveIfActive），End 不落盘——两份文件同进同退。
        public static SessionLedger Begin(string agent) {
            Begin(agent, rebuild: false);
            SessionLedger ledger; active.TryGetValue(agent, out ledger); return ledger;
        }
        public static SessionLedger Peek(string agent) { SessionLedger ledger; return active.TryGetValue(agent, out ledger) ? ledger : null; }

        // Book one event that the caller has already deduplicated against index.Seen.
        // owner = null（重建型扫描器）时只记会话桶，不产生 claim。
        public static void Emit(string agent, LogFile owner, string claimKey, string session, DateTime when, string model, Bucket amounts) {
            if (amounts == null) return;
            SessionLedger ledger;
            if (!active.TryGetValue(agent, out ledger)) return;
            ledger.Book(owner, claimKey, session, when, model, amounts);
        }
        // Claude 的替换路径：同一条消息的更大副本把账从旧 owner 挪到新 owner（canonical
        // seen 键不变，claim 以它为键换主）。amounts 按新副本全额记账；旧侧按 claim 里存的
        // 数额回扣（与 index 侧回扣的 previous 同源，正常时相等）。
        public static void Rebook(string agent, string oldOwner, string claimKey, LogFile newOwner, string session, DateTime when, string model, Bucket amounts) {
            SessionLedger ledger;
            if (!active.TryGetValue(agent, out ledger)) return;
            ledger.RemoveClaim(oldOwner, claimKey);
            ledger.Book(newOwner, claimKey, session, when, model, amounts);
        }
        // LogReader.Reset：一个文件被截断 / 改写并从头回读，它曾认领的全部 claim 回扣。
        public static void GiveBack(string ownerName) {
            string agent = scanningAgent.Value;
            if (agent != null) { SessionLedger ledger; if (active.TryGetValue(agent, out ledger)) ledger.GiveBackOwner(ownerName); return; }
            // 无扫描上下文（自测直调 LogReader.Scan 等）：owner 名跨 agent 不重叠
            //（codex 用文件名，其余用全路径），逐个账本尽力回扣。
            foreach (SessionLedger ledger in active.Values) { try { ledger.GiveBackOwner(ownerName); } catch { } }
        }

        private void Book(LogFile owner, string claimKey, string session, DateTime when, string model, Bucket amounts) {
            try {
                if (String.IsNullOrEmpty(session)) session = "unknown";
                if (String.IsNullOrEmpty(model)) model = "";
                SessionRow row;
                if (!Sessions.TryGetValue(session, out row)) Sessions[session] = row = new SessionRow { Id = session };
                string iso = when.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
                if (row.First.Length == 0 || String.CompareOrdinal(iso, row.First) < 0) row.First = iso;
                if (row.Last.Length == 0 || String.CompareOrdinal(iso, row.Last) > 0) row.Last = iso;
                Bucket bucket;
                if (!row.Models.TryGetValue(model, out bucket)) row.Models[model] = bucket = new Bucket();
                bucket.Add(amounts, 1);
                if (owner == null) return;
                Dictionary<string, SessionClaim> mine;
                if (!Claims.TryGetValue(owner.Name, out mine)) Claims[owner.Name] = mine = new Dictionary<string, SessionClaim>(StringComparer.Ordinal);
                string key = claimKey;
                if (String.IsNullOrEmpty(key)) key = "auto|" + runNamespace + "|" + Interlocked.Increment(ref minted);
                mine[key] = SessionClaim.Of(amounts, session, model);
            } catch { }
        }
        private void RemoveClaim(string ownerName, string claimKey) {
            if (String.IsNullOrEmpty(ownerName) || String.IsNullOrEmpty(claimKey)) return;
            Dictionary<string, SessionClaim> mine;
            if (!Claims.TryGetValue(ownerName, out mine)) return;
            SessionClaim claim;
            if (!mine.TryGetValue(claimKey, out claim)) return;
            mine.Remove(claimKey);
            Unbook(claim);
        }
        private void GiveBackOwner(string ownerName) {
            if (String.IsNullOrEmpty(ownerName)) return;
            Dictionary<string, SessionClaim> mine;
            if (!Claims.TryGetValue(ownerName, out mine)) return;
            Claims.Remove(ownerName);
            foreach (SessionClaim claim in mine.Values) Unbook(claim);
            DropEmpty();
        }
        // 桶清空阈值与 LogIndex.Add 的 sign<0 清理一致（R、Token 总量都 ≤ 0.5 才移除）。
        private void Unbook(SessionClaim claim) {
            SessionRow row;
            if (!Sessions.TryGetValue(claim.Session, out row)) return;
            Bucket bucket;
            if (!row.Models.TryGetValue(claim.Model, out bucket)) return;
            bucket.Add(claim.Amounts(), -1);
            if (bucket.R <= 0.5 && bucket.Tokens() <= 0.5) row.Models.Remove(claim.Model);
            if (row.Models.Count == 0) Sessions.Remove(claim.Session);
        }
        private void DropEmpty() {
            foreach (string id in Sessions.Where(p => p.Value.Models.Count == 0).Select(p => p.Key).ToList()) Sessions.Remove(id);
            foreach (string owner in Claims.Where(p => p.Value.Count == 0).Select(p => p.Key).ToList()) Claims.Remove(owner);
        }
        // 保留期裁剪（对齐 LogReader.RetainDays）：Last 出窗的会话整行移除，悬空 claim 一并
        // 丢弃。在 SaveIfActive 落盘前调用。
        public void Trim(DateTime nowUtc) {
            string cutoff = nowUtc.AddDays(-LogReader.RetainDays).ToString("o", CultureInfo.InvariantCulture);
            foreach (string id in Sessions.Where(p => p.Value.Last.Length == 0 || String.CompareOrdinal(p.Value.Last, cutoff) < 0).Select(p => p.Key).ToList()) Sessions.Remove(id);
            foreach (string owner in Claims.Keys.ToList()) {
                Dictionary<string, SessionClaim> mine = Claims[owner];
                foreach (string key in mine.Where(p => !Sessions.ContainsKey(p.Value.Session)).Select(p => p.Key).ToList()) mine.Remove(key);
                if (mine.Count == 0) Claims.Remove(owner);
            }
            DropEmpty();
        }

        // 落盘：只允许在主索引已成功写入之后（UsageScanner.Persist）调用。扫描中的账本
        // 不在（本轮失败 / persist=false）时静默跳过——sessions 与 *-logs.json 同进同退。
        public static void SaveIfActive(string agent, DateTime nowUtc) {
            SessionLedger ledger;
            if (!active.TryGetValue(agent, out ledger)) return;
            try { ledger.PricingStamp = Pricing.Version; ledger.Trim(nowUtc); Store.Write(FileOf(agent), ledger); } catch { }
        }
        // 面板 / CLI 读取：坏文件返回空账本并把损坏文件隔离删除，使下一轮扫描经 BackfillDue
        // 全量重建；绝不向调用方抛错，也不影响主索引的加载。
        // 读取路径零副作用：损坏 / 戳不符只返回空账本，绝不删除文件、不解锁 backfill——
        // 读取方（面板 / 工具进程）可能还没装载价目表，此时 Pricing.Version 是空串，不能拿
        // 它判定「戳过期」；隔离与重建只属于扫描路径（Begin，ScanAll 已先 EnsureLoaded）。
        public static SessionLedger Load(string agent) {
            var ledger = new SessionLedger { Agent = agent };
            LoadInto(ledger, enforceStamp: false);
            return ledger;
        }
        public static Dictionary<string, SessionLedger> LoadAll() {
            var result = new Dictionary<string, SessionLedger>(StringComparer.Ordinal);
            foreach (var agent in UsageScanner.Agents) {
                SessionLedger ledger = Load(agent.Id);
                if (ledger.Sessions.Count > 0) result[agent.Id] = ledger;
            }
            return result;
        }
        private static void LoadInto(SessionLedger ledger, bool enforceStamp) {
            try {
                string path = Path.Combine(Store.Data, FileOf(ledger.Agent));
                if (!File.Exists(path)) return;
                SessionLedger loaded;
                try { loaded = J.Serializer().Deserialize<SessionLedger>(File.ReadAllText(path)); } catch { loaded = null; }
                bool corrupt = loaded == null || loaded.Version != CurrentVersion || loaded.Sessions == null;
                bool staleStamp = !corrupt && enforceStamp && loaded.PricingStamp != Pricing.Version;
                if (corrupt || staleStamp) {
                    // 扫描路径（enforceStamp）：损坏 / 版本不符 / 价目表换版 → 旧 claims 一律
                    // 不可信（价目换版后主索引已整体重建并全量重读），删除文件并解锁
                    // BackfillDue 让本轮扫描重建。读取路径只在内存里降级为空账本。
                    if (enforceStamp) {
                        try { File.Delete(path); } catch { }
                        lock (backfillTried) backfillTried.Remove(ledger.Agent);
                    }
                    return;
                }
                ledger.Sessions = loaded.Sessions;
                ledger.Claims = loaded.Claims ?? new Dictionary<string, Dictionary<string, SessionClaim>>(StringComparer.OrdinalIgnoreCase);
            } catch { }
        }
        // 首次启用（账本文件不存在）：文件型扫描器把既有文件状态整体 Reset 一次，让既有
        // 日志经「回扣 + 重读」补录会话。每进程每 agent 只尝试一次；落盘成功后文件存在，
        // 此后恒为 false。
        // 已知边界（接受，生产归档已兜底）：ResetAll 回读的是文件当前内容——31~33 天带内
        // 已从主索引热窗口裁掉、只存在于 data/archive/ 的小时桶不会回到会话账本，因此账本
        // 的实际覆盖从本进程首次扫描时刻起算，历史深度可能略小于 33 天；生产环境归档 lane
        // 负责该时段的用量口径，会话视图不重复实现它。
        public static bool BackfillDue(string agent) {
            try { if (File.Exists(Path.Combine(Store.Data, FileOf(agent)))) return false; } catch { return false; }
            lock (backfillTried) return backfillTried.Add(agent);
        }
    }
}
