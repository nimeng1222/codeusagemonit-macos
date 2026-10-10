using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace CodeUsageMonit {
    // Scans every supported agent's local history into its hourly index (data/*-logs.json),
    // archives every observed bucket (data/archive/, Phase 1) and derives the rolling
    // 370-day daily history the panel and CLI show.
    public static class UsageScanner {
        // RebuildSessions：全量重建型扫描器（每轮 new LogIndex）——会话账本同样每轮从空
        // 账本重建（无增量、无 LogReader.Reset，不需要 claim 回扣）。
        public sealed class Agent { public string Id, File; public Func<LogIndex, DateTime, LogIndex> Scan; public bool RebuildSessions; }
        public static readonly Agent[] Agents = {
            new Agent { Id = "codex", File = "codex-logs.json", Scan = CodexLogs.Scan },
            new Agent { Id = "claude", File = "claude-logs.json", Scan = ClaudeLogs.Scan },
            // Cursor has no local log: its dashboard's usage events (CursorUsage.cs).
            new Agent { Id = "cursor", File = "cursor-logs.json", Scan = CursorUsage.Scan },
            // DeepSeek: the DeepSeek Harness sessions (DeepSeekHarness.cs, zstd-compressed JSONL).
            new Agent { Id = "deepseek", File = "deepseek-logs.json", Scan = HarnessLogs.Scan },
            new Agent { Id = "antigravity", File = "antigravity-logs.json", Scan = AgentLogs.ScanAntigravity, RebuildSessions = true },
            new Agent { Id = "zcode", File = "zcode-logs.json", Scan = AgentLogs.ScanZcode, RebuildSessions = true },
            new Agent { Id = "grok", File = "grok-logs.json", Scan = AgentLogs.ScanGrok },
            new Agent { Id = "kimi", File = "kimi-logs.json", Scan = AgentLogs.ScanKimi },
            new Agent { Id = "pi", File = "pi-logs.json", Scan = MoreAgentLogs.ScanPi },
            new Agent { Id = "copilot", File = "copilot-logs.json", Scan = MoreAgentLogs.ScanCopilot, RebuildSessions = true },
            new Agent { Id = "opencode", File = "opencode-logs.json", Scan = MoreAgentLogs.ScanOpenCode, RebuildSessions = true }
        };
        // Scans the given agents in parallel; each index is read from and written to data/.
        public static Dictionary<string, LogIndex> ScanAll(IEnumerable<string> ids, DateTime nowUtc, bool persist) {
            Pricing.EnsureLoaded();
            if (persist) { try { EndpointAttribution.Use(Store.Read<EndpointLog>("endpoints.json") ?? new EndpointLog()); } catch { EndpointAttribution.Use(new EndpointLog()); } }
            var wanted = new HashSet<string>(ids);
            var tasks = Agents.Where(a => wanted.Contains(a.Id)).Select(a => Task.Run(() => {
                LogIndex index = null;
                // D 期：会话账本随扫描挂载（emit 点在去重之后记账）；落盘在 Persist 里、
                // 且只在主索引成功写入之后（SaveIfActive），两份文件同进同退。本轮抛错或
                // persist=false 时不落盘，End 直接丢弃内存账本。
                SessionLedger.Begin(a.Id, a.RebuildSessions);
                try {
                    index = a.Scan(persist ? Store.Read<LogIndex>(a.File) : null, nowUtc);
                    if (persist && index != null) Persist(a.File, index, a.Id, nowUtc);
                } catch (Exception e) {
                    // Phase 3.2：扫描错误不再静默——每平台记最近一次错误与时间（内存态，
                    // 健康摘要与诊断报告消费），并经 SourceHealth.ErrorSink 落 errors.log
                    //（桌面进程挂了 MonitorPanel.LogError；CLI 进程无人订阅即只记录）。
                    // 失败语义不变：index 置 null，该平台本轮保留旧索引。
                    SourceHealth.RecordError(a.Id, e);
                    index = null;
                } finally { SessionLedger.End(a.Id); }
                return new KeyValuePair<string, LogIndex>(a.Id, index);
            })).ToArray();
            Task.WaitAll(tasks);
            return tasks.Select(t => t.Result).Where(p => p.Value != null).ToDictionary(p => p.Key, p => p.Value);
        }
        // 持久化一个 agent 的扫描结果：先归档再落盘（Phase 1）。Finish 已把本轮出窗桶挪进
        // index.Expiring（不持久化的暂存道），Append 把它们连同全部观测桶写进 data/archive/
        // 之后才允许落盘裁剪后的索引。归档失败（返回 -1）时保留 Expiring 且不落盘（P2a）：
        // 被裁的桶还完整留在旧索引文件里，下一轮扫描重新入窗并重试归档，出窗桶不丢。
        internal static void Persist(string file, LogIndex index, string agent, DateTime nowUtc) {
            if (UsageArchive.Append(index, agent, nowUtc) < 0) return;
            index.Expiring.Clear();
            Store.Write(file, index);
            // D 期：主索引落盘成功后才会话账本（Trim 到 RetainDays 窗口后写
            // sessions-<agent>.json）；顺序保证 sessions 永远不会超前于它镜像的小时索引。
            SessionLedger.SaveIfActive(agent, nowUtc);
        }
        // Reads the saved indexes without scanning (the CLI's offline commands).
        public static Dictionary<string, LogIndex> Load(IEnumerable<string> ids) {
            var result = new Dictionary<string, LogIndex>();
            foreach (Agent a in Agents.Where(a => ids.Contains(a.Id))) { try { LogIndex index = Store.Read<LogIndex>(a.File); if (index != null && index.Version == LogIndex.CurrentVersion) result[a.Id] = index; } catch { } }
            return result;
        }
    }

    public static partial class HistoryService {
        public const string Engine = "native-1";
        // Daily rows per agent in the given time zone, from the hourly indexes. Hours are
        // UTC buckets, so zones with a half-hour offset split a day at the nearest hour.
        public static UsageHistory FromIndexes(IDictionary<string, LogIndex> indexes, TimeZoneInfo zone, DateTime today, int days) {
            var history = new UsageHistory { Updated = DateTime.UtcNow.ToString("o"), Zone = zone.Id, Engine = Engine };
            string oldest = DayKey(today.AddDays(-(days - 1))), newest = DayKey(today);
            foreach (var pair in indexes) {
                var byDay = new Dictionary<string, DayUsage>();
                var models = new Dictionary<string, ModelUsage>();
                foreach (HourUsage hour in pair.Value.Entries()) {
                    string day = DayKey(TimeZoneInfo.ConvertTimeFromUtc(hour.Hour, zone).Date);
                    if (String.CompareOrdinal(day, oldest) < 0 || String.CompareOrdinal(day, newest) > 0) continue;
                    DayUsage row; if (!byDay.TryGetValue(day, out row)) { row = new DayUsage { Day = day, Agent = pair.Key, CostKnown = true }; byDay[day] = row; }
                    row.Cost += hour.Cost; row.Tokens += hour.Tokens; row.CachedTokens += hour.Cached; row.InputTokens += hour.Input; row.OutputTokens += hour.Output; row.CacheCreationTokens += hour.CacheWrite; row.Requests += hour.Requests; row.UnpricedTokens += hour.Unpriced;
                    row.TimedRequests += hour.TimedRequests; row.TimedOutput += hour.TimedOutput; row.TimedSeconds += hour.TimedSeconds;
                    if (hour.Model.Length == 0) continue;
                    ModelUsage model; string key = day + "|" + hour.Model;
                    if (!models.TryGetValue(key, out model)) { model = new ModelUsage { Model = hour.Model }; models[key] = model; row.Models.Add(model); }
                    // Same scope as the daily row: hour.Tokens includes Other (independent
                    // reasoning and the rest), so the model split adds up to the day
                    // (review #7 — it used to drop Other and undercount).
                    model.Tokens += hour.Tokens; model.Cost += hour.Cost; model.TimedOutput += hour.TimedOutput; model.TimedSeconds += hour.TimedSeconds;
                }
                foreach (DayUsage row in byDay.Values.OrderBy(r => r.Day, StringComparer.Ordinal)) {
                    if (row.Tokens <= 0 && row.Requests <= 0) continue;
                    row.Models = row.Models.OrderByDescending(m => m.Cost).ThenByDescending(m => m.Tokens).ToList();
                    row.CostKnown = row.Cost > 0 || row.UnpricedTokens < row.Tokens;
                    history.Days.Add(row);
                }
            }
            history.Days = history.Days.OrderBy(d => d.Day, StringComparer.Ordinal).ThenBy(d => d.Agent, StringComparer.Ordinal).ToList();
            return history;
        }
    }
}
