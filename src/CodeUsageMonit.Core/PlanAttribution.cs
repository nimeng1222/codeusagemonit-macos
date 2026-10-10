using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodeUsageMonit {
    // 套餐归属（plan attribution）：一条本机用量记录归属哪个套餐平台的卡。ZCode 这类
    // 多套餐客户端的会话日志里每条记录带 provider_id（moonshot-kimi / xiaomi-mimo /
    // account:bigmodel-individual-coding-plan / new-provider …），按 provider 归属；
    // 旧数据（provider 为空）按模型名启发式；再不行落回 harness 自己的卡（kimi CLI 的
    // 日志 agentId=="kimi"，自然归 Kimi 卡）。
    public static class PlanAttribution {
        // 走归属口径的卡：这些概览卡聚合「所有客户端里走本套餐」的用量；其余平台
        // （codex/claude 等）保持 harness 自己的口径（含官方接口过滤）不变。
        public static bool IsPlanCard(string cardId) { return cardId == "kimi" || cardId == "mimo" || cardId == "zcode" || cardId == "stepfun"; }
        // 归属该条用量的套餐平台 id；无法识别时返回 agentId（harness 自己的卡）。
        // 识别顺序：provider 明确命中 → 直接归属；provider 不命中（如中转标记
        // no-request-id、占位 new-provider）落到模型启发式；再不行落 harness 自己的卡。
        public static string OwnerOf(string entryProvider, string model, string agentId) {
            string p = (entryProvider ?? "").Trim().ToLowerInvariant();
            if (p.Length > 0) {
                if (p.Contains("kimi") || p.Contains("moonshot")) return "kimi";
                if (p.Contains("mimo") || p.Contains("xiaomi")) return "mimo";
                if (p.Contains("bigmodel") || p.Contains("zai")) return "zcode";
                if (p.Contains("step")) return "stepfun";
                if (p.Contains("openai")) return "codex";
            }
            string m = (model ?? "").Trim().ToLowerInvariant();
            if (m.Length > 0) {
                if (m.StartsWith("kimi-") || m.Contains("moonshot")) return "kimi";
                if (m.StartsWith("glm") || m.Contains("zai")) return "zcode";
                if (m.Contains("mimo")) return "mimo";
                if (m.StartsWith("step-")) return "stepfun";
                if (m.Contains("claude")) return "claude";
                if (m.StartsWith("gpt-") || m.Contains("codex")) return "codex";
                if (m.Contains("grok") || m.StartsWith("xai")) return "grok";
                if (m.Contains("deepseek")) return "deepseek";
                if (m.Contains("gemini")) return "antigravity";
            }
            return agentId ?? "";
        }
        public static bool Owns(string cardId, string entryProvider, string model, string agentId) { return OwnerOf(entryProvider, model, agentId) == cardId; }
        // 满额推算：窗口已用 ÷ 已用比例（已用 percent 点数）。已用比例 <0.5% 时推算
        // 不可靠返回 null（界面显示「—」）；窗口内没有已用记录同样返回 null。剩余 100%
        //（已用 0%）由调用方先判 IsUsed，整行显示「未使用」而不是 0。
        public static double? FullQuota(double windowTokens, double usedPercent) {
            if (windowTokens <= 0 || usedPercent < 0.5) return null;
            return windowTokens / (usedPercent / 100.0);
        }

        // ── 归属口径的聚合（概览卡的「本机用量」小节与脚注共用）────────────
        public sealed class OwnedTotals {
            public Bucket Total = new Bucket();
            public readonly Dictionary<string, Bucket> Models = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            public double Tokens { get { return Total.Tokens(); } }
        }
        public sealed class OwnedDay { public double Tokens, Cost, TimedOutput, TimedSeconds; public bool Any; }
        // 跨 agent 的小时索引按 Owns(cardId) 过滤后加权累计（运行中的小时按 LogIndex.Weight
        // 全额计入到现在，与 RangeUsage / UsageDetails.Window 同一口径）。
        public static OwnedTotals Range(IEnumerable<KeyValuePair<string, LogIndex>> sources, string cardId, DateTime startUtc, DateTime endUtc) {
            var totals = new OwnedTotals(); DateTime nowUtc = DateTime.UtcNow;
            foreach (var source in sources ?? Enumerable.Empty<KeyValuePair<string, LogIndex>>()) {
                if (source.Value == null) continue;
                foreach (HourUsage hour in source.Value.Entries()) {
                    if (!Owns(cardId, hour.Provider, hour.Model, source.Key)) continue;
                    double w = LogIndex.Weight(hour.Hour, startUtc, endUtc, nowUtc); if (w <= 0) continue;
                    Add(totals, hour.Model, hour, w);
                }
            }
            return totals;
        }
        private static void Add(OwnedTotals totals, string model, HourUsage hour, double w) {
            Bucket total = totals.Total, part;
            total.I += hour.Input * w; total.C += hour.Cached * w; total.W += hour.CacheWrite * w; total.O += hour.Output * w; total.X += hour.Other * w;
            total.R += hour.Requests * w; total.D += hour.Cost * w; total.U += hour.Unpriced * w;
            total.TN += hour.TimedRequests * w; total.TO += hour.TimedOutput * w; total.TS += hour.TimedSeconds * w;
            if (!totals.Models.TryGetValue(model, out part)) totals.Models[model] = part = new Bucket();
            part.I += hour.Input * w; part.C += hour.Cached * w; part.W += hour.CacheWrite * w; part.O += hour.Output * w; part.X += hour.Other * w;
            part.R += hour.Requests * w; part.D += hour.Cost * w; part.U += hour.Unpriced * w;
            part.TN += hour.TimedRequests * w; part.TO += hour.TimedOutput * w; part.TS += hour.TimedSeconds * w;
        }
        // 退化路径：按日行（无小时索引的 agent）没有 provider 信息，用模型启发式 +
        // agentId；行没有模型明细时整行按 agentId 判定（空模型 → Owns 落 agentId）。
        public static OwnedDay OwnDay(string cardId, DayUsage row) {
            var result = new OwnedDay(); if (row == null) return result;
            if (row.Models != null && row.Models.Count > 0) {
                foreach (ModelUsage m in row.Models) {
                    if (!Owns(cardId, "", m.Model, row.Agent)) continue;
                    result.Tokens += m.Tokens; result.Cost += m.Cost; result.TimedOutput += m.TimedOutput; result.TimedSeconds += m.TimedSeconds; result.Any = true;
                }
                return result;
            }
            if (Owns(cardId, "", "", row.Agent)) {
                result.Tokens = row.Tokens; result.Cost = row.Cost; result.TimedOutput = row.TimedOutput; result.TimedSeconds = row.TimedSeconds;
                result.Any = row.Tokens > 0 || row.Requests > 0;
            }
            return result;
        }
        // 把一条按日行的归属部分累计进 OwnedTotals（模型行只有 Token/费用/计时，没有
        // I/C/W 细分，进 X 槽；Tokens() 求和口径不受影响）。各模型独立成行；行内没有
        // 模型明细、整体按 agentId 归属时进「（未标注模型）」。
        public static void AddDay(OwnedTotals totals, string cardId, DayUsage row) {
            if (row == null) return;
            if (row.Models != null && row.Models.Count > 0) {
                foreach (ModelUsage m in row.Models) {
                    if (!Owns(cardId, "", m.Model, row.Agent)) continue;
                    Bucket model;
                    if (!totals.Models.TryGetValue(m.Model, out model)) totals.Models[m.Model] = model = new Bucket();
                    totals.Total.X += m.Tokens; totals.Total.D += m.Cost; totals.Total.TO += m.TimedOutput; totals.Total.TS += m.TimedSeconds;
                    model.X += m.Tokens; model.D += m.Cost; model.TO += m.TimedOutput; model.TS += m.TimedSeconds;
                }
                return;
            }
            OwnedDay part = OwnDay(cardId, row); if (!part.Any) return;
            Bucket whole;
            if (!totals.Models.TryGetValue("", out whole)) totals.Models[""] = whole = new Bucket();
            totals.Total.X += part.Tokens; totals.Total.D += part.Cost; totals.Total.TO += part.TimedOutput; totals.Total.TS += part.TimedSeconds;
            whole.X += part.Tokens; whole.D += part.Cost;
        }
        // 归属口径的订阅窗口：与 UsageDetails.Window 同一窗口语义（小时索引覆盖窗口起点
        // 则逐小时精确，否则只计完整自然日的下界），但数据跨全部 agent 按 Owns 过滤。
        public static WindowUsage Window(IEnumerable<KeyValuePair<string, LogIndex>> sources, IEnumerable<DayUsage> rows, Quota quota, DateTime now, int previous, TimeZoneInfo zone, string cardId) {
            DateTime reset;
            if (quota == null || quota.WindowSeconds <= 0 || !DateTime.TryParse(quota.ResetUtc, null, DateTimeStyles.RoundtripKind, out reset)) return null;
            var result = new WindowUsage { EndUtc = reset.ToUniversalTime().AddSeconds(-quota.WindowSeconds * previous), StartUtc = reset.ToUniversalTime().AddSeconds(-quota.WindowSeconds * (previous + 1)) };
            DateTime nowUtc = now.ToUniversalTime();
            List<DayUsage> list = rows != null ? rows.ToList() : new List<DayUsage>();
            bool covered = sources != null && sources.Any(s => s.Value != null && s.Value.Covers(result.StartUtc));
            if (covered) {
                result.Exact = true;
                DateTime until = result.EndUtc < nowUtc ? result.EndUtc : nowUtc;
                double tokens = 0, cost = 0, unpriced = 0;
                foreach (var source in sources) {
                    if (source.Value == null) continue;
                    foreach (HourUsage hour in source.Value.Entries()) {
                        if (!Owns(cardId, hour.Provider, hour.Model, source.Key)) continue;
                        double w = LogIndex.Weight(hour.Hour, result.StartUtc, until, nowUtc); if (w <= 0) continue;
                        tokens += hour.Tokens * w; cost += hour.Cost * w; unpriced += hour.Unpriced * w;
                    }
                }
                result.Tokens = tokens; result.Cost = cost;
                result.HasCost = cost > 0 || (tokens > 0 && unpriced < tokens);
                result.CostComplete = unpriced <= 0;
                for (DateTime day = TimeZoneInfo.ConvertTimeFromUtc(result.StartUtc, zone).Date; UsageDetails.LocalMidnightUtc(day, zone) < until; day = day.AddDays(1)) {
                    string key = HistoryService.DayKey(day); DayUsage row = list.FirstOrDefault(d => d.Day == key); if (row != null) result.Days.Add(row);
                }
                return result;
            }
            // 按日下界：与 UsageDetails.Window 同一包含逻辑，行内再按模型归属。
            DateTime localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
            foreach (DayUsage row in list) {
                DateTime date;
                if (!DateTime.TryParseExact(row.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) continue;
                DateTime begin = UsageDetails.LocalMidnightUtc(date, zone), end = UsageDetails.LocalMidnightUtc(date.AddDays(1), zone);
                bool openToday = date.Date == localNow.Date && result.EndUtc >= nowUtc;
                if (!(begin >= result.StartUtc && begin < result.EndUtc && begin < nowUtc && (end <= result.EndUtc || openToday))) continue;
                OwnedDay part = OwnDay(cardId, row); if (!part.Any) continue;
                result.Days.Add(row); result.Tokens += part.Tokens; result.Cost += part.Cost;
                if (row.CostKnown) result.HasCost = true; else result.CostComplete = false;
            }
            return result;
        }
    }
}
