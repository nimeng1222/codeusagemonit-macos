using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeUsageMonit {
    // 套餐归属（PlanAttribution）单测：provider/模型 → 套餐卡的映射表（真实 ZCode
    // provider_id 样本）、满额推算边界、跨 agent 的窗口与今日聚合（精确 + 按日下界）。
    internal static class PlanAttributionTests {
        internal static void Run(Action<string, Action> test) {
            test("Plan attribution routes each provider_id and model to the owning plan card", () => {
                // 真实 ZCode db.sqlite model_usage 样本（provider_id | model_id）。
                Require(PlanAttribution.OwnerOf("moonshot-kimi", "k3-256k", "zcode") == "kimi");
                Require(PlanAttribution.OwnerOf("xiaomi-mimo", "mimo-v2.6-pro", "zcode") == "mimo");
                Require(PlanAttribution.OwnerOf("account:bigmodel-individual-coding-plan", "GLM-5.3", "zcode") == "zcode");
                Require(PlanAttribution.OwnerOf("new-provider", "step-5-preview", "zcode") == "stepfun");
                // 大小写不敏感；provider 空时按模型启发式。
                Require(PlanAttribution.OwnerOf("Moonshot-Kimi", "K3-256K", "zcode") == "kimi");
                Require(PlanAttribution.OwnerOf("", "kimi-for-coding", "kimi") == "kimi");
                Require(PlanAttribution.OwnerOf(null, "whatever-model", "zcode") == "zcode");
                Require(PlanAttribution.OwnerOf(null, "glm-4.6", "zcode") == "zcode");
                Require(PlanAttribution.OwnerOf(null, "mimo-v2.6-pro", "zcode") == "mimo");
                Require(PlanAttribution.OwnerOf(null, "step-5-preview", "zcode") == "stepfun");
                Require(PlanAttribution.OwnerOf(null, "kimi-k2.5", "kimi") == "kimi");
                Require(PlanAttribution.OwnerOf(null, "claude-sonnet-4-5", "claude") == "claude");
                Require(PlanAttribution.OwnerOf(null, "gpt-5.3", "codex") == "codex");
                Require(PlanAttribution.OwnerOf(null, "grok-4", "grok") == "grok");
                Require(PlanAttribution.OwnerOf(null, "deepseek-chat", "deepseek") == "deepseek");
                Require(PlanAttribution.OwnerOf(null, "gemini-3.1-pro", "antigravity") == "antigravity");
                // 未识别的 provider（中转标记、new-provider）落到模型启发式，再落 harness 自己的卡。
                Require(PlanAttribution.OwnerOf("no-request-id", "claude-3-7-sonnet", "claude") == "claude");
                Require(PlanAttribution.OwnerOf("no-request-id", "kimi-k2", "claude") == "kimi");
                Require(PlanAttribution.OwnerOf("totally-unknown", "some-model", "codex") == "codex");
                Require(PlanAttribution.OwnerOf("myrelay", "gpt-5.3", "codex") == "codex");
                Require(PlanAttribution.OwnerOf("openai", "gpt-5.3", "codex") == "codex");
                // Owns：归属卡收得到、其他卡收不到。
                Require(PlanAttribution.Owns("kimi", "moonshot-kimi", "k3-256k", "zcode"));
                Require(!PlanAttribution.Owns("zcode", "moonshot-kimi", "k3-256k", "zcode"));
                Require(PlanAttribution.Owns("zcode", "account:bigmodel-individual-coding-plan", "GLM-5.3", "zcode"));
                Require(PlanAttribution.IsPlanCard("kimi") && PlanAttribution.IsPlanCard("mimo") && PlanAttribution.IsPlanCard("zcode") && PlanAttribution.IsPlanCard("stepfun"));
                Require(!PlanAttribution.IsPlanCard("codex") && !PlanAttribution.IsPlanCard("claude"));
            });
            test("Full-quota estimate divides window usage by the used share, with a reliability floor", () => {
                double? full = PlanAttribution.FullQuota(14.6e6, 27.25);
                Require(full.HasValue && Math.Abs(full.Value - 14.6e6 / 0.2725) < 1e-3);
                Require(!PlanAttribution.FullQuota(1000, 0.4).HasValue, "a used share below 0.5% is too small to estimate from");
                Require(!PlanAttribution.FullQuota(1000, 0).HasValue, "an unused window (remaining 100%) has no share to divide by");
                Require(!PlanAttribution.FullQuota(0, 40).HasValue, "no local usage inside the window cannot produce an estimate");
            });
            test("Owned windows aggregate attributed hours across agents exactly", () => {
                var zcode = new LogIndex(); zcode.CoveredFrom = "2026-09-01T00:00:00Z";
                zcode.Files.Add(new LogFile { Name = "zcode", Hours = {
                    { "2026093010|moonshot-kimi|k3-256k", new Bucket { I = 1000, R = 1 } },
                    { "2026093010|account:bigmodel-individual-coding-plan|GLM-5.3", new Bucket { I = 500, R = 1 } },
                    { "2026093004|moonshot-kimi|k3-256k", new Bucket { I = 9000, R = 1 } } } });
                var kimi = new LogIndex(); kimi.CoveredFrom = "2026-09-01T00:00:00Z";
                kimi.Files.Add(new LogFile { Name = "kimi", Hours = { { "2026093009||kimi-for-coding", new Bucket { I = 300, R = 1 } } } });
                var sources = new[] { new KeyValuePair<string, LogIndex>("zcode", zcode), new KeyValuePair<string, LogIndex>("kimi", kimi) };
                // 5 小时窗口 [09:00, 14:00)，现在 12:00：10:00 的两桶全额计入，04:00 与窗口外。
                var quota = new Quota { Label = "5 小时", Used = 40, WindowSeconds = 18000, ResetUtc = "2026-09-30T14:00:00Z" };
                DateTime now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
                WindowUsage owned = PlanAttribution.Window(sources, new DayUsage[0], quota, now, 0, TimeZoneInfo.Utc, "kimi");
                Require(owned != null && owned.Exact && Math.Abs(owned.Tokens - 1300) < 1e-9, "kimi window must own the zcode kimi-plan hour plus kimi CLI's own hour, got " + (owned == null ? "null" : owned.Tokens.ToString()));
                WindowUsage glm = PlanAttribution.Window(sources, new DayUsage[0], quota, now, 0, TimeZoneInfo.Utc, "zcode");
                Require(glm != null && glm.Exact && Math.Abs(glm.Tokens - 500) < 1e-9, "zcode window must only own its GLM hour, got " + (glm == null ? "null" : glm.Tokens.ToString()));
                // 今日（Range）同样跨 agent：kimi 卡 = k3-256k + kimi-for-coding；zcode 卡只有 GLM。
                DateTime start = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc), end = start.AddDays(1);
                PlanAttribution.OwnedTotals kimiToday = PlanAttribution.Range(sources, "kimi", start, end);
                Require(Math.Abs(kimiToday.Tokens - 10300) < 1e-9, "kimi today must sum both kimi-plan hours, got " + kimiToday.Tokens);
                Require(kimiToday.Models.Count == 2 && kimiToday.Models.ContainsKey("k3-256k") && kimiToday.Models.ContainsKey("kimi-for-coding"));
                Require(!kimiToday.Models.ContainsKey("GLM-5.3"), "the GLM model must not leak into the kimi card");
                PlanAttribution.OwnedTotals zcodeToday = PlanAttribution.Range(sources, "zcode", start, end);
                Require(Math.Abs(zcodeToday.Tokens - 500) < 1e-9 && zcodeToday.Models.Count == 1 && zcodeToday.Models.ContainsKey("GLM-5.3"));
            });
            test("Owned windows fall back to attributed whole days when no hourly index covers the window", () => {
                var quota = new Quota { Label = "每周", Used = 10, WindowSeconds = 604800, ResetUtc = DateTime.UtcNow.AddDays(3).ToString("o") };
                DayUsage row = new DayUsage { Day = HistoryService.DayKey(DateTime.Today), Agent = "zcode", Tokens = 3000, Cost = 0.3 };
                row.Models.Add(new ModelUsage { Model = "glm-4.6", Tokens = 2000, Cost = 0.2 });
                row.Models.Add(new ModelUsage { Model = "kimi-k2", Tokens = 700, Cost = 0.07 });
                row.Models.Add(new ModelUsage { Model = "kimi-tiny", Tokens = 100 });
                DayUsage other = new DayUsage { Day = HistoryService.DayKey(DateTime.Today), Agent = "cursor", Tokens = 5000, Cost = 5 };
                other.Models.Add(new ModelUsage { Model = "composer-1", Tokens = 5000, Cost = 5 });
                WindowUsage kimi = PlanAttribution.Window(new KeyValuePair<string, LogIndex>[0], new[] { row, other }, quota, DateTime.UtcNow, 0, TimeZoneInfo.Local, "kimi");
                Require(kimi != null && !kimi.Exact && Math.Abs(kimi.Tokens - 800) < 1e-9, "kimi daily fallback must own only its models, got " + (kimi == null ? "null" : kimi.Tokens.ToString()));
                WindowUsage zcode = PlanAttribution.Window(new KeyValuePair<string, LogIndex>[0], new[] { row }, quota, DateTime.UtcNow, 0, TimeZoneInfo.Local, "zcode");
                Require(zcode != null && Math.Abs(zcode.Tokens - 2000) < 1e-9, "zcode daily fallback must own only GLM, got " + (zcode == null ? "null" : zcode.Tokens.ToString()));
                Require(zcode.HasCost && Math.Abs(zcode.Cost - 0.2) < 1e-9);
                // 没有模型明细的按日行整行按 agentId 归属。
                DayUsage bare = new DayUsage { Day = HistoryService.DayKey(DateTime.Today), Agent = "kimi", Tokens = 400, Cost = 0.04 };
                WindowUsage whole = PlanAttribution.Window(new KeyValuePair<string, LogIndex>[0], new[] { bare }, quota, DateTime.UtcNow, 0, TimeZoneInfo.Local, "kimi");
                Require(whole != null && Math.Abs(whole.Tokens - 400) < 1e-9);
                WindowUsage foreign = PlanAttribution.Window(new KeyValuePair<string, LogIndex>[0], new[] { bare }, quota, DateTime.UtcNow, 0, TimeZoneInfo.Local, "zcode");
                Require(foreign != null && foreign.Tokens == 0);
                // 今日（Range）按日退化路径同样按模型归属。
                PlanAttribution.OwnedTotals totals = PlanAttribution.Range(new KeyValuePair<string, LogIndex>[0], "kimi", DateTime.Today, DateTime.Today.AddDays(1));
                PlanAttribution.AddDay(totals, "kimi", row); PlanAttribution.AddDay(totals, "kimi", other);
                Require(Math.Abs(totals.Tokens - 800) < 1e-9, "day rows must attribute per model, got " + totals.Tokens);
                Require(totals.Models.ContainsKey("kimi-k2") && totals.Models.ContainsKey("kimi-tiny") && !totals.Models.ContainsKey("composer-1"));
            });
        }
        private static void Require(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
    }
}
