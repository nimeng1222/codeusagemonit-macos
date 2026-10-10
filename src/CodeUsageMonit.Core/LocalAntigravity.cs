using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CodeUsageMonit {
    // Local Antigravity language-server reading. Windows found the process with WMI
    // (Win32_Process + GetOwnerSid) and its listening ports with iphlpapi; on macOS both
    // go through IProcessDiscovery (ps / lsof, see Platform/ProcessDiscovery.cs). The
    // request path, CSRF header and the strict loopback-only certificate exception are
    // kept with their original semantics.
    public static class LocalAntigravity {
        public static string FindApplication() {
            string[] paths = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "Antigravity.app"),
                Path.Combine("/Applications", "Antigravity.app")
            };
            return paths.FirstOrDefault(Directory.Exists) ?? "";
        }
        public static Task<ProviderState> TryRead() {
            return Task.Run(() => {
                var clock = Stopwatch.StartNew();
                try {
                    var servers = ProcessDiscovery.Current.FindAntigravityServers();
                    foreach (var server in servers.Take(2)) foreach (int port in ProcessDiscovery.Current.ListenerPorts(server.Key).Take(3)) {
                        foreach (string scheme in new[] { "https", "http" }) foreach (string method in new[] { "RetrieveUserQuotaSummary", "GetUserStatus", "GetCommandModelConfigs" }) {
                            if (clock.ElapsedMilliseconds > 7000) return null;
                            try {
                                object response = Query(port, scheme, method, server.Value);
                                ProviderState parsed = Parse(response);
                                if (parsed.Quotas.Count > 0) return parsed;
                            } catch { }
                        }
                    }
                } catch { }
                return null;
            });
        }
        // The only TLS exception is this authenticated, PID-owned loopback service's
        // self-signed certificate. External HTTPS requests retain normal validation.
        private static object Query(int port, string scheme, string method, string csrf) {
            var uri = new Uri(scheme + "://127.0.0.1:" + port + "/exa.language_server_pb.LanguageServerService/" + method);
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }) {
                if (scheme == "https") handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) => {
                    var local = request != null ? request.RequestUri : null;
                    return local != null && local.Host == "127.0.0.1" && local.Port == port;
                };
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(1500) })
                using (var request = new HttpRequestMessage(HttpMethod.Post, uri)) {
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    request.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", csrf);
                    request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
                    request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using (HttpResponseMessage response = client.SendAsync(request).GetAwaiter().GetResult()) {
                        if (!response.IsSuccessStatusCode) return null;
                        string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if (json.Length > 1024 * 1024) return null;
                        return J.Parse(json);
                    }
                }
            }
        }
        public static ProviderState Parse(object root) {
            object summary = J.Get(root, "quotaSummary") ?? J.Get(root, "userQuotaSummary") ?? root;
            ProviderState state = Parsers.Antigravity(summary);
            object user = J.Get(root, "userStatus");
            state.Account = J.Str(user, "email");
            if (state.Account.Length == 0) state.Account = J.Str(user, "userInfo", "email");
            string plan = J.Str(user, "planStatus", "planInfo", "planName");
            if (plan.Length == 0) plan = J.Str(user, "planStatus", "planInfo", "displayName");
            if (plan.Length > 0) state.Plan = plan;
            if (state.Quotas.Count == 0) {
                object models = J.Get(user, "cascadeModelConfigData", "clientModelConfigs") ?? J.Get(root, "clientModelConfigs");
                foreach (object model in J.Arr(models)) {
                    double? remaining = J.Num(model, "quotaInfo", "remainingFraction");
                    string name = J.Str(model, "label"); if (name.Length == 0) name = J.Str(model, "modelId");
                    if (remaining.HasValue) Parsers.Add(state, name.Length > 0 ? name : "模型额度", (1 - remaining.Value) * 100, J.Get(model, "quotaInfo", "resetTime"));
                }
            }
            return state;
        }
    }
}
