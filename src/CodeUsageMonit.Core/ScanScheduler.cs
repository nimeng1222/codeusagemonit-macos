using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodeUsageMonit {
    // 调度器产出的一轮扫描请求：Full = 归因不到平台 / 上轮失败重试 / 跨天，此时忽略 Agents。
    public sealed class ScanRequest {
        public bool Full;
        public string[] Agents = Array.Empty<string>();
    }

    // 事件 → 定向扫描调度器（ROADMAP Phase 0.1）。纯 Core 逻辑、时钟注入、可单测：
    // - 1.5s 重臂 debounce：每个事件都重置静默计时，静默期满才触发（持续写入合并成一次）；
    // - 5s 单调 deadline：从"首个未处理事件"起最多等 5s 强制触发。扫描启动即消费 pending，
    //   deadline 只在无扫描运行时评估——执行期间到来的事件重新起算，长扫描不会饿死刷新；
    // - 单飞合并：扫描进行中不排队新扫描，事件只合并范围（平台取并集；fullScan 置位后
    //   不撤销，直到被一次扫描启动消费）。扫描结束后若仍有未处理事件，OnTimer 的下一拍
    //   即按同一套静默/deadline 规则立即续跑（合并范围），无需等 30 秒兜底 tick。
    // 线程模型：Report 来自 FileSystemWatcher 的线程池线程，OnTimer / NotifyScanCompleted
    // 来自 UI 线程——内部用锁保护，fire 回调在锁外调用。
    public sealed class ScanScheduler {
        public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(1.5);
        public static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(5);

        private readonly Func<DateTime> now;
        private readonly Action<ScanRequest> fire;
        private readonly TimeSpan debounce, maxDelay;
        private readonly object gate = new object();
        private readonly HashSet<string> pending = new HashSet<string>(StringComparer.Ordinal);
        private bool pendingFull;                // 一旦置位不撤销，直到被一次扫描启动消费
        private DateTime? firstPendingAt;        // 首个未处理事件的时间（deadline 起点）
        private DateTime lastEventAt = DateTime.MinValue;
        private bool running;

        public ScanScheduler(Func<DateTime> clock, Action<ScanRequest> onFire) : this(clock, onFire, DefaultDebounce, DefaultMaxDelay) { }
        public ScanScheduler(Func<DateTime> clock, Action<ScanRequest> onFire, TimeSpan debounce, TimeSpan maxDelay) {
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            if (onFire == null) throw new ArgumentNullException(nameof(onFire));
            now = clock; fire = onFire; this.debounce = debounce; this.maxDelay = maxDelay;
        }

        // watcher 命中 / 降级轮询命中：归因到平台传 agentId；归因不到传 null（→ 全量）。
        public void Report(string agentId) {
            DateTime at = now();
            lock (gate) {
                if (String.IsNullOrEmpty(agentId)) pendingFull = true;
                else pending.Add(agentId);
                if (!firstPendingAt.HasValue) firstPendingAt = at;
                lastEventAt = at;
            }
        }

        // 跨天等需要全量的场合：把下一次触发的扫描升级为全量（不主动触发）。
        public void MarkFullScan() { lock (gate) pendingFull = true; }

        // UI 定时器（AppMonitor 200ms）/ 测试驱动：静默期满或 deadline 到即触发一轮。
        public void OnTimer() {
            ScanRequest request;
            lock (gate) {
                // 单飞：进行中不触发；没有未处理事件（含"只有全量标记、无事件"）也不触发——
                // 全量标记只改变下一次触发的范围，主动刷新仍走 30 秒 tick / 手动刷新。
                if (running || !firstPendingAt.HasValue) return;
                DateTime at = now();
                bool quiet = at - lastEventAt >= debounce;
                bool deadline = at - firstPendingAt.Value >= maxDelay;
                if (!quiet && !deadline) return;
                request = new ScanRequest { Full = pendingFull, Agents = pending.ToArray() };
                running = true;
                pending.Clear(); pendingFull = false; firstPendingAt = null;
            }
            fire(request);
        }

        // AppMonitor 在一轮扫描真正结束时回调：失败 → 下一轮全量；期间积压的事件在
        // 下一拍按静默/deadline 规则续跑。
        public void NotifyScanCompleted(bool failed) {
            lock (gate) {
                running = false;
                if (failed) pendingFull = true;
            }
        }

        // 测试 / 诊断观察用。
        public bool IsRunning { get { lock (gate) return running; } }
        public int PendingAgents { get { lock (gate) return pending.Count; } }
        public bool PendingFull { get { lock (gate) return pendingFull; } }
    }

    // 事件路径 → agent id（Phase 0.1 按平台定向）：按各平台监视根（Pacing.ActivityFolders，
    // 与 UsageScan.Agents 的扫描根同源）做最长前缀匹配；匹配不到返回 null → 全量扫描。
    // 前缀表进程内缓存一次（环境变量与 home 在进程生命周期内不变）。
    public static class ScanAttribution {
        private static KeyValuePair<string, string>[] prefixes;   // 归一化前缀（以分隔符结尾）→ agentId
        private static readonly object gate = new object();

        public static string AgentFor(string path) {
            if (String.IsNullOrEmpty(path)) return null;
            string full;
            try { full = Path.GetFullPath(path); } catch { return null; }
            foreach (var pair in Map()) {
                if (full.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            }
            return null;
        }

        private static KeyValuePair<string, string>[] Map() {
            lock (gate) {
                if (prefixes != null) return prefixes;
                var list = new List<KeyValuePair<string, string>>();
                foreach (string id in ProviderCatalog.Ids) {
                    foreach (string folder in Pacing.ActivityFolders(id)) {
                        if (String.IsNullOrWhiteSpace(folder)) continue;
                        string prefix;
                        try { prefix = Path.GetFullPath(folder); } catch { continue; }
                        if (!prefix.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) && !prefix.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)) prefix += Path.DirectorySeparatorChar;
                        list.Add(new KeyValuePair<string, string>(prefix, id));
                    }
                }
                // 最长优先：兄弟目录（~/.kimi 与 ~/.kimi-code）靠分隔符结尾互相不吃前缀，
                // 排序只是防御嵌套根（env 把一家指到另一家之下）的场合。
                prefixes = list.OrderByDescending(p => p.Key.Length).ToArray();
                return prefixes;
            }
        }
    }
}
