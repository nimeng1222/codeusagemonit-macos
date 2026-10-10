using System;

namespace CodeUsageMonit {
    // Claude Code 登录凭据（claudeAiOauth JSON）的读取链。macOS 新版 Claude Code 把凭据
    // 存进钥匙串通用密码项：service「Claude Code-credentials」、account=当前用户名，值为
    // 与旧凭据文件相同的 JSON 结构（claudeAiOauth.accessToken / refreshToken / expiresAt /
    // subscriptionType）。读取顺序：钥匙串优先，读不到或内容损坏再回退凭据文件
    //（~/.claude/.credentials.json，CLAUDE_CONFIG_DIR 可改位置，由调用方解析传入）。
    // 两个来源都不含可用 accessToken 时返回 null，Fetch 走既有的 Need → setup 提示路径。
    // 这里不做 token 刷新，过期后由用户重新登录。
    public static class ClaudeCredentials {
        public const string KeychainService = "Claude Code-credentials";
        // keychainGet(service, account)：钥匙串读取通道。生产传 Keychain.GetFromService；
        // 自测注入合成值，绝不触碰真实钥匙串。返回值可为 null（条目不存在）。
        public static object Read(string filePath, Func<string, string, string> keychainGet) {
            string raw = "";
            try { if (keychainGet != null) raw = (keychainGet(KeychainService, Environment.UserName) ?? "").Trim(); } catch { }
            if (raw.Length > 0) {
                object parsed = TryParse(raw);
                if (parsed != null) return parsed;   // 钥匙串命中且可用：不再读文件
            }
            try { return J.File(filePath); } catch { return null; }
        }
        // JSON 里必须真的有 claudeAiOauth.accessToken 才算可用——空壳或损坏内容继续走文件兜底。
        private static object TryParse(string raw) {
            try {
                object root = J.Parse(raw);
                return J.Str(root, "claudeAiOauth", "accessToken").Trim().Length > 0 ? root : null;
            } catch { return null; }
        }
    }
}
