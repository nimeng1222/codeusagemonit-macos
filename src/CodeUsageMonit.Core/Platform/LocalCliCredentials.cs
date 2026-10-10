// 本机 CLI 登录凭据的只读导入（ZCode / Kimi Code）。用户没有保存密钥、也没有设置
// 环境变量时，Fetch 与连接卡片用它静默回退：
// - ZCode 读 ~/.zcode/v2/credentials.json 里以 ":api-key" 结尾的键（多账号取排序后的
//   最后一个，与客户端 active provider 的简化对齐）。值形如
//   `enc:v1:<b64url(iv)>.<b64url(tag)>.<b64url(ciphertext)>`：AES-256-GCM，
//   key = SHA256(secret)，secret 优先环境变量 ZCODE_CREDENTIAL_SECRET（trim 后），
//   否则客户端回退串 `zcode-credential-fallback:<platform>:<homedir>:<username>`
//   （platform 与 node 的 os.platform() 一致：darwin / win32 / linux）。算法与
//   ZCode 客户端逐值对齐（createZCodeCredentialCipher；本机实测可解密）。
//   非 `enc:v1:` 前缀的值按客户端的兼容路径原样采用（明文存储）。
// - Kimi 按序解析：config.toml 内联 api_key（长期凭据，导入即持久化）→
//   ~/.kimi-code/credentials/kimi-code.json 的 OAuth access_token（短寿命凭据：
//   绝不写进钥匙串，每次 Fetch 重新解析；expires_at 进入 30s 余量内自动用
//   refresh_token 刷新——POST 固定端点 auth.kimi.com/api/oauth/token，form 为
//   client_id（客户端内置公开常量）/ grant_type=refresh_token / refresh_token
//   （URL 编码）。200 且返回非空 access_token 才算成功；400/401 视为登录态失效，
//   其他状态与网络错误同样静默回空。成功后把新 token 原子回写凭据文件（保留未知键、
//   0600 权限、临时文件 + Move 覆盖）：refresh_token 会轮转，不回写会让 Kimi Code
//   本体的登录态失效；回写失败不阻断本次使用（新 token 已在内存里））。
// 长期凭据（ZCode、Kimi api_key）导入成功只是调用现有 Store.SetProviderKey 存进
// 钥匙串；任何失败都返回空串、走原有的「请填写 API Key」提示路径——绝不抛出，
// 也绝不把密钥写进日志、异常消息或测试。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace CodeUsageMonit {
    public static class LocalCliCredentials {
        public static string ZCodeCredentialsPath { get { return Path.Combine(Home, ".zcode", "v2", "credentials.json"); } }
        public static string KimiConfigPath { get { return Path.Combine(Home, ".kimi-code", "config.toml"); } }
        public static string KimiCredentialsPath { get { return Path.Combine(Home, ".kimi-code", "credentials", "kimi-code.json"); } }

        // 生产入口：按平台 id 导入。ZCode 与 Kimi api_key 经现有 SetProviderKey 存钥匙串
        // 并返回密钥；Kimi OAuth 只解析不持久化（ProviderKey 恒为空 → 每次 Fetch 重新
        // 解析并按需刷新）；其余一切情况返回空串（Fetch 继续走 Need 的 setup 提示）。
        // accountId（Phase 5）：持久化到指定账户；null = 默认账户（生产调用方传活动账户）。
        public static string Import(string id) { return Import(id, null, null); }
        public static string Import(string id, string accountId) { return Import(id, null, null, accountId); }
        public static string Import(string id, KimiTokenPoster poster, DateTime? nowUtc) { return Import(id, poster, nowUtc, null); }
        public static string Import(string id, KimiTokenPoster poster, DateTime? nowUtc, string accountId) {
            string target = String.IsNullOrEmpty(accountId) ? Accounts.DefaultId : accountId;
            try {
                if (id == "zcode") return Persisted(id, target, ZCodeApiKey(ZCodeCredentialsPath, Secret()));
                if (id == "kimi") return KimiImport(KimiConfigPath, KimiCredentialsPath, nowUtc, poster, (persistId, persistKey) => Store.SetProviderKey(persistId, target, persistKey));
                return "";
            } catch { return ""; }
        }
        // 钥匙串不可写时本次仍使用导入值。
        private static string Persisted(string id, string accountId, string key) {
            if (String.IsNullOrWhiteSpace(key)) return "";
            key = key.Trim();
            try { Store.SetProviderKey(id, accountId, key); } catch { }
            return key;
        }

        // ── ZCode: credentials.json + AES-256-GCM ────────────────────────
        public const string CipherPrefix = "enc:v1:";
        // 读取并解密 api-key；文件缺失、无 api-key 键、解密失败一律返回空串。
        public static string ZCodeApiKey(string path, string secret) {
            try {
                object root = J.File(path);
                string chosenKey = "", stored = "";
                foreach (var pair in J.Dict(root)) {
                    string value = pair.Value as string;
                    if (!pair.Key.EndsWith(":api-key", StringComparison.Ordinal) || String.IsNullOrEmpty(value)) continue;
                    if (String.CompareOrdinal(pair.Key, chosenKey) > 0) { chosenKey = pair.Key; stored = value; }
                }
                if (chosenKey.Length == 0) return "";
                return ZCodeDecrypt(stored, secret).Trim();
            } catch { return ""; }
        }
        // 解密单个 `enc:v1:` 值（AES-256-GCM，key = SHA256(secret)）。非 `enc:v1:` 值
        // 原样返回（客户端明文兼容路径）；段数、IV/Tag 长度、base64 或密钥不匹配时抛出。
        public static string ZCodeDecrypt(string stored, string secret) {
            if (!stored.StartsWith(CipherPrefix, StringComparison.Ordinal)) return stored;
            string[] parts = stored.Substring(CipherPrefix.Length).Split('.');
            if (parts.Length != 3) throw new CryptographicException("ZCode 凭据密文段数非法");
            byte[] iv = Base64Url(parts[0]), tag = Base64Url(parts[1]), cipher = Base64Url(parts[2]);
            if (iv.Length != 12 || tag.Length != 16) throw new CryptographicException("ZCode 凭据 IV/Tag 长度非法");
            byte[] plain = new byte[cipher.Length];
            using (var gcm = new AesGcm(Sha256(secret), 16)) gcm.Decrypt(iv, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        // 环境变量 ZCODE_CREDENTIAL_SECRET 优先（客户端会 trim），否则回退串。
        public static string Secret() {
            string custom = Environment.GetEnvironmentVariable("ZCODE_CREDENTIAL_SECRET");
            if (!String.IsNullOrWhiteSpace(custom)) return custom.Trim();
            string platform = OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsWindows() ? "win32" : "linux";
            return FallbackSecret(platform, Home, UserName());
        }
        // 客户端回退串（node: `zcode-credential-fallback:${os.platform()}:${os.homedir()}:${username}`）。
        public static string FallbackSecret(string platform, string home, string user) {
            return "zcode-credential-fallback:" + platform + ":" + home + ":" + user;
        }
        private static string UserName() {
            try { string name = Environment.UserName; return String.IsNullOrWhiteSpace(name) ? "unknown" : name; } catch { return "unknown"; }
        }
        private static byte[] Base64Url(string text) {
            string padded = text.Replace('-', '+').Replace('_', '/');
            int remainder = padded.Length % 4;
            if (remainder == 1) throw new FormatException("ZCode 凭据 base64 长度非法");
            if (remainder == 2) padded += "=="; else if (remainder == 3) padded += "=";
            return Convert.FromBase64String(padded);
        }
        private static byte[] Sha256(string text) { return SHA256.HashData(Encoding.UTF8.GetBytes(text)); }

        // ── Kimi: config.toml 的内联 api_key ─────────────────────────────
        // 只认 `[providers."managed:kimi-code"]` 表里的 api_key（表内后一行覆盖前一行）。
        public static string KimiApiKey(string path) {
            try {
                string target = "[providers.\"managed:kimi-code\"]";
                bool inSection = false; string found = "";
                foreach (string raw in File.ReadAllText(path).Split('\n')) {
                    string line = raw.Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal)) { inSection = line == target; continue; }
                    if (!inSection || line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    string value = TomlString(line, "api_key");
                    if (value != null) found = value;
                }
                return found.Trim();
            } catch { return ""; }
        }
        // 单行 `key = "value"`：键名在 "=" 左侧 trim 后精确相等，避免把
        // `api_key_v2 = "…"` 这类相邻键误读成 api_key。其余形态返回 null。
        private static string TomlString(string line, string key) {
            int eq = line.IndexOf('=');
            if (eq < 0 || line.Substring(0, eq).Trim() != key) return null;
            string value = line.Substring(eq + 1).Trim();
            if (value.Length < 2 || !value.StartsWith("\"", StringComparison.Ordinal) || !value.EndsWith("\"", StringComparison.Ordinal)) return null;
            return value.Substring(1, value.Length - 2);
        }

        // ── Kimi: OAuth（kimi-code.json）────────────────────────────────
        // 刷新端点是源码常量，凭据文件里的任何值都不能覆盖它（https 仅此一处）。
        public const string KimiOAuthTokenUrl = "https://auth.kimi.com/api/oauth/token";
        // Kimi Code 客户端内置的 OAuth client id（公开标识符，可以写进源码）。
        public const string KimiOAuthClientId = "17e5f671-d194-4dfb-9706-5516cb48c098";
        // 凭据是否需要刷新的余量（expires_at <= now + 30s 才刷新），与客户端一致。
        public const int KimiRefreshSlackSeconds = 30;
        // 回写的临时文件后缀（写完即 Move 覆盖，正常路径不会留下）。
        public const string KimiWriteBackTempSuffix = ".codeusagemonit.tmp";

        public sealed class KimiOAuth {
            public string AccessToken = "", RefreshToken = "";
            public double? ExpiresAt;                    // epoch 秒；可缺省（缺省 = 直接用）
            public Dictionary<string, object> Original;  // 解析出的完整对象，回写时保留未知键
        }
        // 刷新端点的响应（自测用合成 poster 与生产 HTTP 通道共用这一形状）。
        public sealed class KimiTokenReply { public int Status; public string Body; }
        public delegate KimiTokenReply KimiTokenPoster(string url, string formBody);

        // Kimi 导入链：config.toml 内联 api_key（长期凭据，非空优先，经 persist 持久化）
        // → OAuth access_token（短寿命，绝不持久化）。persist 传 null 时不持久化（自测用）；
        // 任何失败回空串。
        public static string KimiImport(string configPath, string credentialsPath, DateTime? nowUtc, KimiTokenPoster poster, Action<string, string> persist) {
            try {
                string apiKey = KimiApiKey(configPath);
                if (!String.IsNullOrWhiteSpace(apiKey)) {
                    apiKey = apiKey.Trim();
                    if (persist != null) { try { persist("kimi", apiKey); } catch { } }
                    return apiKey;
                }
                return KimiOAuthToken(credentialsPath, nowUtc, poster);
            } catch { return ""; }
        }

        // 读取 OAuth 凭据：access_token 为空、文件缺失或损坏都视为没有凭据（null）。
        public static KimiOAuth LoadKimiOAuthCredentials(string path) {
            try {
                object root = J.File(path);
                string access = J.Str(root, "access_token").Trim();
                if (access.Length == 0) return null;
                return new KimiOAuth {
                    AccessToken = access,
                    RefreshToken = J.Str(root, "refresh_token"),
                    ExpiresAt = J.Num(root, "expires_at"),
                    Original = J.Dict(root)
                };
            } catch { return null; }
        }
        // expires_at <= now + 30s 才刷新；无 expires_at 直接用。
        public static bool KimiNeedsRefresh(KimiOAuth credentials, double nowSeconds) {
            if (credentials == null || !credentials.ExpiresAt.HasValue) return false;
            return credentials.ExpiresAt.Value <= nowSeconds + KimiRefreshSlackSeconds;
        }
        // OAuth 解析 + 按需刷新：成功返回 access_token，其余一切情况返回空串。
        public static string KimiOAuthToken(string path, DateTime? nowUtc, KimiTokenPoster poster) {
            try {
                KimiOAuth credentials = LoadKimiOAuthCredentials(path);
                if (credentials == null) return "";
                if (!KimiNeedsRefresh(credentials, EpochSeconds(nowUtc ?? DateTime.UtcNow))) return credentials.AccessToken;
                return KimiRefreshToken(credentials, path, nowUtc ?? DateTime.UtcNow, poster ?? DefaultKimiTokenPoster);
            } catch { return ""; }
        }
        // 刷新：POST 固定端点；200 且 access_token 非空才算成功并回写凭据文件。
        // 400/401 视为登录态失效；其他状态、响应异常与网络错误同样静默回空串
        //（回走「请填写 API Key」提示路径），不抛出、不落日志。
        public static string KimiRefreshToken(KimiOAuth credentials, string path, DateTime nowUtc, KimiTokenPoster poster) {
            try {
                if (credentials == null || String.IsNullOrWhiteSpace(credentials.RefreshToken)) return ""; // 无 refresh_token：需在客户端重新登录
                KimiTokenReply reply = poster(KimiOAuthTokenUrl, KimiRefreshBody(credentials.RefreshToken));
                if (reply == null || reply.Status != 200) return "";
                object root = String.IsNullOrEmpty(reply.Body) ? null : J.Parse(reply.Body);
                string access = J.Str(root, "access_token").Trim();
                if (access.Length == 0) return "";
                WriteBackKimiCredentials(credentials, path, root, access, nowUtc);
                return access;
            } catch { return ""; }
        }
        // 刷新请求体：grant_type 固定 refresh_token，refresh_token 走 RFC 3986 百分号编码。
        public static string KimiRefreshBody(string refreshToken) {
            return "client_id=" + UrlEncode(KimiOAuthClientId) + "&grant_type=refresh_token&refresh_token=" + UrlEncode(refreshToken ?? "");
        }
        // RFC 3986 unreserved（字母数字 - . _ ~）之外全部百分号编码；不用
        // WebUtility.UrlEncode（它把空格编成 "+"，与 OAuth form 语义不符）。
        public static string UrlEncode(string value) {
            var builder = new StringBuilder();
            foreach (byte b in Encoding.UTF8.GetBytes(value ?? "")) {
                bool unreserved = (b >= '0' && b <= '9') || (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || b == '-' || b == '.' || b == '_' || b == '~';
                if (unreserved) builder.Append((char)b);
                else builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }
        // 成功刷新后回写凭据文件：保留原文件未知键，0600 权限，临时文件 + Move 原子覆盖。
        // 新的 refresh_token / expires_in / scope / token_type 有则更新；expires_in 异常
        // 巨大（如 1e300）时跳过 expires_at 回写，防溢出（只失去主动刷新时机）。回写
        // 失败不阻断本次使用（新 token 已在内存里），所以只吞掉异常。
        public static void WriteBackKimiCredentials(KimiOAuth credentials, string path, object reply, string accessToken, DateTime nowUtc) {
            try {
                var updated = new Dictionary<string, object>(credentials.Original ?? new Dictionary<string, object>());
                updated["access_token"] = accessToken;
                string refresh = J.Str(reply, "refresh_token"); if (refresh.Length > 0) updated["refresh_token"] = refresh;
                double? expiresIn = J.Num(reply, "expires_in");
                if (expiresIn.HasValue) {
                    double expiresAt = EpochSeconds(nowUtc) + expiresIn.Value;
                    if (!Double.IsNaN(expiresAt) && !Double.IsInfinity(expiresAt) && expiresAt >= 0 && expiresAt < Int64.MaxValue) updated["expires_at"] = (long)expiresAt;
                }
                string scope = J.Str(reply, "scope"); if (scope.Length > 0) updated["scope"] = scope;
                string tokenType = J.Str(reply, "token_type"); if (tokenType.Length > 0) updated["token_type"] = tokenType;
                string temp = path + KimiWriteBackTempSuffix;
                var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
                // 0600 在创建时就位（不写 0644 再事后收紧，避免中间窗口暴露）。
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (FileStream stream = new FileStream(temp, options)) {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(J.Serializer().Serialize(updated));
                    stream.Write(bytes, 0, bytes.Length);
                }
                File.Move(temp, path, true); // 同目录原子替换；落地的就是 0600 的临时文件
            } catch { }
        }
        private static double EpochSeconds(DateTime utc) { return (utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }

        // 生产刷新通道：与 ProviderService 相同的 HttpClient 模式（沿用系统/环境代理、
        // 无 Cookie、不跟随重定向）。Send 是同步调用：不经异步延续，UI 线程直接阻塞
        // 至多 10s，不会出现 sync-over-async 死锁。
        private static readonly Lazy<HttpClient> RefreshClient = new Lazy<HttpClient>(CreateRefreshClient);
        private static HttpClient CreateRefreshClient() {
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            try {
                string proxy = ProviderService.ResolveProxy("auto");
                if (proxy.Length > 0) { handler.UseProxy = true; handler.Proxy = new WebProxy(proxy); }
            } catch { }
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        }
        private static KimiTokenReply DefaultKimiTokenPoster(string url, string formBody) {
            using (var request = new HttpRequestMessage(HttpMethod.Post, url)) {
                request.Headers.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded");
                using (HttpResponseMessage response = RefreshClient.Value.Send(request)) {
                    return new KimiTokenReply { Status = (int)response.StatusCode, Body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult() };
                }
            }
        }

        private static string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }
    }
}
