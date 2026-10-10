// 完整面板（full）渲染。写法要点：
//   Inlines 一律 new Run(...)；Clipboard 走 Window.Clipboard（异步）；
//   SpinButton 的旋转动画用 DispatcherTimer 步进（重建时按系统时钟取相位）；
//   ToolTip/自动化名用 ToolTip.SetTip / AutomationProperties。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaColor = Avalonia.Media.Color;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace CodeUsageMonit {
    public sealed partial class MonitorPanel {
        private void UpdateScaleLabel() {
            var text = Named<TextBlock>(window, "ScaleText");
            if (text != null) text.Text = I18n.T((config.UiScale * 100).ToString("0") + "% · Ctrl+滚轮");
        }
        // Render() restores the previous offset; switching pages must land at the top, so
        // the reset is issued after it (scroll requests are applied in order on layout).
        private void SelectProvider(string id) { selected = id; Render(); bodyScroll.Offset = new Vector(0, 0); }
        private IEnumerable<string> EnabledIds() { return ProviderCatalog.All.Where(config.Enabled.Contains); }
        private void UpdateStatus() {
            var ids = EnabledIds().ToList();
            int enabled = ids.Count, connected = ids.Count(id => states.ContainsKey(id) && states[id].Status == "ready");
            string text; IBrush dot;
            if (demo) { text = "演示数据 · 不读取账户"; dot = AccentBrush; }
            else if (refreshing) { text = "正在刷新…"; dot = AccentBrush; }
            else if (statusNote.Length > 0) { text = statusNote; dot = WarnBrush; }
            else {
                text = connected + " / " + enabled + " 已连接 · 每 " + config.RefreshMinutes + " 分钟刷新" + (lastRefresh == DateTime.MinValue ? "" : I18n.English ? " · updated " + lastRefresh.ToString("HH:mm") : " · " + lastRefresh.ToString("HH:mm") + " 更新");
                dot = connected == enabled ? GoodBrush : WarnBrush;
                List<string> busy = config.FastInUse ? InUseIds() : new List<string>();
                // A count only, so the line keeps its length; the names are in the tooltip.
                if (busy.Count > 0) text += " · " + (busy.Count == 1 ? ProviderCatalog.Name(busy[0]) + " 使用中" : busy.Count + " 个平台使用中");
                // 自适应模式下显示的是有效间隔（burn rate 伸缩后），与实际刷新一致（Phase 2.2）。
                ToolTip.SetTip(status, busy.Count == 0 ? null : I18n.T("使用中（10 分钟内写过本机会话记录），按更快的间隔刷新：") + "\n" + String.Join("\n", busy.Select(id => ProviderCatalog.Name(id) + " · " + I18n.T("{0} 分钟", limits.EffectiveMinutes(config, id, true)))));
                if (Updates.Newer(updateState)) text += " · " + I18n.T("有新版本 v{0}", updateState.Latest);
            }
            status.Text = I18n.T(text); statusDot.Fill = dot;
        }
        private void Render() {
            if (quitting) return;
            UpdateStatus();
            if (IsCompact) { RenderCompact(); return; }
            if (EditingKey()) { renderDeferred = true; return; }
            renderDeferred = false;
            RenderTabs(); UpdateScaleLabel();
            double offset = bodyScroll.Offset.Y;
            body.Children.Clear();
            if (selected == "overview") {
                heroCard = Card(HeroContent()); body.Children.Add(heroCard);
                // D 期：趋势折线卡（hero 之后、热力图之前）。最近 HorizonDays 天一天都没有
                // 记录时 TrendChart 返回 null，整卡不出现。
                Control trend = TrendChart(); if (trend != null) body.Children.Add(trend);
                // Phase 4.2：活动热力图（日历版，每格一天）。12 周内一天数据都没有时
                // ActivityHeatmap 返回 null，整个模块不出现。
                Control heat = ActivityHeatmap(); if (heat != null) body.Children.Add(heat);
                // 平台卡按额度可获取性排序（与紧凑面板 Urgent 共用 ProviderOrder 口径）：
                // 有额度/余额读数的在前、按最紧剩余%升序；仅本机用量的 ready 平台随后；
                // loading/setup/error 沉底。只排序，不过滤、不隐藏。
                foreach (string id in Urgent(EnabledIds())) body.Children.Add(ProviderCard(states[id]));
                if (ThirdPartyVisible()) body.Children.Add(ThirdPartyCard());
            } else if (selected == ProviderCatalog.ThirdParty) RenderThirdParty();
            else if (selected == "sessions") body.Children.Add(Card(SessionsContent()));
            else RenderDetail(states[selected]);
            bodyScroll.Offset = new Vector(x: 0, y: offset);
            I18nUi.Localize(window);
        }
        // The page appears once a third-party endpoint is in use or has recent usage.
        private bool ThirdPartyVisible() { return config.ShowThirdParty && thirdParty.Endpoints.Count > 0; }

        // ── Provider strip ────────────────────────────────────────────────
        private void RenderTabs() {
            tabs.Children.Clear();
            List<string> ids = new[] { "overview" }.Concat(EnabledIds()).ToList();
            if (ThirdPartyVisible()) ids.Add(ProviderCatalog.ThirdParty);
            ids.Add("sessions");   // D 期：会话页签始终显示，无数据时页面给空态文案
            if (!ids.Contains(selected)) selected = "overview";
            // 页签按钮按内容自适应（WrapPanel 放不下自动折行）：UniformGrid 等分宽度会把
            // 长平台名挤成省略号，平台多时几乎不可读。
            foreach (string id in ids) {
                string captured = id; bool active = selected == id;
                var glyph = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
                Control icon = Icon(id, 16); icon.Opacity = active ? 1 : .78; glyph.Children.Add(icon);
                string dot = TabDot(id);
                if (dot != null) glyph.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Brush(dot), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -5, 0) });
                string tabName = TabName(id);
                var name = Label(tabName, 10.5, active ? Ink : InkDim);
                name.Margin = new Thickness(0, 5, 0, 0); name.HorizontalAlignment = HorizontalAlignment.Center;
                if (active) name.FontWeight = FontWeight.SemiBold;
                var content = new StackPanel(); content.Children.Add(glyph); content.Children.Add(name);
                var button = Styled(new Button { Content = content }, "tab-button");
                button.Background = Brush(active ? "#17FFFFFF" : "#00FFFFFF");
                button.Margin = new Thickness(2, 0, 2, 0);
                ToolTip.SetTip(button, id == "overview" ? I18n.T("全部平台") : id == "sessions" ? I18n.T("本机各工具的会话列表（最近 33 天）") : id == ProviderCatalog.ThirdParty ? I18n.T("经第三方接口的用量（本机日志）") : ProviderCatalog.Name(id) + " · " + StatusWord(states[id]));
                AutomationProperties.SetName(button, tabName);
                button.Click += delegate { SelectProvider(captured); };
                tabs.Children.Add(button);
            }
        }
        // 页签名："sessions" 不在 ProviderCatalog 里；短词直接双语，避免「会话」二字进
        // i18n 片段表后在英文模式替换其它含「会话」的长句。
        private static string TabName(string id) { return id == "sessions" ? (I18n.English ? "Sessions" : "会话") : ProviderCatalog.Name(id); }
        private string TabDot(string id) {
            if (!states.ContainsKey(id)) return null;
            string s = states[id].Status;
            return s == "error" || s == "expired" ? "#F2B36B" : s == "setup" ? "#7D838D" : null;
        }
        private static string StatusWord(ProviderState state) {
            switch (state.Status) { case "ready": return "已连接"; case "cached": return "显示上次读数"; case "loading": return "读取中"; case "setup": return "尚未连接"; default: return "需要处理"; }
        }

        // ── Overview ──────────────────────────────────────────────────────
        // "all" or one provider id: which platform the overview chart and totals show.
        private string heroFilter = "all";
        // Hero 构成分组：按工具（agent）/ 按模型（model）。会话内记住，不落盘；
        // 只在全平台视图生效（单平台只有模型一个有意义的维度）。
        private string heroGroup = "agent";
        // D 期构成列表的会话级状态：「显示全部 N 项」展开开关，与每行点开的 Token 细分
        // （键含维度前缀 m|/a|，仿 connectionDetails / expandedQuotas）。不落盘。
        private bool heroPartsAll;
        private readonly HashSet<string> heroPartDetails = new HashSet<string>();
        private Border heroCard;
        // Only the hero card is rebuilt when the chart filter changes, so the provider
        // cards (and their meters) below stay untouched.
        private Control HeroContent() {
            var stack = new StackPanel();
            List<string> enabled = EnabledIds().ToList();
            // 先归一化再算数：遗留的非 hero 预设（30d 等）在任何 RangeUsage 之前按当天处理，
            // 保证首帧数据与选择器显示一致（任务1）。
            UsageRange range = NormalizeOverviewRange();
            RangeData byAgent = RangeUsage(enabled, range, true, null);
            List<string> agents = byAgent.Parts.Where(p => p.Value.Tokens() > 0 || p.Value.D > 0).Select(p => p.Key).OrderBy(id => Array.IndexOf(ProviderCatalog.Ids, id)).ToList();
            if (heroFilter != "all" && !agents.Contains(heroFilter)) heroFilter = "all";
            bool single = heroFilter != "all";
            bool modelParts = single || heroGroup == "model";   // 构成维度：模型，还是工具（平台）
            RangeData data = single ? RangeUsage(new List<string> { heroFilter }, range, false, heroFilter) : modelParts ? RangeUsage(enabled, range, false, null) : byAgent;
            Action rerender = () => { if (heroCard != null) heroCard.Child = HeroContent(); };
            var head = new Grid();
            // 标题按内容占位（限宽），选择器独占 star 列随剩余宽度伸缩，窄窗口挤不出界。
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var heroTitle = Label((single ? ProviderCatalog.Name(heroFilter) : "全部平台") + " · Token 用量", 11, InkDim); heroTitle.MaxWidth = 190;
            head.Children.Add(heroTitle);
            var picker = HeroRangeSelector(rerender); picker.Margin = new Thickness(8, 0, 4, 0); Grid.SetColumn(picker, 1); head.Children.Add(picker);
            var copy = Styled(new Button(), "icon-button");
            copy.Content = Icons.GlyphBound(copy, Icons.Copy, 12);
            copy.Width = 26; copy.Height = 26;
            ToolTip.SetTip(copy, "复制用量概览"); copy.Margin = new Thickness(0, -5, -7, -5);
            copy.Click += delegate { var ignored = CopySummary(copy); };
            Grid.SetColumn(copy, 2); head.Children.Add(copy); stack.Children.Add(head);
            // 大数字 = 所选时间段的 Token 总数（随定向扫描重建 hero 而实时更新）；
            // API 等价费用与请求数降为右侧小字副行，数据不删。
            var figures = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            figures.ColumnDefinitions.Add(new ColumnDefinition()); figures.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var amount = new TextBlock { Foreground = Ink, VerticalAlignment = VerticalAlignment.Center };
            if (data.Total.Tokens() > 0) {
                amount.Inlines.Add(new Run(Compact(data.Total.Tokens())) { FontSize = 26, FontWeight = FontWeight.SemiBold });
                amount.Inlines.Add(new Run(" Token") { FontSize = 13, Foreground = InkDim });
            } else amount.Inlines.Add(new Run("—") { FontSize = 26, FontWeight = FontWeight.SemiBold });
            Tabular(amount); figures.Children.Add(amount);
            var side = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 3) };
            bool costKnown = data.Total.D > 0 || data.Total.Tokens() > data.Total.U;
            var cost = Label("API 等价费用 " + (costKnown ? Usd(data.Total.D) : "—") + (data.Total.R > 0 ? " · " + data.Total.R.ToString("N0", CultureInfo.InvariantCulture) + " 次请求" : ""), 10.5, InkFaint);
            cost.HorizontalAlignment = HorizontalAlignment.Right; Tabular(cost); ToolTip.SetTip(cost, PricingHint()); side.Children.Add(cost);
            Grid.SetColumn(side, 1); figures.Children.Add(side); stack.Children.Add(figures);
            string line;
            if (history.Days.Count == 0 && !data.FromLogs) line = scanning ? "正在读取本地记录…" : history.Error.Length > 0 ? history.Error : "暂无本地记录";
            else if (single) { var top = data.Parts.FirstOrDefault(); line = "今日 " + TodayMoney(history.Days.Where(d => d.Agent == heroFilter)) + (top.Key != null ? " · 主要模型 " + top.Key : ""); }
            else line = "今日 " + TodayMoney(history.Days.Where(d => enabled.Contains(d.Agent))) + " · " + agents.Count + " 个平台有记录" + (data.Total.U > 0 ? " · 部分模型无价目" : "");
            // 全部档在副行补口径说明（D 期）：本机逐小时记录只有 HorizonDays（370 天）。
            if (range.Preset == "total" && history.Days.Count > 0) line += " · " + I18n.T("覆盖最近 {0} 天", UsageArchive.HorizonDays);
            var sub = Label(line, 11, InkDim); sub.Margin = new Thickness(0, 2, 0, 0); stack.Children.Add(sub);
            // 构成分组切换（全平台 + 多于一项时显示）：作用于下方堆叠图与构成列表。
            if (!single && agents.Count > 1) {
                var groupRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
                Control toggle = Segmented(new[] { new[] { "agent", I18n.T("按工具") }, new[] { "model", I18n.T("按模型") } }, heroGroup, code => { heroGroup = code; rerender(); });
                ToolTip.SetTip(toggle, I18n.T("切换 hero 构成的分组维度：堆叠图与下面的列表一起切换"));
                AddRow(groupRow, Label("构成", 10.5, InkFaint), toggle);
                stack.Children.Add(groupRow);
            }
            if (data.Bars.Count > 0) stack.Children.Add(UsageChart("hero|" + heroFilter, data.Hourly ? "每小时" : "每日", data.Bars, 46, true, false, rerender));
            // 构成列表：各项 Token 为主、费用小字（与上方堆叠图同分组口径）。前 6 项直显，
            // 更多项经「显示全部 N 项」展开（D 期）；每行可点开 Token 构成细分（I/C/W/O +
            // 缓存命中率 + 费用，D 期）。
            List<KeyValuePair<string, Bucket>> parts = data.Parts.Where(p => p.Value.Tokens() > 0 || p.Value.D > 0).OrderByDescending(p => p.Value.Tokens()).ToList();
            if (parts.Count > 1) {
                var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
                var caption = Row(); AddRow(caption, Label(modelParts ? "模型" : "平台", 10.5, InkFaint), Label("Token · 费用", 10.5, InkFaint)); list.Children.Add(caption);
                double sum = Math.Max(1e-9, parts.Sum(p => p.Value.Tokens()));
                List<string> order = parts.Select(p => p.Key).ToList();
                string colorProvider = single ? heroFilter : null;
                IEnumerable<KeyValuePair<string, Bucket>> visible = heroPartsAll ? parts : parts.Take(6);
                foreach (var pair in visible) {
                    string partKey = (modelParts ? "m|" : "a|") + pair.Key;
                    bool open = heroPartDetails.Contains(partKey);
                    var body2 = new StackPanel();
                    var row2 = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                    string color = modelParts ? ModelColor(colorProvider, pair.Key, order) : ProviderCatalog.Color(pair.Key);
                    string price = pair.Value.U > 0 && pair.Value.D <= 0 ? "—" : Usd(pair.Value.D);
                    ToolTip.SetTip(row2, pair.Key + "\n" + Compact(pair.Value.Tokens()) + " Token · " + price + (pair.Value.U > 0 ? "\n没有这个模型的价目" : ""));
                    row2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row2.ColumnDefinitions.Add(new ColumnDefinition()); row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) }); row2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row2.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(2), Background = Brush(color), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                    var name = Label(pair.Key, 11, Ink); Grid.SetColumn(name, 1); row2.Children.Add(name);
                    double share = pair.Value.Tokens() / sum;
                    var track = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0), Background = Brush("#14FFFFFF") };
                    track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.001, share), GridUnitType.Star) }); track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.001, 1 - share), GridUnitType.Star) });
                    track.Children.Add(new Border { Background = Brush(color), CornerRadius = new CornerRadius(2) });
                    Grid.SetColumn(track, 2); row2.Children.Add(track);
                    var value = Label(Compact(pair.Value.Tokens()) + " Token · " + price, 10.5, InkDim); Tabular(value); Grid.SetColumn(value, 3); row2.Children.Add(value);
                    body2.Children.Add(row2);
                    if (open) body2.Children.Add(PartBreakdown(pair));
                    // 每行一个透明按钮（与 QuotaRow 同款负边距补偿），点开/收起该行的细分。
                    var rowButton = new Button { Content = body2, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(7, 2, 7, 3), Margin = new Thickness(-7, 4, -7, 0) };
                    ToolTip.SetTip(rowButton, I18n.T(open ? "点击收起细分" : "点击展开 Token 构成细分"));
                    AutomationProperties.SetName(rowButton, I18n.T("构成 {0}", pair.Key));
                    KeyValuePair<string, Bucket> captured = pair; string capturedKey = partKey;
                    rowButton.Click += delegate { if (!heroPartDetails.Add(capturedKey)) heroPartDetails.Remove(capturedKey); rerender(); };
                    list.Children.Add(rowButton);
                }
                if (parts.Count > 6) {
                    var more = Styled(new Button { Content = heroPartsAll ? I18n.T("收起") : I18n.T("显示全部 {0} 项", parts.Count), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(15, 6, 0, 0) }, "link-button");
                    ToolTip.SetTip(more, I18n.T("列表只显示前 6 项，其余按 Token 从多到少排列"));
                    more.Click += delegate { heroPartsAll = !heroPartsAll; rerender(); };
                    list.Children.Add(more);
                }
                stack.Children.Add(list);
            }
            if (agents.Count > 1) {
                // Filter chips double as the legend: click one to see only that platform.
                var chips = new WrapPanel { Margin = new Thickness(-3, 10, 0, -4) };
                foreach (string key in new[] { "all" }.Concat(agents)) {
                    string captured = key; bool active = heroFilter == key;
                    var content = new StackPanel { Orientation = Orientation.Horizontal };
                    if (key != "all") content.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(2), Background = Brush(ProviderCatalog.Color(key)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                    Bucket part = key == "all" ? null : byAgent.Parts.First(p => p.Key == key).Value;
                    // chip 主文案 = 所选时间段该平台的 Token 数（任务2）；API 等价费用降为 tooltip。
                    var text = Label(key == "all" ? "全部" : ProviderCatalog.Name(key) + " " + Compact(part.Tokens()), 10.5, active ? Ink : InkDim); Tabular(text);
                    content.Children.Add(text);
                    var chip = new Button { Content = content, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 4), Background = Brush(active ? "#1CFFFFFF" : "#00FFFFFF"), BorderBrush = Brush(active ? "#30FFFFFF" : "#00FFFFFF"), BorderThickness = new Thickness(1) };
                    ToolTip.SetTip(chip, key == "all" ? "显示全部平台（按平台堆叠）" : "只看 " + ProviderCatalog.Name(key) + "（按模型堆叠）\n" + I18n.T("API 等价费用") + " " + (part.U > 0 && part.D <= 0 ? "—" : Usd(part.D)));
                    AutomationProperties.SetName(chip, "图表：" + (key == "all" ? "全部" : ProviderCatalog.Name(key)));
                    chip.Click += delegate { heroFilter = captured; rerender(); };
                    chips.Children.Add(chip);
                }
                stack.Children.Add(chips);
            }
            var note = Label("本机会话日志（Cursor 为账户后台）× 官方 API 价目估算，不是订阅账单 ⓘ", 10.5, InkFaint);
            note.Margin = new Thickness(0, 10, 0, 0); ToolTip.SetTip(note, PricingHint()); stack.Children.Add(note);
            return stack;
        }
        // hero 构成行点开的细分（D 期）：费用 / 请求数 + Token 构成（Composition 的
        // I/C/W/O 与缓存命中率口径）+ 无价目提示。I/C/W/O 已在 RangeUsage().Parts 按小时
        // 累加，这里直接聚合展示；只有按日历史（无小时索引，如 demo）时细分退化为首行合计。
        private static Control PartBreakdown(KeyValuePair<string, Bucket> pair) {
            Bucket part = pair.Value;
            var detail = new Border { Background = Brush("#0AFFFFFF"), CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 7, 10, 8), Margin = new Thickness(0, 6, 0, 2) };
            var lines = new StackPanel();
            var figures = Label((part.U > 0 && part.D <= 0 ? "—" : Usd(part.D)) + (part.R > 0 ? " · " + part.R.ToString("N0", CultureInfo.InvariantCulture) + " 次请求" : ""), 10.5, InkDim);
            Tabular(figures); lines.Children.Add(figures);
            Control composition = Composition(part.I, part.O, part.C, part.W, "Token 构成");
            if (composition is StackPanel panel) panel.Margin = new Thickness(0, 6, 0, 0);
            if (composition != null) lines.Children.Add(composition);
            if (part.U > 0) lines.Children.Add(new TextBlock { Text = I18n.T("部分模型没有价目，未计入费用"), FontSize = 10, Foreground = InkFaint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            detail.Child = lines;
            return detail;
        }
        // ── Sessions 会话页（D 期最大单项）────────────────────────────────
        // 数据 = 每 agent 一份 SessionLedger（扫描器在既有去重之后记账，与小时桶同源，
        // 33 天窗口）。行 = 一次会话：工具 · 主要模型 · 最近活动 · 消息数(R) · Token ·
        // 费用 · 会话 ID · 费用占比；按最近活动倒序，默认 50 条，「加载更多」续看；
        // 点击行展开按模型明细（BarDetail 同款）。demo / 无数据给空态文案，不空白。
        private readonly HashSet<string> sessionDetails = new HashSet<string>();
        private int sessionLimit = 50;
        private sealed class SessionEntry { public string Agent; public SessionRow Row; public Bucket Total; public string Model; }
        private static string SessionsWord { get { return I18n.English ? "Sessions" : "会话"; } }
        private Control SessionsContent() {
            var stack = new StackPanel();
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = Label(SessionsWord, 13, Ink); title.FontWeight = FontWeight.SemiBold; head.Children.Add(title);
            var badge = Label(I18n.English ? "last " + LogReader.RetainDays + " days · local logs" : "最近 " + LogReader.RetainDays + " 天 · 本机日志", 10.5, InkFaint);
            ToolTip.SetTip(badge, I18n.T("会话按各工具的本机日志聚合：一次对话从第一条到最后一条记录为一个会话；Token 与费用的口径与「本机用量」一致。"));
            Grid.SetColumn(badge, 1); head.Children.Add(badge);
            stack.Children.Add(head);
            var entries = new List<SessionEntry>();
            foreach (var pair in sessionLedgers) foreach (SessionRow row in pair.Value.Sessions.Values) {
                if (row.Models.Count == 0) continue;
                Bucket total = row.Total();
                if (total.Tokens() <= 0 && total.R <= 0) continue;
                entries.Add(new SessionEntry { Agent = pair.Key, Row = row, Total = total, Model = row.Models.OrderByDescending(m => m.Value.D).ThenByDescending(m => m.Value.Tokens()).First().Key });
            }
            // 排序（任务2）：最近活动倒序为主，同刻并列时按 Token 从多到少（原为按费用）。
            entries = entries.OrderByDescending(e => e.Row.Last, StringComparer.Ordinal).ThenByDescending(e => e.Total.Tokens()).ToList();
            if (entries.Count == 0) {
                stack.Children.Add(Notice(scanning ? I18n.T("正在扫描本地历史…") : I18n.T("还没有会话记录。各工具的会话产生后会出现在这里；只统计本机日志，范围为最近 33 天。"), false));
                return stack;
            }
            // 占比与展开明细一律按 Token（任务2）：会话行没有费用/Token 切换 UI，默认口径
            // 从 byCost 改为 Token，变量保留以复用行构建的既有形状。
            double tokenSum = entries.Sum(e => e.Total.Tokens());
            bool byCost = false;
            double sum = Math.Max(1e-9, tokenSum);
            var caption = Row(); caption.Margin = new Thickness(0, 12, 0, 0);
            AddRow(caption, Label(I18n.English ? "Session · tool · model" : "会话 · 工具 · 模型", 10.5, InkFaint), Label(I18n.English ? "msgs · tokens · cost" : "消息 · Token · 费用", 10.5, InkFaint));
            stack.Children.Add(caption);
            int limit = Math.Max(1, sessionLimit);
            foreach (SessionEntry entry in entries.Take(limit)) stack.Children.Add(SessionRowControl(entry, byCost, sum));
            if (entries.Count > limit) {
                var more = Styled(new Button { Content = I18n.T("加载更多（还有 {0} 个会话）", entries.Count - limit), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) }, "link-button");
                ToolTip.SetTip(more, I18n.T("列表按最近活动从新到旧排列"));
                more.Click += delegate { sessionLimit = limit + 50; Render(); };
                stack.Children.Add(more);
            }
            var note = Label(I18n.T("覆盖最近 {0} 天", LogReader.RetainDays) + (I18n.English ? " · this computer only" : " · 只统计本机"), 10.5, InkFaint);
            note.Margin = new Thickness(0, 12, 0, 0); stack.Children.Add(note);
            return stack;
        }
        private static string SessionWhen(string iso) {
            DateTimeOffset at;
            if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at)) return I18n.T("时间未知");
            return at.LocalDateTime.ToString(I18n.T("M月d日 HH:mm"), I18n.Culture);
        }
        private Control SessionRowControl(SessionEntry entry, bool byCost, double sum) {
            string detailKey = entry.Agent + "|" + entry.Row.Id;
            bool open = sessionDetails != null && sessionDetails.Contains(detailKey);
            var body2 = new StackPanel();
            var row = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            string id8 = entry.Row.Id.Length > 8 ? entry.Row.Id.Substring(0, 8) : entry.Row.Id;
            string price = entry.Total.U > 0 && entry.Total.D <= 0 ? "—" : Usd(entry.Total.D);
            ToolTip.SetTip(row, entry.Row.Id + "\n" + ProviderCatalog.Name(entry.Agent) + " · " + entry.Model + "\n" + SessionWhen(entry.Row.First) + " → " + SessionWhen(entry.Row.Last) + "\n" + Math.Round(entry.Total.R).ToString("N0", CultureInfo.InvariantCulture) + I18n.T(" 次请求") + " · " + Compact(entry.Total.Tokens()) + " Token · " + price + (open ? "" : "\n" + I18n.T("点击展开会话明细")));
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Control icon = Icon(entry.Agent, 13); icon.Margin = new Thickness(0, 0, 8, 0); row.Children.Add(icon);
            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { FontSize = 11, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            name.Inlines.Add(new Run(ProviderCatalog.Name(entry.Agent)) { FontWeight = FontWeight.SemiBold });
            name.Inlines.Add(new Run("  " + id8) { FontSize = 10, Foreground = InkFaint });
            mid.Children.Add(name);
            var sub = Label(entry.Model + " · " + SessionWhen(entry.Row.Last), 10.5, InkDim); sub.Margin = new Thickness(0, 1, 8, 0);
            mid.Children.Add(sub);
            Grid.SetColumn(mid, 1); row.Children.Add(mid);
            double share = (byCost ? entry.Total.D : entry.Total.Tokens()) / sum;
            var track = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0), Background = Brush("#14FFFFFF") };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.001, share), GridUnitType.Star) }); track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.001, 1 - share), GridUnitType.Star) });
            track.Children.Add(new Border { Background = Brush(ProviderCatalog.Color(entry.Agent)), CornerRadius = new CornerRadius(2) });
            Grid.SetColumn(track, 2); row.Children.Add(track);
            var value = Label(Math.Round(entry.Total.R).ToString("N0", CultureInfo.InvariantCulture) + " · " + Compact(entry.Total.Tokens()) + " · " + price, 10.5, InkDim); Tabular(value);
            value.MinWidth = 130; value.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(value, 3); row.Children.Add(value);
            body2.Children.Add(row);
            if (open) body2.Children.Add(SessionDetail(entry, byCost));
            // 每行一个透明按钮（与 hero 构成行同款负边距补偿），点开/收起该会话的明细。
            var rowButton = new Button { Content = body2, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(7, 2, 7, 3), Margin = new Thickness(-7, 4, -7, 0) };
            ToolTip.SetTip(rowButton, I18n.T(open ? "点击收起会话明细" : "点击展开会话明细"));
            AutomationProperties.SetName(rowButton, I18n.T("构成 {0}", ProviderCatalog.Name(entry.Agent)) + " " + id8);
            string capturedKey = detailKey;
            rowButton.Click += delegate { if (!sessionDetails.Add(capturedKey)) sessionDetails.Remove(capturedKey); Render(); };
            return rowButton;
        }
        // 展开明细（BarDetail 模式）：本会话的每个模型一行（费用/Token 占比条），再加
        // 起止时间与完整会话 ID。
        private Control SessionDetail(SessionEntry entry, bool byCost) {
            var box = new Border { Background = Brush("#0CFFFFFF"), BorderBrush = Brush("#14FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 8), Margin = new Thickness(0, 8, 0, 0) };
            var lines = new StackPanel();
            var head = Row();
            AddRow(head, Label(I18n.English ? "Models in this session" : "本会话的模型", 10.5, InkDim), Label((I18n.English ? "first " : "首次 ") + SessionWhen(entry.Row.First) + (I18n.English ? " · last " : " · 最近 ") + SessionWhen(entry.Row.Last), 10.5, InkFaint));
            lines.Children.Add(head);
            var order = entry.Row.Models.Keys.ToList();
            double total = Math.Max(1e-9, byCost ? entry.Total.D : entry.Total.Tokens());
            int shown = 0;
            foreach (var pair in entry.Row.Models.OrderByDescending(p => byCost ? p.Value.D : p.Value.Tokens()).ThenByDescending(p => p.Value.Tokens())) {
                if (shown == 6 && entry.Row.Models.Count > 6) {
                    var rest = Label(I18n.T("另有 {0} 个模型", entry.Row.Models.Count - 6), 10.5, InkFaint); rest.Margin = new Thickness(15, 5, 0, 0);
                    lines.Children.Add(rest);
                    break;
                }
                shown++;
                Bucket part = pair.Value;
                var row = new Grid { Margin = new Thickness(0, 5, 4, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(2), Background = Brush(ModelColor(entry.Agent, pair.Key, order)), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
                var name = Label(pair.Key.Length == 0 ? I18n.T("（未标注模型）") : pair.Key, 10.5, InkDim); Grid.SetColumn(name, 1); row.Children.Add(name);
                double share = (byCost ? part.D : part.Tokens()) / total;
                var track = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0), Background = Brush("#14FFFFFF") };
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.001, share), GridUnitType.Star) }); track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.001, 1 - share), GridUnitType.Star) });
                track.Children.Add(new Border { Background = Brush(ModelColor(entry.Agent, pair.Key, order)), CornerRadius = new CornerRadius(2) });
                Grid.SetColumn(track, 2); row.Children.Add(track);
                string price = part.U > 0 && part.D <= 0 ? "—" : Usd(part.D);
                var figures = Label(price + " · " + Compact(part.Tokens()) + (part.R > 0 ? " · " + Math.Round(part.R).ToString("N0", CultureInfo.InvariantCulture) : ""), 10.5, Ink); Tabular(figures); Grid.SetColumn(figures, 3); row.Children.Add(figures);
                lines.Children.Add(row);
            }
            if (entry.Total.U > 0) lines.Children.Add(new TextBlock { Text = I18n.T("部分模型没有价目，未计入费用"), FontSize = 10, Foreground = InkFaint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            var id = Label((I18n.English ? "Session " : "会话 ") + entry.Row.Id, 10, InkFaint);
            id.Margin = new Thickness(0, 7, 0, 0);
            ToolTip.SetTip(id, entry.Row.Id);
            lines.Children.Add(id);
            box.Child = lines;
            return box;
        }
        private async Task CopySummary(Button source) {            // The clipboard can be held by another process; that must not become an error dialog.
            try { await SetClipboardText(window, SummaryText()); } catch { ToolTip.SetTip(source, I18n.T("剪贴板被占用，请重试")); return; }
            source.Content = Icons.Glyph(Icons.Check, 12, GoodBrush);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            timer.Tick += delegate { timer.Stop(); source.Content = Icons.GlyphBound(source, Icons.Copy, 12); source.ClearValue(TemplatedControl.ForegroundProperty); };
            timer.Start();
        }
        private string SummaryText() {
            var report = new StringBuilder("codeusagemonit · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\n");
            foreach (string id in EnabledIds()) {
                ProviderState state = states[id];
                report.AppendLine(ProviderCatalog.Name(id) + " · " + (state.Status == "ready" ? String.Join(" / ", state.Quotas.Select(q => q.Label + "剩余 " + q.Remaining.ToString("0.#") + "%").Concat(state.Balances.Select(b => b.Currency + " " + b.Amount.ToString("N2", CultureInfo.InvariantCulture)))) : state.Message));
            }
            List<DayUsage> days = HistoryService.Recent(history.Days.Where(d => config.Enabled.Contains(d.Agent)), 30);
            report.AppendLine("近 30 天 API 等价估算：" + Money(days) + " · " + Compact(days.Sum(d => d.Tokens)) + " Token（非订阅账单）");
            return report.ToString();
        }
        private Control ProviderCard(ProviderState state) {
            var stack = new StackPanel();
            // The whole header opens the provider page; its refresh button sits inside, on
            // the name's line, with the account right-aligned beneath it. Header 与 QuotaRow
            // 的横向 Padding/Margin 统一为 7/7，内容左缘严格对齐（此前一处 5 一处 6 差 1px）。
            var header = new Button { Content = Identity(state, false), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(7, 5, 7, 5), Margin = new Thickness(-7, -5, -7, -5) };
            ToolTip.SetTip(header, "查看 " + ProviderCatalog.Name(state.Id) + " 详情");
            AutomationProperties.SetName(header, ProviderCatalog.Name(state.Id) + " 详情");
            header.Click += delegate { SelectProvider(state.Id); };
            stack.Children.Add(header);
            AccountBody(stack, state, false);
            // 卡片脚注的 "30 天" 口径不随 history 窗口（370 天）变大。套餐归属卡
            // （kimi/mimo/zcode/stepfun）的今日 / 30 天按套餐归属合计：跨全部客户端的
            // 小时索引按 PlanAttribution.Owns 过滤（如 ZCode 里走 Kimi 套餐的用量计入
            // Kimi 卡），其余平台仍是该 harness 自己的按日历史。
            List<DayUsage> days = HistoryService.Recent(history.Days.Where(d => d.Agent == state.Id), 30);
            bool planCard = PlanAttribution.IsPlanCard(state.Id);
            double planToday = 0, planMonth = 0;
            if (planCard) {
                planMonth = PlanRange(state.Id, DateTime.Today.AddDays(-29), DateTime.Today.AddDays(1)).Tokens;
                planToday = PlanRange(state.Id, DateTime.Today, DateTime.Today.AddDays(1)).Tokens;
            }
            if (days.Count > 0 || planMonth > 0) {
                stack.Children.Add(new Border { Height = 1, Background = Hairline, Margin = new Thickness(0, 14, 0, 10) });
                var footer = Row();
                // Token 优先（任务2）：主行 = 今日 / 30 天 Token；API 等价费用降为 tooltip，
                // 版面不膨胀（参考 hero 大数字 + 费用小字的写法）。
                string todayKey = Today();
                double todayTokens = planCard ? planToday : days.Where(d => d.Day == todayKey).Sum(d => d.Tokens);
                var summary = Label("今日 " + Compact(todayTokens) + " Token · 30 天 " + Compact(planCard ? planMonth : days.Sum(d => d.Tokens)), 11, InkDim); Tabular(summary);
                summary.TextWrapping = TextWrapping.Wrap;   // 窄卡兜底：折行而不是把「30 天」截掉（费用口径在 tooltip）
                ToolTip.SetTip(summary, I18n.T("费用为 API 等价估算（非订阅账单）：今日 {0} · 30 天 {1}", TodayMoney(days), Money(days)));
                // 速度：非套餐卡沿用 harness 自己的近 7 天按日行/小时索引；套餐归属卡
                // （kimi/mimo/zcode/stepfun）改从归属数据取 —— 该 harness 自己的按日行
                // 常常是空的（如 kimi CLI 没跑过，用量都来自 ZCode 里的 Kimi 套餐），旧
                // 口径会一直不显示。这里不设 OutputTiming.Supported(id) 门槛：kimi/mimo/
                // stepfun 不在计时支持的 agent 表里，但归属合计里的计时样本本身就来自
                // 支持计时的客户端，有计时数据即可显示（MinTimedSeconds 门槛在
                // OutputTiming.Speed 内）。
                double? speed = planCard ? PlanSpeed(state.Id) : RecentSpeed(state.Id, days);
                // 速度列固定最小宽右对齐：与 QuotaRow 的右缘口径一致，卡片间不随文本漂移。
                var tokens = Label(speed.HasValue ? OutputTiming.Text(speed) : "", 11, InkFaint); Tabular(tokens);
                tokens.MinWidth = 150; tokens.TextAlignment = TextAlignment.Right;
                if (speed.HasValue) ToolTip.SetTip(tokens, I18n.T(ProviderCatalog.Name(state.Id) + " 近 7 天输出速度 " + OutputTiming.Text(speed) + "\n" + OutputTiming.Definition));
                AddRow(footer, summary, tokens); stack.Children.Add(footer);
            }
            // 卡内「本机/账户用量」小节（今日模型拆分 + 订阅窗口估算）：整块没有本机数据
            //（近 30 天无记录且无逐小时索引）或小节自身模型与窗口都为空时不渲染；
            // 小节自带分隔线，脚注/倒计时 chips 的衔接条件随之下移。套餐归属卡的归属
            // 数据可能全部来自其他客户端（如 ZCode 里的 Kimi 套餐），所以补 PlanHasData
            // 门槛；没有任何归属数据的卡（如从未用过的 MiMo/StepFun）仍整块不渲染。
            Control peek = days.Count > 0 || usageIndexes.ContainsKey(state.Id) || (planCard && PlanHasData(state.Id)) ? LocalUsagePeek(state) : null;
            if (peek != null) stack.Children.Add(peek);
            // D 期：多窗口重置倒计时并列（脚注区一行）。各窗口标签 + Countdown 横排、窄窗口
            // 折行；字段全在 state.Quotas，被前 3 条 QuotaRow 截掉的窗口在这里也可见。
            List<Quota> timed = state.Quotas.Where(q => q != null && (q.ResetUtc ?? "").Length > 0).ToList();
            if (timed.Count > 0) {
                if (days.Count == 0 && peek == null) stack.Children.Add(new Border { Height = 1, Background = Hairline, Margin = new Thickness(0, 14, 0, 10) });
                var clocks = new WrapPanel { Margin = new Thickness(0, days.Count > 0 || peek != null ? 7 : 0, 0, 0) };
                foreach (Quota quota in timed) {
                    string clock = quota.Label + " " + Countdown(quota.ResetUtc);
                    var chip = Label(clock, 10, InkFaint); Tabular(chip); chip.Margin = new Thickness(0, 0, 12, 2);
                    ToolTip.SetTip(chip, I18n.T("{0}：{1}", quota.Label, Countdown(quota.ResetUtc)));
                    AutomationProperties.SetName(chip, ProviderCatalog.Name(state.Id) + " " + clock);
                    clocks.Children.Add(chip);
                }
                stack.Children.Add(clocks);
            }
            return Card(stack);
        }

        // ── Card local-usage peek（概览平台卡内的小节）────────────────────
        // 今日按模型的紧凑拆分（前 3 行 + 「+N 个模型」）与订阅窗口估算（如
        // 「5 小时 已用 X · 满额约 Y tok」）。套餐归属卡（kimi/mimo/zcode/stepfun）的
        // 数据口径 = 跨全部 agent 的小时索引按 PlanAttribution.Owns(cardId) 过滤
        // （Kimi 卡显示所有客户端里走 Kimi 套餐的用量，ZCode 卡只含 GLM 模型），窗口走
        // PlanWindow（同一窗口语义、归属过滤）；其余平台保持单 harness 口径
        //（codex/claude 另加官方接口过滤；ResetUtc 解析不了的窗口直接返回 null，整条跳过）。
        // 模型与窗口都为空时返回 null，整块不渲染。
        private Control LocalUsagePeek(ProviderState state) {
            bool planCard = PlanAttribution.IsPlanCard(state.Id);
            List<DayUsage> allDays = history.Days.Where(d => d.Agent == state.Id).ToList();
            var windows = state.Quotas.Where(q => q != null && q.WindowSeconds > 0)
                .Select(q => new { Quota = q, Usage = planCard ? PlanWindow(state.Id, q, 0) : CurrentWindow(state.Id, allDays, q, 0) })
                .Where(w => w.Usage != null).ToList();
            List<KeyValuePair<string, Bucket>> models;
            if (planCard) {
                PlanAttribution.OwnedTotals today = PlanRange(state.Id, DateTime.Today, DateTime.Today.AddDays(1));
                models = today.Models.Where(p => p.Value.Tokens() > 0 || p.Value.D > 0)
                    .OrderByDescending(p => p.Value.Tokens()).ThenByDescending(p => p.Value.D).ToList();
            } else {
                RangeData today = RangeUsage(new List<string> { state.Id }, new UsageRange { Preset = "today" }, false, state.Id);
                models = today.Parts.Where(p => p.Value.Tokens() > 0 || p.Value.D > 0)
                    .OrderByDescending(p => p.Value.Tokens()).ThenByDescending(p => p.Value.D).ToList();
            }
            if (models.Count == 0 && windows.Count == 0) return null;
            WindowUsage head = windows.Count > 0 ? windows[0].Usage : null;
            var stack = new StackPanel();
            stack.Children.Add(new Border { Height = 1, Background = Hairline, Margin = new Thickness(0, 12, 0, 8) });
            var head0 = Row();
            var title = Label(ProviderCatalog.UsageTitle(state.Id), 11.5, Ink); title.FontWeight = FontWeight.SemiBold;
            // 右侧徽标沿用详情页额度窗口的口径（按小时精确 / 完整自然日下界），tooltip 即 WindowHint。
            if (head != null) {
                var badge = Label(head.Exact ? "按小时精确" : "只计完整自然日 · 下界", 10, InkFaint);
                ToolTip.SetTip(badge, WindowHintFor(planCard, head));
                AddRow(head0, title, badge);
            } else head0.Children.Add(title);
            stack.Children.Add(head0);
            // 今日按模型：模型名原样显示（k3-256k、GLM-5.3 …）；占比分母是当日归属模型
            // 的总量，超出前 3 的合并成「+N 个模型」。
            if (models.Count > 0) {
                double sum = Math.Max(1e-9, models.Sum(p => p.Value.Tokens()));
                List<string> order = models.Select(p => p.Key).ToList();
                foreach (var pair in models.Take(3)) stack.Children.Add(LocalModelRow(state.Id, pair.Key, pair.Value, sum, order));
                if (models.Count > 3) {
                    var more = Label(I18n.T("+{0} 个模型", models.Count - 3), 10, InkFaint);
                    more.Margin = new Thickness(13, 4, 0, 0);
                    stack.Children.Add(more);
                }
            }
            // 订阅窗口估算：全部 WindowSeconds>0 的窗口逐窗口一行（此前 join 成一行，
            // 两三个窗口就把「满额约」数值挤出卡片被 Ellipsis 截断）。套餐归属卡升级为
            // 满额推算（已用 + 满额约），其余平台保持既有「≥ 下界」口径；tooltip 逐行给
            // 本窗口的口径。首行沿用与上文的 7px 间距，后续行收紧；Wrap 兜底极窄卡。
            if (windows.Count > 0) {
                for (int i = 0; i < windows.Count; i++) {
                    var w = windows[i];
                    var line = Label(planCard ? WindowCell(w.Quota, w.Usage) : w.Quota.Label + " " + WindowTokens(w.Usage), 10.5, InkDim);
                    Tabular(line); line.Margin = new Thickness(0, i == 0 ? 7 : 3, 0, 0);
                    line.TextWrapping = TextWrapping.Wrap;
                    ToolTip.SetTip(line, planCard ? WindowCellTip(w.Quota, w.Usage) : w.Quota.Label + "\n" + WindowHint(w.Usage));
                    stack.Children.Add(line);
                }
            }
            return stack;
        }
        // 卡片版模型行：详情页模型行（UsageBlock）的紧凑化 —— 无费用/速度列、更小字号。
        private Control LocalModelRow(string id, string model, Bucket part, double sum, List<string> order) {
            var line = new Grid { Margin = new Thickness(0, 5, 0, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(2), Background = Brush(ModelColor(id, model, order)), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            // 模型名列限宽：长名（如「Xiaomi Token Plan/mimo-k2.6-pro」）在此收成省略号，
            // 不挤压占比条与右列数值；全名在 tooltip。
            var name = Label(model.Length == 0 ? I18n.T("（未标注模型）") : model, 10.5, InkDim);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(name, model.Length == 0 ? I18n.T("（未标注模型）") : model);
            Grid.SetColumn(name, 1); line.Children.Add(name);
            double share = part.Tokens() / sum;
            var track = new Grid { Height = 3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0), Background = Brush("#14FFFFFF") };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.001, share), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.001, 1 - share), GridUnitType.Star) });
            track.Children.Add(new Border { Background = Brush(ModelColor(id, model, order)), CornerRadius = new CornerRadius(2) });
            Grid.SetColumn(track, 2); line.Children.Add(track);
            var tokens = Label(Compact(part.Tokens()), 10, Ink); Tabular(tokens);
            Grid.SetColumn(tokens, 3); line.Children.Add(tokens);
            return line;
        }

        // ── 套餐归属聚合（概览卡「本机用量」小节与脚注共用）────────────────
        // 跨全部 agent 的小时索引按 PlanAttribution.Owns(cardId) 过滤；无小时索引的
        // agent 用按日历史的模型明细退化归属（provider 信息在按日行上没有，走模型
        // 启发式 + agentId）。startLocal/endLocal 是本地自然日边界（[start, end)）。
        private PlanAttribution.OwnedTotals PlanRange(string cardId, DateTime startLocal, DateTime endLocal) {
            DateTime startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, TimeZoneInfo.Local), endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, TimeZoneInfo.Local);
            PlanAttribution.OwnedTotals totals = PlanAttribution.Range(usageIndexes.ToList(), cardId, startUtc, endUtc);
            foreach (DayUsage row in history.Days) {
                if (usageIndexes.ContainsKey(row.Agent)) continue;
                DateTime day;
                if (!DateTime.TryParseExact(row.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) continue;
                if (day < startLocal.Date || day >= endLocal) continue;
                PlanAttribution.AddDay(totals, cardId, row);
            }
            return totals;
        }
        // 归属口径的订阅窗口（满额推算的数据侧），窗口语义与 CurrentWindow 相同
        //（小时索引覆盖窗口起点则逐小时精确，否则完整自然日下界）。
        private WindowUsage PlanWindow(string cardId, Quota quota, int previous) {
            return PlanAttribution.Window(usageIndexes.ToList(), history.Days, quota, DateTime.UtcNow, previous, TimeZoneInfo.Local, cardId);
        }
        // 是否有任何归属到该套餐的本机数据（任意 agent 的小时桶或按日行）。
        private bool PlanHasData(string cardId) {
            foreach (var pair in usageIndexes) {
                if (pair.Value == null) continue;
                foreach (HourUsage hour in pair.Value.Entries()) if (PlanAttribution.Owns(cardId, hour.Provider, hour.Model, pair.Key)) return true;
            }
            foreach (DayUsage row in history.Days) if (PlanAttribution.OwnDay(cardId, row).Any) return true;
            return false;
        }
        // 满额推算的窗口行：「5 小时 已用 14.6M · 满额约 53.6M tok」。剩余 100%（完全
        // 没用）→「未使用」而不是 0；已用比例 <0.5%（推算不可靠）或窗口内没有本机
        // 记录 → 满额显示「—」。
        private static string WindowCell(Quota quota, WindowUsage usage) {
            if (quota.Used <= 0) return quota.Label + " " + I18n.T("未使用");
            double? full = PlanAttribution.FullQuota(usage == null ? 0 : usage.Tokens, quota.Used);
            return quota.Label + " " + I18n.T("已用 {0}", WindowTokens(usage)) + " · " + (full.HasValue ? I18n.T("满额约 {0} tok", Compact(full.Value)) : I18n.T("满额约 —"));
        }
        private static string WindowCellTip(Quota quota, WindowUsage usage) {
            double? full = PlanAttribution.FullQuota(usage == null ? 0 : usage.Tokens, quota.Used);
            string estimate = quota.Used <= 0 ? I18n.T("账户剩余 100%：还没有已用量，无法推算满额。")
                : full.HasValue ? I18n.T("满额 ≈ 窗口已用 ÷ 已用比例（已用 {0}%）。", quota.Used.ToString("0.#", CultureInfo.InvariantCulture))
                : I18n.T("已用比例过低（<0.5%）或窗口内没有本机记录，满额推算不可靠，显示为 —。");
            return quota.Label + "\n" + WindowHintFor(true, usage) + "\n" + estimate;
        }
        // 窗口提示：套餐归属卡补一句口径说明（跨客户端归属合计）。
        private static string WindowHintFor(bool planCard, WindowUsage usage) {
            string hint = WindowHint(usage);
            return planCard ? hint + "\n" + I18n.T("口径：所有客户端里归属于本套餐的用量（跨工具合计）。") : hint;
        }

        // ── Account section (shared by overview cards and the detail page) ──
        private Control Identity(ProviderState state, bool detail) {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(detail ? 26 : 24) }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Control icon = Icon(state.Id, detail ? 22 : 18); icon.Margin = new Thickness(0, 0, 10, 0); icon.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRowSpan(icon, 2); grid.Children.Add(icon);
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var name = Label(ProviderCatalog.Name(state.Id), detail ? 16 : 14, Ink); name.FontWeight = FontWeight.SemiBold; title.Children.Add(name);
            if (!String.IsNullOrEmpty(state.Plan)) title.Children.Add(Pill(state.Plan));
            // D 期：多账户平台在标题区并列一枚「N 个账户」胶囊（与认证页状态胶囊同文案口径）。
            if (MultipleAccounts(state.Id)) title.Children.Add(Pill(I18n.T("{0} 个账户", Accounts.List(config, state.Id).Count)));
            Grid.SetColumn(title, 1); grid.Children.Add(title);
            if (!ProviderCatalog.LocalOnly(state.Id)) {
                // Phase 5：多账户时刷新按钮左侧多一枚「活动账户」小切换片，点击循环切换
                // 并只定向刷新该平台；单账户不占位。
                // 右对齐：第 3 列（Auto）由「账户名（row1）与刷新按钮（row0）的较大者」撑宽，
                // 刷新按钮必须贴住右缘，否则长账户名会把按钮推离卡片右缘。
                var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
                Control chip = AccountSwitchChip(state.Id);
                if (chip != null) { chip.Margin = new Thickness(0, -1, 6, -1); actions.Children.Add(chip); }
                Button refresh = ProviderRefreshButton(state.Id, 26); refresh.Margin = new Thickness(0, -1, -2, -1);
                actions.Children.Add(refresh);
                Grid.SetColumn(actions, 2); grid.Children.Add(actions);
            }
            // row1 两列不再叠加（任务3）：更新时间在 star 列左侧（放不下省略号截断），账户名
            // 独占第 3 列（Auto，MaxWidth=190 + tooltip）——此前 owner 以 ColumnSpan=2 叠在
            // fresh 同一格上，账户名一长就盖住「更新时间」。概览卡与详情页（detail）共用。
            string freshness; IBrush tone = InkFaint;
            if (!config.Enabled.Contains(state.Id)) freshness = "已关闭";
            else if (state.Status == "ready") freshness = UpdatedAgo(state.LastSuccess);
            else if (state.Stale) { freshness = "上次读数 · " + UpdatedAgo(state.LastSuccess); tone = state.Status == "cached" ? InkFaint : WarnBrush; }
            else if (state.Status == "loading") freshness = "正在读取…";
            else if (state.Status == "setup") freshness = "尚未连接";
            else { freshness = "需要处理"; tone = WarnBrush; }
            var fresh = Label(freshness, 10.5, tone); fresh.HorizontalAlignment = HorizontalAlignment.Left; fresh.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetRow(fresh, 1); Grid.SetColumn(fresh, 1); grid.Children.Add(fresh);
            // Phase 5：多账户时账户行显示「活动账户名 · 邮箱」（邮箱遮挡口径不变）。
            string account = AccountLabel(state.Id, state.Account);
            if (account.Length > 0) {
                var owner = Label(account, 10.5, InkFaint); owner.MaxWidth = 190; owner.Margin = new Thickness(10, 0, 0, 0); owner.HorizontalAlignment = HorizontalAlignment.Right; owner.TextTrimming = TextTrimming.CharacterEllipsis;
                ToolTip.SetTip(owner, I18n.T(detail ? (config.HideAccounts ? "可在设置中关闭邮箱遮挡" : account) : null));
                Grid.SetRow(owner, 1); Grid.SetColumn(owner, 2); grid.Children.Add(owner);
            }
            return grid;
        }
        private void AccountBody(StackPanel stack, ProviderState state, bool detail) {
            if (!config.Enabled.Contains(state.Id)) {
                stack.Children.Add(Notice("此平台已关闭，可在设置中启用。", false));
                // 密钥类平台给出直达入口：只说“可在设置中启用”时，key 该去哪填仍然难找。
                if (KeyProviders.Contains(state.Id)) {
                    var open = Styled(new Button { Content = "前往设置启用并填写 Key", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, 6, 0, 0) }, "link-button");
                    open.Click += delegate { settingsTab = "accounts"; OpenSettings(); };
                    stack.Children.Add(open);
                }
                return;
            }
            if (ProviderCatalog.LocalOnly(state.Id)) { stack.Children.Add(Notice(ProviderCatalog.Help(state.Id), false)); return; }
            bool first = true;
            // Cursor lists Grok Bot under the three plan meters, including on the overview card.
            int preview = detail ? 30 : state.Id == "cursor" ? state.Quotas.Count : 3;
            foreach (var quota in state.Quotas.Take(preview)) {
                Control row = QuotaRow(quota, state.Id, state.Stale);
                if (first) { row.Margin = new Thickness(row.Margin.Left, 9, row.Margin.Right, 0); first = false; }
                stack.Children.Add(row);
            }
            if (!detail && state.Quotas.Count > preview) {
                var more = Styled(new Button { Content = "查看其余 " + (state.Quotas.Count - preview) + " 项额度 ›", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, 8, 0, 0) }, "link-button");
                more.Click += delegate { SelectProvider(state.Id); }; stack.Children.Add(more);
            }
            if (state.ProductUsage != null && state.ProductUsage.Count > 0) stack.Children.Add(ProductUsageBlock(state));
            foreach (var balance in state.Balances) {
                var row = Row(); row.Margin = new Thickness(0, 14, 0, 0);
                var value = Label(balance.Currency + " " + balance.Amount.ToString("N2", CultureInfo.InvariantCulture), 20, Ink); value.FontWeight = FontWeight.SemiBold; Tabular(value);
                AddRow(row, Label("可用余额", 12.5, InkDim), value); stack.Children.Add(row);
            }
            if (state.Id == "codex" && (state.Status == "ready" || detail) && (state.ResetCreditsAvailable.HasValue || detail)) stack.Children.Add(ResetCredits(state));
            if (state.Id == "deepseek" && state.Status == "ready" && state.Balances.Count == 0) {
                stack.Children.Add(Notice("没有填写 API Key，这里只统计本机 DeepSeek Harness 的用量。DeepSeek Harness 的登录查不了 API 余额；想看余额，可在下面直接填写 DeepSeek API Key（可选）。", false));
                // Harness ready 态把 key 输入直接放在卡片上（复用 KeyConnect 的粘贴框 + 保存并
                // 连接，写入同一把钥匙串密钥并刷新状态），不再只有一行浅色提示 + 设置跳转。
                stack.Children.Add(ConnectPanel(state));
            }
            if (state.Status != "ready") {
                string message = state.Message;
                if (state.Id == "antigravity" && state.Status == "expired") message = "登录需要更新。请打开 Antigravity 并登录，保持应用运行后刷新。";
                if (state.Status != "loading" && state.Status != "cached") stack.Children.Add(Notice(message + (state.Stale ? " 当前显示的是上次成功读数。" : ""), state.Status == "error" || state.Status == "expired"));
                // Not connected / login expired: connect right here. Other errors (network,
                // proxy, rate limit) usually pass on a retry, so connecting is one click away.
                if (state.Status == "error") stack.Children.Add(ReconnectToggle(state.Id));
                if (state.Status == "setup" || state.Status == "expired" || (state.Status == "error" && connectionDetails.Contains(state.Id))) stack.Children.Add(ConnectPanel(state));
            }
        }
        private Control ProductUsageBlock(ProviderState state) {
            var breakdown = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Opacity = state.Stale ? .62 : 1 };
            breakdown.Children.Add(Label("本期消耗构成 · 共用当前账期额度", 10.5, InkFaint));
            foreach (var product in state.ProductUsage) {
                var row = Row(); row.Margin = new Thickness(0, 5, 0, 0);
                var amount = Label("已消耗总额度 " + product.UsedPercent.ToString("0.#") + "%", 11, InkDim);
                Tabular(amount); AddRow(row, Label(product.DisplayName, 11, InkDim), amount);
                ToolTip.SetTip(row, I18n.T("该消耗已计入当前账期的已用额度，各产品共享上方显示的剩余额度。"));
                breakdown.Children.Add(row);
            }
            return breakdown;
        }
        private Control ReconnectToggle(string id) {
            bool open = connectionDetails.Contains(id);
            var toggle = Styled(new Button { Content = open ? "收起连接选项" : "重新连接…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, 6, 0, 0) }, "link-button");
            toggle.Click += delegate { if (!connectionDetails.Add(id)) connectionDetails.Remove(id); Render(); };
            return toggle;
        }
        // Per-provider refresh: spins while that provider (or a full refresh) is in flight.
        private Button ProviderRefreshButton(string id, double size) {
            string name = ProviderCatalog.Name(id);
            bool busy = refreshing || refreshingIds.Contains(id);
            Button button = SpinButton(size, busy, busy ? "正在刷新 " + name + "…" : "只刷新 " + name);
            AutomationProperties.SetName(button, "刷新 " + name);
            button.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; if (!refreshing && !refreshingIds.Contains(id)) { var ignored = RefreshOne(id); } };
            return button;
        }
        private Button RefreshAllButton(double size) {
            Button button = SpinButton(size, refreshing, refreshing ? "正在刷新…" : "刷新全部平台（F5）");
            AutomationProperties.SetName(button, "刷新全部");
            button.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; var ignored = Refresh(); };
            return button;
        }
        // Pages are rebuilt while a refresh runs; the start angle comes from the clock so a
        // rebuilt button keeps spinning in phase instead of jumping back to 0°.
        private Button SpinButton(double size, bool busy, string tip) {
            var button = Styled(new Button(), "icon-button");
            button.Width = size; button.Height = size;
            var glyph = Icons.GlyphBound(button, Icons.Refresh, Math.Round(size * .44));
            var rotate = new RotateTransform(); glyph.RenderTransform = rotate;
            DispatcherTimer timer = null;
            if (busy) {
                rotate.Angle = DateTime.Now.TimeOfDay.TotalMilliseconds % 900 / 900 * 360;
                timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
                timer.Tick += delegate { var r = glyph.RenderTransform as RotateTransform; if (r != null) r.Angle = (r.Angle + 12) % 360; };
                timer.Start();
            }
            button.Content = glyph;
            ToolTip.SetTip(button, tip);
            if (busy) { button.Foreground = AccentBrush; button.DetachedFromVisualTree += delegate { if (timer != null) timer.Stop(); }; }
            return button;
        }
        // The segmented meter is the user's own design: 24 cells, 2 px gaps, remaining
        // fraction filled in the provider colour, warm colour below 10%. Keep it as is.
        // Click a window to expand its exact reset time, usage and window length.
        private Control QuotaRow(Quota quota, string id, bool stale) {
            string key = id + "|" + quota.Label; bool open = expandedQuotas.Contains(key);
            var stack = new StackPanel { Opacity = stale ? .62 : 1 };
            var row = Row();
            var heading = new TextBlock { Foreground = Ink, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            heading.Inlines.Add(new Run(quota.Label + " "));
            heading.Inlines.Add(new Run(quota.Remaining.ToString("0.#") + "%") { FontSize = 17, FontWeight = FontWeight.SemiBold });
            heading.Inlines.Add(new Run(" 剩余"));
            // 右段（倒计时 + 箭头）固定最小宽并右对齐：各平台卡片的重置列从同一条右缘起排
            //（此前随文本变化，三张卡三种右缘）；数字等宽（Tabular）避免跳动。
            var right = new StackPanel { Orientation = Orientation.Horizontal, MinWidth = 112, HorizontalAlignment = HorizontalAlignment.Right };
            var reset = Label(Countdown(quota.ResetUtc), 10.5, InkDim); reset.Margin = new Thickness(8, 0, 0, 0); Tabular(reset); right.Children.Add(reset);
            right.Children.Add(Icons.Glyph(open ? Icons.ChevronUp : Icons.ChevronDown, 8, InkFaint, HorizontalAlignment.Left, VerticalAlignment.Center).Also(g => g.Margin = new Thickness(7, 1, 0, 0)));
            AddRow(row, heading, right); stack.Children.Add(row);
            var segments = SegmentBar(quota.Remaining, ProviderCatalog.Color(id), 24, 8); segments.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(segments);
            PaceInfo pace = UsageDetails.Pace(quota, DateTime.UtcNow);
            if (pace != null) {
                bool even = Math.Abs(pace.Reserve) < 1;
                string reserve = even ? "进度均衡" : pace.Reserve >= 0 ? "余量 " + Math.Abs(pace.Reserve).ToString("0") + "%" : "超前消耗 " + Math.Abs(pace.Reserve).ToString("0") + "%";
                string duration = pace.Lasts ? "按当前速度可持续到重置" : pace.SecondsUntilEmpty.HasValue ? "预计 " + Duration(pace.SecondsUntilEmpty.Value) + "后用尽" : "使用节奏估算";
                string buffer = pace.Reserve > 15 && pace.SpeedMultiplier >= 1.5 ? " · ≥1.5 倍余量" : "";
                var line = new TextBlock { FontSize = 10.5, Foreground = InkDim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) };
                line.Inlines.Add(new Run(reserve) { Foreground = even ? InkDim : pace.Reserve >= 0 ? GoodBrush : WarnBrush });
                line.Inlines.Add(new Run(" · " + duration + buffer));
                stack.Children.Add(line);
            }
            if (open) {
                var detail = new Border { Background = Brush("#0AFFFFFF"), CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 7, 10, 8), Margin = new Thickness(0, 8, 0, 0) };
                var lines = new StackPanel();
                Action<string, string> add = (label, value) => { var r = Row(); r.Margin = new Thickness(0, 2, 0, 1); var v = Label(value, 10.5, Ink); Tabular(v); AddRow(r, Label(label, 10.5, InkFaint), v); lines.Children.Add(r); };
                add("已用 / 剩余", quota.Used.ToString("0.#") + "% / " + quota.Remaining.ToString("0.#") + "%");
                if (quota.WindowSeconds > 0) add("窗口长度", quota.WindowSeconds % 86400 == 0 ? (quota.WindowSeconds / 86400) + " 天" : (quota.WindowSeconds / 3600.0).ToString("0.#") + " 小时");
                DateTimeOffset resetAt; if (DateTimeOffset.TryParse(quota.ResetUtc, out resetAt)) add("重置时间", resetAt.LocalDateTime.ToString(I18n.T("M月d日 ddd HH:mm"), I18n.Culture));
                if (pace != null) add("线性节奏下应已用", Math.Max(0, Math.Min(100, quota.Used - pace.Reserve)).ToString("0") + "%");
                lines.Children.Add(new TextBlock { Text = "节奏按本周期平均速度线性估算，不是官方承诺。", FontSize = 10, Foreground = InkFaint, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
                detail.Child = lines; stack.Children.Add(detail);
            }
            var button = new Button { Content = stack, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(7, 5, 7, 6), Margin = new Thickness(-7, 11, -7, 0) };
            ToolTip.SetTip(button, open ? "点击收起" : "点击查看重置时间与用量明细");
            AutomationProperties.SetName(button, ProviderCatalog.Name(id) + " " + quota.Label + " 额度");
            button.Click += delegate { if (!expandedQuotas.Add(key)) expandedQuotas.Remove(key); Render(); };
            return button;
        }
        // The segmented meter itself: equal cells with 2 px gaps, the remaining fraction in
        // the provider colour (warm below 10%). Panel rows use 24 cells; widgets use fewer.
        // The fill is laid out with star columns in the first layout pass (no SizeChanged
        // callback), so rebuilding a page never shows empty meters for a frame.
        private static UniformGrid SegmentBar(double remaining, string color, int count, double height) {
            var segments = new UniformGrid { Columns = count, Height = height };
            IBrush empty = Brush("#30FFFFFF"), fill = Brush(remaining < 10 ? "#E6A083" : color);
            for (int i = 0; i < count; i++) {
                double fraction = Math.Max(0, Math.Min(1, remaining / 100 * count - i));
                var cell = new Grid { Background = fraction >= 1 ? fill : empty, Margin = new Thickness(0, 0, i == count - 1 ? 0 : 2, 0) };
                if (fraction > 0 && fraction < 1) {
                    cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
                    cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fraction, GridUnitType.Star) });
                    cell.Children.Add(new Border { Background = fill });
                }
                segments.Children.Add(cell);
            }
            return segments;
        }
        private Control ResetCredits(ProviderState state) {
            var block = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            var row = Row();
            var count = Label(state.ResetCreditsAvailable.HasValue ? state.ResetCreditsAvailable + " 次可用" : "暂未返回", 12.5, Ink); count.FontWeight = FontWeight.SemiBold;
            AddRow(row, Label("限额重置额度", 12.5, Ink), count); block.Children.Add(row);
            string detail = state.ResetCreditExpiries.Count > 0 ? "到期 " + String.Join(" · ", state.ResetCreditExpiries.Take(3).Select(ExpiryCountdown)) : !state.ResetCreditsAvailable.HasValue && state.ResetCreditsError.Length > 0 ? state.ResetCreditsError : "";
            if (detail.Length > 0) {
                var hint = Label(detail, 10.5, InkFaint); hint.Margin = new Thickness(0, 3, 0, 0);
                ToolTip.SetTip(hint, I18n.T(state.ResetCreditExpiries.Count == 0 ? "只读显示，不会自动使用重置额度" : "有效期：\n" + String.Join("\n", state.ResetCreditExpiries.Select(LocalTime)) + "\n只读显示，不会自动使用。"));
                block.Children.Add(hint);
            }
            return block;
        }
        // ── Detail page ───────────────────────────────────────────────────
        private void RenderDetail(ProviderState state) {
            var top = new StackPanel();
            top.Children.Add(Identity(state, true));
            AccountBody(top, state, true);
            body.Children.Add(Card(top));
            List<DayUsage> days = history.Days.Where(d => d.Agent == state.Id).ToList();
            if (days.Count > 0 || usageIndexes.ContainsKey(state.Id)) body.Children.Add(Card(UsageBlock(state, days)));
            else if (!ProviderCatalog.Custom.ContainsKey(state.Id)) {
                var empty = new StackPanel();
                var title = Label("本机用量", 13, Ink); title.FontWeight = FontWeight.SemiBold; empty.Children.Add(title);
                empty.Children.Add(Notice(scanning ? "正在扫描本地历史…" : "本机暂未发现该平台的 Token 历史。账户额度来自服务商接口，本机用量来自本地会话日志，两者相互独立。", false));
                body.Children.Add(Card(empty));
            }
        }
        private WindowUsage CurrentWindow(string id, List<DayUsage> days, Quota quota, int previous) {
            // A subscription window counts only the official endpoint: Codex's own provider,
            // Claude responses that carry Anthropic's request id; relays are left out.
            LogIndex logs; usageIndexes.TryGetValue(id, out logs);
            Func<string, bool> official = id == "codex" ? (Func<string, bool>)CodexLogs.IsOfficial : id == "claude" ? (Func<string, bool>)(p => p.Length == 0) : null;
            return UsageDetails.Window(days, quota, DateTime.UtcNow, previous, logs, TimeZoneInfo.Local, official);
        }
        // Local usage of one provider over a chosen period (button: 当天 / 1d / 7d / 14d / 30d
        // or a custom start and end): totals, token composition, an interactive chart and
        // the models used; then the subscription windows.
        private Control UsageBlock(ProviderState state, List<DayUsage> days) {
            var stack = new StackPanel();
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = Label(ProviderCatalog.UsageTitle(state.Id), 13, Ink); title.FontWeight = FontWeight.SemiBold; head.Children.Add(title);
            bool account = ProviderCatalog.AccountUsage(state.Id);
            var picker = RangeSelector(state.Id, Render, false); picker.Margin = new Thickness(8, 0, 8, 0); Grid.SetColumn(picker, 1); head.Children.Add(picker);
            var badge = Label("API 等价 ⓘ", 10.5, InkFaint); ToolTip.SetTip(badge, PricingHint()); Grid.SetColumn(badge, 2); head.Children.Add(badge);
            stack.Children.Add(head);
            RangeData data = RangeUsage(new List<string> { state.Id }, RangeFor(state.Id), false, state.Id);
            Bucket total = data.Total;
            bool timed = OutputTiming.Supported(state.Id);
            var figures = new UniformGrid { Columns = timed ? 4 : 3, Margin = new Thickness(0, 12, 0, 0) };
            // 四联顺序（任务2）：Token 在前、费用次之，与全应用「Token 优先」口径一致。
            figures.Children.Add(BigFigure("Token", Compact(total.Tokens()), "新增输入 + 输出 + 缓存命中 + 缓存写入"));
            figures.Children.Add(BigFigure("费用", total.D > 0 || total.Tokens() > total.U ? Usd(total.D) : "—", total.U > 0 ? "部分模型没有价目，未计入费用" : account ? "Cursor 后台给出的每次调用 API 等价价格，不是订阅账单" : "按各模型官方 API 单价估算，不是订阅账单"));
            figures.Children.Add(BigFigure("请求", total.R > 0 ? total.R.ToString("N0", CultureInfo.InvariantCulture) : "—", data.FromLogs ? (account ? "Cursor 后台记录的模型调用次数（含按次计入套餐的请求）" : "本机日志里的模型请求次数") : "这一时间段只有按日汇总，没有请求次数"));
            if (timed) figures.Children.Add(BigFigure("输出速度", OutputTiming.Text(total.Speed()), ProviderCatalog.Name(state.Id) + "：" + (total.Speed().HasValue ? "这一时间段 " + Math.Round(total.TN).ToString("N0", CultureInfo.InvariantCulture) + " 次请求计时，" + Compact(total.TO) + " 输出 Token ÷ " + Duration(total.TS) + "\n" : "这一时间段没有可计时的请求\n") + OutputTiming.Definition));
            stack.Children.Add(figures);
            Control composition = Composition(total.I, total.O, total.C, total.W, "Token 构成");
            if (composition != null) stack.Children.Add(composition);
            double relayed = thirdParty.AppMonth(state.Id);
            if (ThirdPartyVisible() && relayed > 0) {
                var link = Styled(new Button { Content = "其中经第三方接口：近 30 天 " + Compact(relayed) + " Token ›", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, 8, 0, 0) }, "link-button");
                ToolTip.SetTip(link, "上面的用量包含所有本机用量；额度窗口只计官方订阅。");
                link.Click += delegate { SelectProvider(ProviderCatalog.ThirdParty); };
                stack.Children.Add(link);
            }
            if (data.Bars.Count > 0) stack.Children.Add(UsageChart("usage|" + state.Id, (data.Hourly ? "每小时" : "每日") + " · " + RangeFor(state.Id).Label(), data.Bars, 58, true, false, Render, true, timed ? ProviderCatalog.Color(state.Id) : null));
            List<KeyValuePair<string, Bucket>> models = data.Parts.Where(p => p.Value.Tokens() > 0 || p.Value.D > 0).OrderByDescending(p => p.Value.Tokens()).ThenByDescending(p => p.Value.D).ToList();
            if (models.Count > 0) {
                var list = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
                var caption = Row(); AddRow(caption, Label("模型", 10.5, InkFaint), Label("Token · 费用" + (timed ? " · 速度" : ""), 10.5, InkFaint)); list.Children.Add(caption);
                // 占比默认按 Token（任务2）：模型行按 Token 从多到少排列，占比条同口径。
                double sum = Math.Max(1e-9, models.Sum(p => p.Value.Tokens()));
                List<string> order = models.Select(p => p.Key).ToList();
                foreach (var pair in models.Take(6)) {
                    var line = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                    ToolTip.SetTip(line, pair.Key + "\n" + Compact(pair.Value.Tokens()) + " Token · " + Usd(pair.Value.D) + (pair.Value.Speed().HasValue ? "\n输出速度 " + OutputTiming.Text(pair.Value.Speed()) + "（" + Math.Round(pair.Value.TN).ToString("N0", CultureInfo.InvariantCulture) + " 次请求计时）" : "") + (pair.Value.U > 0 ? "\n没有这个模型的价目" : ""));
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) }); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    line.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(2), Background = Brush(ModelColor(state.Id, pair.Key, order)), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                    var name = Label(pair.Key, 11, Ink); Grid.SetColumn(name, 1); line.Children.Add(name);
                    double share = pair.Value.Tokens() / sum;
                    var track = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0), Background = Brush("#14FFFFFF") };
                    track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.001, share), GridUnitType.Star) }); track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.001, 1 - share), GridUnitType.Star) });
                    track.Children.Add(new Border { Background = Brush(ModelColor(state.Id, pair.Key, order)), CornerRadius = new CornerRadius(2) });
                    Grid.SetColumn(track, 2); line.Children.Add(track);
                    var value = Label(Compact(pair.Value.Tokens()) + " · " + (pair.Value.U > 0 && pair.Value.D <= 0 ? "—" : Usd(pair.Value.D)) + (timed ? " · " + OutputTiming.Text(pair.Value.Speed()) : ""), 10.5, InkDim); Tabular(value); Grid.SetColumn(value, 3); line.Children.Add(value);
                    list.Children.Add(line);
                }
                if (models.Count > 6) { var more = Label("另有 " + (models.Count - 6) + " 个模型", 10.5, InkFaint); more.Margin = new Thickness(15, 6, 0, 0); list.Children.Add(more); }
                stack.Children.Add(list);
            }
            Quota quota = UsageDetails.MainWindow(state);
            WindowUsage current = quota == null ? null : CurrentWindow(state.Id, days, quota, 0);
            if (quota != null) {
                string windowHint = WindowHint(current);
                stack.Children.Add(new Border { Height = 1, Background = Hairline, Margin = new Thickness(0, 14, 0, 12) });
                var caption = Row();
                AddRow(caption, Label("额度窗口用量 · " + WindowName(quota), 11, InkDim), Label(current != null && current.Exact ? "按小时精确" : "只计完整自然日 · 下界", 10.5, InkFaint));
                ToolTip.SetTip(caption, windowHint); stack.Children.Add(caption);
                for (int i = 0; i < 3; i++) {
                    WindowUsage usage = i == 0 ? current : CurrentWindow(state.Id, days, quota, i);
                    if (usage == null || (i > 0 && usage.Tokens <= 0)) continue;
                    var row = Row(); row.Margin = new Thickness(0, 7, 0, 0); ToolTip.SetTip(row, WindowHint(usage));
                    string range = usage.StartUtc.ToLocalTime().ToString("M/d HH:mm") + " – " + usage.EndUtc.ToLocalTime().ToString("M/d HH:mm");
                    var left = Label((i == 0 ? "当前 · " : "") + range, 11, i == 0 ? Ink : InkDim);
                    string value = usage.Tokens <= 0 && usage.Days.Count == 0 ? "暂无记录" : (usage.HasCost ? WindowMoney(usage) + " · " : "") + WindowTokens(usage);
                    var right = Label(value, 11, i == 0 ? Ink : InkDim); Tabular(right);
                    AddRow(row, left, right); stack.Children.Add(row);
                }
            }
            var foot = Label((account ? "数据来自 Cursor 账户后台，含该账户所有设备 · 没有请求耗时，无法算输出速度" : ProviderCatalog.UsageSource(state.Id) != null ? I18n.T("数据来自本机 {0} 日志，不含其他设备", ProviderCatalog.UsageSource(state.Id)) : "数据来自本机日志，不含其他设备") + (data.FromLogs ? " · 按小时统计" : ""), 10.5, InkFaint);
            foot.TextWrapping = TextWrapping.Wrap;
            foot.Margin = new Thickness(0, 14, 0, 0); stack.Children.Add(foot);
            return stack;
        }
        // One agent's output speed over the last 7 days (hourly index, else the daily history).
        private double? RecentSpeed(string id, List<DayUsage> days) {
            if (!OutputTiming.Supported(id)) return null;
            LogIndex index;
            if (usageIndexes.TryGetValue(id, out index) && index != null) return index.Usage(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow, null).Speed();
            string from = HistoryService.DayKey(DateTime.Today.AddDays(-6));
            List<DayUsage> recent = days.Where(d => String.CompareOrdinal(d.Day, from) >= 0).ToList();
            return OutputTiming.Speed(recent.Sum(d => d.TimedOutput), recent.Sum(d => d.TimedSeconds));
        }
        // 套餐归属卡的输出速度：近 7 个自然日（含今天，与 RecentSpeed 的按日回退同窗口）
        // 的归属计时合计。窗口与脚注的「今日 / 30 天」同为本地自然日口径。
        private double? PlanSpeed(string cardId) {
            PlanAttribution.OwnedTotals recent = PlanRange(cardId, DateTime.Today.AddDays(-6), DateTime.Today.AddDays(1));
            return OutputTiming.Speed(recent.Total.TO, recent.Total.TS);
        }
        private static Control BigFigure(string title, string value, string tip) {
            var box = new StackPanel();
            ToolTip.SetTip(box, tip);
            box.Children.Add(Label(title, 10.5, InkFaint));
            var number = Label(value, 17, Ink); number.FontWeight = FontWeight.SemiBold; number.Margin = new Thickness(0, 2, 0, 0); Tabular(number); box.Children.Add(number);
            return box;
        }
        private static Control Composition(DayUsage day, string caption) { return Composition(day.InputTokens, day.OutputTokens, day.CachedTokens, day.CacheCreationTokens, caption); }
        private static Control Composition(double input, double output, double read, double write, string caption) {
            if (input + output + read + write <= 0) return null;
            var stack = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            double prompt = input + read + write;
            var head = Row();
            var rate = Label(prompt > 0 ? "缓存命中率 " + (read / prompt * 100).ToString("0.0") + "%" : "", 10.5, InkDim);
            ToolTip.SetTip(rate, I18n.T("缓存命中 ÷（新增输入 + 缓存命中 + 缓存写入）。与 CC Switch 的口径一致。"));
            AddRow(head, Label(caption, 10.5, InkFaint), rate); stack.Children.Add(head);
            var grid = new UniformGrid { Columns = write > 0 ? 4 : 3, Margin = new Thickness(0, 6, 0, 0) };
            grid.Children.Add(MiniStat("新增输入", input, "未命中缓存、按全价计费的输入"));
            grid.Children.Add(MiniStat("输出", output, "含推理 Token"));
            grid.Children.Add(MiniStat("缓存命中", read, "命中提示缓存的输入，按缓存价计费"));
            if (write > 0) grid.Children.Add(MiniStat("缓存写入", write, "写入提示缓存的输入（Claude）"));
            stack.Children.Add(grid);
            return stack;
        }
        private static Control MiniStat(string label, double value, string tip) {
            var box = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
            ToolTip.SetTip(box, tip);
            box.Children.Add(Label(label, 10, InkFaint));
            var number = Label(Compact(value), 12.5, Ink); number.Margin = new Thickness(0, 2, 0, 0); Tabular(number); box.Children.Add(number);
            return box;
        }

        // ── Third-party endpoints（总览卡片）────────────────────────────────
        // Usage only: relays set their own weekly/monthly limits and do not publish
        // them, so there is deliberately no quota meter here.
        private Control ThirdPartyCard() {
            var stack = new StackPanel();
            var header = new Button { Content = ThirdPartyHeader(false), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(7, 5, 7, 5), Margin = new Thickness(-7, -5, -7, -5) };
            ToolTip.SetTip(header, "查看第三方接口用量");
            AutomationProperties.SetName(header, "第三方 API 详情");
            header.Click += delegate { SelectProvider(ProviderCatalog.ThirdParty); };
            stack.Children.Add(header);
            foreach (EndpointUsage endpoint in thirdParty.Endpoints.Take(4)) {
                var row = Row(); row.Margin = new Thickness(0, 12, 0, 0);
                var left = new StackPanel();
                var title = new TextBlock { FontSize = 12.5, Foreground = Ink, TextTrimming = TextTrimming.CharacterEllipsis };
                title.Inlines.Add(new Run(endpoint.Title));
                if (endpoint.Current) title.Inlines.Add(new Run("  使用中") { FontSize = 10.5, Foreground = AccentBrush });
                left.Children.Add(title);
                var sub = Label(AppName(endpoint.App) + (endpoint.Name.Length > 0 && endpoint.Host.Length > 0 ? " · " + endpoint.Host : ""), 10.5, InkFaint); sub.Margin = new Thickness(0, 2, 0, 0); left.Children.Add(sub);
                var right = Label(endpoint.Month > 0 ? "今日 " + Compact(endpoint.Today) + " · 30 天 " + Compact(endpoint.Month) : "暂无用量", 11, InkDim); Tabular(right);
                AddRow(row, left, right); stack.Children.Add(row);
            }
            if (thirdParty.Endpoints.Count > 4) { var more = Label("另有 " + (thirdParty.Endpoints.Count - 4) + " 个接口", 10.5, InkFaint); more.Margin = new Thickness(0, 10, 0, 0); stack.Children.Add(more); }
            stack.Children.Add(new Border { Height = 1, Background = Hairline, Margin = new Thickness(0, 14, 0, 10) });
            stack.Children.Add(Label("单位 Token · 限额由各服务商决定，本机无法得知", 10.5, InkFaint));
            return Card(stack);
        }
        private Control ThirdPartyHeader(bool detail) {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Control icon = Icon(ProviderCatalog.ThirdParty, detail ? 20 : 17); icon.Margin = new Thickness(0, 0, 10, 0); grid.Children.Add(icon);
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var name = Label("第三方 API", detail ? 16 : 14, Ink); name.FontWeight = FontWeight.SemiBold; title.Children.Add(name);
            title.Children.Add(Pill("本机日志"));
            Grid.SetColumn(title, 1); grid.Children.Add(title);
            if (!detail) {
                var chevron = Icons.Glyph(Icons.ChevronRight, 10, InkFaint, HorizontalAlignment.Left, VerticalAlignment.Center);
                Grid.SetColumn(chevron, 2); grid.Children.Add(chevron);
            }
            return grid;
        }
        private static string AppName(string app) { return app == Endpoints.ClaudeApp ? "Claude Code" : app == Endpoints.CodexApp ? "Codex" : app; }
        private static string CurrentLabel(EndpointMark mark) {
            if (mark == null) return "尚未检测";
            if (mark.Official) return "官方";
            return mark.Name.Length > 0 ? mark.Name + " · " + mark.Host : mark.Host;
        }
        private static string Ago(string iso) {
            DateTimeOffset date; if (!DateTimeOffset.TryParse(iso, out date)) return "时间未知";
            double minutes = (DateTimeOffset.UtcNow - date).TotalMinutes;
            return minutes < 60 ? "1 小时内" : minutes < 24 * 60 ? (int)(minutes / 60) + " 小时前" : date.LocalDateTime.ToString(I18n.T("M月d日"), I18n.Culture);
        }
    }
}
