// MiMo 桌面客户端自动续期：从本机小米 MiMo 客户端的 xiaomi-account 分区 Cookies 库读
// passToken/userId，现场铸 console serviceToken（不落盘）。两条链路的参考实现是 MiMo
// 客户端自身的 src/shared/providers/mimo/{desktop,session,browserHeaders}.js（解包实证）
// 与 github.com/mmqz/cpa-multi-plugins 的 docs/MIMO_AUTH.md。
//
// 读库（desktop.js）：Chromium Cookies SQLite，host_key=.account.xiaomi.com，只认
// passToken/userId（cUserId 顺带读但换票用不到）；value 为空且只有 encrypted_value 时
// 判加密存储（不尝试解密）。桌面客户端运行时锁库——先复制 0600 副本再只读打开，用完即删。
//
// 换票（session.js 的 redirect-walk）：一次性自管 Cookie Jar，GET console 入口
// /api/v1/balance；401 取 body 的 loginUrl（域白名单 xiaomi.com/mi.com/miui.com、仅
// https）触发 SSO 链，链回来后再打一次入口；200 且 body code==0 即成功，入口上的
// Cookie 串就是新鲜 console 凭据。429=限流；body code 403/46109（HTTP 200 也会带）或
// 终态 401/停在登录域=拒绝。每段链路 ≤8 跳（Mint 最多三段：入口→登录→入口），全程 15 秒总时限。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodeUsageMonit {
    // ── Cookies 库读取 ────────────────────────────────────────────────
    internal static class MimoDesktop {
        public sealed class AccountCookie { public string PassToken = "", UserId = "", CUserId = ""; }
        public enum ReadReason { Ok, StoreAbsent, Unreadable, Encrypted, NotSignedIn, Incomplete }
        public sealed class ReadResult {
            public ReadReason Reason;
            public AccountCookie Cookie;
            public static ReadResult Of(ReadReason reason, AccountCookie cookie = null) { return new ReadResult { Reason = reason, Cookie = cookie }; }
        }
        // macOS 上客户端分区的两种落点（旧版 "Xiaomi MiMo AI" + Network/，现行 "Xiaomi MiMo"）。
        // 其他平台没有客户端，返回空（调用方走优雅降级）。
        public static string[] StoreCandidates(string home) {
            if (!OperatingSystem.IsMacOS() || String.IsNullOrWhiteSpace(home)) return Array.Empty<string>();
            return new[] {
                Path.Combine(home, "Library", "Application Support", "Xiaomi MiMo AI", "Partitions", "xiaomi-account", "Network", "Cookies"),
                Path.Combine(home, "Library", "Application Support", "Xiaomi MiMo", "Partitions", "xiaomi-account", "Cookies")
            };
        }
        internal const string AccountHost = ".account.xiaomi.com";
        internal static readonly string[] WantedNames = { "passToken", "userId", "cUserId" };
        internal const string RequiredNames = "passToken、userId";
        // 生产入口：定位库 → 0600 副本 → 只读查询 → 删除副本。库不存在 = 客户端未装（StoreAbsent，
        // 不是错误）；读不了/加密/没登录各自成类，调用方统一回退手动 Cookie。
        public static ReadResult ReadAccount(string[] candidates, string tempDir = null) {
            string store = candidates.FirstOrDefault(path => { try { return File.Exists(path); } catch { return false; } });
            if (store == null) return ReadResult.Of(ReadReason.StoreAbsent);
            string copy = Path.Combine(String.IsNullOrEmpty(tempDir) ? Path.GetTempPath() : tempDir, "mimo-cookies-" + Guid.NewGuid().ToString("N") + ".tmp");
            try {
                File.Copy(store, copy, false);
                if (OperatingSystem.IsMacOS()) { try { File.SetUnixFileMode(copy, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { } }   // 0600 副本：运行中的客户端或其他用户读不到内容
                return ReadStore(copy);
            } catch {
                return ReadResult.Of(ReadReason.Unreadable);
            } finally {
                try { File.Delete(copy); } catch { }
            }
        }
        // 单库解析（path 已是副本）。查询行结构与 NativeCredentials.SqliteRows 的 object[] 形状一致。
        public static ReadResult ReadStore(string dbPath) {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            HashSet<string> sealedNames = new HashSet<string>(StringComparer.Ordinal);
            // 只读打开副本；查询带行数与单元格上限（SqliteRows 自带 5000 行 / 4MB）。
            foreach (string[] row in NativeCredentials.SqliteRows(dbPath, "SELECT name, value, encrypted_value FROM cookies WHERE host_key = '" + AccountHost + "' AND name IN ('passToken', 'userId', 'cUserId')")) {
                if (row.Length < 3) continue;
                string name = row[0], value = (row[1] ?? "").Trim();
                if (name.Length == 0) continue;
                if (value.Length > 0) values[name] = value;
                else if (row[2].Length > 0) sealedNames.Add(name);
            }
            List<string> missing = new[] { "passToken", "userId" }.Where(name => !values.ContainsKey(name)).ToList();
            if (missing.Any(sealedNames.Contains)) return ReadResult.Of(ReadReason.Encrypted);
            if (values.Count == 0) return ReadResult.Of(ReadReason.NotSignedIn);
            if (missing.Count > 0) return ReadResult.Of(ReadReason.Incomplete);
            return ReadResult.Of(ReadReason.Ok, new AccountCookie { PassToken = values["passToken"], UserId = values["userId"], CUserId = values.GetValueOrDefault("cUserId", "") });
        }
        // 种子 Cookie 串：换票入口前种在 account.xiaomi.com 的名字对（cUserId 实测可有可无，带上无害）。
        public static string SeedHeader(AccountCookie cookie) {
            string pairs = "passToken=" + cookie.PassToken + "; userId=" + cookie.UserId;
            return cookie.CUserId.Length > 0 ? pairs + "; cUserId=" + cookie.CUserId : pairs;
        }
    }

    // ── 换票 redirect-walk（session.js 的 C# 移植）─────────────────────
    internal static class MimoExchange {
        public const string ConsoleHost = "platform.xiaomimimo.com";
        public const string ConsoleOrigin = "https://" + ConsoleHost;
        // console 通道入口：/balance 是 /userProfile 等所有读取共用的探活端点。
        public static Uri ConsoleEntry() { return new Uri(ConsoleOrigin + "/api/v1/balance"); }
        // 登录链可达的域；console 域之外的每一跳都必须落在这里面。
        public static readonly string[] LoginDomains = { "xiaomi.com", "mi.com", "miui.com" };
        // Jar 的吸收范围：登录沿途各域的 Cookie 只进这几个根，官方域之外的 Set-Cookie 一律丢弃。
        public static readonly string[] CookieDomains = { "xiaomimimo.com", "xiaomi.com", "mi.com", "miui.com" };
        // 上游的拒绝码：46109 以 HTTP 200 出现，必须解 body。
        public static readonly int[] RejectedCodes = { 403, 46109 };
        public const int MaxHops = 8;

        public static bool HostMatches(string host, string domain) {
            host = (host ?? "").ToLowerInvariant(); domain = (domain ?? "").ToLowerInvariant();
            return host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
        }
        private static Uri Parse(string value, Uri baseUri) {
            Uri uri;
            if (!Uri.TryCreate(baseUri, (value ?? "").Trim(), out uri)) return null;
            return uri;
        }
        // 登录 URL 白名单：仅 https、无 userinfo、无显式端口、host 落在登录域内。
        public static Uri AllowedLoginUrl(string value, Uri baseUri) {
            Uri uri = Parse(value, baseUri);
            if (uri == null || !String.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || uri.Scheme != Uri.UriSchemeHttps) return null;
            string host = uri.Host.ToLowerInvariant();
            return LoginDomains.Any(domain => HostMatches(host, domain)) ? uri : null;
        }
        // 链上 URL 白名单：console 域本身（http 升 https，绝不降级）或登录域（仅 https）。
        public static Uri AllowedExchangeUrl(string value, Uri baseUri, string serviceHost) {
            Uri uri = Parse(value, baseUri);
            if (uri == null || !String.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort) return null;
            string host = uri.Host.ToLowerInvariant();
            serviceHost = (serviceHost ?? "").ToLowerInvariant();
            if (host == serviceHost) {
                string upgraded = Uri.UriSchemeHttps + uri.AbsoluteUri.Substring(uri.Scheme.Length);
                return Uri.TryCreate(upgraded, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
            }
            return uri.Scheme == Uri.UriSchemeHttps && LoginDomains.Any(domain => HostMatches(host, domain)) ? uri : null;
        }

        // 一次性 Cookie Jar：条目 = name + domain + path 唯一；max-age<=0 即删；hostOnly /
        // secure / 路径前缀都按 RFC 6265 匹配。与 session.js 的差异：Domain 属性去掉前导
        // 点再参与匹配（参考实现的 ".x" 后缀比较永远失配，属上游潜伏缺陷，此处按标准行为实现）。
        public sealed class Jar {
            private sealed class Entry { public string Name, Value, Domain, Path; public bool HostOnly, Secure; public double ExpiresAt = double.NaN; }
            private readonly List<Entry> cookies = new List<Entry>();
            private readonly Func<DateTime> now;
            public Jar(Func<DateTime> now = null) { this.now = now ?? (Func<DateTime>)(() => DateTime.UtcNow); }
            private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            private double NowMs() { return now().ToUniversalTime().Subtract(Epoch).TotalSeconds * 1000; }
            private static string DefaultPath(Uri url) {
                string path = url.AbsolutePath;
                int cut = path.LastIndexOf('/');
                return cut <= 0 ? "/" : path.Substring(0, cut);
            }
            private void Store(string name, string value, string domain, string path, bool hostOnly, bool secure, double expiresAt, Uri url) {
                string host = url.Host.ToLowerInvariant();
                if (name.Length == 0) return;
                if (value.Length > 4096) return;
                domain = (domain ?? "").ToLowerInvariant();
                if (domain.Length == 0) domain = host;
                if (!HostMatches(host, domain)) return;
                if (!CookieDomains.Any(root => HostMatches(domain, root))) return;
                if (String.IsNullOrEmpty(path) || !path.StartsWith("/", StringComparison.Ordinal)) path = DefaultPath(url);
                Entry stored = new Entry { Name = name, Value = value, Domain = domain, Path = path, HostOnly = hostOnly, Secure = secure, ExpiresAt = expiresAt };
                int index = cookies.FindIndex(existing => existing.Name == stored.Name && existing.Domain == stored.Domain && existing.Path == stored.Path);
                if (!Double.IsNaN(expiresAt) && expiresAt <= NowMs()) {
                    if (index >= 0) cookies.RemoveAt(index);
                    return;
                }
                if (index >= 0) cookies[index] = stored; else cookies.Add(stored);
                if (cookies.Count > 256) cookies.RemoveAt(0);   // 安全阀：异常站点刷屏时不无限膨胀
            }
            // 种子：裸 "name=value; …" 凭据对，视作 host-only + secure + path=/（参考实现同语义：
            // 来自 HTTPS 的凭据不再发给 http）。
            public void Seed(string cookieHeader, Uri url) {
                foreach (string pair in (cookieHeader ?? "").Split(';')) {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    Store(pair.Substring(0, eq).Trim(), pair.Substring(eq + 1).Trim(), "", "/", true, true, double.NaN, url);
                }
            }
            // 吸收一跳的 Set-Cookie（每行一条）。只认 Path / Max-Age / Domain 属性，Expires 不解析
            //（与参考实现一致：无寿命属性的按会话 Cookie 处理）。
            public void Absorb(IEnumerable<string> lines, Uri url) {
                foreach (string line in lines ?? Enumerable.Empty<string>()) {
                    string text = line ?? "";
                    int eq = text.IndexOf('=');
                    int semi = text.IndexOf(';');
                    if (eq <= 0 || (semi >= 0 && eq > semi)) continue;
                    string name = text.Substring(0, eq).Trim();
                    string value = semi < 0 ? text.Substring(eq + 1).Trim() : text.Substring(eq + 1, semi - eq - 1).Trim();
                    string domain = "", path = "";
                    double maxAge = double.NaN;
                    bool secure = false;
                    foreach (string rawAttribute in text.Split(';').Skip(1)) {
                        int at = rawAttribute.IndexOf('=');
                        string key = (at <= 0 ? rawAttribute : rawAttribute.Substring(0, at)).Trim().ToLowerInvariant();
                        string val = at <= 0 ? "" : rawAttribute.Substring(at + 1).Trim();
                        if (key == "domain") domain = val.StartsWith(".", StringComparison.Ordinal) ? val.Substring(1) : val;
                        else if (key == "path") path = val;
                        else if (key == "max-age") { long parsed; if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) maxAge = parsed * 1000.0; }
                        else if (key == "secure") secure = true;
                    }
                    double expiresAt = double.NaN;
                    if (!Double.IsNaN(maxAge)) expiresAt = maxAge <= 0 ? 0 : NowMs() + maxAge;
                    Store(name, value, domain, path, domain.Length == 0, secure, expiresAt, url);
                }
            }
            // 出口 Cookie 串：路径长者优先（RFC 6265 5.4），只发匹配当前 URL 的条目。
            public string HeaderFor(Uri url) {
                string host = url.Host.ToLowerInvariant(), scheme = url.Scheme, path = url.AbsolutePath;
                double nowMs = NowMs();
                IEnumerable<string> sent = cookies
                    .Where(c => Double.IsNaN(c.ExpiresAt) || c.ExpiresAt > nowMs)
                    .Where(c => c.HostOnly ? c.Domain == host : HostMatches(host, c.Domain))
                    .Where(c => !c.Secure || scheme == Uri.UriSchemeHttps)
                    .Where(c => path == c.Path || (path.StartsWith(c.Path, StringComparison.Ordinal) && (c.Path.EndsWith("/", StringComparison.Ordinal) || (path.Length > c.Path.Length && path[c.Path.Length] == '/'))))
                    .OrderByDescending(c => c.Path.Length)
                    .Select(c => c.Name + "=" + c.Value);
                return String.Join("; ", sent);
            }
        }

        // 传输缝：一跳请求（生产实现 = HttpClient + 手动跟随；测试 = 合成链）。
        public sealed class Hop { public Uri Url; public string Cookie = ""; public IDictionary<string, string> Headers; }
        public sealed class Reply {
            public int Status; public string Body = "", Location = "";
            public IList<string> SetCookie = new List<string>(); public Uri Url;
        }
        public delegate Task<Reply> Transport(Hop request);
        public enum Outcome { Ok, Rejected, RateLimited, Unavailable }
        public sealed class Result { public Outcome Outcome; public string CookieHeader = "", Detail = ""; }

        // 每跳的请求头（browserHeaders.js 同形态）：浏览器 UA + Accept；Origin/Referer 只对
        // console 域携带，绝不发给 SSO / 登录域。
        public static Dictionary<string, string> ExchangeHeaders(string cookieHeader, Uri url, string userAgent) {
            var headers = new Dictionary<string, string> {
                { "Accept", "application/json, text/plain, */*" },
                { "Accept-Language", "en-US,en;q=0.9" },
                { "Cookie", cookieHeader ?? "" },
                { "User-Agent", userAgent }
            };
            if (String.Equals(url.Host, ConsoleHost, StringComparison.OrdinalIgnoreCase)) {
                headers["Origin"] = ConsoleOrigin;
                headers["Referer"] = ConsoleOrigin + "/#/console/balance";
            }
            return headers;
        }
        // body code 提取（HTTP 200 也可能带拒绝码）+ 状态归类。
        public static int RejectionCode(int status, string body) {
            object root = null;
            try { root = J.Parse(body ?? ""); } catch { }
            double? code = root == null ? (double?)null : J.Num(root, "code");
            if (code.HasValue && Array.IndexOf(RejectedCodes, (int)code.Value) >= 0) return (int)code.Value;
            return Array.IndexOf(RejectedCodes, status) >= 0 ? status : 0;
        }
        public static bool ConsoleOk(int status, string body) {
            if (status != 200) return false;
            object root = null;
            try { root = J.Parse(body ?? ""); } catch { }
            return root != null && J.Num(root, "code") == 0;
        }
        // console 用 401 + body.loginUrl 指路，而不是重定向。
        public static Uri LoginUrl(int status, string body, Uri baseUri) {
            if (status != 401) return null;
            object root = null;
            try { root = J.Parse(body ?? ""); } catch { }
            return root == null ? null : AllowedLoginUrl(J.Str(root, "loginUrl"), baseUri);
        }

        private sealed class WalkAbort : Exception {
            public Outcome Outcome;
            public WalkAbort(Outcome outcome) : base("mimo walk aborted") { Outcome = outcome; }
        }
        // 一次完整换票：accountCookieHeader 种在 account.xiaomi.com，从入口打到出结果。
        // 任何一跳的传输异常都归 Unavailable（与参考实现的 catch 语义一致）。
        public static async Task<Result> Mint(string accountCookieHeader, Uri entry, Transport transport, Func<DateTime> now = null, CancellationToken deadline = default(CancellationToken)) {
            var jar = new Jar(now);
            jar.Seed(accountCookieHeader, new Uri("https://account.xiaomi.com/"));
            string serviceHost = entry.Host.ToLowerInvariant();
            try {
                Reply walked = await Request(entry, serviceHost, jar, transport, deadline).ConfigureAwait(false);
                if (walked.Status == 401) {
                    Uri login = LoginUrl(walked.Status, walked.Body, entry);
                    if (login == null) return new Result { Outcome = Outcome.Rejected };
                    walked = await Request(login, serviceHost, jar, transport, deadline).ConfigureAwait(false);
                    // 链会回答它停下的那个端点；只有没停回 console 时才需要再打一次入口。
                    if (!ConsoleOk(walked.Status, walked.Body)) walked = await Request(entry, serviceHost, jar, transport, deadline).ConfigureAwait(false);
                }
                if (walked.Status == 429) return new Result { Outcome = Outcome.RateLimited };
                if (RejectionCode(walked.Status, walked.Body) != 0 || walked.Status == 401) return new Result { Outcome = Outcome.Rejected };
                // 链停在登录域 = 入口不再接受这份账户 Cookie（passToken 失效的形态）。
                if (!String.Equals(walked.Url.Host, entry.Host, StringComparison.OrdinalIgnoreCase)) return new Result { Outcome = Outcome.Rejected };
                if (!ConsoleOk(walked.Status, walked.Body)) return new Result { Outcome = Outcome.Unavailable };
                return new Result { Outcome = Outcome.Ok, CookieHeader = jar.HeaderFor(entry) };
            } catch (WalkAbort abort) {
                // 跳数打满 / 重定向越界 / 缺 Location：换票没走成，归 unavailable。
                return new Result { Outcome = Outcome.Unavailable, Detail = abort.Message };
            } catch (OperationCanceledException) {
                return new Result { Outcome = Outcome.Unavailable, Detail = "deadline" };
            } catch (Exception e) {
                return new Result { Outcome = Outcome.Unavailable, Detail = e.Message };
            }
        }
        // 单次请求的手动重定向循环（≤8 跳）：逐跳吸收 Set-Cookie，Location 按白名单放行。
        // serviceHost 是整个换票的入口域（session.js 的 serviceUrl 语义），不是本次请求的起点——
        // 从登录域跳回 console 的那一跳要靠它放行。
        private static async Task<Reply> Request(Uri start, string serviceHost, Jar jar, Transport transport, CancellationToken deadline) {
            Uri current = start;
            for (int hop = 0; ; hop++) {
                if (hop >= MaxHops) throw new WalkAbort(Outcome.Unavailable);
                if (deadline.CanBeCanceled && deadline.IsCancellationRequested) throw new OperationCanceledException(deadline);
                string cookie = jar.HeaderFor(current);
                var hopRequest = new Hop { Url = current, Cookie = cookie, Headers = ExchangeHeaders(cookie, current, ProviderService.DesktopUserAgent) };
                Reply reply = await transport(hopRequest).ConfigureAwait(false);
                jar.Absorb(reply.SetCookie, current);
                int status = reply.Status;
                if (status >= 300 && status < 400) {
                    if (String.IsNullOrWhiteSpace(reply.Location)) throw new WalkAbort(Outcome.Unavailable);
                    Uri next = AllowedExchangeUrl(reply.Location, current, serviceHost);
                    if (next == null) throw new WalkAbort(Outcome.Unavailable);
                    current = next;
                    continue;
                }
                reply.Url = current;
                return reply;
            }
        }
    }
}
