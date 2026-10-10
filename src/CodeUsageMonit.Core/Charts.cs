// Data classes behind the charts. The interactive chart itself is UI control assembly
// and lives in the Desktop project; ThirdParty.cs and the CLI need the plain numbers.
using System;
using System.Collections.Generic;

namespace CodeUsageMonit {
    // One bar of a usage chart: a day or an hour, optionally split into coloured parts
    // (providers in the overview, models on a provider page).
    public sealed class ChartBar {
        public DateTime Start; public bool Hourly, Current;
        public double Cost, Tokens, Requests, TimedOutput, TimedSeconds; public bool CostPartial;
        public List<ChartPart> Parts = new List<ChartPart>();
        public double? Speed { get { return OutputTiming.Speed(TimedOutput, TimedSeconds); } }
    }
    public sealed class ChartPart {
        public string Name = "", Color = "#FFFFFF"; public double Cost, Tokens, TimedOutput, TimedSeconds;
        public double? Speed { get { return OutputTiming.Speed(TimedOutput, TimedSeconds); } }
    }

    // GitHub 贡献图式的日历热力图数据（Phase 4.2 重做）：把小时索引按本地时区的日期归并，
    // 每格是具体的一天（列 = 周、行 = 星期）。纯函数——不读盘、不联网，自测直接构造索引验证。
    // 窗口取最近 Weeks 周（含当周；调用方传 UsageArchive.MergeAll / Devices.Attach 之后的索引即可）。
    public sealed class ActivityDay {
        public DateTime Date;                 // 本地日期（该时区当天 00:00）
        public double Cost, Tokens, Requests;
    }
    public sealed class ActivityHeat {
        public DateTime Start, Today;         // 窗口首日（周首）与本地今天，均为本地 00:00
        public List<ActivityDay> Days = new List<ActivityDay>();   // 恒 Weeks*7 天，按 week*7+row 排（row 0 = 周首），含零与未来日
        public const int Weeks = 12;
        // firstDay：周首（与 UI 的星期行序一致，如区域设置的周日 / 周一开始）。窗口从
        // 「今天所在周的首日 − (Weeks−1) 周」起，正好铺满 12 列。未来日子保留条目（值为 0），
        // 由 UI 决定置灰或不渲染。
        public static ActivityHeat Calendar(IEnumerable<LogIndex> indexes, DateTime nowUtc, TimeZoneInfo zone, DayOfWeek firstDay) {
            DateTime today = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date;
            DateTime start = today.AddDays(-(((int)today.DayOfWeek - (int)firstDay + 7) % 7) - 7 * (Weeks - 1));
            var heat = new ActivityHeat { Start = start, Today = today };
            for (int i = 0; i < 7 * Weeks; i++) heat.Days.Add(new ActivityDay { Date = start.AddDays(i) });
            if (indexes == null) return heat;
            DateTime startUtc = TimeZoneInfo.ConvertTimeToUtc(start, zone), endUtc = TimeZoneInfo.ConvertTimeToUtc(start.AddDays(7 * Weeks), zone);
            var byDate = new Dictionary<DateTime, ActivityDay>();
            foreach (ActivityDay day in heat.Days) byDate[day.Date] = day;
            foreach (LogIndex index in indexes) {
                if (index == null) continue;
                foreach (HourUsage hour in index.Entries()) {
                    // 与图表同一口径：正在进行的整点按已过去的时间计权（LogIndex.Weight）。
                    double w = LogIndex.Weight(hour.Hour, startUtc, endUtc, nowUtc);
                    if (w <= 0) continue;
                    ActivityDay day; if (!byDate.TryGetValue(TimeZoneInfo.ConvertTimeFromUtc(hour.Hour, zone).Date, out day)) continue;
                    day.Cost += hour.Cost * w; day.Tokens += hour.Tokens * w; day.Requests += hour.Requests * w;
                }
            }
            return heat;
        }
    }
}
