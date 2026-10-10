using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace CodeUsageMonit {
    // MiMo / StepFun 云端额度通道的 fixture 回归：规格样例 JSON → Parsers 解析结果、
    // 凭据归一化、webid 派生与出口 URL 白名单。纯解析与纯函数，不发起网络请求，
    // 清单里不出现真实凭据（所有 token / cookie 均为自测字面量）。
    internal static class CloudProviderTests {
        private static void Check(bool condition) { if (!condition) throw new Exception("Cloud provider assertion failed"); }
        private static ProviderException Fault(Action action) {
            try { action(); } catch (ProviderException e) { return e; }
            return null;
        }
        private static string B64Url(string json) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }

        internal static void Run(Action<string, Action> test) {
            // ── MiMo ──────────────────────────────────────────────────────
            test("MiMo wallet prefers balance over cash+gift and reads snake_case without summing", () => {
                var s = new ProviderState();
                Parsers.MimoWallet(s, J.Parse("{\"code\":0,\"data\":{\"balance\":12.5,\"currency\":\"USDT\",\"cashBalance\":20,\"giftBalance\":30}}"));
                Check(s.Balances.Count == 1 && s.Balances[0].Amount == 12.5 && s.Balances[0].Currency == "USDT");
                var snake = new ProviderState();
                Parsers.MimoWallet(snake, J.Parse("{\"code\":0,\"data\":{\"cash_balance\":3,\"gift_balance\":4}}"));
                Check(snake.Balances.Count == 1 && snake.Balances[0].Amount == 7 && snake.Balances[0].Currency == "USD");
            });
            test("MiMo cookie normalization keeps only channel cookies, sorted, and reports missing required names", () => {
                string kept = Parsers.MimoCookie("_ga=GA1.2.x; userId=8601; api-platform_serviceToken=st-1; api-platform_slh=slh-9; other=1");
                Check(kept == "api-platform_serviceToken=st-1; api-platform_slh=slh-9; userId=8601");
                ProviderException missing = Fault(() => Parsers.MimoCookie("api-platform_serviceToken=tok; api-platform_ph=p"));
                Check(missing != null && missing.State == "setup" && missing.Message.Contains("userId"));
                ProviderException empty = Fault(() => Parsers.MimoCookie(""));
                Check(empty != null && empty.State == "setup" && empty.Message.Contains("api-platform_serviceToken"));
            });
            test("MiMo envelope maps code 401/403 to unauthorized and other codes to service errors", () => {
                Check(Fault(() => Parsers.MimoEnvelope(J.Parse("{\"code\":401}"))) is { State: "expired" });
                Check(Fault(() => Parsers.MimoEnvelope(J.Parse("{\"code\":403}"))) is { State: "expired" });
                Check(Fault(() => Parsers.MimoEnvelope(J.Parse("{\"code\":5,\"message\":\"x\"}"))) is { State: "error" });
                Check(Fault(() => Parsers.MimoEnvelope(J.Parse("{\"data\":{}}"))) is { State: "error" });
            });
            test("MiMo month_total_token recomputes from used/limit, clamps over-quota, and falls back to percent", () => {
                var s = new ProviderState();
                Parsers.MimoUsage(s, J.Parse("{\"code\":0,\"data\":{\"monthUsage\":{\"items\":[{\"name\":\"day_1\",\"used\":1,\"limit\":2},{\"name\":\"month_total_token\",\"used\":30,\"limit\":100,\"percent\":0.9}]}}}"), "2026-11-01T00:00:00Z");
                Check(s.Quotas.Count == 1 && s.Quotas[0].Label == "每月 Token" && Math.Abs(s.Quotas[0].Used - 30) < 1e-9 && s.Quotas[0].Remaining == 70);
                Check(s.Quotas[0].WindowSeconds == 2592000 && s.Quotas[0].ResetUtc.StartsWith("2026-11-01"));
                var over = new ProviderState();
                Parsers.MimoUsage(over, J.Parse("{\"code\":0,\"data\":{\"month_usage\":{\"items\":[{\"name\":\"MONTH_TOTAL_TOKEN\",\"used\":150,\"limit\":100,\"percent\":1.5}]}}}"), "");
                Check(over.Quotas.Count == 1 && over.Quotas[0].Used == 100 && over.Quotas[0].Remaining == 0);
                var ratio = new ProviderState();
                Parsers.MimoUsage(ratio, J.Parse("{\"code\":0,\"data\":{\"monthUsage\":{\"items\":[{\"name\":\"month_total_token\",\"percent\":0.4}]}}}"), "");
                Check(ratio.Quotas.Count == 1 && Math.Abs(ratio.Quotas[0].Used - 40) < 1e-9);
            });
            test("MiMo plan name and period end flow into Plan and the quota reset", () => {
                var s = new ProviderState();
                string end = Parsers.MimoPlan(s, J.Parse("{\"code\":0,\"data\":{\"planCode\":\"mimo_pro\",\"currentPeriodEnd\":1900000000}}"));
                Check(s.Plan == "mimo_pro" && end.StartsWith("2030-03-17T17:46"));
                Check(Parsers.MimoPlan(new ProviderState(), null) == "");
                var account = new ProviderState();
                Check(Parsers.MimoAccount(J.Parse("{\"code\":0,\"data\":{\"nickName\":\"nm\",\"email\":\"a@b.c\"}}")) == "a@b.c");
                Check(Parsers.MimoAccount(J.Parse("{\"code\":0,\"data\":{\"nickName\":\"nm\"}}")) == "nm");
            });

            // ── StepFun ───────────────────────────────────────────────────
            test("StepFun coding plan converts left rates to used percent (spec fixture, camel/snake, string reset)", () => {
                var s = Parsers.StepFun(J.Parse("{\"status\":1,\"five_hour_usage_left_rate\":0.8,\"five_hour_usage_reset_time\":\"1900000000\",\"weekly_usage_left_rate\":0.5,\"weekly_usage_reset_time\":1900000000}"));
                Check(s.Quotas.Count == 2 && s.Quotas[0].Label == "5 小时" && Math.Abs(s.Quotas[0].Used - 20) < 1e-9 && s.Quotas[0].WindowSeconds == 18000);
                Check(s.Quotas[1].Label == "每周" && Math.Abs(s.Quotas[1].Used - 50) < 1e-9 && s.Quotas[1].WindowSeconds == 604800);
                Check(s.Quotas[0].ResetUtc.StartsWith("2030-03-17T17:46") && s.Quotas[1].ResetUtc.StartsWith("2030-03-17T17:46"));
                var camel = Parsers.StepFun(J.Parse("{\"status\":1,\"data\":{\"fiveHourUsageLeftRate\":0.8}}"));
                Check(camel.Quotas.Count == 1 && Math.Abs(camel.Quotas[0].Used - 20) < 1e-9);
                var noReset = Parsers.StepFun(J.Parse("{\"status\":1,\"five_hour_usage_left_rate\":0.8,\"five_hour_usage_reset_time\":0}"));
                Check(noReset.Quotas.Count == 1 && noReset.Quotas[0].ResetUtc == "");
            });
            test("StepFun credit buckets weight remaining by total and never sum the fallback rates", () => {
                var s = Parsers.StepFun(J.Parse("{\"status\":1,\"plan_family\":2,\"plan_credit_rate_limit\":{\"credit_buckets\":[{\"credit_total\":100,\"credit_residual\":25},{\"credit_total\":300,\"credit_residual\":150}]},\"subscription_credit_reset_time\":1900000000}"));
                Check(s.Quotas.Count == 1 && s.Quotas[0].Label == "每月 Token" && Math.Abs(s.Quotas[0].Used - 56.25) < 1e-9 && Math.Abs(s.Quotas[0].Remaining - 43.75) < 1e-9 && s.Quotas[0].WindowSeconds == 2592000);
                var fallback = Parsers.StepFun(J.Parse("{\"status\":1,\"subscription_credit_left_rate\":0.5,\"topup_credit_left_rate\":0.2}"));
                Check(fallback.Quotas.Count == 1 && Math.Abs(fallback.Quotas[0].Used - 50) < 1e-9);
                var topupOnly = Parsers.StepFun(J.Parse("{\"status\":1,\"topup_credit_left_rate\":0.2}"));
                Check(topupOnly.Quotas.Count == 1 && Math.Abs(topupOnly.Quotas[0].Used - 80) < 1e-9);
                var broken = Parsers.StepFun(J.Parse("{\"status\":1,\"five_hour_usage_left_rate\":0.8,\"five_hour_usage_reset_time\":1900000000,\"plan_credit_rate_limit\":{\"credit_buckets\":[{\"credit_total\":100,\"credit_residual\":250}]}}"));
                // 坏桶只丢弃 credit 窗口：Coding Plan 的 5h 窗口照常输出，credit 不输出也不报错。
                Check(broken.Quotas.Count == 1 && broken.Quotas[0].Label == "5 小时" && Math.Abs(broken.Quotas[0].Used - 20) < 1e-9 && broken.Quotas[0].ResetUtc.StartsWith("2030-03-17T17:46"));
                Check(!broken.Quotas.Any(q => q.Label == "每月 Token"));
            });
            test("StepFun token-plan-only response hides the inactive coding windows", () => {
                // 真实响应形态（国际站 Token Plan 用户，无 Coding Plan）：5h/weekly 的 left_rate=0
                // 且 reset=0——这是窗口未激活而非用尽，不应展示。
                var s = Parsers.StepFun(J.Parse("{\"status\":1,\"five_hour_usage_left_rate\":0,\"five_hour_usage_reset_time\":\"0\",\"weekly_usage_left_rate\":0,\"weekly_usage_reset_time\":\"0\",\"plan_family\":2,\"plan_credit_rate_limit\":{\"subscription_credit_left_rate\":0.8706769,\"subscription_credit_reset_time\":\"0\",\"topup_credit_left_rate\":0,\"credit_buckets\":[{\"type\":1,\"credit_total\":\"1600000000\",\"credit_residual\":\"1393083013\",\"expire_at\":\"1792733785\",\"next_reset_at\":\"0\"}]}}"));
                Check(s.Quotas.Count == 1 && s.Quotas[0].Label == "每月 Token");
                Check(Math.Abs(s.Quotas[0].Remaining - 87.0676) < 0.001);
                // 有 reset 的用尽窗口（left=0、reset 有效）是真用尽，正常展示。
                var exhausted = Parsers.StepFun(J.Parse("{\"status\":1,\"five_hour_usage_left_rate\":0,\"five_hour_usage_reset_time\":1900000000}"));
                Check(exhausted.Quotas.Count == 1 && exhausted.Quotas[0].Label == "5 小时" && exhausted.Quotas[0].Used == 100);
            });
            test("StepFun envelope classifies explicit auth keywords as unauthorized and the rest as service errors", () => {
                Check(Parsers.StepFunEnvelope(J.Parse("{\"status\":1,\"data\":{}}")) != null);
                Check(Parsers.StepFunEnvelope(J.Parse("{\"status\":1}")) != null);
                // 裸 "token" 已从关键词移除：限额等业务文案带 token 字样不是凭据失效。
                Check(Fault(() => Parsers.StepFunEnvelope(J.Parse("{\"status\":0,\"message\":\"token plan limit exceeded\"}"))) is { State: "error" });
                Check(Fault(() => Parsers.StepFunEnvelope(J.Parse("{\"status\":0,\"message\":\"invalid token\"}"))) is { State: "error" });
                // 明确的鉴权失效信号仍然判过期。
                Check(Fault(() => Parsers.StepFunEnvelope(J.Parse("{\"status\":0,\"message\":\"login expired\"}"))) is { State: "expired" });
                Check(Fault(() => Parsers.StepFunEnvelope(J.Parse("{\"status\":0,\"desc\":\"please login\"}"))) is { State: "expired" });
                Check(Fault(() => Parsers.StepFunEnvelope(J.Parse("{\"status\":0,\"message\":\"登录已失效\"}"))) is { State: "expired" });
                Check(Fault(() => Parsers.StepFunEnvelope(J.Parse("{\"status\":0,\"message\":\"未登录\"}"))) is { State: "expired" });
                Check(Fault(() => Parsers.StepFunEnvelope(J.Parse("{\"status\":0,\"message\":\"unauthorized\"}"))) is { State: "expired" });
                Check(Fault(() => Parsers.StepFunEnvelope(J.Parse("{\"status\":0,\"message\":\"server busy\"}"))) is { State: "error" });
            });
            test("StepFun token accepts plain values and Oasis-Token cookies, rejects control characters and stray semicolons", () => {
                Check(Parsers.StepFunToken("  st-value-1 ") == "st-value-1");
                Check(Parsers.StepFunToken("Oasis-Token=st-value-1") == "st-value-1");
                Check(Parsers.StepFunToken("a=b; Oasis-Token= st-value-1 ; c=d") == "st-value-1");
                Check(Parsers.StepFunToken("a;b") == "");
                Check(Parsers.StepFunToken("st\nvalue") == "");
                Check(Parsers.StepFunToken("") == "");
            });
            test("StepFun webid reads device_id from the last valid JWT segment and falls back to the constant", () => {
                string payload = B64Url("{\"device_id\":\"dev-abc_123\",\"exp\":1900000000}");
                Check(Parsers.StepFunWebId("front..." + "hdr." + payload + ".sig...tail") == "dev-abc_123");
                Check(Parsers.StepFunWebId("not-a-jwt") == Parsers.StepFunFallbackWebId);
                Check(Parsers.StepFunWebId("") == Parsers.StepFunFallbackWebId);
                string noClaim = B64Url("{\"exp\":1}");
                Check(Parsers.StepFunWebId("hdr." + noClaim + ".sig") == Parsers.StepFunFallbackWebId);
            });
            test("StepFun appid reads app_id from any JWT segment and falls back per region", () => {
                string withAppId = B64Url("{\"app_id\":20700,\"device_id\":\"dev-abc_123\"}");
                Check(Parsers.StepFunAppId("front..." + "hdr." + withAppId + ".sig", "global") == "20700");
                Check(Parsers.StepFunAppId("hdr." + withAppId + ".sig", "china") == "20700");   // token 自身优先于区域
                Check(Parsers.StepFunAppId("not-a-jwt", "global") == "20700");
                Check(Parsers.StepFunAppId("not-a-jwt", "china") == "10300");
                Check(Parsers.StepFunAppId("", "global") == "20700");
                string noClaim = B64Url("{\"exp\":1}");
                Check(Parsers.StepFunAppId("hdr." + noClaim + ".sig", "china") == "10300");
            });
            test("StepFun plan name comes from GetStepPlanStatus subscription.name", () => {
                Check(Parsers.StepFunPlan(J.Parse("{\"status\":1,\"data\":{\"subscription\":{\"name\":\"Starter Pro\"}}}")) == "Starter Pro");
                Check(Parsers.StepFunPlan(null) == "");
            });
            test("StepFun host follows the region and lands inside the official allowlist", () => {
                Check(Parsers.StepFunHost("china") == "https://platform.stepfun.com");
                Check(Parsers.StepFunHost("global") == "https://platform.stepfun.ai" && Parsers.StepFunHost("") == "https://platform.stepfun.ai" && Parsers.StepFunHost(null) == "https://platform.stepfun.ai");
                Uri china = ProviderService.CheckedUri(Parsers.StepFunHost("china") + "/api/step.openapi.devcenter.Dashboard/QueryStepPlanRateLimit", "platform.stepfun.ai", "platform.stepfun.com");
                Check(china.Host == "platform.stepfun.com" && china.AbsolutePath == "/api/step.openapi.devcenter.Dashboard/QueryStepPlanRateLimit");
            });
            test("Cloud status mapping keeps 401 expired but demotes 403 and redirects to service errors", () => {
                // 401 是 HTTP 层唯一可靠的凭据失效信号；403/3xx 多为 WAF 风控或 CDN 重定向。
                ProviderException unauthorized = ProviderService.CloudStatusException(401, "登录信息");
                Check(unauthorized.State == "expired" && unauthorized.Message == "登录信息");
                Check(ProviderService.CloudStatusException(403, "登录信息").State == "error");
                foreach (int status in new[] { 300, 301, 302, 307, 308, 399 })
                    Check(ProviderService.CloudStatusException(status, "登录信息").State == "error");
                // 其余状态不在此映射（429/5xx/2xx 交回调用方原分支处理）。
                Check(ProviderService.CloudStatusException(200, "登录信息") == null);
                Check(ProviderService.CloudStatusException(429, "登录信息") == null);
                Check(ProviderService.CloudStatusException(500, "登录信息") == null);
            });
            test("StepFun channel keeps the reading ready when only the plan request fails, even as expired", () => {
                // 主请求成功即证明凭据有效：套餐名失败（含 expired）不再把整份读数连坐成过期。
                ProviderState s = ProviderService.StepFunChannel("st-token", "global",
                    (url, token, webid, appid, timeout) => Task.FromResult<object>(url.Contains("QueryStepPlanRateLimit")
                        ? J.Parse("{\"status\":1,\"five_hour_usage_left_rate\":0.8,\"five_hour_usage_reset_time\":1900000000}")
                        : J.Parse("{\"status\":0,\"message\":\"login expired\"}")),
                    region => { throw new InvalidOperationException("套餐名失败不得触发区域写回"); }).GetAwaiter().GetResult();
                Check(s.Quotas.Count == 1 && s.Quotas[0].Label == "5 小时" && s.Plan == "StepFun");
            });
            test("StepFun channel keeps the reading ready when the plan request times out or the transport fails", () => {
                // 套餐名走 6 秒短超时：超时抛 TaskCanceledException、传输层抛 HttpRequestException，
                // 都不是凭据信号——主读数已成，任何异常只吞掉套餐名，不连坐、不写回区域。
                foreach (Exception failure in new Exception[] { new TaskCanceledException(), new HttpRequestException("connection reset") }) {
                    ProviderState s = ProviderService.StepFunChannel("st-token", "global",
                        (url, token, webid, appid, timeout) => url.Contains("QueryStepPlanRateLimit")
                            ? Task.FromResult<object>(J.Parse("{\"status\":1,\"five_hour_usage_left_rate\":0.8,\"five_hour_usage_reset_time\":1900000000}"))
                            : Task.FromException<object>(failure),
                        region => { throw new InvalidOperationException("套餐名失败不得触发区域写回"); }).GetAwaiter().GetResult();
                    Check(s.Quotas.Count == 1 && s.Quotas[0].Label == "5 小时" && Math.Abs(s.Quotas[0].Used - 20) < 0.01 && s.Plan == "StepFun");
                }
            });
            test("StepFun channel retries the other region on expired, recomputes appid and persists the region", () => {
                var calls = new List<string>(); var appids = new List<string>(); string persisted = null;
                ProviderState s = ProviderService.StepFunChannel("st-token", "global",
                    (url, token, webid, appid, timeout) => {
                        calls.Add(url); appids.Add(appid);
                        if (url.Contains("platform.stepfun.ai")) throw new ProviderException(Parsers.StepFunExpiredMessage, "expired");   // 国际站判过期
                        if (url.Contains("GetStepPlanStatus")) return Task.FromResult<object>(J.Parse("{\"status\":1,\"data\":{\"subscription\":{\"name\":\"Starter Pro\"}}}"));
                        return Task.FromResult<object>(J.Parse("{\"status\":1,\"five_hour_usage_left_rate\":0.8,\"five_hour_usage_reset_time\":1900000000}"));
                    },
                    region => persisted = region).GetAwaiter().GetResult();
                Check(calls.Any(u => u.Contains("platform.stepfun.ai")) && calls.All(u => u.Contains("platform.stepfun.ai") || u.Contains("platform.stepfun.com")));
                Check(calls.Where(u => u.Contains("stepfun.com")).Count() == 2);   // 回退站点跑完整流程：额度 + 套餐名
                Check(appids.SequenceEqual(new[] { "20700", "10300", "10300" }));   // 回退值随区域重算（垃圾 token 走区域回退）
                Check(persisted == "china");
                Check(s.Quotas.Count == 1 && s.Quotas[0].Label == "5 小时" && s.Plan == "Starter Pro");
            });
            test("StepFun channel does not persist when the first region succeeds", () => {
                string persisted = null;
                ProviderState s = ProviderService.StepFunChannel("st-token", "china",
                    (url, token, webid, appid, timeout) => Task.FromResult<object>(url.Contains("GetStepPlanStatus")
                        ? J.Parse("{\"status\":1,\"data\":{\"subscription\":{\"name\":\"Pro\"}}}")
                        : J.Parse("{\"status\":1,\"five_hour_usage_left_rate\":0.5,\"five_hour_usage_reset_time\":1900000000}")),
                    region => persisted = region).GetAwaiter().GetResult();
                Check(persisted == null && s.Quotas.Count == 1 && s.Plan == "Pro");
            });
            test("StepFun channel throws the last expired when both regions reject and never persists", () => {
                int calls = 0; string persisted = null; ProviderException thrown = null;
                try {
                    ProviderService.StepFunChannel("st-token", "china",
                        (url, token, webid, appid, timeout) => { calls++; throw new ProviderException(Parsers.StepFunExpiredMessage, "expired"); },
                        region => persisted = region).GetAwaiter().GetResult();
                } catch (ProviderException e) { thrown = e; }
                Check(thrown != null && thrown.State == "expired" && thrown.Message == Parsers.StepFunExpiredMessage);
                Check(calls == 2 && persisted == null);   // 每个区域只打主请求，主请求挂了不追套餐名
            });
            test("StepFun channel only retries across regions on expired, not on service errors", () => {
                int calls = 0; string persisted = null; ProviderException thrown = null;
                try {
                    ProviderService.StepFunChannel("st-token", "global",
                        (url, token, webid, appid, timeout) => { calls++; throw new ProviderException("服务暂时不可用", "error"); },
                        region => persisted = region).GetAwaiter().GetResult();
                } catch (ProviderException e) { thrown = e; }
                Check(thrown != null && thrown.State == "error" && calls == 1 && persisted == null);
            });
            test("CheckedUri rejects non-https, off-allowlist and look-alike hosts", () => {
                Check(Fault(() => ProviderService.CheckedUri("http://platform.stepfun.com/api", "platform.stepfun.com")) != null);
                Check(Fault(() => ProviderService.CheckedUri("https://evil.com/api", "platform.stepfun.ai")) != null);
                // user-info 伪装：host 是 evil.com，不是白名单内的官方域名。
                Check(Fault(() => ProviderService.CheckedUri("https://platform.stepfun.ai@evil.com/", "platform.stepfun.ai")) != null);
                Check(Fault(() => ProviderService.CheckedUri("https://platform.xiaomimimo.com.evil.com/", "platform.xiaomimimo.com")) != null);
                Uri mimo = ProviderService.CheckedUri("https://platform.xiaomimimo.com/api/v1/balance", "platform.xiaomimimo.com");
                Check(mimo.Host == "platform.xiaomimimo.com" && mimo.AbsolutePath == "/api/v1/balance");
            });
            test("IsPrivateAddress flags loopback, private and reserved ranges including IPv4-mapped IPv6", () => {
                foreach (string ip in new[] { "127.0.0.1", "10.1.2.3", "172.16.0.1", "172.31.255.255", "192.168.1.1", "0.0.0.0", "169.254.1.1" })
                    Check(ProviderService.IsPrivateAddress(IPAddress.Parse(ip)));
                foreach (string ip in new[] { "8.8.8.8", "172.32.0.1", "11.0.0.1" })
                    Check(!ProviderService.IsPrivateAddress(IPAddress.Parse(ip)));
                Check(ProviderService.IsPrivateAddress(IPAddress.IPv6Loopback) && ProviderService.IsPrivateAddress(IPAddress.Parse("fe80::1")) && ProviderService.IsPrivateAddress(IPAddress.Parse("fd00::1")));
                Check(ProviderService.IsPrivateAddress(IPAddress.Parse("::ffff:127.0.0.1")));
                Check(!ProviderService.IsPrivateAddress(IPAddress.Parse("2606:4700::1111")));
                Check(ProviderService.IsPrivateAddress(null));
            });
            test("MiMo and StepFun are registered as opt-in key providers with env fallbacks", () => {
                Check(ProviderCatalog.Ids.Contains("mimo") && ProviderCatalog.Ids.Contains("stepfun"));
                Check(!ProviderCatalog.DefaultEnabled.Contains("mimo") && !ProviderCatalog.DefaultEnabled.Contains("stepfun"));
                Check(!ProviderCatalog.LocalOnly("mimo") && !ProviderCatalog.LocalOnly("stepfun"));
                Check(ProviderCatalog.Name("mimo") == "MiMo" && ProviderCatalog.Name("stepfun") == "StepFun");
                Check(ProviderCatalog.Color("mimo").Length == 7 && ProviderCatalog.Color("stepfun").Length == 7);
                Check(ProviderCatalog.KeyEnv("mimo", new AppConfig()).SequenceEqual(new[] { "MIMO_COOKIE" }));
                Check(ProviderCatalog.KeyEnv("stepfun", new AppConfig()).SequenceEqual(new[] { "STEPFUN_TOKEN" }));
                Check(ProviderCatalog.Help("mimo").Contains("platform.xiaomimimo.com") && ProviderCatalog.Help("stepfun").Contains("platform.stepfun.ai"));
                Check(new AppConfig().StepfunRegion == "global");
            });
        }
    }
}
