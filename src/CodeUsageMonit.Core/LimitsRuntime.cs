using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeUsageMonit {
    // ROADMAP Phase 2（额度刷新体系）：每平台一条独立的刷新通道，替代"固定周期 + 全量刷新"。
    //
    // 通道状态机（每平台独立）：
    //   normal  — 正常：到期时刻 = 上次尝试 + 有效周期（固定周期，或 burn rate 伸缩后的周期）；
    //   backoff — 瞬时失败（429/5xx/超时/网络）：保留 lastGood 旧值继续展示（RetainLastGood/Stale
    //             语义不变），连续失败按 1→2→4→8 分钟指数退避，封顶该平台配置周期；到点自动重试；
    //   auth    — 凭据失效（expired/setup）：不进退避，仅按 AuthProbeMinutes（30 分钟）慢速
    //             探测一次——Fetch 内部会重试 CLI 静默导入凭据（kimi/zcode），用户之后在
    //             CLI 登录能被自动认上；其余时候等用户重新登录/填 Key（或凭据变更触发的
    //             定向刷新）。手动刷新永远放行。
    // 成功把通道复位回 normal，并清零连续失败计数。
    //
    // lastGood / lastAttempt 分离记录（LastGoodUtc / LastAttemptUtc），连同连续失败次数，
    // 供 Phase 3 的数据源健康状态消费。
    //
    // burn rate（自适应周期）：记录相邻两次成功读数的"主额度剩余百分比"，取相邻样本消耗速率的
    // 中位数（抗单点噪声）。快 → 周期按 基准速率/速率 缩短（下限 InUseMinutes），慢/零消耗 →
    // 放长（上限 AdaptiveMaxMinutes）；样本 < 2、余额型额度（无百分比）或读数回升（窗口重置）
    // 时退化为固定周期。基准速率 ReferenceBurnPerHour 下周期不变。
    //
    // 纯逻辑：时钟由调用方传入，网络结果由调用方回填，可离线单测。
    public sealed class LimitsRuntime {
        public sealed class Sample { public DateTime AtUtc; public double Remaining; }
        public sealed class Channel {
            public string Kind = "normal";                       // normal | backoff | auth
            public DateTime LastAttemptUtc = DateTime.MinValue;  // 上次尝试（任何来源，含手动）
            public DateTime LastGoodUtc = DateTime.MinValue;     // 上次成功（Phase 3 健康状态用）
            public DateTime NextAutoUtc = DateTime.MinValue;     // backoff 的最早自动重试时刻
            public int ConsecutiveFailures;                      // 连续失败（含凭据失效；成功复位）
            public int TransientFailures;                        // 连续瞬时失败（驱动退避；成功复位）
            public int LastBackoffMinutes;                       // 最近一次退避时长（测试/诊断用）
            public bool HasPercentModel = true;                  // 最近一次成功读数含百分比额度
            public double? BurnPerHour;                          // 平滑后的消耗速率（%/小时；样本不足为 null）
            internal readonly List<Sample> Samples = new List<Sample>();
        }
        public sealed class RefreshPlan {
            public readonly HashSet<string> Reset = new HashSet<string>();   // 重置并立即刷新
            public readonly HashSet<string> Forget = new HashSet<string>();  // 已停用：丢弃通道
        }

        public const double ReferenceBurnPerHour = 5.0;   // 基准消耗速率（%/小时）：恰好用配置周期
        public const int AdaptiveMaxMinutes = 30;         // 自适应周期上限
        public const int MaxSamples = 8;                  // 每通道保留的速率样本上限
        public const double MinSampleGapHours = 30.0 / 3600.0;  // 相邻样本最小间隔，避免除零噪声
        public const double ResetEpsilon = 0.01;          // 剩余回升超过该值视为窗口重置
        // auth 态（expired/setup）的慢速探测间隔（review P2-4）：不骚扰凭据失效平台，但
        // kimi/zcode 的 CLI 静默导入凭据发生在 Fetch 内部——用户之后在 CLI 登录时，靠这条
        // 低频探测自动认上（恢复旧版行为），成功即复活，失败重新计时再等 30 分钟。
        public const int AuthProbeMinutes = 30;

        // channels 的唯一互斥门（review P2-1）：诊断报告在后台线程读通道（ChannelOf），
        // 与 UI 线程 30 秒 tick 的 AutoDue→ChannelOf、RecordSuccess/RecordFailure 并发，
        // Dictionary 非线程安全。所有公开入口统一 lock；lock 可重入，内部互调无死锁。
        // 只保护容器与字段的读写一致性，不改变状态机语义。
        private readonly object gate = new object();
        private readonly Dictionary<string, Channel> channels = new Dictionary<string, Channel>();

        public Channel ChannelOf(string id) { lock (gate) { Channel c; if (!channels.TryGetValue(id ?? "", out c)) { c = new Channel(); channels[id ?? ""] = c; } return c; } }
        public void Drop(string id) { lock (gate) { if (id != null) channels.Remove(id); } }
        // 凭据/配置变更：清空通道，使该平台立即到期，速率样本与退避一并作废。
        public void ResetFor(string id) { lock (gate) channels.Remove(id); }

        // 自动刷新（30 秒 tick）是否到期。退避中的平台不到点不到期；auth 平台平时不自动
        // 重试，仅按 AuthProbeMinutes 慢速探测一次（Fetch 内部会重试 CLI 静默导入凭据）。
        // 手动刷新（F5 / 卡片 / 托盘）不经过这里，天然绕过。
        public bool AutoDue(AppConfig config, string id, bool inUse, DateTime nowUtc) {
            lock (gate) {
                Channel c = ChannelOf(id);
                if (c.Kind == "auth") return nowUtc - c.LastAttemptUtc >= TimeSpan.FromMinutes(AuthProbeMinutes);
                if (c.NextAutoUtc != DateTime.MinValue) return nowUtc >= c.NextAutoUtc - Pacing.EarlyMargin;
                return Pacing.Due(c.LastAttemptUtc, EffectiveMinutes(config, id, inUse), nowUtc);
            }
        }

        // 本次自动刷新实际使用的周期（分钟）：固定模式或自适应条件不满足时即 Pacing.Minutes。
        public int EffectiveMinutes(AppConfig config, string id, bool inUse) {
            lock (gate) {
                int baseMinutes = Pacing.Minutes(config, id, inUse);
                Channel c;
                if (config == null || config.RefreshMode != "adaptive" || !channels.TryGetValue(id ?? "", out c)) return baseMinutes;
                if (!c.HasPercentModel || !c.BurnPerHour.HasValue) return baseMinutes;   // 余额型/样本不足：固定周期
                int lower = Math.Max(1, config.InUseMinutes);
                double burn = Math.Max(0.05, c.BurnPerHour.Value);                       // 零消耗交给上限
                double scaled = baseMinutes * ReferenceBurnPerHour / burn;
                return Math.Max(lower, Math.Min(AdaptiveMaxMinutes, Math.Max(1, (int)Math.Round(scaled))));
            }
        }

        // 一次成功读取：复位状态机并喂入 burn rate 样本。
        public void RecordSuccess(string id, ProviderState state, DateTime nowUtc) {
            lock (gate) {
                Channel c = ChannelOf(id);
                c.Kind = "normal"; c.NextAutoUtc = DateTime.MinValue;
                c.ConsecutiveFailures = 0; c.TransientFailures = 0; c.LastBackoffMinutes = 0;
                c.LastAttemptUtc = nowUtc; c.LastGoodUtc = nowUtc;
                FeedBurn(c, state, nowUtc);
            }
        }

        // 一次失败读取。status 为 expired/setup（凭据问题）时进入 auth 态——不退避，仅按
        // AuthProbeMinutes 慢速探测；其余（429/5xx/超时/网络/解析失败）进入退避：
        // 1→2→4→8 分钟，封顶该平台配置周期。
        public void RecordFailure(string id, string status, AppConfig config, bool inUse, DateTime nowUtc) {
            lock (gate) {
                Channel c = ChannelOf(id);
                c.LastAttemptUtc = nowUtc;
                c.ConsecutiveFailures++;
                if (status == "expired" || status == "setup") { c.Kind = "auth"; c.NextAutoUtc = DateTime.MinValue; return; }
                c.TransientFailures++;
                int cap = Pacing.Minutes(config, id, inUse);
                // 1→2→4→8；更多次失败保持 8，再与平台配置周期取小。
                int minutes = Math.Min(cap, 1 << Math.Min(3, Math.Max(0, c.TransientFailures - 1)));
                c.Kind = "backoff"; c.LastBackoffMinutes = minutes;
                c.NextAutoUtc = nowUtc + TimeSpan.FromMinutes(minutes);
            }
        }

        private static void FeedBurn(Channel c, ProviderState state, DateTime nowUtc) {
            Quota headline = UsageDetails.Primary(state);
            double? remaining = headline != null ? headline.Remaining : (double?)null;
            if (!remaining.HasValue) { c.HasPercentModel = false; c.Samples.Clear(); c.BurnPerHour = null; return; }
            c.HasPercentModel = true;
            if (c.Samples.Count > 0 && remaining.Value > c.Samples[c.Samples.Count - 1].Remaining + ResetEpsilon) {
                c.Samples.Clear(); c.BurnPerHour = null;    // 读数回升＝窗口重置：速率状态清零
            }
            c.Samples.Add(new Sample { AtUtc = nowUtc, Remaining = remaining.Value });
            if (c.Samples.Count > MaxSamples) c.Samples.RemoveAt(0);
            c.BurnPerHour = EstimateBurnPerHour(c.Samples);
        }

        // 相邻样本消耗速率（%/小时）的中位数（偶数对取中间两值的平均）；样本 < 2 或全部
        // 间隔过短时 null。中位数让单点读数跳变不主导决策。
        public static double? EstimateBurnPerHour(IReadOnlyList<Sample> samples) {
            if (samples == null || samples.Count < 2) return null;
            var rates = new List<double>();
            for (int i = 1; i < samples.Count; i++) {
                double hours = (samples[i].AtUtc - samples[i - 1].AtUtc).TotalHours;
                if (hours < MinSampleGapHours) continue;
                rates.Add((samples[i - 1].Remaining - samples[i].Remaining) / hours);
            }
            if (rates.Count == 0) return null;
            rates.Sort();
            int middle = rates.Count / 2;
            return rates.Count % 2 == 1 ? rates[middle] : (rates[middle - 1] + rates[middle]) / 2;
        }

        // ── 定向刷新（Phase 2.1）：凭据/启用变更后只动真正变化的平台 ──
        // keysBefore/After：变更前后各平台的已保存密钥（Store.SavedKey）；不同即视为变更
        //（覆盖 填写新 Key、移除 Key、自定义平台密钥）。endpointChanged：区域切换等改变
        // 请求端点的平台。enabledBefore/After：启用集合——新增的平台需要首查，移除的平台丢通道。
        public static RefreshPlan PlanCredentialRefresh(Dictionary<string, string> keysBefore, Dictionary<string, string> keysAfter,
            IEnumerable<string> endpointChanged, IEnumerable<string> enabledBefore, IEnumerable<string> enabledAfter) {
            var plan = new RefreshPlan();
            var before = enabledBefore ?? new string[0];
            var after = enabledAfter ?? new string[0];
            foreach (string id in before.Where(id => !after.Contains(id))) plan.Forget.Add(id);
            if (keysBefore != null && keysAfter != null) {
                foreach (string id in keysBefore.Keys) {
                    string was, now;
                    keysBefore.TryGetValue(id, out was);
                    keysAfter.TryGetValue(id, out now);
                    if (!String.Equals(was, now, StringComparison.Ordinal)) plan.Reset.Add(id);
                }
            }
            foreach (string id in endpointChanged ?? new string[0]) plan.Reset.Add(id);
            foreach (string id in after.Where(id => !before.Contains(id))) plan.Reset.Add(id);
            plan.Reset.ExceptWith(plan.Forget);
            return plan;
        }
    }
}
