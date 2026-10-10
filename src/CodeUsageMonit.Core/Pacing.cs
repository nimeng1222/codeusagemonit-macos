using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CodeUsageMonit {
    // How often each platform is read. By default every platform follows the global interval
    // (设置 › 高级 › 自动刷新); a platform can have its own, and one whose client is writing
    // its local session logs right now ("in use", e.g. a Claude Code conversation) is read
    // more often, quotas and local usage alike. A 30-second tick reads whatever is due.
    public static class Pacing {
        public static readonly TimeSpan InUseWindow = TimeSpan.FromMinutes(10);
        public static readonly int[] OwnChoices = { 0, 1, 2, 3, 5, 10, 15, 30, 60 };   // 0 = follow the global interval
        public static readonly int[] InUseChoices = { 1, 2, 3, 5 };
        // A little early rather than a whole tick late（30 秒 tick 下的提前量，Due 与 LimitsRuntime 共用）。
        public static readonly TimeSpan EarlyMargin = TimeSpan.FromSeconds(20);

        // Minutes between reads of one platform.
        public static int Minutes(AppConfig config, string id, bool inUse) {
            int own; int minutes = config.ProviderMinutes != null && config.ProviderMinutes.TryGetValue(id, out own) && own > 0 ? own : config.RefreshMinutes;
            if (config.FastInUse && inUse) minutes = Math.Min(minutes, Math.Max(1, config.InUseMinutes));
            return Math.Max(1, minutes);
        }
        public static bool Due(DateTime lastUtc, int minutes, DateTime nowUtc) {
            return lastUtc == DateTime.MinValue || nowUtc - lastUtc >= TimeSpan.FromMinutes(minutes) - EarlyMargin;
        }
        // Folders whose files change while a platform's client is working.
        public static IEnumerable<string> ActivityFolders(string id) {
            string home = LogReader.Home;
            Func<string, string, string> env = (variable, fallback) => { string v = Environment.GetEnvironmentVariable(variable); return String.IsNullOrWhiteSpace(v) ? fallback : v.Trim(); };
            switch (id) {
                case "codex": return new[] { Path.Combine(ProviderService.CodexHome(), "sessions") };
                case "claude": return ClaudeLogs.ConfigDirs().Select(d => Path.Combine(d, "projects"));
                // Windows 的 %APPDATA%\Cursor\User\globalStorage 在 macOS 的对应目录。
                case "cursor": return new[] { PlatformPaths.CursorStorageDir };
                case "antigravity": return new[] { Path.Combine(home, ".gemini") };
                case "deepseek": return new[] { Path.Combine(HarnessLogs.Root, "sessions") };
                case "grok": return new[] { Path.Combine(env("GROK_HOME", Path.Combine(home, ".grok")), "sessions") };
                case "kimi": return new[] { Path.Combine(home, ".kimi"), Path.Combine(home, ".kimi-code") };
                case "zcode": return new[] { env("ZCODE_HOME", Path.Combine(home, ".zcode")) };
                case "opencode": return new[] { Path.Combine(home, ".local", "share", "opencode") };
                case "pi": return new[] { Path.Combine(home, ".pi", "agent", "sessions") };
                case "copilot": return new[] { env("COPILOT_HOME", Path.Combine(home, ".copilot")) };
                default: return new string[0];
            }
        }
        // Whether a changed file in a watched folder means work (not, say, a config file).
        // Phase 0.3：watcher 升级为扫描触发源后，这里同时是防死循环的第一道闸。
        public static bool CountsAsWork(string id, string path) {
            string name = Path.GetFileName(path).ToLowerInvariant();
            // 自写侧车排除：SQLite WAL 库（ZCode 的 db.sqlite、Cursor 的 state.vscdb 等）的
            // -shm/-wal 不是工作量。只读打开 WAL 库也可能重写它们（WAL 恢复/首个连接场景），
            // 放行会让“事件→扫描→侧车写入→事件”成死循环；本机实测
            // 0/20 次 shm 重写、1/20 次 wal 变化，见 verification/phase0/shm-measurement.txt——
            // 排除是纵深防御：侧车变化总伴随主库文件本身的事件，排除不漏工作量）。
            if (name.EndsWith("-shm", StringComparison.Ordinal) || name.EndsWith("-wal", StringComparison.Ordinal)) return false;
            // 我们自己写入的 data/（history.json、*-logs.json 等）通常不在监视根下，但便携
            // 模式或 CODEUSAGEMONIT_DATA 指进监视根时会被自己触发——一并排除。
            if (UnderOwnData(path)) return false;
            if (id == "cursor") return name.StartsWith("state.vscdb", StringComparison.Ordinal);
            // Antigravity: its conversation databases, not the app's caches and logs.
            if (id == "antigravity") return path.IndexOf("antigravity", StringComparison.OrdinalIgnoreCase) >= 0 && path.IndexOf("conversations", StringComparison.OrdinalIgnoreCase) >= 0;
            return name.EndsWith(".jsonl") || name.Contains(".jsonl.") || name.EndsWith(".db") || name.EndsWith(".sqlite") || name.EndsWith(".json");
        }
        // 是否位于本程序自己的数据目录（Store.Data）之内。
        private static bool UnderOwnData(string path) {
            try {
                string data = Store.Data;
                if (String.IsNullOrEmpty(data)) return false;
                string root = Path.GetFullPath(data);
                if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) && !root.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)) root += Path.DirectorySeparatorChar;
                return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            } catch { return false; }
        }
    }
}
