// macOS Keychain, in place of Windows DPAPI (ProtectedData) for saved provider keys and
// the "gemini:antigravity" credential lookup. Implemented over the `security` command
// line (add-generic-password / find-generic-password -w / delete-generic-password) with
// the service name "codeusagemonit"; the account is the provider id or the credential
// target. Secrets never leave the keychain: the data/*.key file holds only the
// "keychain:<account>" reference, so backups still contain no keys.
using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace CodeUsageMonit {
    internal static class Keychain {
        public const string ServiceName = "codeusagemonit";
        // 自测缝：非 null 时替代真实 security 子进程（合成成功/失败，绝不触碰真实钥匙串）；
        // 生产代码恒为 null。仅 Core 自测装配体内可见。
        internal static Func<string[], string> RunOverride;

        public static string Get(string account) {
            if (String.IsNullOrWhiteSpace(account)) return "";
            string output = Run("find-generic-password", "-s", ServiceName, "-a", account, "-w");
            return output == null ? "" : output.TrimEnd('\r', '\n');
        }
        // 读其他应用写入的通用密码项（如 Claude Code 的「Claude Code-credentials」）：
        // service 由调用方指定，account 通常是当前用户名。值原样返回（可能是 JSON）。
        public static string GetFromService(string service, string account) {
            if (String.IsNullOrWhiteSpace(service) || String.IsNullOrWhiteSpace(account)) return "";
            string output = Run("find-generic-password", "-s", service, "-a", account, "-w");
            return output == null ? "" : output.TrimEnd('\r', '\n');
        }
        // 只判断条目是否存在（find-generic-password 不带 -w：命中 exit 0、不存在 exit 44），
        // 绝不读出密钥值——供凭据存在性探测（SourceHealth / 认证页检测区）使用。
        public static bool Exists(string service, string account) {
            if (String.IsNullOrWhiteSpace(service) || String.IsNullOrWhiteSpace(account)) return false;
            return Run("find-generic-password", "-s", service, "-a", account) != null;
        }

        public static bool Set(string account, string value) {
            if (String.IsNullOrWhiteSpace(account)) return false;
            // -U updates the item when the account already exists.
            return Run("add-generic-password", "-U", "-s", ServiceName, "-a", account, "-w", value ?? "") != null;
        }

        public static bool Delete(string account) {
            if (String.IsNullOrWhiteSpace(account)) return false;
            return Run("delete-generic-password", "-s", ServiceName, "-a", account) != null;
        }

        // Exit status 44 on find-generic-password means "item not found" — a miss, not an error.
        private static string Run(params string[] arguments) {
            Func<string[], string> injected = RunOverride;
            if (injected != null) { try { return injected(arguments); } catch { return null; } }
            return Execute("security", arguments, 4000);
        }
        // 子进程执行，4 秒超时对读取同样生效。旧实现先同步 ReadToEnd stdout、再读 stderr：
        // 两个管道可能互锁，且同步读取发生在 WaitForExit(4000) 之前——子进程只要不关闭
        // 管道，超时永远不会被检查（实测 6 秒才返回，甚至可能永久阻塞 UI 线程）。
        // 现在两个管道从头就并行异步读取，退出等待与管道读取共用同一超时；超时杀死
        // 进程树并回 null。ExitCode != 0（含 find-generic-password 的 44 = 条目不存在）
        // 同样回 null。internal 供自测用合成命令回归超时与并发读取。
        internal static string Execute(string fileName, string[] arguments, int timeoutMs) {
            try {
                var start = new ProcessStartInfo(fileName) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string argument in arguments) start.ArgumentList.Add(argument);
                using (var process = Process.Start(start)) {
                    if (process == null) return null;
                    Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(timeoutMs)) {
                        try { process.Kill(true); } catch { try { process.Kill(); } catch { } }
                        Observe(stdout, stderr);
                        return null;
                    }
                    // 进程已退出但管道可能被孙进程占着：兜底限时收尾，仍然守住总时长。
                    if (!WaitForReads(stdout, stderr, 2000)) { try { process.Kill(true); } catch { } return null; }
                    return process.ExitCode == 0 ? stdout.Result : null;
                }
            } catch { return null; }
        }
        // 等待两个读取任务完成；超时或任务异常都算失败（异常只观察、不上抛）。
        private static bool WaitForReads(Task stdout, Task stderr, int timeoutMs) {
            try { return Task.WaitAll(new Task[] { stdout, stderr }, timeoutMs); } catch { return false; }
        }
        private static void Observe(Task stdout, Task stderr) { WaitForReads(stdout, stderr, 2000); }
    }
}
