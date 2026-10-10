using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace CodeUsageMonit {
    // 各平台官方状态页（Phase 4.3）。只收录核实过返回 Statuspage v2 JSON 的平台；
    // 查不到公开接口形态的一律不进白名单（宁缺毋滥）：status.deepseek.com 域名存在，
    // 但其 /api/v2/status.json 未能核实，暂缺；status.x.ai 返回 403，跳过；
    // status.anthropic.com 301 → status.claude.com，两者都在白名单内，允许一跳
    // 白名单内重定向。安全约束：只允许 https；host 硬编码白名单；即使白名单被写错，
    // 环回 / 私网 / 保留地址也在请求前被拒；超时 10 秒；失败静默——不写 SourceHealth、
    // 不碰任何额度通道，只影响这一小块展示。缓存 10 分钟，不随 30 秒刷新走。
    public static class ServiceStatus {
        public sealed class Source { public string Id, Name, Host, Page; }
        public sealed class Reading { public string Id, Name, Page, Indicator = "", Description = "", Checked = ""; public bool Ok; }
        public sealed class Cache { public string Updated = ""; public List<Reading> Readings = new List<Reading>(); }

        // 已核实（2026-10 逐一请求生效）返回 Statuspage v2 status.json 的官方状态页。
        // status.anthropic.com 不单列：已核实它 301 → status.claude.com（同一页），单列
        // 只会多出一行重复读数；下面的一跳白名单内重定向逻辑留作别名兜底。
        public static readonly Source[] Sources = {
            new Source { Id = "claude", Name = "Claude", Host = "status.claude.com", Page = "https://status.claude.com" },
            new Source { Id = "codex", Name = "OpenAI", Host = "status.openai.com", Page = "https://status.openai.com" },
            new Source { Id = "cursor", Name = "Cursor", Host = "status.cursor.com", Page = "https://status.cursor.com" },
            new Source { Id = "kimi", Name = "Kimi", Host = "status.moonshot.cn", Page = "https://status.moonshot.cn" },
            new Source { Id = "copilot", Name = "Copilot", Host = "www.githubstatus.com", Page = "https://www.githubstatus.com" },
        };
        public const string ApiPath = "/api/v2/status.json";
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10), CacheFor = TimeSpan.FromMinutes(10);

        // ── 白名单与地址校验（纯函数，自测覆盖）────────────────────────
        // https + host 精确等于白名单条目；否则 null。子域、端口、userinfo 都不算命中。
        public static Source SourceOf(string url) {
            Uri uri;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0) return null;
            string host = uri.Host.TrimStart('.').ToLowerInvariant();
            if (uri.PathAndQuery.IndexOf('\n') >= 0) return null;
            return Sources.FirstOrDefault(s => String.Equals(s.Host, host, StringComparison.Ordinal));
        }
        // 白名单之外的兜底：环回 / 私网 / 链路本地 / CGNAT / 基准测试段 / 组播与保留段。
        public static bool PrivateOrReserved(Uri uri) {
            string host = (uri.Host ?? "").Trim('[', ']');
            if (uri.IsLoopback || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)) return true;
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip)) return false;
            byte[] b = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC || (b[0] == 0 && b[1] == 0);
            return b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19));
        }
        // 请求前最后一道校验：https + 白名单 + 非私网/保留。通过返回 null。
        internal static string Check(string url) {
            if (SourceOf(url) == null) return "url is not on the https whitelist";
            if (PrivateOrReserved(new Uri(url))) return "url points at a private or reserved address";
            return null;
        }

        // ── 抓取与缓存 ────────────────────────────────────────────────
        // 后台线程调用。单个平台失败只少一条读数（静默），全部失败沿用旧缓存。
        public static async Task<List<Reading>> FetchAll(AppConfig config) {
            string proxy;
            try { proxy = ProviderService.ResolveProxy(config == null ? "auto" : config.Proxy); } catch { proxy = ""; }
            var readings = new List<Reading>();
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) {
                if (proxy.Length > 0) { handler.UseProxy = true; handler.Proxy = new WebProxy(proxy); }
                using (var client = new HttpClient(handler) { Timeout = Timeout }) {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
                    foreach (Source source in Sources) {
                        Reading reading = await FetchOne(client, source).ConfigureAwait(false);
                        if (reading != null) readings.Add(reading);
                    }
                }
            }
            return readings;
        }
        private static async Task<Reading> FetchOne(HttpClient client, Source source) {
            try {
                string url = "https://" + source.Host + ApiPath;
                for (int hop = 0; hop < 2; hop++) {
                    if (Check(url) != null) return null;
                    using (var deadline = new CancellationTokenSource(Timeout))
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url)) {
                        request.Headers.TryAddWithoutValidation("Accept", "application/json");
                        using (HttpResponseMessage response = await client.SendAsync(request, deadline.Token).ConfigureAwait(false)) {
                            int status = (int)response.StatusCode;
                            if (status >= 300 && status < 400) {           // 只跟白名单内的一跳重定向
                                string location = response.Headers.Location != null ? response.Headers.Location.ToString() : "";
                                if (hop == 0 && SourceOf(location) != null && Check(location) == null) { url = location; continue; }
                                return null;
                            }
                            if (!response.IsSuccessStatusCode) return null;
                            object root = J.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                            string indicator = J.Str(J.Get(root, "status"), "indicator"), description = J.Str(J.Get(root, "status"), "description");
                            if (indicator.Length == 0) return null;        // 不是 Statuspage v2 形态：宁可没有
                            return new Reading { Id = source.Id, Name = source.Name, Page = source.Page, Indicator = indicator, Description = description, Checked = DateTime.UtcNow.ToString("o"), Ok = indicator == "none" };
                        }
                    }
                }
            } catch { }
            return null;
        }
        private const string CacheFile = "service-status.json";
        public static Cache Load() { return Store.Read<Cache>(CacheFile); }
        public static Cache Refresh(AppConfig config) {
            List<Reading> fresh = FetchAll(config).GetAwaiter().GetResult();
            Cache merged = Merge(Load(), fresh, DateTime.UtcNow);
            // 有新读数才落盘：全部失败时旧缓存原样保留（时间戳未动，Stale 会再触发重试）。
            if (fresh.Count > 0) { try { Store.Write(CacheFile, merged); } catch { } }
            return merged;
        }
        // 按源合并（Phase 4 初审 P3）：新读数覆盖同源旧条目，这一轮没取到的源保留旧条目，
        // 部分成功不再把整份缓存缩水成本轮的几条。白名单已移除的源、超过 7 天没有成功
        // 读数的条目不再保留；全部成功时与整体覆盖等价。
        internal static Cache Merge(Cache cached, List<Reading> fresh, DateTime nowUtc) {
            var byId = new Dictionary<string, Reading>(StringComparer.Ordinal);
            if (cached != null) foreach (Reading old in cached.Readings) if (!byId.ContainsKey(old.Id)) byId[old.Id] = old;
            foreach (Reading reading in fresh) byId[reading.Id] = reading;
            List<string> order = Sources.Select(s => s.Id).ToList();
            DateTime oldest = nowUtc.AddDays(-7);
            var merged = new Cache { Updated = fresh.Count > 0 || cached == null ? nowUtc.ToString("o") : cached.Updated };
            foreach (Reading reading in byId.Values) {
                if (!order.Contains(reading.Id)) continue;   // 已移出白名单的源
                DateTime at;
                if (LogIndex.Parse(reading.Checked, out at) && at < oldest) continue;   // 太久没成功过
                merged.Readings.Add(reading);
            }
            merged.Readings = merged.Readings.OrderBy(r => order.IndexOf(r.Id)).ToList();
            return merged;
        }
        public static bool Stale(Cache cache, DateTime nowUtc) {
            DateTime at;
            return cache == null || cache.Readings == null || cache.Readings.Count == 0 || !LogIndex.Parse(cache.Updated, out at) || nowUtc - at >= CacheFor;
        }
        // indicator → 展示等级：ok（绿）/ warn（黄：维护或部分故障）/ down（红：重大故障）。
        public static string Level(string indicator) {
            switch (indicator ?? "") { case "none": return "ok"; case "maintenance": case "minor": return "warn"; default: return "down"; }
        }
    }
}
