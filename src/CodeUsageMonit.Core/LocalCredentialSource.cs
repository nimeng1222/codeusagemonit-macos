using System;
using System.IO;
using System.Linq;

namespace CodeUsageMonit {
    // 认证页「本机检测到的账户」区的只读探测：每个已知平台一行，回答「凭据在哪」。
    // 与 Fetch 的取用顺序同口径（env → 已保存密钥 → 本机 CLI 登录/钥匙串 → 应用登录），
    // 但只判断存在性，绝不读出密钥值、绝不发起网络请求（Antigravity 只看应用包是否存在，
    // 不做进程/回环探测）。返回短标签；空串 = 未检测到。
    public static class LocalCredentialSource {
        public static string Describe(string id, AppConfig config) {
            if (ProviderCatalog.LocalOnly(id)) return "本机日志";
            // Key 型平台 Fetch 先取环境变量（ProviderKey 语义），这里保持同一优先级。
            if (Store.KeyEnvironmentName(id, config).Length > 0) return "环境变量";
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string active = Accounts.ActiveId(config, id);
            switch (id) {
                case "codex": return File.Exists(Path.Combine(ProviderService.CodexHome(), "auth.json")) ? "CLI 登录" : "";
                case "claude":
                    // 标签口径：钥匙串条目存在即「钥匙串」。Fetch 在条目损坏（无可用
                    // accessToken）时会回退凭据文件，但「损坏」只能读值判定，而这里约定
                    // 绝不读密钥值——存在性探测接受这一近似（评审 P2-2：无更干净做法）。
                    if (Keychain.Exists(ClaudeCredentials.KeychainService, Environment.UserName)) return "钥匙串";
                    return ClaudeLogs.ConfigDirs().Any(d => File.Exists(Path.Combine(d, ".credentials.json"))) ? "CLI 登录" : "";
                case "cursor": return File.Exists(PlatformPaths.CursorStateDb) ? "应用登录" : "";
                case "antigravity": return LocalAntigravity.FindApplication().Length > 0 ? "桌面应用" : "";
                case "grok": return File.Exists(Path.Combine(home, ".grok", "auth.json")) ? "CLI 登录" : "";
                case "copilot":
                    if (Store.HasSavedKey("copilot")) return "已保存密钥";
                    return ProviderService.CopilotTokenPaths.Any(File.Exists) ? "客户端登录" : "";
                case "deepseek":
                    if (Store.HasSavedKey("deepseek", active)) return "已保存密钥";
                    return HarnessLogs.Present ? "Harness 登录" : "";
                case "kimi":
                    if (Store.HasSavedKey("kimi", active)) return "已保存密钥";
                    return File.Exists(LocalCliCredentials.KimiConfigPath) || File.Exists(LocalCliCredentials.KimiCredentialsPath) ? "CLI 登录" : "";
                case "zcode":
                    if (Store.HasSavedKey("zcode", active)) return "已保存密钥";
                    return File.Exists(LocalCliCredentials.ZCodeCredentialsPath) ? "CLI 登录" : "";
                default:
                    if (id != null && id.StartsWith("custom-", StringComparison.Ordinal)) {
                        CustomProvider custom;
                        if (ProviderCatalog.Custom.TryGetValue(id, out custom) && custom.Auth == "none") return "无需密钥";
                        return Store.HasSavedKey(id, active) ? "已保存密钥" : "";
                    }
                    return Store.HasSavedKey(id, active) ? "已保存密钥" : "";
            }
        }
    }
}
