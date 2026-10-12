// StepFun Oasis-Token 滚动刷新（POST /passport/proto.api.passport.v1.PassportService/RefreshToken）。
// 请求形态与三家开源实现一致（steipete/CodexBar StepFunUsageFetcher.swift、yetone/magpie
// stepfun_plan.go、btsouth/ceiling）：整段 token 放 Cookie（Oasis-Token=<access>...<refresh>）、
// 空 JSON 体、oasis-appid/oasis-platform/oasis-webid 头与浏览器 UA；响应取 accessToken /
// refreshToken 的 {raw}（顶层，容忍包在 data 里）。本文件只做纯解析与编排，HTTP 由调用方
// 注入（与 StepFunChannel 的 post 缝同一形状），测试全部走 fixture。
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CodeUsageMonit {
    internal static class StepFunAuth {
        // Oasis-Token 是若干段 JWT 用 "…"（三个点）连接：第一段是 access，最后一段是 refresh。
        public static string AccessSegment(string token) {
            string[] parts = (token ?? "").Split(new[] { "..." }, StringSplitOptions.None);
            return parts.Length > 0 ? parts[0] : "";
        }
        public static string RefreshSegment(string token) {
            string[] parts = (token ?? "").Split(new[] { "..." }, StringSplitOptions.None);
            return parts.Length > 1 ? parts[parts.Length - 1] : "";
        }
        // 一段 JWT 的载荷声明（不验签，仅读取；垃圾段返回 null）。
        public sealed class Claims {
            public double? Exp;            // exp：Unix 秒
            public bool Activated;         // activated：匿名令牌为 false
            public bool HasActivated;      // 声明是否存在（区分 false 与缺失）
            public string OasisId;         // oasis_id：账户标识
        }
        public static Claims TokenClaims(string jwt) {
            object payload = J.Jwt(jwt);
            if (payload == null) return null;
            object activated = J.Get(payload, "activated");
            return new Claims { Exp = J.Num(payload, "exp"), Activated = activated is bool && (bool)activated, HasActivated = activated is bool, OasisId = J.Str(payload, "oasis_id") };
        }
        // 存量 token 的账户标识：各段里第一个非空 oasis_id（与 webid 派生同款逐段扫描）。
        public static string StoredOasisId(string token) {
            foreach (string part in (token ?? "").Split(new[] { "..." }, StringSplitOptions.None)) {
                Claims claims = TokenClaims(part);
                if (claims != null && claims.OasisId.Length > 0) return claims.OasisId;
            }
            return "";
        }
        // access 剩余寿命不足该阈值就先滚动刷新（30 分钟）。
        public const double ExpirySlackSeconds = 1800;
        // access 段即将过期（或已过期）。exp 读不出来时不强刷——避免拿不可判定的 token 烧 refresh。
        public static bool AccessNeedsRefresh(string token, DateTime nowUtc) {
            Claims claims = TokenClaims(AccessSegment(token));
            if (claims == null || !claims.Exp.HasValue) return false;
            return claims.Exp.Value - UnixSeconds(nowUtc) < ExpirySlackSeconds;
        }
        // refresh 段已死（缺失、payload 不可读、无 exp 或已过期）——直接判会话结束，不发请求。
        public static bool RefreshExpired(string token, DateTime nowUtc) {
            Claims claims = TokenClaims(RefreshSegment(token));
            return claims == null || !claims.Exp.HasValue || claims.Exp.Value <= UnixSeconds(nowUtc);
        }
        private static double UnixSeconds(DateTime utc) { return (double)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }

        // 一次刷新的结局：Ok = 拿到新 token（Token 已按 access...refresh 拼好）；否则 State 是
        // "expired"（会话已死：401 / 匿名令牌 / oasis_id 换人 / refresh 段过期）或 "error"
        //（服务端异常形态，旧 token 保留照常用）。
        public sealed class RefreshResult { public bool Ok; public string Token = "", State = ""; }
        // 刷新请求与 QueryStepPlanRateLimit 同款头形态，端点不同：/passport/…/RefreshToken。
        public const string RefreshPath = "/passport/proto.api.passport.v1.PassportService/RefreshToken";
        public static async Task<RefreshResult> RefreshAsync(string token, string webid, string appid, string host, Func<string, string, string, string, int, Task<object>> post, DateTime nowUtc) {
            if (RefreshExpired(token, nowUtc)) return new RefreshResult { State = "expired" };
            string oldRefresh = RefreshSegment(token), oldOasisId = StoredOasisId(token);
            object root;
            try { root = await post(host + RefreshPath, token, webid, appid, 18).ConfigureAwait(false); }
            catch (ProviderException e) { return new RefreshResult { State = e.State == "expired" ? "expired" : "error" }; }
            catch (Exception) { return new RefreshResult { State = "error" }; }
            // 连接层响应：{accessToken:{raw}, refreshToken:{raw}}；容忍包在 data 信封里。
            object envelope = J.Get(root, "data") ?? root;
            string access = J.Str(J.Get(envelope, "accessToken"), "raw");
            string refresh = J.Str(J.Get(envelope, "refreshToken"), "raw");
            if (access.Length == 0) return new RefreshResult { State = "error" };
            // 平台在 refresh 过期后会发匿名令牌（照 magpie）：activated=false 或 oasis_id
            // 与存量不一致都判会话已死，绝不把匿名 token 写回钥匙串顶掉真凭据。
            Claims claims = TokenClaims(access);
            if (claims == null) return new RefreshResult { State = "error" };
            if (claims.HasActivated && !claims.Activated) return new RefreshResult { State = "expired" };
            if (oldOasisId.Length > 0 && claims.OasisId.Length > 0 && claims.OasisId != oldOasisId) return new RefreshResult { State = "expired" };
            // refreshToken 缺省时保留旧 refresh 段（照 magpie：只在非空时替换），绝不缩水成裸 access。
            string nextRefresh = refresh.Length > 0 ? refresh : oldRefresh;
            return new RefreshResult { Ok = true, Token = nextRefresh.Length > 0 ? access + "..." + nextRefresh : access };
        }

        // 进程内刷新锁（按旧 token 分键，多账户互不阻塞）：同一 token 的并发 tick 只允许一次
        // 在途刷新，其余共用同一任务的结果。失败不删旧 token。
        private static readonly object RefreshGate = new object();
        private static readonly Dictionary<string, Task<string>> inflight = new Dictionary<string, Task<string>>(StringComparer.Ordinal);
        // 进程内派生缓存：旧 token 原文 → 新 token 原文，与 RefreshGate 同一把锁保护。
        // 为什么需要：env 凭据（persist=null）不落钥匙串，或钥匙串写回失败时，下一 tick
        // 重读到的仍是旧 token；没有这份缓存就会拿同一个旧 refresh 段重复调 RefreshToken
        // （重放），可能触发平台风控甚至吊销会话。命中后以派生 token 继续后续逻辑。
        private static readonly Dictionary<string, string> derived = new Dictionary<string, string>(StringComparer.Ordinal);
        // 缓存防爆上限。entry 只是「旧 token → 新 token」的映射，丢失的代价仅是退化为现状
        // （下一次多打一次刷新），因此整体 Clear 即可，不需要逐条淘汰或 LRU。
        private const int DerivedCacheMaxEntries = 128;
        // EnsureFresh：按需滚动（force 跳过剩余寿命预检，供 401/expired 兜底路径调用）。
        // persist 回调把新 token 写回钥匙串（写失败不影响本次读数）；返回值要么是直接可用
        // 的 token（未刷新 / 缓存命中的派生 token / 暂态失败时参与刷新的那个），要么是新
        // token。会话已死抛 ProviderException("expired")。
        public static Task<string> EnsureFreshAsync(string token, string webid, string appid, string host, bool force, Func<string, string, string, string, int, Task<object>> post, Action<string> persist) {
            // 先查派生缓存：调用方持任一历史旧值时直接解析到最新 token，预检/inflight 都
            // 基于派生后的 token，不再重放旧 refresh 段。
            string current = token;
            lock (RefreshGate) {
                string cached;
                if (derived.TryGetValue(token, out cached) && cached.Length > 0) current = cached;
            }
            if (!force && !AccessNeedsRefresh(current, DateTime.UtcNow)) return Task.FromResult(current);
            lock (RefreshGate) {
                // 登记 inflight 前复查派生缓存：上一个锁释放后若有并发刷新刚好完成，
                // 用最新值参与刷新，避免重放刚被轮换掉的旧 refresh 段。
                string rechecked;
                if (derived.TryGetValue(current, out rechecked) && rechecked.Length > 0) current = rechecked;
                Task<string> running;
                if (inflight.TryGetValue(current, out running)) {
                    // 异源构造：本方原始 token 与在途刷新的键不同（经派生缓存解析过来的
                    // 旧值）时，直接返回在途任务，本方 token→结果的映射不落缓存——长链
                    // 旧值下一 tick 仍要重放 refresh。挂续尾在成功时补记映射。不包装
                    // 任务本身：并发调用返回同一 Task 实例的观察点不变，去重的实质保证
                    // 是「只有一次真实刷新」。两处 ContinueWith 都显式指定线程池调度：默认
                    // 取 TaskScheduler.Current，未来若有自定义 scheduler 上的调用点会排错队。
                    if (token != current) {
                        running.ContinueWith(done => { if (done.Status == TaskStatus.RanToCompletion) Remember(token, done.Result); }, TaskScheduler.Default);
                    }
                    return running;
                }
                running = RunFreshAsync(token, current, webid, appid, host, post, persist);
                inflight[current] = running;
                // 挂接续尾清锁（不 await：调用方各自 await 返回的同一任务）。
                running.ContinueWith(_ => { lock (RefreshGate) { inflight.Remove(current); } }, TaskScheduler.Default);
                return running;
            }
        }
        // callerToken = 调用方传入的原始 token（可能与 current 不同：经派生缓存解析过）；
        // token = 实际参与刷新的 token（派生后的最新值）。
        private static async Task<string> RunFreshAsync(string callerToken, string token, string webid, string appid, string host, Func<string, string, string, string, int, Task<object>> post, Action<string> persist) {
            RefreshResult result = await RefreshAsync(token, webid, appid, host, post, DateTime.UtcNow).ConfigureAwait(false);
            if (result.Ok) {
                if (persist != null) { try { persist(result.Token); } catch { } }   // 写失败不拖垮本次已到手的读数
                // 刷新成功即落派生缓存：写 调用方原始 token→新 token 与（若有映射）
                // 中间 token→新 token，保证调用方持任一旧值都能直接命中最新值；persist
                // 成功也写（无害且统一，env 凭据与写回失败都靠它避免下一 tick 重放）。
                Remember(callerToken, result.Token);
                if (callerToken != token) Remember(token, result.Token);
                return result.Token;
            }
            if (result.State == "expired") throw new ProviderException(Parsers.StepFunExpiredMessage, "expired");
            return token;   // 暂态失败：返回参与刷新的 token（经缓存解析时是最新值，优于调用方的旧值）照常参与本次请求
        }
        private static void Remember(string oldToken, string newToken) {
            if (oldToken.Length == 0 || newToken.Length == 0 || oldToken == newToken) return;
            lock (RefreshGate) {
                if (derived.Count >= DerivedCacheMaxEntries) derived.Clear();
                derived[oldToken] = newToken;
            }
        }
    }
}
