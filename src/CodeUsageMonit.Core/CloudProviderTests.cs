using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace CodeUsageMonit {
    // MiMo / StepFun 云端额度通道的 fixture 回归：规格样例 JSON → Parsers 解析结果、
    // 凭据归一化、webid 派生与出口 URL 白名单。纯解析与纯函数，不发起网络请求，
    // 清单里不出现真实凭据（所有 token / cookie 均为自测字面量）。
    internal static class CloudProviderTests {
        private static void Check(bool condition, string note = null, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0) { if (!condition) throw new Exception("Cloud provider assertion failed @" + line + (note == null ? "" : " (" + note + ")")); }
        private static ProviderException Fault(Action action) {
            try { action(); } catch (ProviderException e) { return e; }
            return null;
        }
        // 等到「同一 token 的在途刷新登记项已清锁」，供「下一个 tick 持同一旧 token」类
        // 用例排除竞态：清锁走线程池 ContinueWith，与主调同步推进之间存在竞态——不排除的
        // 话，第二次调用可能命中尚未清理的 inflight，返回同一次在途任务的结果（见上面
        // dedup-same-task 的同一观察点），测的就不是派生缓存了。判据是确定性的：inflight
        // 命中返回同一个 Task 对象，缓存命中返回全新 Task；缓存路径在预检前就返回，探针
        // 不会调 post（webid/appid/host 对缓存路径无影响，随手给即可）。
        private static void AfterInflightCleared(string token, Func<string, string, string, string, int, Task<object>> post, Task<string> previous) {
            for (int i = 0; i < 200; i++) {
                Task<string> probe = StepFunAuth.EnsureFreshAsync(token, "dev-1", "20700", "https://platform.stepfun.ai", false, post, null);
                if (!ReferenceEquals(probe, previous)) return;
                System.Threading.Thread.Sleep(2);
            }
            throw new Exception("inflight registration was not cleared in time");
        }
        private static string B64Url(string json) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        // 合成 Chromium Cookies 库（与 desktop store reader 用例同形）：只写
        // name / value / encrypted_value 三列，host_key 固定 account.xiaomi.com。
        // dir 由调用方创建与清理；返回库文件路径。fixture 值均为自测字面量，
        // 不含真实凭据。
        private static string BuildMimoStore(string dir, Action<Action<string, string, string>> seed) {
            string path = Path.Combine(dir, "Cookies-" + Guid.NewGuid().ToString("N"));
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate }.ToString())) {
                connection.Open();
                using (var create = connection.CreateCommand()) { create.CommandText = "CREATE TABLE cookies (name TEXT, value TEXT, encrypted_value BLOB, host_key TEXT)"; create.ExecuteNonQuery(); }
                seed((name, value, blob) => {
                    using (var insert = connection.CreateCommand()) {
                        insert.CommandText = "INSERT INTO cookies (name, value, encrypted_value, host_key) VALUES (@n, @v, @e, @h)";
                        insert.Parameters.AddWithValue("@n", name); insert.Parameters.AddWithValue("@v", (object)value ?? DBNull.Value);
                        insert.Parameters.AddWithValue("@e", blob != null ? (object)Encoding.UTF8.GetBytes(blob) : DBNull.Value);
                        insert.Parameters.AddWithValue("@h", MimoDesktop.AccountHost);
                        insert.ExecuteNonQuery();
                    }
                });
            }
            return path;
        }

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
                // 帮助文案保留 console 域名锚点，并提到桌面客户端自动续期。
                Check(ProviderCatalog.Help("mimo").Contains("platform.xiaomimimo.com") && ProviderCatalog.Help("mimo").Contains("桌面客户端"));
                Check(ProviderCatalog.Help("stepfun").Contains("platform.stepfun.ai"));
                Check(new AppConfig().StepfunRegion == "global");
            });

            // ── StepFun 滚动刷新（StepFunAuth）────────────────────────────
            string B64(string json) { return B64Url(json); }
            Func<double, string> jwtWithExp = exp => "hdr." + B64("{\"exp\":" + exp.ToString("0", CultureInfo.InvariantCulture) + ",\"oasis_id\":\"acct-1\",\"activated\":true,\"app_id\":20700,\"device_id\":\"dev-1\"}") + ".sig";
            DateTime now = DateTime.UtcNow;
            // 过期时刻助手（Unix 秒）。各用例故意错开取值：派生缓存是进程内静态的，token
            // 字面量相同会跨用例串味。
            Func<TimeSpan, double> expIn = delta => now.Add(delta).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            test("StepFunAuth reads token claims and splits access/refresh segments", () => {
                string token = jwtWithExp(now.AddHours(-2).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds) + "..." + jwtWithExp(now.AddDays(20).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
                Check(StepFunAuth.AccessSegment(token) == token.Split(new[] { "..." }, StringSplitOptions.None)[0]);
                Check(StepFunAuth.RefreshSegment(token) == token.Split(new[] { "..." }, StringSplitOptions.None)[1]);
                StepFunAuth.Claims claims = StepFunAuth.TokenClaims(StepFunAuth.AccessSegment(token));
                Check(claims != null && claims.HasActivated && claims.Activated && claims.OasisId == "acct-1");
                Check(StepFunAuth.StoredOasisId(token) == "acct-1");
                Check(StepFunAuth.TokenClaims("garbage") == null);
                Check(StepFunAuth.StoredOasisId("garbage") == "");
                Check(StepFunAuth.RefreshSegment("only-access") == "");
            });
            test("StepFunAuth refresh triggers 30 min before access expiry and skips when exp unreadable", () => {
                string epoch(double seconds) { return "hdr." + B64("{\"exp\":" + seconds.ToString("0", CultureInfo.InvariantCulture) + "}") + ".sig"; }
                double fresh = now.AddHours(2).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                double soon = now.AddMinutes(10).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                double past = now.AddMinutes(-5).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                Check(!StepFunAuth.AccessNeedsRefresh(epoch(fresh), now));
                Check(StepFunAuth.AccessNeedsRefresh(epoch(soon), now));
                Check(StepFunAuth.AccessNeedsRefresh(epoch(past), now));
                Check(!StepFunAuth.AccessNeedsRefresh("hdr." + B64("{}") + ".sig", now));   // 无 exp：不强刷
                Check(!StepFunAuth.AccessNeedsRefresh("garbage", now));
                // refresh 段判定：缺失 / 无 exp / 已过期都算死。
                Check(StepFunAuth.RefreshExpired("only-access", now));
                Check(StepFunAuth.RefreshExpired(epoch(fresh) + "..." + "hdr." + B64("{}") + ".sig", now));
                Check(StepFunAuth.RefreshExpired(epoch(fresh) + "..." + epoch(past), now));
                Check(!StepFunAuth.RefreshExpired(epoch(past) + "..." + epoch(fresh), now));
            });
            test("StepFunAuth refresh assembles access...refresh, keeps old refresh when absent, accepts data envelope", () => {
                double accessExp = now.AddHours(1).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                double refreshExp = now.AddDays(20).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                string old = jwtWithExp(now.AddMinutes(-30).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds) + "..." + jwtWithExp(refreshExp);
                string newAccess = jwtWithExp(accessExp), newRefresh = jwtWithExp(refreshExp);
                Func<string, object> reply = shape => J.Parse(shape.Replace("ACCESS", newAccess).Replace("REFRESH", newRefresh));
                StepFunAuth.RefreshResult ok = StepFunAuth.RefreshAsync(old, "dev-1", "20700", "https://platform.stepfun.ai",
                    (url, token, webid, appid, timeout) => {
                        Check(url == "https://platform.stepfun.ai" + StepFunAuth.RefreshPath && token == old && webid == "dev-1" && appid == "20700");
                        return Task.FromResult(reply("{\"accessToken\":{\"raw\":\"ACCESS\"},\"refreshToken\":{\"raw\":\"REFRESH\"}}"));
                    }, now).GetAwaiter().GetResult();
                Check(ok.Ok && ok.Token == newAccess + "..." + newRefresh);
                // 旧 refresh 保留：响应缺 refreshToken 时不缩水成裸 access。
                StepFunAuth.RefreshResult keepOld = StepFunAuth.RefreshAsync(old, "dev-1", "20700", "https://platform.stepfun.ai",
                    (url, token, webid, appid, timeout) => Task.FromResult(reply("{\"accessToken\":{\"raw\":\"ACCESS\"}}")), now).GetAwaiter().GetResult();
                Check(keepOld.Ok && keepOld.Token == newAccess + "..." + jwtWithExp(refreshExp));
                // data 信封形态照常解析。
                StepFunAuth.RefreshResult enveloped = StepFunAuth.RefreshAsync(old, "dev-1", "20700", "https://platform.stepfun.ai",
                    (url, token, webid, appid, timeout) => Task.FromResult(reply("{\"status\":1,\"data\":{\"accessToken\":{\"raw\":\"ACCESS\"},\"refreshToken\":{\"raw\":\"REFRESH\"}}}")), now).GetAwaiter().GetResult();
                Check(enveloped.Ok && enveloped.Token == newAccess + "..." + newRefresh);
            });
            test("StepFunAuth classifies anonymous token, oasis mismatch, 401 and spent refresh as dead session", () => {
                double refreshExp = now.AddDays(20).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                string old = jwtWithExp(now.AddMinutes(-30).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds) + "..." + jwtWithExp(refreshExp);
                // 匿名令牌（refresh 过 30 天后平台的回应）：activated=false → expired。
                string anonymous = "hdr." + B64("{\"exp\":" + now.AddHours(1).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + ",\"oasis_id\":\"anon\",\"activated\":false,\"app_id\":20700}") + ".sig";
                StepFunAuth.RefreshResult anon = StepFunAuth.RefreshAsync(old, "dev-1", "20700", "https://platform.stepfun.ai",
                    (url, token, webid, appid, timeout) => Task.FromResult<object>(J.Parse("{\"accessToken\":{\"raw\":\"" + anonymous + "\"}}")), now).GetAwaiter().GetResult();
                Check(!anon.Ok && anon.State == "expired");
                // oasis_id 与存量不一致 → 换了账户，同样判死。
                string other = "hdr." + B64("{\"exp\":" + now.AddHours(1).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + ",\"oasis_id\":\"acct-2\",\"activated\":true}") + ".sig";
                StepFunAuth.RefreshResult mismatch = StepFunAuth.RefreshAsync(old, "dev-1", "20700", "https://platform.stepfun.ai",
                    (url, token, webid, appid, timeout) => Task.FromResult<object>(J.Parse("{\"accessToken\":{\"raw\":\"" + other + "\"}}")), now).GetAwaiter().GetResult();
                Check(!mismatch.Ok && mismatch.State == "expired");
                // HTTP 401 → expired；其他异常 → error（旧 token 保留照常用）。
                foreach (var failure in new (Exception ex, string state)[] { (new ProviderException(Parsers.StepFunExpiredMessage, "expired"), "expired"), (new HttpRequestException("reset"), "error") }) {
                    StepFunAuth.RefreshResult failed = StepFunAuth.RefreshAsync(old, "dev-1", "20700", "https://platform.stepfun.ai",
                        (url, token, webid, appid, timeout) => Task.FromException<object>(failure.ex), now).GetAwaiter().GetResult();
                    Check(!failed.Ok && failed.State == failure.state);
                }
                // refresh 段已过期：不发请求直接判死。
                int calls = 0;
                StepFunAuth.RefreshResult spent = StepFunAuth.RefreshAsync(jwtWithExp(refreshExp) + "..." + "garbage", "dev-1", "20700", "https://platform.stepfun.ai",
                    (url, token, webid, appid, timeout) => { calls++; throw new InvalidOperationException("refresh 段已过期不得发请求"); }, now).GetAwaiter().GetResult();
                Check(!spent.Ok && spent.State == "expired" && calls == 0);
                // 响应缺 accessToken → error（服务端异常形态，不是凭据信号）。
                StepFunAuth.RefreshResult noAccess = StepFunAuth.RefreshAsync(old, "dev-1", "20700", "https://platform.stepfun.ai",
                    (url, token, webid, appid, timeout) => Task.FromResult<object>(J.Parse("{}")), now).GetAwaiter().GetResult();
                Check(!noAccess.Ok && noAccess.State == "error");
            });
            test("StepFunAuth EnsureFresh rolls near expiry, persists, dedupes concurrent calls and skips fresh tokens", () => {
                string epoch(double seconds) { return "hdr." + B64("{\"exp\":" + seconds.ToString("0", CultureInfo.InvariantCulture) + ",\"oasis_id\":\"acct-1\",\"activated\":true}") + ".sig"; }
                double soon = now.AddMinutes(10).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                double freshExp = now.AddHours(2).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                string old = epoch(soon) + "..." + epoch(now.AddDays(20).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
                string renewed = epoch(freshExp), newRefresh = epoch(now.AddDays(30).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
                int calls = 0; string persisted = null;
                Func<string, string, string, string, int, Task<object>> post = (url, token, webid, appid, timeout) => {
                    calls++;
                    return Task.FromResult<object>(J.Parse("{\"accessToken\":{\"raw\":\"" + renewed + "\"},\"refreshToken\":{\"raw\":\"" + newRefresh + "\"}}"));
                };
                // 未到期且不 force：不发请求。
                string untouched = StepFunAuth.EnsureFreshAsync(epoch(freshExp) + "..." + epoch(freshExp), "dev-1", "20700", "https://platform.stepfun.ai", false, post, null).GetAwaiter().GetResult();
                Check(untouched == epoch(freshExp) + "..." + epoch(freshExp) && calls == 0);
                // 到期滚动 + persist 回调；同 token 的并发调用共用一次在途刷新（post 挂起在
                // TaskCompletionSource 上，第二次调用必须命中在途任务而不是再发一次请求）。
                var gate = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                Func<string, string, string, string, int, Task<object>> gatedPost = (url, token, webid, appid, timeout) => { calls++; return gate.Task; };
                Task<string> first = StepFunAuth.EnsureFreshAsync(old, "dev-1", "20700", "https://platform.stepfun.ai", false, gatedPost, token => persisted = token);
                Task<string> second = StepFunAuth.EnsureFreshAsync(old, "dev-1", "20700", "https://platform.stepfun.ai", false, gatedPost, token => persisted = token);
                Check(calls == 1, "dedup-dispatch");
                Check(first == second, "dedup-same-task");   // 两调用共用同一次在途刷新
                gate.SetResult(J.Parse("{\"accessToken\":{\"raw\":\"" + renewed + "\"}}"));
                string rolled = Task.WhenAll(first, second).GetAwaiter().GetResult().First();
                Check(calls == 1 && rolled == renewed + "..." + old.Split(new[] { "..." }, StringSplitOptions.None)[1] && persisted == rolled);   // 响应缺 refreshToken：旧 refresh 段保留
                // force 跳过预检（401/expired 兜底路径），响应带 refreshToken 时整对滚动。
                //（换新 token：inflight 按 token 分键，避免与上一段共用键后命中去重。）
                calls = 0; persisted = null;
                string old2 = epoch(soon - 1) + "..." + epoch(now.AddDays(20).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds - 1);
                string forced = StepFunAuth.EnsureFreshAsync(old2, "dev-1", "20700", "https://platform.stepfun.ai", true, post, token => persisted = token).GetAwaiter().GetResult();
                Check(calls == 1 && forced == renewed + "..." + newRefresh && persisted == forced);
                // 暂态失败：原 token 返回、persist 不触发。
                calls = 0; persisted = null;
                string old3 = epoch(soon - 2) + "..." + epoch(now.AddDays(20).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds - 2);
                string kept = StepFunAuth.EnsureFreshAsync(old3, "dev-1", "20700", "https://platform.stepfun.ai", true,
                    (url, token, webid, appid, timeout) => Task.FromException<object>(new HttpRequestException("proxy down")), token => persisted = token).GetAwaiter().GetResult();
                Check(kept == old3 && persisted == null && calls == 0);   // 本段 post 不经过计数器（直接抛传输异常）
                // 会话已死：抛 expired。
                ProviderException dead = null;
                string old4 = epoch(soon - 3) + "..." + epoch(now.AddDays(20).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds - 3);
                try { StepFunAuth.EnsureFreshAsync(old4, "dev-1", "20700", "https://platform.stepfun.ai", true, (url, token, webid, appid, timeout) => Task.FromException<object>(new ProviderException(Parsers.StepFunExpiredMessage, "expired")), null).GetAwaiter().GetResult(); }
                catch (ProviderException e) { dead = e; }
                Check(dead != null && dead.State == "expired");
            });
            test("StepFun token appid locks the site: host from app_id, no cross-site retry", () => {
                Check(Parsers.StepFunTokenAppId("hdr." + B64("{\"app_id\":20700}") + ".sig") == "20700");
                Check(Parsers.StepFunTokenAppId("not-a-jwt") == "");
                Check(Parsers.StepFunHostForAppId("10300") == "https://platform.stepfun.com");
                Check(Parsers.StepFunHostForAppId("20700") == "https://platform.stepfun.ai" && Parsers.StepFunHostForAppId("") == "https://platform.stepfun.ai" && Parsers.StepFunHostForAppId(null) == "https://platform.stepfun.ai");
                // 区域配置是 china，但 token 自带 20700：只打国际站一次，expired 不换站、不写回区域。
                string token = "hdr." + B64("{\"app_id\":20700,\"device_id\":\"dev-1\"}") + ".sig";
                int calls = 0; string persisted = null; ProviderException thrown = null;
                var hosts = new List<string>();
                try {
                    ProviderService.StepFunChannel(token, "china", (url, t, webid, appid, timeout) => { calls++; hosts.Add(new Uri(url).Host); throw new ProviderException(Parsers.StepFunExpiredMessage, "expired"); }, region => persisted = region).GetAwaiter().GetResult();
                } catch (ProviderException e) { thrown = e; }
                Check(thrown != null && thrown.State == "expired" && calls == 1 && persisted == null);
                Check(hosts.Single() == "platform.stepfun.ai");
                // 锁站成功路径：appid 用 token 自带值，配置区域不影响。
                string used = null;
                ProviderState s = ProviderService.StepFunChannel(token, "china", (url, t, webid, appid, timeout) => {
                    used = url + "|" + appid;
                    return Task.FromResult<object>(url.Contains("GetStepPlanStatus") ? J.Parse("{\"status\":1,\"data\":{\"subscription\":{\"name\":\"Pro\"}}}") : J.Parse("{\"status\":1,\"five_hour_usage_left_rate\":0.8,\"five_hour_usage_reset_time\":1900000000}"));
                }, region => persisted = region).GetAwaiter().GetResult();
                Check(used.Contains("platform.stepfun.ai") && used.EndsWith("|20700") && s.Plan == "Pro" && persisted == null);
            });
            test("StepFun refresh pairs host with the effective appid: no-app_id token + china goes to .com/10300", () => {
                // 回归：token 提取不到 app_id 且区域 china 时，刷新的 host 与 oasis-appid 必须
                // 由同一生效 appid 派生——host 落 .ai 而 appid=10300 的跨站错配会被平台判盗用
                //（embezzled）烧 token。
                string plain(double exp) { return "hdr." + B64("{\"exp\":" + exp.ToString("0", CultureInfo.InvariantCulture) + ",\"oasis_id\":\"acct-cn\",\"activated\":true,\"device_id\":\"dev-cn\"}") + ".sig"; }
                double soon = now.AddMinutes(10).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                double far = now.AddDays(20).Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                string old = plain(soon) + "..." + plain(far);   // 各段均无 app_id claim
                string region = "china";
                string usedUrl = null, usedAppId = null, usedWebId = null;
                // 与 Core roll 闭包同款派生：host 与 appid 出自同一个生效 appid（区域回退）。
                Func<string, bool, Task<string>> roll = (current, force) => StepFunAuth.EnsureFreshAsync(current, Parsers.StepFunWebId(current), Parsers.StepFunAppId(current, region), Parsers.StepFunHostForAppId(Parsers.StepFunAppId(current, region)), force,
                    (url, token, webid, appid, timeout) => { usedUrl = url; usedAppId = appid; usedWebId = webid; return Task.FromResult<object>(J.Parse("{\"accessToken\":{\"raw\":\"" + plain(far) + "\"}}")); }, null);
                roll(old, true).GetAwaiter().GetResult();
                Check(usedUrl == "https://platform.stepfun.com" + StepFunAuth.RefreshPath && usedAppId == "10300" && usedWebId == "dev-cn");
                // 对照：同一 token 在 global 区域两侧同样同站，落国际站（.ai / 20700）。
                Check(Parsers.StepFunAppId(old, "global") == "20700" && Parsers.StepFunHostForAppId(Parsers.StepFunAppId(old, "global")) == "https://platform.stepfun.ai");
            });
            // 派生缓存（旧 token 原文 → 新 token 原文）：env 凭据（persist=null）或钥匙串
            // 写回失败时，下一 tick 重读到的仍是旧 token；没有缓存就会用同一个旧 refresh 段
            // 重放 RefreshToken（平台风控/吊销会话）。以下用例全部走注入 post 缝。
            test("StepFunAuth derived cache resolves the same old token across ticks without replaying the refresh", () => {
                string oldRefresh = jwtWithExp(expIn(TimeSpan.FromDays(21)));
                string old = jwtWithExp(expIn(TimeSpan.FromMinutes(11))) + "..." + oldRefresh;
                string renewed = jwtWithExp(expIn(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1)));
                int calls = 0;
                Func<string, string, string, string, int, Task<object>> post = (url, token, webid, appid, timeout) => {
                    calls++;
                    return Task.FromResult<object>(J.Parse("{\"accessToken\":{\"raw\":\"" + renewed + "\"}}"));
                };
                // 第一个 tick：access 临期触发滚动（响应缺 refreshToken，旧 refresh 段保留）。
                Task<string> first = StepFunAuth.EnsureFreshAsync(old, "dev-1", "20700", "https://platform.stepfun.ai", false, post, null);
                Check(first.Result == renewed + "..." + oldRefresh && calls == 1);
                AfterInflightCleared(old, post, first);
                // 第二个 tick 持同一旧 token（env 凭据的形态）：命中缓存，派生 token 的 access
                // 仍新鲜，直接返回新 token——RefreshToken 不被重放。
                string second = StepFunAuth.EnsureFreshAsync(old, "dev-1", "20700", "https://platform.stepfun.ai", false, post, null).GetAwaiter().GetResult();
                Check(second == first.Result && calls == 1);
            });
            test("StepFunAuth derived cache covers the tick after the persist callback throws", () => {
                // 钥匙串写回不可用时 persist 抛异常：刷新结果只在内存，下一 tick 仍持旧 token，
                // 靠派生缓存兜底——post 仍只调一次，第二次直接拿到新 token，不重放。
                string oldRefresh = jwtWithExp(expIn(TimeSpan.FromDays(22)));
                string old = jwtWithExp(expIn(TimeSpan.FromMinutes(12))) + "..." + oldRefresh;
                string renewed = jwtWithExp(expIn(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(2)));
                int calls = 0, persists = 0;
                Func<string, string, string, string, int, Task<object>> post = (url, token, webid, appid, timeout) => {
                    calls++;
                    return Task.FromResult<object>(J.Parse("{\"accessToken\":{\"raw\":\"" + renewed + "\"}}"));
                };
                Action<string> persist = fresh => { persists++; throw new InvalidOperationException("keychain unavailable"); };
                Task<string> first = StepFunAuth.EnsureFreshAsync(old, "dev-1", "20700", "https://platform.stepfun.ai", false, post, persist);
                Check(first.Result == renewed + "..." + oldRefresh && calls == 1 && persists == 1);
                AfterInflightCleared(old, post, first);
                string second = StepFunAuth.EnsureFreshAsync(old, "dev-1", "20700", "https://platform.stepfun.ai", false, post, persist).GetAwaiter().GetResult();
                Check(second == first.Result && calls == 1 && persists == 1);   // 缓存兜底：不重放、也不再 persist
            });
            test("StepFunAuth derived cache chains to the latest pair and updates the old mapping", () => {
                // 派生 token 也临期时，持最旧 token 的调用继续刷新到最新；刷新成功后旧映射
                // 同步更新，之后持最旧或中间任一旧值都能直接命中最新值，不再发请求。
                string oldestRefresh = jwtWithExp(expIn(TimeSpan.FromDays(23)));
                string oldest = jwtWithExp(expIn(TimeSpan.FromMinutes(13))) + "..." + oldestRefresh;
                string midRefresh = jwtWithExp(expIn(TimeSpan.FromDays(24)));
                string mid = jwtWithExp(expIn(TimeSpan.FromMinutes(6)));
                string midPair = mid + "..." + midRefresh;
                string latestRefresh = jwtWithExp(expIn(TimeSpan.FromDays(25)));
                string latest = jwtWithExp(expIn(TimeSpan.FromHours(4)));
                string latestPair = latest + "..." + latestRefresh;
                int calls = 0;
                Func<string, string, string, string, int, Task<object>> post = (url, token, webid, appid, timeout) => {
                    calls++;
                    // 第一跳（刷 oldest）返回同样临期的 mid；第二跳（刷 midPair）返回全新的 latest。
                    return Task.FromResult<object>(J.Parse(token == midPair
                        ? "{\"accessToken\":{\"raw\":\"" + latest + "\"},\"refreshToken\":{\"raw\":\"" + latestRefresh + "\"}}"
                        : "{\"accessToken\":{\"raw\":\"" + mid + "\"},\"refreshToken\":{\"raw\":\"" + midRefresh + "\"}}"));
                };
                string first = StepFunAuth.EnsureFreshAsync(oldest, "dev-1", "20700", "https://platform.stepfun.ai", false, post, null).GetAwaiter().GetResult();
                Check(first == midPair && calls == 1);
                // 持最旧 token 再来：缓存解析到 midPair，mid 也临期 → 继续刷新到 latestPair。
                string second = StepFunAuth.EnsureFreshAsync(oldest, "dev-1", "20700", "https://platform.stepfun.ai", false, post, null).GetAwaiter().GetResult();
                Check(second == latestPair && calls == 2);
                // 最旧值与中间值此后都直接命中最新映射，不再请求。
                Check(StepFunAuth.EnsureFreshAsync(oldest, "dev-1", "20700", "https://platform.stepfun.ai", false, post, null).GetAwaiter().GetResult() == latestPair && calls == 2);
                Check(StepFunAuth.EnsureFreshAsync(midPair, "dev-1", "20700", "https://platform.stepfun.ai", false, post, null).GetAwaiter().GetResult() == latestPair && calls == 2);
            });
            test("StepFunAuth derived cache clears wholesale past 128 entries instead of growing unbounded", () => {
                // 防爆：条数超上限整体 Clear。entry 是旧→新的映射，丢失只是退化为现状（多打
                // 一次刷新）；这里验证清理后旧键重新刷新、新写入的键仍然命中。300 个互不
                // 相同的临期 token，足以无论之前残留多少条都触发清理。
                double near = expIn(TimeSpan.FromMinutes(14)), far = expIn(TimeSpan.FromDays(26));
                string renewed = jwtWithExp(expIn(TimeSpan.FromHours(5)));
                int calls = 0;
                Func<string, string, string, string, int, Task<object>> post = (url, token, webid, appid, timeout) => {
                    calls++;
                    return Task.FromResult<object>(J.Parse("{\"accessToken\":{\"raw\":\"" + renewed + "\"}}"));
                };
                string last = null, lastRefresh = null;
                for (int i = 0; i < 300; i++) {
                    lastRefresh = jwtWithExp(far - i);
                    last = jwtWithExp(near - i) + "..." + lastRefresh;
                    StepFunAuth.EnsureFreshAsync(last, "dev-1", "20700", "https://platform.stepfun.ai", false, post, null).GetAwaiter().GetResult();
                }
                Check(calls == 300);
                // 最后写入的键仍在缓存：命中不请求。
                Check(StepFunAuth.EnsureFreshAsync(last, "dev-1", "20700", "https://platform.stepfun.ai", false, post, null).GetAwaiter().GetResult() == renewed + "..." + lastRefresh && calls == 300);
                // 被 Clear 清掉的最早键：退化为现状，重新刷新一次。
                string firstRefresh = jwtWithExp(far);
                Check(StepFunAuth.EnsureFreshAsync(jwtWithExp(near) + "..." + firstRefresh, "dev-1", "20700", "https://platform.stepfun.ai", false, post, null).GetAwaiter().GetResult() == renewed + "..." + firstRefresh && calls == 301);
            });
            // W7：异源构造的 joiner 也落派生缓存。在途刷新由持 midPair 的调用方发起，
            // 持更旧 older 的并发调用（经派生缓存解析到 midPair）直接共用该任务；刷新
            // 成功后续尾把「older → 新 token」补记进缓存，下一 tick 不再重放 refresh
            // （修复前 derived[older] 停留在 midPair，持 older 的调用会再刷一次）。
            test("StepFunAuth joiner remembers its own original token when it shares an in-flight refresh", () => {
                string olderRefresh = jwtWithExp(expIn(TimeSpan.FromDays(27)));
                string older = jwtWithExp(expIn(TimeSpan.FromMinutes(15))) + "..." + olderRefresh;   // 第一轮后被刷新成 midPair
                string midRefresh = jwtWithExp(expIn(TimeSpan.FromDays(28)));
                string mid = jwtWithExp(expIn(TimeSpan.FromMinutes(7)));                              // 第二轮参与刷新的键
                string midPair = mid + "..." + midRefresh;
                string renewedRefresh = jwtWithExp(expIn(TimeSpan.FromDays(29)));
                string renewed = jwtWithExp(expIn(TimeSpan.FromHours(3)));
                string renewedPair = renewed + "..." + renewedRefresh;
                int calls = 0;
                Func<string, string, string, string, int, Task<object>> firstPost = (url, token, webid, appid, timeout) => {
                    calls++;
                    return Task.FromResult<object>(J.Parse("{\"accessToken\":{\"raw\":\"" + mid + "\"},\"refreshToken\":{\"raw\":\"" + midRefresh + "\"}}"));
                };
                // 第一轮：older 临期 → 刷新成 midPair，derived[older] = midPair（同步落在
                // 任务完成前，first.Result 可观察时映射已存在）。
                Task<string> first = StepFunAuth.EnsureFreshAsync(older, "dev-1", "20700", "https://platform.stepfun.ai", false, firstPost, null);
                Check(first.Result == midPair && calls == 1);
                // 第二轮：midPair 临期，持 midPair 的调用方发起在途刷新（post 挂在闸门上）。
                var gate = new TaskCompletionSource<object>();
                Func<string, string, string, string, int, Task<object>> gatedPost = (url, token, webid, appid, timeout) => { calls++; return gate.Task; };
                Task<string> starter = StepFunAuth.EnsureFreshAsync(midPair, "dev-1", "20700", "https://platform.stepfun.ai", false, gatedPost, null);
                Check(calls == 2, "the starter must dispatch exactly one refresh");
                // 异源 joiner：持 older 并发进入（解析到 midPair），共用同一次在途刷新——
                // 同一 Task 实例（去重不包装任务），post 不叠加。
                Task<string> joiner = StepFunAuth.EnsureFreshAsync(older, "dev-1", "20700", "https://platform.stepfun.ai", false, gatedPost, null);
                Check(ReferenceEquals(starter, joiner), "the joiner must share the in-flight task instance");
                Check(calls == 2, "the joiner must not dispatch another refresh");
                gate.SetResult(J.Parse("{\"accessToken\":{\"raw\":\"" + renewed + "\"},\"refreshToken\":{\"raw\":\"" + renewedRefresh + "\"}}"));
                Check(Task.WhenAll(starter, joiner).GetAwaiter().GetResult().All(t => t == renewedPair), "both callers must get the renewed pair");
                // 先等在途登记清锁（探针命中派生缓存、不发请求），排除「探针撞上仍在途的
                // 刷新」造成的假阳性；续尾与清锁都走线程池，之后轮询等映射落定。
                AfterInflightCleared(midPair, gatedPost, starter);
                for (int i = 0; i < 200; i++) {
                    // 探针的 post 一律抛：续尾未落缓存时探针发起的刷新不被 Remember、
                    // 不污染缓存（暂态失败原样返回参与刷新的 token），重试即可。
                    string probe = StepFunAuth.EnsureFreshAsync(older, "dev-1", "20700", "https://platform.stepfun.ai", false,
                        (url, token, webid, appid, timeout) => Task.FromException<object>(new InvalidOperationException("probe must not refresh")), null).GetAwaiter().GetResult();
                    if (probe == renewedPair) return;   // 命中缓存即通过
                    System.Threading.Thread.Sleep(2);
                }
                throw new Exception("the joiner mapping was not remembered in time");
            });

            // ── MiMo 桌面客户端换票（MimoDesktop / MimoExchange）────────────
            test("MiMo jar seeds, absorbs, replaces and expires cookies with RFC 6265 matching", () => {
                DateTime t = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
                double tick = 0;
                var jar = new MimoExchange.Jar(() => t.AddSeconds(tick));
                Uri console = new Uri("https://platform.xiaomimimo.com/api/v1/balance");
                Uri account = new Uri("https://account.xiaomi.com/");
                jar.Seed("passToken=pt-1; userId=8601; bad; =novalue", account);
                Check(jar.HeaderFor(account) == "passToken=pt-1; userId=8601");      // 种子按序输出、垃圾项丢弃
                Check(jar.HeaderFor(console) == "");                                  // host-only：不跨域发送
                // 吸收：host-only 默认、Domain 去点、Max-Age 负值即删、Path 默认取目录。
                jar.Absorb(new[] { "serviceToken=st-1; Path=/; Max-Age=60; Secure", "api-platform_ph=ph-1; Domain=.xiaomimimo.com; Path=/", "short=lived; Max-Age=-1" }, console);
                Check(jar.HeaderFor(console).Contains("serviceToken=st-1") && jar.HeaderFor(console).Contains("api-platform_ph=ph-1"));
                Check(!jar.HeaderFor(console).Contains("short=lived"));
                Check(jar.HeaderFor(new Uri("https://platform.xiaomimimo.com/api/v1/userProfile")).Contains("serviceToken=st-1"));   // 路径前缀匹配
                // 同 name+domain+path 替换；不同 path 共存且长者优先。
                jar.Absorb(new[] { "serviceToken=st-2; Path=/; Max-Age=60; Secure" }, console);
                Check(jar.HeaderFor(console).Contains("serviceToken=st-2") && !jar.HeaderFor(console).Contains("st-1"));
                jar.Absorb(new[] { "serviceToken=st-deep; Path=/api/v1/balance" }, console);
                Check(jar.HeaderFor(console).StartsWith("serviceToken=st-deep;", StringComparison.Ordinal));   // 路径长者优先
                // Max-Age 到期条目不再发送（会话条目保留）；secure 条目不发 http；白名单外域名丢弃。
                tick = 61;
                string afterExpiry = jar.HeaderFor(console);
                Check(!afterExpiry.Contains("st-2") && afterExpiry.Contains("st-deep"));
                jar.Absorb(new[] { "later=1; Max-Age=600; Secure" }, console);
                string overHttp = jar.HeaderFor(new Uri("http://platform.xiaomimimo.com/api/v1/balance"));
                Check(overHttp.Contains("api-platform_ph=ph-1"), "over-http-ph");
                Check(overHttp.Contains("serviceToken=st-deep"), "over-http-session-cookie");
                Check(!overHttp.Contains("later=1"), "over-http-secure-excluded");
                jar.Absorb(new[] { "evil=1; Domain=evil.com" }, console);
                Check(!jar.HeaderFor(console).Contains("evil=1"));
                jar.Absorb(new[] { "crossed=1; Domain=xiaomi.com" }, new Uri("https://account.xiaomi.com/"));
                Check(jar.HeaderFor(new Uri("https://account.xiaomi.com/")).Contains("crossed=1"));
                Check(jar.HeaderFor(new Uri("https://ftp.xiaomi.com/")).Contains("crossed=1"));         // Domain 条目匹配子域
                Check(jar.HeaderFor(new Uri("https://xiaomimimo.com/")) == "" || !jar.HeaderFor(new Uri("https://xiaomimimo.com/")).Contains("crossed=1"));
            });
            test("MiMo login/exchange URL rules: https only, no userinfo or port, domain whitelist, service http upgraded", () => {
                Uri entry = MimoExchange.ConsoleEntry();
                Check(MimoExchange.AllowedLoginUrl("https://account.xiaomi.com/login", entry) != null);
                Check(MimoExchange.AllowedLoginUrl("https://login.mobile.mi.com/sts", entry) != null);
                Check(MimoExchange.AllowedLoginUrl("https://sts.miui.com/sso", entry) != null);
                Check(MimoExchange.AllowedLoginUrl("http://account.xiaomi.com/login", entry) == null);        // 明文拒绝
                Check(MimoExchange.AllowedLoginUrl("https://account.xiaomi.com:8080/login", entry) == null);  // 显式端口拒绝
                Check(MimoExchange.AllowedLoginUrl("https://user@account.xiaomi.com/", entry) == null);       // userinfo 拒绝
                Check(MimoExchange.AllowedLoginUrl("https://evil.com/?next=/", entry) == null);               // 白名单外拒绝
                Check(MimoExchange.AllowedLoginUrl("https://platform.xiaomimimo.com/", entry) == null);       // console 域不是登录域
                Check(MimoExchange.AllowedLoginUrl("garbage", entry) == null && MimoExchange.AllowedLoginUrl(null, entry) == null);
                // 链上 URL：console 域的 http 升 https；其余只放登录域的 https。
                Check(MimoExchange.AllowedExchangeUrl("http://platform.xiaomimimo.com/api/v1/balance", entry, entry.Host).Scheme == Uri.UriSchemeHttps);
                Check(MimoExchange.AllowedExchangeUrl("https://account.xiaomi.com/sts", entry, entry.Host) != null);
                Check(MimoExchange.AllowedExchangeUrl("http://account.xiaomi.com/sts", entry, entry.Host) == null);
                Check(MimoExchange.AllowedExchangeUrl("https://cdn.evil.com/x", entry, entry.Host) == null);
            });
            test("MiMo exchange headers carry Origin/Referer only for the console host", () => {
                var consoleHeaders = MimoExchange.ExchangeHeaders("a=1", new Uri("https://platform.xiaomimimo.com/api/v1/balance"), "UA");
                Check(consoleHeaders["Origin"] == "https://platform.xiaomimimo.com" && consoleHeaders["Referer"].Contains("platform.xiaomimimo.com") && consoleHeaders["Cookie"] == "a=1" && consoleHeaders["User-Agent"] == "UA");
                var loginHeaders = MimoExchange.ExchangeHeaders("a=1", new Uri("https://account.xiaomi.com/sts"), "UA");
                Check(!loginHeaders.ContainsKey("Origin") && !loginHeaders.ContainsKey("Referer"));
            });
            test("MiMo rejection codes read the body (46109 rides HTTP 200) and console answers are classified", () => {
                Check(MimoExchange.RejectionCode(200, "{\"code\":46109,\"message\":\"x\"}") == 46109);
                Check(MimoExchange.RejectionCode(200, "{\"code\":403}") == 403);
                Check(MimoExchange.RejectionCode(403, "") == 403);
                Check(MimoExchange.RejectionCode(200, "{\"code\":0}") == 0 && MimoExchange.RejectionCode(429, "{}") == 0 && MimoExchange.RejectionCode(200, "not json") == 0);
                Check(MimoExchange.ConsoleOk(200, "{\"code\":0}") && !MimoExchange.ConsoleOk(200, "{\"code\":5}") && !MimoExchange.ConsoleOk(401, "{\"code\":0}") && !MimoExchange.ConsoleOk(200, "garbage"));
                Uri entry = MimoExchange.ConsoleEntry();
                Uri login = MimoExchange.LoginUrl(401, "{\"loginUrl\":\"https://account.xiaomi.com/login\"}", entry);
                Check(login != null && login.Host == "account.xiaomi.com");
                Check(MimoExchange.LoginUrl(401, "{\"loginUrl\":\"https://evil.com/\"}", entry) == null);
                Check(MimoExchange.LoginUrl(200, "{\"loginUrl\":\"https://account.xiaomi.com/login\"}", entry) == null);
                Check(MimoExchange.LoginUrl(401, "garbage", entry) == null);
            });
            // 合成链换票的各分类分支（401 → loginUrl → 链回入口 → code 0 为成功）。
            test("MiMo walk mints the console cookie directly when the entry answers code 0", () => {
                var visited = new List<string>();
                MimoExchange.Result result = MimoExchange.Mint("passToken=pt; userId=1", MimoExchange.ConsoleEntry(), hop => {
                    visited.Add(hop.Url.Host + " " + hop.Headers["Cookie"]);
                    return Task.FromResult(new MimoExchange.Reply { Status = 200, Body = "{\"code\":0}", SetCookie = new[] { "api-platform_serviceToken=fresh-1; Path=/; Domain=.xiaomimimo.com; HttpOnly" } });
                }).GetAwaiter().GetResult();
                Check(result.Outcome == MimoExchange.Outcome.Ok);
                Check(result.CookieHeader.Contains("api-platform_serviceToken=fresh-1"));
                Check(visited.Count == 1 && visited[0].StartsWith("platform.xiaomimimo.com", StringComparison.Ordinal));
            });
            test("MiMo walk follows the 401 loginUrl through the SSO chain and re-asks the entry", () => {
                Uri entry = MimoExchange.ConsoleEntry();
                var cookies = new List<string>(); var hosts = new List<string>(); int entryHits = 0;
                MimoExchange.Result result = MimoExchange.Mint("passToken=pt; userId=1", entry, hop => {
                    hosts.Add(hop.Url.Host); cookies.Add(hop.Headers["Cookie"]);
                    if (hop.Url.Host == "platform.xiaomimimo.com") {
                        entryHits++;
                        return entryHits == 1
                            ? Task.FromResult(new MimoExchange.Reply { Status = 401, Body = "{\"loginUrl\":\"https://account.xiaomi.com/login?sid=mimopc\"}" })
                            : Task.FromResult(new MimoExchange.Reply { Status = 200, Body = "{\"code\":0}", SetCookie = new[] { "api-platform_serviceToken=fresh-2; Path=/; Domain=.xiaomimimo.com" } });
                    }
                    if (hop.Url.Host == "account.xiaomi.com") return Task.FromResult(new MimoExchange.Reply { Status = 302, Location = "https://platform.xiaomimimo.com/api/v1/balance", SetCookie = new[] { "serviceToken=ss-1; Domain=.xiaomi.com; Path=/; Secure" } });
                    throw new Exception("unexpected hop " + hop.Url);
                }).GetAwaiter().GetResult();
                Check(result.Outcome == MimoExchange.Outcome.Ok, "walk-ok:" + result.Outcome + " detail:" + result.Detail);
                Check(result.CookieHeader.Contains("api-platform_serviceToken=fresh-2"), "walk-cookie:" + result.CookieHeader);
                Check(hosts.SequenceEqual(new[] { "platform.xiaomimimo.com", "account.xiaomi.com", "platform.xiaomimimo.com" }), "walk-hosts:" + String.Join(">", hosts));
                // 种子凭据发往 account.xiaomi.com；console 跳带 Origin/Referer，SSO 跳不带。
                Check(cookies[1].Contains("passToken=pt") && cookies[1].Contains("userId=1"));
                Check(MimoExchange.ExchangeHeaders("", entry, "UA").ContainsKey("Origin"));
                var loginHopHeaders = MimoExchange.ExchangeHeaders("", new Uri("https://account.xiaomi.com/login"), "UA");
                Check(!loginHopHeaders.ContainsKey("Origin") && !loginHopHeaders.ContainsKey("Referer"));
            });
            test("MiMo walk classifies: no loginUrl, foreign loginUrl, 429, 46109, login-host ending, non-zero code, redirect cap", () => {
                Uri entry = MimoExchange.ConsoleEntry();
                Func<MimoExchange.Reply, MimoExchange.Result> run = last => MimoExchange.Mint("pt=1", entry, hop => Task.FromResult(last)).GetAwaiter().GetResult();
                Check(run(new MimoExchange.Reply { Status = 401, Body = "{}" }).Outcome == MimoExchange.Outcome.Rejected);
                Check(run(new MimoExchange.Reply { Status = 401, Body = "{\"loginUrl\":\"https://evil.com/\"}" }).Outcome == MimoExchange.Outcome.Rejected);
                Check(run(new MimoExchange.Reply { Status = 429, Body = "" }).Outcome == MimoExchange.Outcome.RateLimited);
                Check(run(new MimoExchange.Reply { Status = 200, Body = "{\"code\":46109}" }).Outcome == MimoExchange.Outcome.Rejected);
                Check(run(new MimoExchange.Reply { Status = 200, Body = "{\"code\":5}" }).Outcome == MimoExchange.Outcome.Unavailable);
                // 重定向跳出白名单 → unavailable；打满跳数上限 → unavailable；缺 Location → unavailable。
                Check(MimoExchange.Mint("pt=1", entry, hop => Task.FromResult(new MimoExchange.Reply { Status = 302, Location = "https://cdn.evil.com/x" })).GetAwaiter().GetResult().Outcome == MimoExchange.Outcome.Unavailable);
                Check(MimoExchange.Mint("pt=1", entry, hop => Task.FromResult(new MimoExchange.Reply { Status = 302, Location = "https://account.xiaomi.com/x" })).GetAwaiter().GetResult().Outcome == MimoExchange.Outcome.Unavailable);
                Check(MimoExchange.Mint("pt=1", entry, hop => Task.FromResult(new MimoExchange.Reply { Status = 302, Location = "" })).GetAwaiter().GetResult().Outcome == MimoExchange.Outcome.Unavailable);
                // 链停在登录域（passToken 失效的形态）→ rejected。
                MimoExchange.Result stuckAtLogin = MimoExchange.Mint("pt=1", entry, hop => {
                    if (hop.Url.Host == "platform.xiaomimimo.com") return Task.FromResult(new MimoExchange.Reply { Status = 401, Body = "{\"loginUrl\":\"https://account.xiaomi.com/login\"}" });
                    return Task.FromResult(new MimoExchange.Reply { Status = 200, Body = "<html>请登录</html>" });
                }).GetAwaiter().GetResult();
                Check(stuckAtLogin.Outcome == MimoExchange.Outcome.Rejected);
                // 传输异常 → unavailable，不外抛。
                MimoExchange.Result transport = MimoExchange.Mint("pt=1", entry, hop => Task.FromException<MimoExchange.Reply>(new HttpRequestException("reset"))).GetAwaiter().GetResult();
                Check(transport.Outcome == MimoExchange.Outcome.Unavailable);
            });
            // Cookies 库读取：真实 SQLite fixture（Microsoft.Data.Sqlite 建库），名称/长度断言，不含凭据值。
            test("MiMo desktop store reader classifies ok, encrypted, not-signed-in and incomplete stores", () => {
                string dir = Path.Combine(Path.GetTempPath(), "cum-mimo-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                string BuildCookies(Action<Action<string, string, string>> seed) {
                    string path = Path.Combine(dir, "Cookies-" + Guid.NewGuid().ToString("N"));
                    using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate }.ToString())) {
                        connection.Open();
                        using (var create = connection.CreateCommand()) { create.CommandText = "CREATE TABLE cookies (name TEXT, value TEXT, encrypted_value BLOB, host_key TEXT)"; create.ExecuteNonQuery(); }
                        seed((name, value, blob) => {
                            using (var insert = connection.CreateCommand()) {
                                insert.CommandText = "INSERT INTO cookies (name, value, encrypted_value, host_key) VALUES (@n, @v, @e, @h)";
                                insert.Parameters.AddWithValue("@n", name); insert.Parameters.AddWithValue("@v", (object)value ?? DBNull.Value);
                                insert.Parameters.AddWithValue("@e", blob != null ? (object)Encoding.UTF8.GetBytes(blob) : DBNull.Value);
                                insert.Parameters.AddWithValue("@h", MimoDesktop.AccountHost);
                                insert.ExecuteNonQuery();
                            }
                        });
                    }
                    return path;
                }
                try {
                    MimoDesktop.ReadResult ok = MimoDesktop.ReadStore(BuildCookies(put => { put("passToken", "pt-fixture", null); put("userId", "8601", null); put("cUserId", "8601", null); }));
                    Check(ok.Reason == MimoDesktop.ReadReason.Ok && ok.Cookie.PassToken == "pt-fixture" && ok.Cookie.UserId == "8601" && ok.Cookie.CUserId == "8601");
                    Check(MimoDesktop.SeedHeader(ok.Cookie) == "passToken=pt-fixture; userId=8601; cUserId=8601");
                    // 加密存储：value 空且只有 encrypted_value → Encrypted（不尝试解密）。
                    MimoDesktop.ReadResult encrypted = MimoDesktop.ReadStore(BuildCookies(put => { put("passToken", null, "sealed-blob"); put("userId", "8601", null); }));
                    Check(encrypted.Reason == MimoDesktop.ReadReason.Encrypted);
                    // 没登录：库里根本没有这两个名字。
                    MimoDesktop.ReadResult absent = MimoDesktop.ReadStore(BuildCookies(put => put("other", "x", null)));
                    Check(absent.Reason == MimoDesktop.ReadReason.NotSignedIn);
                    // 半个会话：缺 userId → Incomplete。
                    MimoDesktop.ReadResult incomplete = MimoDesktop.ReadStore(BuildCookies(put => put("passToken", "pt-fixture", null)));
                    Check(incomplete.Reason == MimoDesktop.ReadReason.Incomplete);
                    // 客户端未装：候选全不存在 → StoreAbsent。
                    Check(MimoDesktop.ReadAccount(new[] { Path.Combine(dir, "missing") }).Reason == MimoDesktop.ReadReason.StoreAbsent);
                    // 候选路径仅 macOS 非空（其他平台没有桌面客户端）：门控断言，避免非
                    // macOS 上把「返回空」误判成失败。
                    if (OperatingSystem.IsMacOS()) Check(MimoDesktop.StoreCandidates("/nonexistent-home").Length == 2);
                    // 生产入口：副本读取 + 用完即删（临时目录不残留副本）。
                    string store = BuildCookies(put => { put("passToken", "pt-fixture", null); put("userId", "8601", null); });
                    string tempDir = Path.Combine(dir, "tmp"); Directory.CreateDirectory(tempDir);
                    MimoDesktop.ReadResult copied = MimoDesktop.ReadAccount(new[] { Path.Combine(dir, "missing"), store }, tempDir);
                    Check(copied.Reason == MimoDesktop.ReadReason.Ok);
                    Check(Directory.GetFiles(tempDir).Length == 0 && File.Exists(store));   // 副本已删、原库未动
                } finally { try { Directory.Delete(dir, true); } catch { } }
            });
            // W1：桌面客户端已登录即算已配置（与 kimi/zcode 同口径：只看候选文件存在，
            // 绝不读取内容）。经 home 可注入重载用假 home 构造候选文件。
            test("SourceHealth counts the MiMo desktop client store as configured without reading it", () => {
                string dir = Path.Combine(Path.GetTempPath(), "cum-mimo-health-" + Guid.NewGuid().ToString("N"));
                string previous = Store.SwapData(dir);   // 隔离：HasSavedKey 只查数据目录里的引用文件
                try {
                    Directory.CreateDirectory(dir);
                    var config = new AppConfig { Enabled = new[] { "mimo" } };
                    Check(!SourceHealth.HasCredential("mimo", config, dir), "an empty home must read as unconfigured");
                    if (!OperatingSystem.IsMacOS()) return;   // 候选列表仅 macOS 非空
                    string legacy = Path.Combine(dir, "Library", "Application Support", "Xiaomi MiMo AI", "Partitions", "xiaomi-account", "Network", "Cookies");
                    Directory.CreateDirectory(Path.GetDirectoryName(legacy));
                    File.WriteAllText(legacy, "fixture");   // 只看存在，不读内容
                    Check(SourceHealth.HasCredential("mimo", config, dir), "the legacy partition store must count as configured");
                    File.Delete(legacy);
                    Check(!SourceHealth.HasCredential("mimo", config, dir), "deleting the store must read as unconfigured again");
                    string current = Path.Combine(dir, "Library", "Application Support", "Xiaomi MiMo", "Partitions", "xiaomi-account", "Cookies");
                    Directory.CreateDirectory(Path.GetDirectoryName(current));
                    File.WriteAllText(current, "fixture");
                    Check(SourceHealth.HasCredential("mimo", config, dir), "the current partition store must count as configured");
                    SourceHealth.HasCredential("mimo", config);   // 默认重载（真实 home）行为不变
                } finally {
                    Store.SwapData(previous);
                    try { Directory.Delete(dir, true); } catch { }
                }
            });
            // W2：逐候选换票。第一候选（旧版分区，passToken 已失效）换票被拒后继续
            // 第二个候选并用它的 Console Cookie；旧实现只读第一个候选，升级用户的
            // 自动续期永不生效。
            test("MiMo automatic renewal falls through to the next store candidate when the first mint is rejected", () => {
                string dir = Path.Combine(Path.GetTempPath(), "cum-mimo-cand-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try {
                    string stale = BuildMimoStore(dir, put => { put("passToken", "pt-stale", null); put("userId", "8601", null); });
                    string fresh = BuildMimoStore(dir, put => { put("passToken", "pt-fresh", null); put("userId", "8601", null); });
                    var sent = new List<string>();
                    Func<MimoExchange.Hop, System.Threading.CancellationToken, Task<MimoExchange.Reply>> transport = (hop, _) => {
                        string seed = hop.Headers["Cookie"];
                        sent.Add(seed);
                        if (hop.Url.Host == "account.xiaomi.com") {
                            // 种子是 account.xiaomi.com 的 host-only Cookie，登录域才带得上：
                            // 失效链停在登录页，有效链 302 跳回入口。
                            return seed.Contains("passToken=pt-stale")
                                ? Task.FromResult(new MimoExchange.Reply { Status = 200, Body = "<html>请登录</html>", Url = hop.Url })
                                : Task.FromResult(new MimoExchange.Reply { Status = 302, Location = "https://platform.xiaomimimo.com/api/v1/balance", Url = hop.Url });
                        }
                        // 入口：见过有效候选的登录跳之后就回答 code 0；此前一直 401 指路登录域
                        //（第一候选的链重试入口仍是 401 → Rejected，换下一个候选）。
                        return sent.Any(c => c.Contains("passToken=pt-fresh"))
                            ? Task.FromResult(new MimoExchange.Reply { Status = 200, Body = "{\"code\":0}", SetCookie = new[] { "api-platform_serviceToken=fresh-2; Path=/; Domain=.xiaomimimo.com" }, Url = hop.Url })
                            : Task.FromResult(new MimoExchange.Reply { Status = 401, Body = "{\"loginUrl\":\"https://account.xiaomi.com/login\"}", Url = hop.Url });
                    };
                    string cookie = ProviderService.MimoDesktopCookie(new[] { stale, fresh, Path.Combine(dir, "missing") }, transport, default(System.Threading.CancellationToken)).GetAwaiter().GetResult();
                    Check(cookie != null && cookie.Contains("api-platform_serviceToken=fresh-2"), "must use the second candidate's console cookie: " + cookie);
                    // 两条候选各自读过库、种子发往登录域，且按候选顺序进行（失效在前、有效在后）。
                    int staleLogin = sent.FindIndex(c => c.Contains("passToken=pt-stale"));
                    int freshLogin = sent.FindIndex(c => c.Contains("passToken=pt-fresh"));
                    Check(staleLogin >= 0 && freshLogin > staleLogin, "both candidates must be walked in order: " + String.Join(" | ", sent));
                } finally { try { Directory.Delete(dir, true); } catch { } }
            });
            // W4：换票失败不再完全静默。经 SourceHealth.RecordError 记一条（Desktop 的
            // ErrorSink 会落 errors.log），并按「结局变化才记」去重——每 tick 都写会刷屏。
            // 文案只带枚举结局，不含任何凭据值。
            test("MiMo mint failures are logged once per outcome change and never carry credentials", () => {
                string dir = Path.Combine(Path.GetTempPath(), "cum-mimo-diag-" + Guid.NewGuid().ToString("N"));
                string previous = Store.SwapData(dir);
                var previousSink = SourceHealth.ErrorSink;
                int sinkCalls = 0;
                SourceHealth.ErrorSink = (key, e) => { sinkCalls++; };
                SourceHealth.ClearErrors();
                try {
                    Directory.CreateDirectory(dir);
                    string store = BuildMimoStore(dir, put => { put("passToken", "pt-diag", null); put("userId", "8601", null); });
                    Func<MimoExchange.Reply, Func<MimoExchange.Hop, System.Threading.CancellationToken, Task<MimoExchange.Reply>>> fixedTransport = last => (hop, _) => Task.FromResult(last);
                    Func<MimoExchange.Reply, Task<string>> mint = last => ProviderService.MimoDesktopCookie(new[] { store }, fixedTransport(last), default(System.Threading.CancellationToken));
                    // 结局一：限流（429）→ 记一条。
                    Check(mint(new MimoExchange.Reply { Status = 429, Body = "", Url = MimoExchange.ConsoleEntry() }).GetAwaiter().GetResult() == null, "a rate-limited mint must fall back");
                    SourceHealth.ErrorRecord recorded = SourceHealth.LastErrorOf("mimo");
                    Check(recorded != null && recorded.Message.Contains("RateLimited"), "the rate limit must be recorded: " + (recorded == null ? "<none>" : recorded.Message));
                    Check(sinkCalls == 1, "one log line for the first failure");
                    Check(!recorded.Message.Contains("pt-diag"), "the diagnostic must not carry the passToken");
                    // 同一结局再来：去重，不重复落盘（LastErrorOf 返回同一条记录）。
                    mint(new MimoExchange.Reply { Status = 429, Body = "", Url = MimoExchange.ConsoleEntry() }).GetAwaiter().GetResult();
                    Check(sinkCalls == 1 && ReferenceEquals(SourceHealth.LastErrorOf("mimo"), recorded), "the same outcome must not be logged again");
                    // 结局变化（HTTP 200 + code 5 → 不可用）→ 再记一条。
                    mint(new MimoExchange.Reply { Status = 200, Body = "{\"code\":5}", Url = MimoExchange.ConsoleEntry() }).GetAwaiter().GetResult();
                    Check(sinkCalls == 2 && SourceHealth.LastErrorOf("mimo").Message.Contains("Unavailable"), "a changed outcome must be logged");
                    Check(!SourceHealth.LastErrorOf("mimo").Message.Contains("pt-diag"), "the diagnostic must not carry the passToken");
                    // 换票成功：只清零不记；此后同一结局的失败算新变化，日志恢复。
                    string ok = ProviderService.MimoDesktopCookie(new[] { store }, (hop, _) => Task.FromResult(new MimoExchange.Reply { Status = 200, Body = "{\"code\":0}", SetCookie = new[] { "api-platform_serviceToken=st-ok; Path=/" }, Url = hop.Url }), default(System.Threading.CancellationToken)).GetAwaiter().GetResult();
                    Check(ok != null && ok.Contains("api-platform_serviceToken=st-ok") && sinkCalls == 2, "a successful mint must not log");
                    mint(new MimoExchange.Reply { Status = 429, Body = "", Url = MimoExchange.ConsoleEntry() }).GetAwaiter().GetResult();
                    Check(sinkCalls == 3, "a failure after a success counts as a new change");
                } finally {
                    SourceHealth.ErrorSink = previousSink;   // 约定：自测缝用毕归原值
                    SourceHealth.ClearErrors();
                    Store.SwapData(previous);
                    try { Directory.Delete(dir, true); } catch { }
                }
            });
            // W4 补充：候选读不出来（客户端已装但加密存储/未登录）同样记一条去重诊断——
            // 这是自动续期静默失效里最难排障的形态；StoreAbsent（客户端未装）不记，
            // 那是「功能不可用」的正常形态，不该进错误日志。
            test("MiMo desktop read failures are logged once per reason, absent store stays silent", () => {
                string dir = Path.Combine(Path.GetTempPath(), "cum-mimo-readdiag-" + Guid.NewGuid().ToString("N"));
                string previous = Store.SwapData(dir);
                var previousSink = SourceHealth.ErrorSink;
                int sinkCalls = 0;
                SourceHealth.ErrorSink = (key, e) => { sinkCalls++; };
                SourceHealth.ClearErrors();
                try {
                    Directory.CreateDirectory(dir);
                    // passToken 只有 encrypted_value（value 为空）→ ReadReason.Encrypted。
                    string encrypted = BuildMimoStore(dir, put => { put("passToken", "", "enc-blob"); put("userId", "8601", null); });
                    int transportCalls = 0;
                    Func<MimoExchange.Hop, System.Threading.CancellationToken, Task<MimoExchange.Reply>> transport = (hop, _) => { transportCalls++; return Task.FromResult(new MimoExchange.Reply { Status = 200, Body = "{\"code\":0}", Url = hop.Url }); };
                    Check(ProviderService.MimoDesktopCookie(new[] { encrypted }, transport, default(System.Threading.CancellationToken)).GetAwaiter().GetResult() == null, "an encrypted store must fall back");
                    Check(transportCalls == 0, "an unreadable credential must never reach the exchange transport");
                    SourceHealth.ErrorRecord recorded = SourceHealth.LastErrorOf("mimo");
                    Check(recorded != null && recorded.Message.Contains("Encrypted") && sinkCalls == 1, "the encrypted store must be logged once: " + (recorded == null ? "<none>" : recorded.Message));
                    ProviderService.MimoDesktopCookie(new[] { encrypted }, transport, default(System.Threading.CancellationToken)).GetAwaiter().GetResult();
                    Check(sinkCalls == 1 && ReferenceEquals(SourceHealth.LastErrorOf("mimo"), recorded), "the same read reason must not be logged again");
                    Check(ProviderService.MimoDesktopCookie(new[] { Path.Combine(dir, "missing") }, transport, default(System.Threading.CancellationToken)).GetAwaiter().GetResult() == null && sinkCalls == 1, "an absent store is the normal unavailable path and must stay silent");
                } finally {
                    SourceHealth.ErrorSink = previousSink;   // 约定：自测缝用毕归原值
                    SourceHealth.ClearErrors();
                    Store.SwapData(previous);
                    try { Directory.Delete(dir, true); } catch { }
                }
            });
            // W5：换票链响应体限量读取（对齐 NativeCredentials 的 4MB 口径）。MimoExchangeHop
            // 走真实 HTTP 不自测，这里直接喂合成内容验证限量与截断语义。
            test("MiMo exchange hop reads the response body with the 4MB cap", () => {
                string small = "{\"code\":0}";
                Check(ProviderService.ReadCappedAsync(new StringContent(small), 4 * 1024 * 1024).GetAwaiter().GetResult() == small);
                Check(ProviderService.ReadCappedAsync(new StringContent(""), 4 * 1024 * 1024).GetAwaiter().GetResult() == "");
                string capped = ProviderService.ReadCappedAsync(new StringContent(new string('x', 5 * 1024 * 1024)), 4 * 1024 * 1024).GetAwaiter().GetResult();
                Check(capped.Length == 4 * 1024 * 1024, "an oversized body must be truncated to the cap, got " + capped.Length);
                // 上限边界：正好等于上限的内容不被截断。
                Check(ProviderService.ReadCappedAsync(new StringContent(new string('y', 4 * 1024 * 1024)), 4 * 1024 * 1024).GetAwaiter().GetResult().Length == 4 * 1024 * 1024);
            });
        }
    }
}
