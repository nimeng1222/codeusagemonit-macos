// 时间段选择器：UsageRange 纯逻辑 + Popup 快速预设、日期/整点双视图、
// 跟随当前时刻开关与自定义起止。日历与小时格用按钮网格。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using AvaColor = Avalonia.Media.Color;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Controls.Documents;

namespace CodeUsageMonit {
    // A usage period: a preset (today, this month, the whole 370-day horizon, last 24 h,
    // 7 / 14 / 30 days) or a custom start and end in local time with hour precision (the
    // local index is hourly).
    public sealed class UsageRange {
        public string Preset = "30d";
        public DateTime Start, End;
        public bool FollowNow = true;
        public void Resolve(DateTime now, out DateTime start, out DateTime end) {
            end = now;
            switch (Preset) {
                case "today": start = now.Date; break;
                case "month": start = new DateTime(now.Year, now.Month, 1); break;
                // 全部档：本机逐小时记录的完整上限（UsageArchive.HorizonDays = 370 天），
                // 与自定义日历最早可选日同一条下限（OldestSelectable）。
                case "total": start = now.Date.AddDays(-(UsageArchive.HorizonDays - 1)); break;
                case "1d": start = now.AddHours(-24); break;
                case "7d": start = now.Date.AddDays(-6); break;
                case "14d": start = now.Date.AddDays(-13); break;
                case "custom": start = Start; end = FollowNow ? now : End; break;
                default: start = now.Date.AddDays(-29); break;
            }
            if (end > now) end = now;
            if (start >= end) start = end.AddHours(-1);
        }
        public string Label() {
            switch (Preset) {
                case "today": return "当天";
                case "month": return "本月";
                case "total": return "全部";
                case "1d": return "近 24 小时";
                case "7d": return "近 7 天";
                case "14d": return "近 14 天";
                case "custom": return Start.ToString("M/d HH:mm") + " – " + (FollowNow ? "现在" : End.ToString(End.Date == Start.Date ? "HH:mm" : "M/d HH:mm"));
                default: return "近 30 天";
            }
        }
        public UsageRange Copy() { return new UsageRange { Preset = Preset, Start = Start, End = End, FollowNow = FollowNow }; }
    }

    public sealed partial class MonitorPanel {
        private readonly Dictionary<string, UsageRange> ranges = new Dictionary<string, UsageRange>();
        // 所有页面（hero 与紧凑概览、详情页、紧凑单平台页、第三方板）的新建时间段一律
        // 播种「当天」：UsageRange 的字段默认仍是 30d（类型默认不动，Core 自测回归），只是
        // 新建键的初值改为 today —— 每页首帧数据与选择器同口径。用户此后选中的任何档位
        // 原样保留；overview 的遗留非 hero 档位（旧会话遗留 30d 等）仍由 NormalizeOverviewRange
        // 归一化为当天。
        private UsageRange RangeFor(string id) {
            UsageRange r;
            if (!ranges.TryGetValue(id, out r)) { r = new UsageRange { Preset = "today" }; ranges[id] = r; }
            return r;
        }
        // Hourly detail is kept for about a month hot plus the archive projection
        // (UsageArchive.HorizonDays): custom ranges may reach a year back. The default
        // presets are untouched — only the custom calendar can go further.
        private static DateTime OldestSelectable { get { return DateTime.Today.AddDays(-(UsageArchive.HorizonDays - 1)); } }

        // 时间段选择（fix6 #4 重做）：头部行内嵌 Segmented 预设（当天 / 1天 / 7天 / 14天 /
        // 30天，点击即生效），末段「自定义…」才打开 Popup——自定义起止、日历与小时格能力
        // 保持不变。自定义区间无选中段，选择器左侧显示区间文本；compact 用更紧凑的标签
        // （今 / 1 / 7 / 14 / 30 / …）。
        // 分段用等分 star 列的 Grid（沿用 "segment" 样式）：完整面板的选择器在表头 star 列里
        // 随可用宽度伸缩、超宽时文本省略；UniformGrid 版按内容计量，窄窗口会把末段挤出卡片。
        private Control RangeSelector(string id, Action changed, bool compact) {
            UsageRange range = RangeFor(id);
            string[][] presets = compact
                ? new[] { new[] { "today", "今" }, new[] { "1d", "1" }, new[] { "7d", "7" }, new[] { "14d", "14" }, new[] { "30d", "30" }, new[] { "custom", "…" } }
                : new[] { new[] { "today", "当天" }, new[] { "1d", "1天" }, new[] { "7d", "7天" }, new[] { "14d", "14天" }, new[] { "30d", "30天" }, new[] { "custom", "自定义…" } };
            string[] tips = { "当天（本地自然日）", "近 24 小时", "近 7 天", "近 14 天", "近 30 天", "自定义起止（日历与整点）" };
            string group = "range" + Guid.NewGuid().ToString("N");
            var grid = new Grid();
            Control segmented = null;
            for (int i = 0; i < presets.Length; i++) {
                string code = presets[i][0], text = I18n.T(presets[i][1]);
                // 不设 MinWidth：选择器所在列按剩余空间均分，窄窗口下靠省略号收缩，
                // 任何列宽下都不会把末段或右侧按钮挤出卡片。
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var button = new RadioButton { GroupName = group, IsChecked = code == (range.Preset == "custom" ? "" : range.Preset) };
                Styled(button, "segment");
                button.Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
                AutomationProperties.SetName(button, text);
                ToolTip.SetTip(button, I18n.T(tips[i]));
                OnChecked(button, () => {
                    if (code != "custom") { ranges[id] = new UsageRange { Preset = code }; changed(); return; }
                    // 自定义段：打开既有 Popup。取消/点外关闭而区间仍是预设时，经 changed() 重建，
                    // 让误点的选中态回到真实预设（apply 成功时已是 custom，重建为无选中段）。
                    ShowRangePicker(segmented, RangeFor(id), r => { ranges[id] = r; changed(); }, () => { if (RangeFor(id).Preset != "custom") changed(); });
                });
                Grid.SetColumn(button, i);
                grid.Children.Add(button);
            }
            segmented = new Border { Background = Brush("#0DFFFFFF"), BorderBrush = Brush("#14FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = grid, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(segmented, I18n.T("选择统计时间段"));
            AutomationProperties.SetName(segmented, I18n.T("选择时间段"));
            var root = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (range.Preset == "custom") {
                var text = Label(range.Label(), compact ? 9.5 : 10.5, InkDim);
                text.MaxWidth = compact ? 96 : 130;
                ToolTip.SetTip(text, I18n.T("自定义时间段：{0}", range.Label()));
                root.Children.Add(text);
            }
            root.Children.Add(segmented);
            return root;
        }
        // overview hero 专用档位（D 期对标）：当天 / 本月 / 全部 / 自定义「…」四段，替换
        // hero 原有的 6 段预设条。分段样式与 RangeSelector 同一 "segment" 槽；供应商详情页
        // （UsageBlock）继续用 6 段选择器，不动。会话级沿用 ranges["overview"]，不落盘；
        // 旧会话遗留的其它预设（30d 等）按当天处理，保证任何时刻都有一段选中。
        // month：自本月 1 日零点起；total：UsageArchive.HorizonDays（370 天）上限，与自定义
        // 日历最早可选日同一条下限。
        // 归一化必须在 hero 数据计算之前执行（否则首帧按遗留预设算数、选择器却显示当天）：
        // HeroContent 开头与选择器构建都走这里，选择器里那次只是幂等兜底。
        private UsageRange NormalizeOverviewRange() {
            UsageRange range = RangeFor("overview");
            if (range.Preset != "today" && range.Preset != "month" && range.Preset != "total" && range.Preset != "custom") range.Preset = "today";
            return range;
        }
        private Control HeroRangeSelector(Action changed) {
            UsageRange range = NormalizeOverviewRange();
            string[][] presets = { new[] { "today", "当天" }, new[] { "month", "本月" }, new[] { "total", "全部" }, new[] { "custom", "…" } };
            string[] tips = { "当天（本地自然日）", "自本月 1 日零点起", "覆盖最近 " + UsageArchive.HorizonDays + " 天", "自定义起止（日历与整点）" };
            string group = "range" + Guid.NewGuid().ToString("N");
            var grid = new Grid();
            Control segmented = null;
            for (int i = 0; i < presets.Length; i++) {
                string code = presets[i][0], text = I18n.T(presets[i][1]);
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var button = new RadioButton { GroupName = group, IsChecked = code == (range.Preset == "custom" ? "" : range.Preset) };
                Styled(button, "segment");
                button.Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
                AutomationProperties.SetName(button, text);
                ToolTip.SetTip(button, I18n.T(tips[i]));
                OnChecked(button, () => {
                    if (code != "custom") { ranges["overview"] = new UsageRange { Preset = code }; changed(); return; }
                    // 自定义段：复用既有 Popup（起止、日历与整点能力不变）。
                    ShowRangePicker(segmented, RangeFor("overview"), r => { ranges["overview"] = r; changed(); }, () => { if (RangeFor("overview").Preset != "custom") changed(); });
                });
                Grid.SetColumn(button, i);
                grid.Children.Add(button);
            }
            segmented = new Border { Background = Brush("#0DFFFFFF"), BorderBrush = Brush("#14FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = grid, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(segmented, I18n.T("选择统计时间段"));
            AutomationProperties.SetName(segmented, I18n.T("选择时间段"));
            var root = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (range.Preset == "custom") {
                var text = Label(range.Label(), 10.5, InkDim);
                text.MaxWidth = 130;
                ToolTip.SetTip(text, I18n.T("自定义时间段：{0}", range.Label()));
                root.Children.Add(text);
            }
            root.Children.Add(segmented);
            return root;
        }

        private void ShowRangePicker(Control anchor, UsageRange current, Action<UsageRange> apply, Action closed = null) {
            var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.BottomEdgeAlignedLeft, IsLightDismissEnabled = true, VerticalOffset = 6 };
            if (closed != null) popup.Closed += delegate { closed(); };
            DateTime now = DateTime.Now, start, end; current.Resolve(now, out start, out end);
            // Editing state: whole hours, which endpoint the calendar edits, date or hour pane.
            start = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0);
            end = current.Preset == "custom" && !current.FollowNow ? current.End : new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
            bool follow = current.Preset == "custom" ? current.FollowNow : true;
            string editing = "start", pane = "date";
            DateTime month = new DateTime(start.Year, start.Month, 1);
            string preset = current.Preset;
            Action rebuild = null;
            Action<string> pick = code => {
                var r = new UsageRange { Preset = code, FollowNow = true };
                popup.IsOpen = false; apply(r);
            };
            rebuild = () => {
                var root = new Grid();
                root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) }); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) }); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition());
                // Quick presets apply at once.
                var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
                foreach (var option in new[] { new[] { "today", "当天" }, new[] { "1d", "1d" }, new[] { "7d", "7d" }, new[] { "14d", "14d" }, new[] { "30d", "30d" } }) {
                    string code = option[0]; bool active = preset == code;
                    var chipContent = Label(option[1], 11.5, active ? Brush("#0B1216") : Ink);
                    if (active) chipContent.FontWeight = FontWeight.SemiBold;
                    var chip = new Button { Content = chipContent, Padding = new Thickness(12, 5, 12, 6), Margin = new Thickness(0, 0, 8, 0), Background = active ? AccentBrush : Brush("#12FFFFFF"), BorderBrush = Brush(active ? "#5CC8E0" : "#26FFFFFF"), BorderThickness = new Thickness(1) };
                    AutomationProperties.SetName(chip, "时间段 " + option[1]);
                    string captured = code; chip.Click += delegate { pick(captured); };
                    chips.Children.Add(chip);
                }
                Grid.SetColumnSpan(chips, 3); root.Children.Add(chips);
                var left = new StackPanel(); Grid.SetRow(left, 1); root.Children.Add(left);
                left.Children.Add(Label("支持日期与时间 · 按小时统计", 10.5, InkFaint));
                left.Children.Add(TimeCard("开始时间", start, editing == "start", false, () => { editing = "start"; pane = "date"; month = new DateTime(start.Year, start.Month, 1); rebuild(); }, () => { editing = "start"; pane = "time"; rebuild(); }));
                left.Children.Add(TimeCard("结束时间", follow ? now : end, editing == "end" && !follow, follow, () => { if (follow) return; editing = "end"; pane = "date"; month = new DateTime(end.Year, end.Month, 1); rebuild(); }, () => { if (follow) return; editing = "end"; pane = "time"; rebuild(); }));
                var followBox = new CheckBox { IsChecked = follow, Margin = new Thickness(2, 10, 0, 0), Foreground = InkDim };
                var followLabel = Label("结束时间跟随当前时刻", 11.5, InkDim);
                followBox.Content = followLabel;
                followBox.IsCheckedChanged += delegate {
                    if (followBox.IsChecked == true) { follow = true; if (editing == "end") editing = "start"; }
                    else { follow = false; end = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0); }
                    rebuild();
                };
                left.Children.Add(followBox);
                var actions = new Grid { Margin = new Thickness(0, 14, 0, 0) };
                actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) }); actions.ColumnDefinitions.Add(new ColumnDefinition());
                var cancel = Styled(new Button { Content = "取消", HorizontalAlignment = HorizontalAlignment.Stretch }, "secondary");
                cancel.Click += delegate { popup.IsOpen = false; };
                var ok = Styled(new Button { Content = "确定", HorizontalAlignment = HorizontalAlignment.Stretch }, "primary");
                AutomationProperties.SetName(ok, "确定时间段");
                ok.Click += delegate {
                    DateTime a = start, b = follow ? now : end;
                    if (b <= a) { DateTime t = a; a = b; b = t; if (b == a) b = a.AddHours(1); }
                    popup.IsOpen = false; apply(new UsageRange { Preset = "custom", Start = a, End = b, FollowNow = follow });
                };
                actions.Children.Add(cancel); Grid.SetColumn(ok, 2); actions.Children.Add(ok);
                left.Children.Add(actions);
                // Right: month calendar, or the hour grid for the endpoint being edited.
                DateTime target = editing == "start" ? start : end;
                Control right = pane == "time" ? HourGrid(target, h => {
                    if (editing == "start") start = start.Date.AddHours(h); else end = end.Date.AddHours(h);
                    rebuild();
                }) : Calendar(month, start, follow ? now : end, editing, m => { month = m; rebuild(); }, day => {
                    if (editing == "start") { start = day.AddHours(start.Hour); if (!follow) { editing = "end"; if (end < start) end = start.AddHours(1); } }
                    else { end = day.AddHours(end.Hour); if (end < start) { DateTime t = start; start = end; end = t; } }
                    rebuild();
                });
                Grid.SetRow(right, 1); Grid.SetColumn(right, 2); root.Children.Add(right);
                var frame = new Border { Background = PopupSurface(), BorderBrush = Brush("#33FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 14), Child = root };
                popup.Child = frame;
            };
            rebuild();
            I18nUi.Localize(popup.Child as Visual);
            popup.IsOpen = true;
        }
        private Control TimeCard(string title, DateTime value, bool active, bool disabled, Action pickDate, Action pickTime) {
            var card = new Border { Background = Brush(active ? "#145CC8E0" : "#0AFFFFFF"), BorderBrush = Brush(active ? "#5CC8E0" : "#1FFFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 8, 8), Margin = new Thickness(0, 8, 0, 0), Opacity = disabled ? .5 : 1 };
            var stack = new StackPanel();
            stack.Children.Add(Label(title, 11, InkDim));
            var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var date = new Button { Content = Label(disabled ? "现在" : value.ToString("yyyy/MM/dd"), 14, Ink), Padding = new Thickness(2, 1, 2, 1), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = !disabled };
            date.Click += delegate { pickDate(); }; row.Children.Add(date);
            var cal = Styled(new Button(), "icon-button");
            cal.Width = 26; cal.Height = 26;
            cal.Content = Icons.GlyphBound(cal, Icons.Calendar, 12);
            cal.IsEnabled = !disabled; ToolTip.SetTip(cal, "选择日期");
            cal.Click += delegate { pickDate(); };
            Grid.SetColumn(cal, 1); row.Children.Add(cal);
            var time = new Button { Content = Label(disabled ? "" : value.ToString("HH:mm"), 14, Ink), Padding = new Thickness(2, 1, 2, 1), IsEnabled = !disabled };
            time.Click += delegate { pickTime(); }; Grid.SetColumn(time, 3); row.Children.Add(time);
            var clock = Styled(new Button(), "icon-button");
            clock.Width = 26; clock.Height = 26;
            clock.Content = Icons.GlyphBound(clock, Icons.Clock, 12);
            clock.IsEnabled = !disabled; ToolTip.SetTip(clock, "选择时刻（整点）");
            clock.Click += delegate { pickTime(); };
            Grid.SetColumn(clock, 4); row.Children.Add(clock);
            AutomationProperties.SetName(date, title + "日期"); AutomationProperties.SetName(time, title + "时刻");
            stack.Children.Add(row);
            card.Child = stack;
            card.PointerPressed += delegate(object sender, Avalonia.Input.PointerPressedEventArgs e) { if (!disabled) { pickDate(); e.Handled = true; } };
            return card;
        }
        private Control Calendar(DateTime month, DateTime start, DateTime end, string editing, Action<DateTime> changeMonth, Action<DateTime> pick) {
            var box = new Border { Background = Brush("#08FFFFFF"), BorderBrush = Brush("#14FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 8) };
            var stack = new StackPanel();
            var head = new Grid(); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var prev = Styled(new Button(), "icon-button");
            prev.Width = 26; prev.Height = 26;
            prev.Content = Icons.GlyphBound(prev, Icons.ChevronDown, 10);
            prev.RenderTransform = new RotateTransform(90);
            prev.IsEnabled = month > new DateTime(OldestSelectable.Year, OldestSelectable.Month, 1); ToolTip.SetTip(prev, "上个月");
            prev.Click += delegate { changeMonth(month.AddMonths(-1)); }; head.Children.Add(prev);
            var title = Label(month.ToString(I18n.T("yyyy年M月"), I18n.Culture), 13, Ink); title.FontWeight = FontWeight.SemiBold; title.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(title, 1); head.Children.Add(title);
            var next = Styled(new Button(), "icon-button");
            next.Width = 26; next.Height = 26;
            next.Content = Icons.GlyphBound(next, Icons.ChevronDown, 10);
            next.RenderTransform = new RotateTransform(-90);
            next.IsEnabled = month.AddMonths(1) <= DateTime.Today; ToolTip.SetTip(next, "下个月");
            next.Click += delegate { changeMonth(month.AddMonths(1)); }; Grid.SetColumn(next, 2); head.Children.Add(next);
            stack.Children.Add(head);
            var grid = new UniformGrid { Columns = 7, Margin = new Thickness(0, 6, 0, 0) };
            foreach (string d in I18n.English ? new[] { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" } : new[] { "日", "一", "二", "三", "四", "五", "六" }) { var l = Label(d, 10.5, InkFaint); l.HorizontalAlignment = HorizontalAlignment.Center; l.Margin = new Thickness(0, 2, 0, 6); grid.Children.Add(l); }
            DateTime first = month.AddDays(-(int)month.DayOfWeek);
            for (int i = 0; i < 42; i++) {
                DateTime day = first.AddDays(i); bool inMonth = day.Month == month.Month;
                bool selectable = day >= OldestSelectable && day <= DateTime.Today;
                bool isStart = day == start.Date, isEnd = day == end.Date, inside = day > start.Date && day < end.Date;
                var text = Label(day.Day.ToString(), 12, isStart || isEnd ? Brush("#0B1216") : !selectable ? Brush("#40FFFFFF") : inMonth ? Ink : InkFaint);
                text.HorizontalAlignment = HorizontalAlignment.Center; if (isStart || isEnd) text.FontWeight = FontWeight.SemiBold;
                var cell = new Button { Content = text, Height = 30, Padding = new Thickness(0), Margin = new Thickness(1.5), IsEnabled = selectable, Background = isStart || isEnd ? AccentBrush : inside ? Brush("#1F5CC8E0") : Brush("#00FFFFFF"), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(cell, day.ToString("M月d日 ddd", CultureInfo.GetCultureInfo("zh-CN")) + (selectable ? "" : " · 超出本机逐小时记录的范围"));
                if (day == DateTime.Today && !(isStart || isEnd)) { cell.BorderBrush = Brush("#595CC8E0"); cell.BorderThickness = new Thickness(1); }
                DateTime captured = day; cell.Click += delegate { pick(captured); };
                AutomationProperties.SetName(cell, day.ToString("yyyy-MM-dd"));
                grid.Children.Add(cell);
            }
            stack.Children.Add(grid);
            stack.Children.Add(Label(editing == "start" ? "点击日期设为开始" : "点击日期设为结束", 10, InkFaint));
            box.Child = stack;
            return box;
        }
        private Control HourGrid(DateTime value, Action<int> pick) {
            var box = new Border { Background = Brush("#08FFFFFF"), BorderBrush = Brush("#14FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 8) };
            var stack = new StackPanel();
            var title = Label(value.ToString(I18n.T("M月d日"), I18n.Culture) + " · 选择整点", 13, Ink); title.FontWeight = FontWeight.SemiBold; title.HorizontalAlignment = HorizontalAlignment.Center; title.Margin = new Thickness(0, 4, 0, 8); stack.Children.Add(title);
            var grid = new UniformGrid { Columns = 4 };
            DateTime now = DateTime.Now;
            for (int h = 0; h < 24; h++) {
                int captured = h; bool active = value.Hour == h; bool future = value.Date == now.Date && h > now.Hour;
                var text = Label(h.ToString("00") + ":00", 12, active ? Brush("#0B1216") : future ? Brush("#40FFFFFF") : Ink);
                text.HorizontalAlignment = HorizontalAlignment.Center;
                var cell = new Button { Content = text, Height = 30, Margin = new Thickness(2), Background = active ? AccentBrush : Brush("#0AFFFFFF"), IsEnabled = !future, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
                cell.Click += delegate { pick(captured); };
                grid.Children.Add(cell);
            }
            stack.Children.Add(grid);
            box.Child = stack;
            return box;
        }
    }
}
