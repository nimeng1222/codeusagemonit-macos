// 紧凑尺寸（small / medium / large 三档命令式页面 + 滚轮/方向键翻页 + 内联连接页 + 尺寸菜单）。
// Avalonia 写法：Visibility.Collapsed → IsVisible=false、DarkMenu 替代 ContextMenu、
// switcher/dots/图标字形同 Presentation。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using AvaColor = Avalonia.Media.Color;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace CodeUsageMonit {
    // One window, five display sizes: small / medium / large and the vertical "edge" dock
    // render here into CompactRoot; "full" is the panel. Every compact size can be resized
    // by dragging its edges, and is interactive: switch pages, refresh or connect a
    // provider, cycle / expand quota windows, hover the charts for daily figures.
    public sealed partial class MonitorPanel {
        private string activeSize = "full";
        private bool IsCompact { get { return activeSize != "full"; } }
        public static readonly string[] DisplaySizes = { "small", "medium", "large", "full", "edge" };
        private static string SizeName(string size) { return size == "small" ? "小" : size == "medium" ? "中" : size == "large" ? "大" : size == "edge" ? "窄条" : "完整"; }
        // Provider being connected inside a compact size (key box, device code, CLI login).
        private string compactConnect;
        // Which quota window a provider's headline shows (click to cycle); default = lowest.
        private readonly Dictionary<string, string> headlineChoice = new Dictionary<string, string>();
        private readonly HashSet<string> expandedQuotas = new HashSet<string>();
        private DispatcherTimer compactRenderTimer;

        // ── Switching sizes and pages ─────────────────────────────────────
        private bool sizeApplied;
        private void ApplyDisplaySize(string size) {
            bool wasCompact = IsCompact, switching = sizeApplied; sizeApplied = true;
            // Keep the page when the user switches: the panel's provider page becomes the
            // compact page and back (not at startup, which restores the saved page).
            if (switching && size != "full" && !wasCompact) config.CompactProvider = CompactPages().Contains(selected) ? selected : "overview";
            if (switching && size == "full" && wasCompact) selected = config.CompactProvider == "overview" || EnabledIds().Contains(config.CompactProvider) ? config.CompactProvider : "overview";
            activeSize = size;
            compactConnect = null;
            scaleRoot.IsVisible = !IsCompact;
            compactRoot.IsVisible = IsCompact;
            frame.SetMode(size);
            if (IsCompact) RenderCompact(); else Render();
        }
        private void SetDisplaySize(string size) {
            if (!DisplaySizes.Contains(size)) return;
            config.DisplaySize = size; SaveConfig();
            if (activeSize != size) ApplyDisplaySize(size);
            if (!window.IsVisible) Reveal();
        }
        private List<string> CompactPages() { return new[] { "overview" }.Concat(EnabledIds()).ToList(); }
        private void CycleCompact(int step) {
            List<string> pages = CompactPages();
            int index = pages.IndexOf(config.CompactProvider);
            SelectCompact(pages[((index < 0 ? 0 : index) + step + pages.Count) % pages.Count]);
        }
        private void SelectCompact(string id) {
            compactConnect = null;
            if (config.CompactProvider != id) { config.CompactProvider = id; SaveConfig(); }
            RenderCompact();
        }
        private void OpenCompactConnect(string id) { compactConnect = id; RenderCompact(); }
        private void CloseCompactConnect() { compactConnect = null; window.Focus(); RenderCompact(); }
        // Resizing re-lays out the page (how many rows fit); coalesced while dragging.
        private void QueueCompactRender() {
            if (!IsCompact) return;
            if (compactRenderTimer == null) {
                compactRenderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                compactRenderTimer.Tick += delegate { compactRenderTimer.Stop(); RenderCompact(); };
            }
            compactRenderTimer.Stop(); compactRenderTimer.Start();
        }
        private Size CompactSpace() {
            double w = compactRoot.Bounds.Width, h = compactRoot.Bounds.Height;
            if (w < 1 || h < 1) { w = window.Width / config.UiScale; h = window.Height / config.UiScale; }
            return new Size(w, h);
        }

        // Right-click menu of every size (also the panel header's size button).
        private DarkMenu SizeMenu() {
            var menu = new DarkMenu();
            foreach (string size in DisplaySizes) {
                string captured = size;
                menu.Item(I18n.T(SizeName(size) + (size == "full" ? "面板" : "尺寸")), delegate { SetDisplaySize(captured); }, activeSize == size);
            }
            menu.Separator();
            string page = IsCompact ? config.CompactProvider : selected;
            bool provider = page != "overview" && ProviderCatalog.IsKnown(page) && !ProviderCatalog.LocalOnly(page);
            if (provider) menu.Item(I18n.T("刷新 {0}", ProviderCatalog.Name(page)), delegate { var ignored = RefreshOne(page); });
            menu.Item(I18n.T("刷新全部"), delegate { var ignored = Refresh(); });
            menu.Item(I18n.T("复制用量概览"), delegate { var ignored = SetClipboardText(window, SummaryText()); });
            if (IsCompact) {
                string target = page;
                menu.Item(I18n.T("在完整面板中查看") + (provider ? " " + ProviderCatalog.Name(page) : ""), delegate { SetDisplaySize("full"); SelectProvider(ProviderCatalog.IsKnown(target) ? target : "overview"); });
            }
            menu.Separator();
            menu.Item(I18n.T("总在最前"), delegate { config.AlwaysOnTop = !config.AlwaysOnTop; SaveConfig(); UpdatePin(); }, config.AlwaysOnTop);
            // 菜单项文案由片段拼接，翻译按拼接后的整句进行。
            menu.Item(I18n.T("重置" + SizeName(activeSize) + (IsCompact ? "尺寸" : "面板") + "的位置和大小"), delegate { frame.ResetPosition(); });
            menu.Item(I18n.T("设置…"), delegate { OpenSettings(); });
            menu.Item(I18n.T("收起到托盘"), delegate { window.Hide(); });
            return menu;
        }

        private void RenderCompact() {
            if (quitting || !IsCompact) return;
            if (EditingKey()) { renderDeferred = true; return; }
            renderDeferred = false;
            List<string> pages = CompactPages();
            string id = config.CompactProvider; if (!pages.Contains(id)) id = "overview";
            if (compactConnect != null) {
                ProviderState connecting;
                // Connected (or the provider was switched off): back to its page.
                if (!states.TryGetValue(compactConnect, out connecting) || !config.Enabled.Contains(compactConnect) || connecting.Status == "ready") compactConnect = null;
            }
            Size space = CompactSpace();
            Control content;
            // 窄条（edge）不分页：一竖条显示今日汇总 + 全部有额度平台（EdgeDock.cs）。
            // compactConnect 在 ApplyDisplaySize 已清空，edge 里也没有连接入口。
            if (activeSize == "edge") content = EdgeDockContent();
            else if (compactConnect != null) content = CompactConnectPage(states[compactConnect], space);
            else if (id == "overview") content = activeSize == "small" ? OverviewSmall(pages, space) : activeSize == "medium" ? OverviewMedium(pages, space) : OverviewLarge(pages, space);
            else {
                ProviderState state; if (!states.TryGetValue(id, out state)) state = new ProviderState { Id = id };
                // "30 天"口径（history 现在存 370 天，紧凑页的汇总仍是 30 天）。
                List<DayUsage> days = HistoryService.Recent(history.Days.Where(d => d.Agent == id), 30);
                content = activeSize == "small" ? CompactSmall(state, days, pages, space) : activeSize == "large" ? CompactLarge(state, days, pages, space) : CompactMedium(state, days, pages, space);
            }
            compactScaleHost.Child = content;
            if (activeSize == "edge") FitEdgeHeight();
            I18nUi.Localize(window);
        }

        // ── Small ─────────────────────────────────────────────────────────
        private Control CompactSmall(ProviderState state, List<DayUsage> days, List<string> pages, Size space) {
            Grid root = PageGrid(new Thickness(12, 9, 9, 6), 3, 1);
            root.Children.Add(PageHeader(state.Id, 14, 12.5, false, ProviderRefreshButton(state.Id, 22), MoreButton(22)));
            var body2 = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
            Quota main = Headline(state);
            if (main != null) {
                body2.Children.Add(HeadlineButton(state, main, Clamp(Math.Min(space.Width, space.Height) * .19, 28, 56)));
                var bar = SegmentBar(main.Remaining, ProviderCatalog.Color(state.Id), space.Width > 240 ? 24 : 16, 6); bar.Margin = new Thickness(0, 7, 0, 0); body2.Children.Add(bar);
                var reset = Label(Countdown(main.ResetUtc), 10, InkFaint); reset.Margin = new Thickness(0, 5, 0, 0); ToolTip.SetTip(reset, I18n.T("重置时间：" + LocalTime(main.ResetUtc))); body2.Children.Add(reset);
            } else AlternateBody(body2, state, days, Clamp(Math.Min(space.Width, space.Height) * .16, 24, 44));
            Grid.SetRow(body2, 1); root.Children.Add(body2);
            var dots = Dots(pages, state.Id); Grid.SetRow(dots, 2); root.Children.Add(dots);
            return root;
        }
        private Control OverviewSmall(List<string> pages, Size space) {
            Grid root = PageGrid(new Thickness(12, 9, 9, 6), 3, 1);
            root.Children.Add(PageHeader("overview", 14, 12.5, false, RefreshAllButton(22), MoreButton(22)));
            // The most urgent providers that fit; the dots below page through the rest.
            int fit = Math.Max(2, (int)((space.Height - 56) / 22));
            var list = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            List<string> ids = Urgent(pages.Skip(1)).ToList();
            foreach (string id in ids.Take(fit)) list.Children.Add(OverviewLine(id, false, space.Width > 240 ? 12 : 8));
            if (ids.Count == 0) list.Children.Add(Label("没有启用的平台", 11, InkDim));
            Grid.SetRow(list, 1); root.Children.Add(list);
            var dots = Dots(pages, "overview"); Grid.SetRow(dots, 2); root.Children.Add(dots);
            return root;
        }

        // ── Medium ────────────────────────────────────────────────────────
        private Control CompactMedium(ProviderState state, List<DayUsage> days, List<string> pages, Size space) {
            Grid root = PageGrid(new Thickness(14, 9, 10, 8), 3, 1);
            root.Children.Add(PageHeader(state.Id, 15, 13, false, Switcher(pages, state.Id, SwitcherFit(space.Width - 190), 18), ProviderRefreshButton(state.Id, 22), MoreButton(22)));
            var body2 = new Grid { Margin = new Thickness(0, 6, 0, 4) };
            body2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(112, space.Width * .34)) }); body2.ColumnDefinitions.Add(new ColumnDefinition());
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Quota main = Headline(state);
            List<Quota> others = state.Quotas.Where(q => q != main).ToList();
            if (main != null) {
                left.Children.Add(HeadlineButton(state, main, Clamp(space.Height * .18, 26, 46)));
                var reset = Label(Countdown(main.ResetUtc), 10, InkFaint); reset.Margin = new Thickness(0, 3, 0, 0); ToolTip.SetTip(reset, I18n.T("重置时间：" + LocalTime(main.ResetUtc))); left.Children.Add(reset);
            } else AlternateBody(left, state, days, Clamp(space.Height * .16, 24, 40));
            body2.Children.Add(left);
            // Right: quota windows only (no chart in this size). A single window gets its pace,
            // exact reset time and, for Codex, the reset credits in the space a chart used.
            var right = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            if (main != null) {
                int cells = space.Width > 480 ? 24 : 20;
                double room = space.Height - 104;
                right.Children.Add(CompactRow(main, state.Id, cells, false, null));
                if (others.Count == 0) {
                    Control pace = PaceLine(main, 10); if (pace != null) { pace.Margin = new Thickness(0, 6, 0, 0); right.Children.Add(pace); room -= 18; }
                    var exact = Label(LocalTime(main.ResetUtc) + " 重置" + WindowLength(main, " · "), 10, InkFaint); exact.Margin = new Thickness(0, 3, 0, 0); exact.TextTrimming = TextTrimming.CharacterEllipsis; right.Children.Add(exact); room -= 16;
                } else {
                    int fit = Math.Max(0, (int)(room / 34));
                    foreach (Quota other in others.Take(fit)) right.Children.Add(CompactRow(other, state.Id, cells, true, state));
                    room -= Math.Min(fit, others.Count) * 34;
                }
                if (state.ResetCreditsAvailable.HasValue && room >= 24) {
                    var credits = Row(); credits.Margin = new Thickness(0, 10, 0, 0);
                    AddRow(credits, Label("限额重置额度", 10.5, InkDim), Label(state.ResetCreditsAvailable + " 次可用", 10.5, Ink)); right.Children.Add(credits);
                }
            } else MediumFacts(right, state, days, space.Height - 104);
            Grid.SetColumn(right, 1); body2.Children.Add(right);
            Grid.SetRow(body2, 1); root.Children.Add(body2);
            var foot = CompactFooter(state, days); Grid.SetRow(foot, 2); root.Children.Add(foot);
            return root;
        }
        // Medium page of a provider without quota windows (local-only, balance-only): a few
        // text facts instead of a chart. The left side already shows the 30-day total or balance.
        private void MediumFacts(StackPanel panel, ProviderState state, List<DayUsage> days, double room) {
            var facts = new List<KeyValuePair<string, string>>();
            foreach (Balance extra in state.Balances.Skip(1)) facts.Add(new KeyValuePair<string, string>(extra.Currency + " 余额", extra.Amount.ToString("N2", CultureInfo.InvariantCulture)));
            if (days.Count > 0 && !NeedsConnect(state)) {
                string week = HistoryService.DayKey(DateTime.Today.AddDays(-6));
                List<DayUsage> recent = days.Where(d => String.CompareOrdinal(d.Day, week) >= 0).ToList();
                facts.Add(new KeyValuePair<string, string>("近 7 天", Money(recent) + " · " + Compact(recent.Sum(d => d.Tokens)) + " Token"));
                double requests = days.Sum(d => d.Requests);
                facts.Add(new KeyValuePair<string, string>("30 天 Token", Compact(days.Sum(d => d.Tokens)) + (requests > 0 ? " · " + requests.ToString("N0", CultureInfo.InvariantCulture) + " 次请求" : "")));
                var model = days.Where(d => d.Models != null).SelectMany(d => d.Models).GroupBy(m => m.Model).Select(g => new { Name = g.Key, Tokens = g.Sum(m => m.Tokens) }).OrderByDescending(m => m.Tokens).FirstOrDefault();
                if (model != null && !String.IsNullOrEmpty(model.Name)) facts.Add(new KeyValuePair<string, string>("主要模型", model.Name));
            }
            int fit = Math.Max(1, (int)(room / 24));
            bool first = true;
            foreach (var fact in facts.Take(fit)) {
                var row = Row(); row.Margin = new Thickness(0, first ? 0 : 9, 0, 0); first = false;
                var value = Label(fact.Value, 11, Ink); Tabular(value); value.TextTrimming = TextTrimming.CharacterEllipsis;
                AddRow(row, Label(fact.Key, 10.5, InkDim), value); panel.Children.Add(row);
            }
        }
        // "余量 16% · 可持续到重置" — the linear pace of a window, or null without enough data.
        private static Control PaceLine(Quota quota, double size) {
            PaceInfo pace = UsageDetails.Pace(quota, DateTime.UtcNow);
            if (pace == null) return null;
            bool even = Math.Abs(pace.Reserve) < 1;
            var line = new TextBlock { FontSize = size, Foreground = InkFaint, TextTrimming = TextTrimming.CharacterEllipsis };
            line.Inlines.Add(new Run(even ? "进度均衡" : pace.Reserve >= 0 ? "余量 " + pace.Reserve.ToString("0") + "%" : "超前消耗 " + (-pace.Reserve).ToString("0") + "%") { Foreground = even ? InkDim : pace.Reserve >= 0 ? GoodBrush : WarnBrush });
            line.Inlines.Add(new Run(pace.Lasts ? " · 可持续到重置" : pace.SecondsUntilEmpty.HasValue ? " · 约 " + Duration(pace.SecondsUntilEmpty.Value) + "后用尽" : ""));
            ToolTip.SetTip(line, I18n.T("按本周期平均速度线性估算，不是官方承诺。"));
            return line;
        }
        private static string WindowLength(Quota quota, string prefix) {
            if (quota.WindowSeconds <= 0) return "";
            return prefix + (quota.WindowSeconds % 86400 == 0 ? (quota.WindowSeconds / 86400) + " 天" : (quota.WindowSeconds / 3600.0).ToString("0.#") + " 小时") + (I18n.English ? " window" : "窗口");
        }
        private Control OverviewMedium(List<string> pages, Size space) {
            Grid root = PageGrid(new Thickness(14, 9, 10, 8), 3, 1);
            root.Children.Add(PageHeader("overview", 15, 13, false, Switcher(pages, "overview", SwitcherFit(space.Width - 170), 18), RefreshAllButton(22), MoreButton(22)));
            var body2 = new Grid { Margin = new Thickness(0, 6, 0, 4) };
            body2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(118, space.Width * .36)) }); body2.ColumnDefinitions.Add(new ColumnDefinition());
            List<DayUsage> all = HistoryService.Recent(history.Days.Where(d => pages.Contains(d.Agent)), 30);
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            // Token 优先（任务2）：大数字 = 30 天 Token 总量，API 等价费用降为小字副行。
            left.Children.Add(Label("近 30 天 · Token", 10.5, InkDim));
            left.Children.Add(BigValue(Compact(all.Sum(d => d.Tokens)), Clamp(space.Height * .16, 22, 40)));
            var today = Label("今日 " + Compact(all.Where(d => d.Day == Today()).Sum(d => d.Tokens)) + " Token · API 等价 " + (all.Any(d => d.CostKnown) ? Money(all) : "—"), 10, InkFaint); today.Margin = new Thickness(0, 3, 0, 0); Tabular(today); left.Children.Add(today);
            ToolTip.SetTip(left, PricingHint());
            body2.Children.Add(left);
            var list = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            int fit = Math.Max(2, (int)((space.Height - 72) / 22));
            List<string> ids = Urgent(pages.Skip(1)).ToList();
            foreach (string id in ids.Take(ids.Count > fit ? fit - 1 : fit)) list.Children.Add(OverviewLine(id, true, space.Width > 480 ? 16 : 10));
            if (ids.Count > fit) list.Children.Add(MoreLine(ids.Count - (fit - 1)));
            Grid.SetColumn(list, 1); body2.Children.Add(list);
            Grid.SetRow(body2, 1); root.Children.Add(body2);
            var foot = Row();
            var summary = Label(ConnectedSummary(pages.Skip(1).ToList()), 10, InkFaint);
            AddRow(foot, summary, Label(scanning ? "正在读取本地历史…" : "", 10, InkFaint));
            Grid.SetRow(foot, 2); root.Children.Add(foot);
            return root;
        }

        // ── Large ─────────────────────────────────────────────────────────
        private Control CompactLarge(ProviderState state, List<DayUsage> days, List<string> pages, Size space) {
            var root = new Grid { Margin = new Thickness(16, 11, 12, 10) };
            foreach (bool star in new[] { false, false, false, false, true, false, false }) root.RowDefinitions.Add(new RowDefinition { Height = star ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var fresh = Label(state.Status == "ready" ? UpdatedAgo(state.LastSuccess) : StatusWord(state), 10, InkFaint); fresh.Margin = new Thickness(0, 0, 2, 0); right.Children.Add(fresh);
            // Phase 5：多账户时页头带活动账户切换片（与完整面板卡片同一入口）。
            Control accountChip = AccountSwitchChip(state.Id);
            root.Children.Add(accountChip == null
                ? PageHeader(state.Id, 18, 14.5, true, right, ProviderRefreshButton(state.Id, 24), MoreButton(24))
                : PageHeader(state.Id, 18, 14.5, true, right, accountChip, ProviderRefreshButton(state.Id, 24), MoreButton(24)));
            var switcher = Switcher(pages, state.Id, SwitcherFit(space.Width - 40), 20); switcher.HorizontalAlignment = HorizontalAlignment.Left; switcher.Margin = new Thickness(-4, 8, 0, 2);
            Grid.SetRow(switcher, 1); root.Children.Add(switcher);
            var quotas = new StackPanel();
            int fit = Math.Max(1, (int)((space.Height - (days.Count > 0 ? 250 : 150)) / 66));
            if (state.Quotas.Count > 0) {
                foreach (Quota quota in state.Quotas.Take(fit)) quotas.Children.Add(FullRow(quota, state));
                if (state.Quotas.Count > fit) { var more = Label("另有 " + (state.Quotas.Count - fit) + " 项额度 · 拉高窗口或在完整面板查看", 10, InkFaint); more.Margin = new Thickness(0, 8, 0, 0); quotas.Children.Add(more); }
            } else if (state.Balances.Count > 0 || days.Count == 0 || NeedsConnect(state)) { var alt = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; AlternateBody(alt, state, days, 28); quotas.Children.Add(alt); }
            else { var note = Label(ProviderCatalog.LocalOnly(state.Id) ? "没有账户额度 · 只统计本机日志" : StatusWord(state) + " · 下面是本机用量", 11, InkDim); note.Margin = new Thickness(0, 10, 0, 0); quotas.Children.Add(note); }
            if (state.ResetCreditsAvailable.HasValue) {
                var credits = Row(); credits.Margin = new Thickness(0, 10, 0, 0);
                AddRow(credits, Label("限额重置额度", 11, InkDim), Label(state.ResetCreditsAvailable + " 次可用", 11, Ink)); quotas.Children.Add(credits);
            }
            if (state.ProductUsage != null && state.ProductUsage.Count > 0) quotas.Children.Add(ProductUsageBlock(state));
            Grid.SetRow(quotas, 2); root.Children.Add(quotas);
            if (days.Count > 0 || usageIndexes.ContainsKey(state.Id)) {
                RangeData data = RangeUsage(new List<string> { state.Id }, RangeFor(state.Id), false, state.Id);
                var metrics = new StackPanel();
                metrics.Children.Add(new Border { Height = 1, Background = Hairline, Margin = new Thickness(0, 12, 0, 8) });
                var period = Row(); period.Margin = new Thickness(0, 0, 0, 6);
                AddRow(period, Label(ProviderCatalog.UsageTitle(state.Id), 10.5, InkDim), RangeSelector(state.Id, RenderCompact, true)); metrics.Children.Add(period);
                bool timed = OutputTiming.Supported(state.Id);
                var grid = new UniformGrid { Columns = timed ? 4 : 3 };
                grid.Children.Add(Metric("Token", Compact(data.Total.Tokens()))); grid.Children.Add(Metric("费用", data.Total.D > 0 || data.Total.Tokens() > data.Total.U ? Usd(data.Total.D) : "—")); grid.Children.Add(Metric("请求", data.Total.R > 0 ? data.Total.R.ToString("N0", CultureInfo.InvariantCulture) : "—"));
                if (timed) { Control speed = Metric("速度", OutputTiming.Text(data.Total.Speed())); ToolTip.SetTip(speed, OutputTiming.Definition); grid.Children.Add(speed); }
                metrics.Children.Add(grid);
                Grid.SetRow(metrics, 3); root.Children.Add(metrics);
                // The chart grows with the window; click a bar to pin that day (or hour).
                var lower = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
                if (data.Bars.Count > 0) lower.Children.Add(UsageChart("c-large|" + state.Id, "", data.Bars, Clamp(space.Height * .16, 34, 170), true, true, RenderCompact, true, timed ? ProviderCatalog.Color(state.Id) : null));
                Control mix = space.Height >= 560 ? Composition(data.Total.I, data.Total.O, data.Total.C, data.Total.W, "Token 构成") : null;
                if (mix != null) lower.Children.Add(mix);
                Grid.SetRow(lower, 4); root.Children.Add(lower);
            }
            // Phase 5：多账户时页脚显示「活动账户名 · 邮箱」（遮挡口径与完整面板一致）。
            string account = AccountLabel(state.Id, state.Account);
            var foot = Label(account, 10, InkFaint); foot.Margin = new Thickness(0, 6, 0, 0);
            Grid.SetRow(foot, 6); root.Children.Add(foot);
            return root;
        }
        // Large "概览": 30-day total and chart, then every enabled provider's headline window.
        private Control OverviewLarge(List<string> pages, Size space) {
            var root = new Grid { Margin = new Thickness(16, 11, 12, 10) };
            foreach (bool star in new[] { false, false, false, false, true, false }) root.RowDefinitions.Add(new RowDefinition { Height = star ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            root.Children.Add(PageHeader("overview", 18, 14.5, false, RefreshAllButton(24), MoreButton(24)));
            var switcher = Switcher(pages, "overview", SwitcherFit(space.Width - 40), 20); switcher.HorizontalAlignment = HorizontalAlignment.Left; switcher.Margin = new Thickness(-4, 8, 0, 4);
            Grid.SetRow(switcher, 1); root.Children.Add(switcher);
            List<DayUsage> all = history.Days.Where(d => pages.Contains(d.Agent)).ToList();
            RangeData data = RangeUsage(pages.Skip(1).ToList(), RangeFor("overview"), true, null);
            // 与完整面板 hero 同口径：大数字 = Token 总数，API 等价费用降为小字副行。
            var hero = new StackPanel();
            var period = Row(); AddRow(period, Label("Token 用量", 10.5, InkDim), RangeSelector("overview", RenderCompact, true)); hero.Children.Add(period);
            bool costKnown = data.Total.D > 0 || data.Total.Tokens() > data.Total.U;
            var amount = BigValue(data.Total.Tokens() > 0 ? Compact(data.Total.Tokens()) : "—", 24);
            ToolTip.SetTip(amount, I18n.T("所选时间段的本机 Token 总量（新增输入 + 输出 + 缓存）。费用按官方 API 价目估算，见下行。")); hero.Children.Add(amount);
            var todayText = Label("今日 " + TodayMoney(all) + (costKnown ? " · API 等价费用 " + Usd(data.Total.D) : "") + (data.Total.R > 0 ? " · " + data.Total.R.ToString("N0", CultureInfo.InvariantCulture) + " 次请求" : ""), 10.5, InkFaint); todayText.Margin = new Thickness(0, 3, 0, 0); Tabular(todayText); hero.Children.Add(todayText);
            Grid.SetRow(hero, 2); root.Children.Add(hero);
            if (data.Bars.Count > 0) { var chart = UsageChart("c-overview", "", data.Bars, Clamp(space.Height * .13, 34, 140), true, true, RenderCompact); chart.VerticalAlignment = VerticalAlignment.Top; Grid.SetRow(chart, 3); root.Children.Add(chart); }
            var list = new StackPanel();
            list.Children.Add(new Border { Height = 1, Background = Hairline, Margin = new Thickness(0, 8, 0, 4) });
            List<string> ids = Urgent(pages.Skip(1)).ToList();
            int fit = Math.Max(3, (int)((space.Height - 190 - Clamp(space.Height * .13, 34, 140)) / 24));
            foreach (string id in ids.Take(ids.Count > fit ? fit - 1 : fit)) list.Children.Add(OverviewLine(id, true, space.Width > 480 ? 16 : 12));
            if (ids.Count > fit) list.Children.Add(MoreLine(ids.Count - (fit - 1)));
            Grid.SetRow(list, 4); root.Children.Add(list);
            var foot = Label(ConnectedSummary(pages.Skip(1).ToList()), 10, InkFaint); foot.Margin = new Thickness(0, 4, 0, 0);
            Grid.SetRow(foot, 5); root.Children.Add(foot);
            return root;
        }

        // ── Connecting inside a compact size ──────────────────────────────
        private Control CompactConnectPage(ProviderState state, Size space) {
            var root = new Grid { Margin = new Thickness(8, 7, 8, 6) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition());
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new ColumnDefinition());
            var back = Styled(new Button(), "icon-button");
            back.Width = 24; back.Height = 24;
            back.Content = Icons.GlyphBound(back, Icons.ChevronRight, 11);
            ToolTip.SetTip(back, "返回（Esc）");
            AutomationProperties.SetName(back, "返回");
            back.Click += delegate { CloseCompactConnect(); };
            head.Children.Add(back);
            Control icon = Icon(state.Id, 14); icon.Margin = new Thickness(4, 0, 6, 0); icon.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(icon, 1); head.Children.Add(icon);
            var title = Label((state.Status == "expired" ? "重新登录 " : "连接 ") + ProviderCatalog.Name(state.Id), 12.5, Ink); title.FontWeight = FontWeight.SemiBold; Grid.SetColumn(title, 2); head.Children.Add(title);
            root.Children.Add(head);
            var content = new StackPanel { Margin = new Thickness(4, 2, 4, 0) };
            // Actions first; the setup help (often long) follows them. Errors explain what went
            // wrong, so they stay above.
            bool help = state.Status == "setup";
            TextBlock message = null;
            if (state.Status == "loading") content.Children.Add(Label("正在连接…", 11, AccentBrush));
            else if (!String.IsNullOrEmpty(state.Message) && state.Status != "cached") message = new TextBlock { Text = state.Message, FontSize = 10.5, Foreground = help ? InkFaint : Brush("#F5D6AE"), TextWrapping = TextWrapping.Wrap, LineHeight = 15, Margin = new Thickness(0, help ? 8 : 4, 0, 0) };
            if (message != null && !help) content.Children.Add(message);
            content.Children.Add(ConnectPanel(state, space.Width < 320));
            if (message != null && help) content.Children.Add(message);
            // Background=透明：命中测试覆盖卡片缝隙，滚轮/拖动在空白处同样生效（App.axaml
            // ScrollViewer 模板把它绑到 PART_ContentPresenter；Fluent 默认同值，此处显式）。
            var scroll = new ScrollViewer { Content = content, Background = Brushes.Transparent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
            Grid.SetRow(scroll, 1); root.Children.Add(scroll);
            return root;
        }

        // ── Pieces ────────────────────────────────────────────────────────
        private static Grid PageGrid(Thickness margin, int rows, int starRow) {
            var grid = new Grid { Margin = margin };
            for (int i = 0; i < rows; i++) grid.RowDefinitions.Add(new RowDefinition { Height = i == starRow ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            return grid;
        }
        private static double Clamp(double value, double min, double max) { return Math.Max(min, Math.Min(max, value)); }
        private static int SwitcherFit(double width) { return Math.Max(2, (int)(width / 23)); }
        private Grid PageHeader(string id, double icon, double size, bool plan, params Control[] right) {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            Control glyph = Icon(id, icon); glyph.Margin = new Thickness(0, 0, 7, 0); glyph.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(glyph);
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var name = Label(id == "overview" ? "概览" : ProviderCatalog.Name(id), size, Ink); name.FontWeight = FontWeight.SemiBold; title.Children.Add(name);
            ProviderState state;
            if (plan && states.TryGetValue(id, out state) && !String.IsNullOrEmpty(state.Plan) && state.Plan != ProviderCatalog.Name(id)) { Border pill = Pill(state.Plan); pill.Margin = new Thickness(6, 1, 0, 0); ((TextBlock)pill.Child).FontSize = 10; title.Children.Add(pill); }
            Grid.SetColumn(title, 1); grid.Children.Add(title);
            foreach (Control element in right) {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                element.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(element, grid.ColumnDefinitions.Count - 1); grid.Children.Add(element);
            }
            return grid;
        }
        private Button MoreButton(double size) {
            var more = Styled(new Button(), "icon-button");
            more.Width = size; more.Height = size;
            more.Content = Icons.GlyphBound(more, Icons.Size, 11);
            ToolTip.SetTip(more, "更多：尺寸、刷新、复制、设置（也可以右键）");
            AutomationProperties.SetName(more, "更多");
            more.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; SizeMenu().Open(more); };
            return more;
        }
        // Headline: the chosen window (click cycles through the provider's windows), else the lowest.
        // Cursor's Grok Bot row stays in the list, but it is not the automatic headline.
        private Quota Headline(ProviderState state) {
            string label;
            if (headlineChoice.TryGetValue(state.Id, out label)) { Quota chosen = state.Quotas.FirstOrDefault(q => q.Label == label); if (chosen != null) return chosen; }
            return AutomaticQuota(state);
        }
        private static Quota AutomaticQuota(ProviderState state) {
            return ProviderOrder.MainQuota(state);   // 共享口径见 Core/ProviderOrder.cs（概览排序同源）
        }
        private void ChooseHeadline(ProviderState state, Quota quota) { headlineChoice[state.Id] = quota.Label; RenderCompact(); }
        private Control HeadlineButton(ProviderState state, Quota main, double size) {
            var stack = new StackPanel();
            stack.Children.Add(BigValue(main.Remaining.ToString("0") + "%", size));
            int index = state.Quotas.IndexOf(main), count = state.Quotas.Count;
            var caption = Label(main.Label + "剩余" + (count > 1 ? "  " + (index + 1) + "/" + count : ""), 10.5, InkDim); stack.Children.Add(caption);
            if (count < 2) { ToolTip.SetTip(stack, I18n.T(main.Label + " 已用 " + main.Used.ToString("0.#") + "% · 重置 " + LocalTime(main.ResetUtc))); return stack; }
            var button = new Button { Content = stack, HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 1, 6, 2), Margin = new Thickness(-4, -1, 0, 0) };
            ToolTip.SetTip(button, "点击切换额度窗口（共 " + count + " 个）");
            AutomationProperties.SetName(button, "切换额度窗口");
            button.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; ChooseHeadline(state, state.Quotas[(index + 1) % count]); };
            return button;
        }
        private static TextBlock BigValue(string text, double size) { var value = Label(text, size, Ink); value.FontWeight = FontWeight.SemiBold; Tabular(value); value.Margin = new Thickness(0, 0, 0, -2); return value; }
        private static bool NeedsConnect(ProviderState state) { return !ProviderCatalog.LocalOnly(state.Id) && (state.Status == "setup" || state.Status == "expired" || state.Status == "error"); }
        private void AlternateBody(StackPanel body2, ProviderState state, List<DayUsage> days, double size) {
            Balance balance = state.Balances.FirstOrDefault();
            if (balance != null) { body2.Children.Add(BigValue(balance.Amount.ToString("N2", CultureInfo.InvariantCulture), size)); body2.Children.Add(Label(balance.Currency + " 可用余额", 10.5, InkDim)); }
            else if (days.Count > 0 && !NeedsConnect(state)) {
                // Token 优先（任务2）：大数字 = 30 天 Token，费用降为小字。
                body2.Children.Add(BigValue(Compact(days.Sum(d => d.Tokens)), size));
                body2.Children.Add(Label("近 30 天 Token · 今日 " + TodayMoney(days), 10.5, InkDim));
            }
            else {
                body2.Children.Add(BigValue("—", size));
                body2.Children.Add(new TextBlock { Text = state.Status == "loading" ? "正在读取…" : state.Status == "ready" ? "暂无额度数据" : StatusWord(state), FontSize = 10.5, Foreground = InkDim, TextWrapping = TextWrapping.Wrap });
            }
            if (NeedsConnect(state)) body2.Children.Add(CompactConnectButton(state));
        }
        // Connecting stays in this size: the button opens the inline connect page.
        private Button CompactConnectButton(ProviderState state) {
            string text = state.Status == "expired" ? "重新登录" : state.Status == "error" ? "重新连接" : ProviderCatalog.Custom.ContainsKey(state.Id) ? "编辑接口" : "连接";
            var button = Styled(new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 8, 0, 0) }, "secondary");
            ToolTip.SetTip(button, "在这里登录或填写密钥");
            AutomationProperties.SetName(button, text + " " + ProviderCatalog.Name(state.Id));
            button.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; OpenCompactConnect(state.Id); };
            return button;
        }
        // Medium: the headline's row, then other windows (click one to make it the headline).
        private Control CompactRow(Quota quota, string id, int cells, bool gap, ProviderState promote) {
            var stack = new StackPanel();
            var row = Row();
            var label = new TextBlock { FontSize = 11, Foreground = InkDim, TextTrimming = TextTrimming.CharacterEllipsis };
            label.Inlines.Add(new Run(quota.Label + " ")); label.Inlines.Add(new Run(quota.Remaining.ToString("0") + "%") { Foreground = Ink, FontWeight = FontWeight.SemiBold });
            string reset = Countdown(quota.ResetUtc); if (reset.EndsWith("后重置")) reset = reset.Substring(0, reset.Length - 3);
            AddRow(row, label, Label(reset, 10, InkFaint)); stack.Children.Add(row);
            var bar = SegmentBar(quota.Remaining, ProviderCatalog.Color(id), cells, 6); bar.Margin = new Thickness(0, 5, 0, 0); stack.Children.Add(bar);
            string tip = quota.Label + " 已用 " + quota.Used.ToString("0.#") + "% · 重置 " + LocalTime(quota.ResetUtc);
            if (promote == null) { stack.Margin = new Thickness(0, gap ? 10 : 0, 0, 0); ToolTip.SetTip(stack, tip); return stack; }
            var button = new Button { Content = stack, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(-4, gap ? 6 : 0, -4, 0) };
            ToolTip.SetTip(button, tip + "\n点击设为主显示");
            button.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; ChooseHeadline(promote, quota); };
            return button;
        }
        // Large: a window with pace; click to expand the exact reset time and usage.
        private Control FullRow(Quota quota, ProviderState state) {
            string key = state.Id + "|" + quota.Label; bool open = expandedQuotas.Contains(key);
            var stack = new StackPanel();
            var row = Row();
            var heading = new TextBlock { FontSize = 12.5, Foreground = Ink, TextTrimming = TextTrimming.CharacterEllipsis };
            heading.Inlines.Add(new Run(quota.Label + " ")); heading.Inlines.Add(new Run(quota.Remaining.ToString("0.#") + "%") { FontSize = 16, FontWeight = FontWeight.SemiBold }); heading.Inlines.Add(new Run(" 剩余"));
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(Label(Countdown(quota.ResetUtc), 10, InkDim));
            right.Children.Add(Icons.Glyph(open ? Icons.ChevronUp : Icons.ChevronDown, 8, InkFaint, HorizontalAlignment.Left, VerticalAlignment.Center).Also(g => g.Margin = new Thickness(6, 1, 0, 0)));
            AddRow(row, heading, right); stack.Children.Add(row);
            var bar = SegmentBar(quota.Remaining, ProviderCatalog.Color(state.Id), 24, 7); bar.Margin = new Thickness(0, 6, 0, 0); stack.Children.Add(bar);
            Control pace = PaceLine(quota, 10);
            if (pace != null) { pace.Margin = new Thickness(0, 5, 0, 0); ToolTip.SetTip(pace, null); stack.Children.Add(pace); }
            if (open) {
                string windowLength = quota.WindowSeconds > 0 ? WindowLength(quota, "") + " · " : "";
                var detail = new TextBlock { FontSize = 10, Foreground = InkDim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), LineHeight = 15 };
                detail.Text = I18n.T(windowLength + "已用 " + quota.Used.ToString("0.#") + "% · " + LocalTime(quota.ResetUtc) + " 重置" + (pace != null ? "\n节奏按本周期平均速度线性估算，不是官方承诺。" : ""));
                stack.Children.Add(detail);
            }
            var button = new Button { Content = stack, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(5, 4, 5, 5), Margin = new Thickness(-5, 7, -5, 0) };
            ToolTip.SetTip(button, open ? "点击收起" : "点击展开重置时间与用量");
            AutomationProperties.SetName(button, quota.Label + " 额度");
            button.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; if (!expandedQuotas.Add(key)) expandedQuotas.Remove(key); RenderCompact(); };
            return button;
        }
        private static Control Metric(string title, string value) {
            var stack = new StackPanel(); stack.Children.Add(Label(title, 10, InkFaint));
            var number = Label(value, 14, Ink); number.FontWeight = FontWeight.SemiBold; Tabular(number); number.Margin = new Thickness(0, 2, 0, 0); stack.Children.Add(number);
            return stack;
        }
        // 30 daily columns that stretch with the window; hover a day for its figures.
        private static Control FluidChart(List<DayUsage> days, string id) {
            var series = Enumerable.Range(0, 30).Select(i => DateTime.Today.AddDays(i - 29)).Select(date => {
                string key = HistoryService.DayKey(date);
                return new { Date = date, Cost = days.Where(d => d.Day == key && d.CostKnown).Sum(d => d.Cost), Tokens = days.Where(d => d.Day == key).Sum(d => d.Tokens) };
            }).ToList();
            double max = Math.Max(.01, series.Max(d => d.Cost));
            var columns = new UniformGrid { Columns = 30, Rows = 1 };
            for (int i = 0; i < 30; i++) {
                var item = series[i];
                var column = new Grid { Margin = new Thickness(.75, 0, .75, 0), Background = Brushes.Transparent };
                ToolTip.SetTip(column, item.Date.ToString(I18n.T("M月d日 ddd"), I18n.Culture) + "\n" + Usd(item.Cost) + " · " + Compact(item.Tokens) + " Token");
                if (item.Cost > 0) {
                    column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(max - item.Cost, GridUnitType.Star) }); column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(item.Cost, GridUnitType.Star), MinHeight = 2 });
                    var bar = new Border { Background = Brush(ProviderCatalog.Color(id)), Opacity = i == 29 ? 1 : .55, CornerRadius = new CornerRadius(1, 1, 0, 0) }; Grid.SetRow(bar, 1); column.Children.Add(bar);
                } else column.Children.Add(new Border { Height = 1, Background = Brush("#FFFFFF"), Opacity = .12, VerticalAlignment = VerticalAlignment.Bottom });
                columns.Children.Add(column);
            }
            return columns;
        }
        private static Control FluidStackedChart(List<DayUsage> days) {
            List<string> order = ProviderCatalog.Ids.Where(id => days.Any(d => d.Agent == id)).ToList();
            var series = Enumerable.Range(0, 30).Select(i => DateTime.Today.AddDays(i - 29)).Select(date => {
                string key = HistoryService.DayKey(date);
                return new { Date = date, Parts = order.Select(id => new { Id = id, Cost = days.Where(x => x.Day == key && x.Agent == id && x.CostKnown).Sum(x => x.Cost) }).Where(p => p.Cost > 0).ToList() };
            }).ToList();
            double max = Math.Max(.01, series.Max(d => d.Parts.Sum(p => p.Cost)));
            var columns = new UniformGrid { Columns = 30, Rows = 1 };
            foreach (var item in series) {
                double total = item.Parts.Sum(p => p.Cost);
                string tip = item.Date.ToString(I18n.T("M月d日 ddd"), I18n.Culture) + " · " + Usd(total) + String.Concat(item.Parts.Select(p => "\n" + ProviderCatalog.Name(p.Id) + "  " + Usd(p.Cost)));
                var column = new Grid { Margin = new Thickness(.75, 0, .75, 0), Background = Brushes.Transparent };
                ToolTip.SetTip(column, tip);
                if (total <= 0) column.Children.Add(new Border { Height = 1, Background = Brush("#1FFFFFFF"), VerticalAlignment = VerticalAlignment.Bottom });
                else {
                    column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(max - total, GridUnitType.Star) });
                    foreach (var part in item.Parts.AsEnumerable().Reverse()) {
                        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(part.Cost, GridUnitType.Star), MinHeight = 1.5 });
                        var segment = new Border { Background = Brush(ProviderCatalog.Color(part.Id)), Opacity = item.Date == DateTime.Today ? 1 : .7 };
                        Grid.SetRow(segment, column.RowDefinitions.Count - 1); column.Children.Add(segment);
                    }
                }
                columns.Children.Add(column);
            }
            return columns;
        }
        private Control CompactFooter(ProviderState state, List<DayUsage> days) {
            var row = Row();
            // Token 优先（任务2）：主行 = 今日 / 30 天 Token；费用降为 tooltip。
            string spend = days.Count > 0
                ? "今日 " + Compact(days.Where(d => d.Day == Today()).Sum(d => d.Tokens)) + " · 30 天 " + Compact(days.Sum(d => d.Tokens)) + " Token"
                : state.Balances.Count > 0 ? "余额 " + state.Balances[0].Currency + " " + state.Balances[0].Amount.ToString("N2", CultureInfo.InvariantCulture) : "";
            var left = Label(spend, 10.5, InkDim); Tabular(left);
            if (days.Count > 0) ToolTip.SetTip(left, I18n.T("费用为 API 等价估算（非订阅账单）：今日 {0} · 30 天 {1}", TodayMoney(days), Money(days)));
            AddRow(row, left, Label(state.Status == "ready" ? UpdatedAgo(state.LastSuccess) : StatusWord(state), 10, InkFaint));
            return row;
        }
        private string ConnectedSummary(List<string> ids) {
            int connected = ids.Count(id => states.ContainsKey(id) && states[id].Status == "ready");
            return connected + " / " + ids.Count + " 已连接" + (lastRefresh == DateTime.MinValue ? "" : I18n.English ? " · updated " + lastRefresh.ToString("HH:mm") : " · " + lastRefresh.ToString("HH:mm") + " 更新");
        }
        // Overview order shared with the full panel's provider cards (ProviderOrder): the
        // providers with a readable quota/balance first, tightest remaining on top; then the
        // ready ones without readings (local-usage-only); then loading/setup/error. Stable
        // within a tier, and nothing is filtered out.
        private IEnumerable<string> Urgent(IEnumerable<string> ids) {
            return ids.Select((id, i) => new { Id = id, Index = i, State = states.TryGetValue(id, out ProviderState s) ? s : null })
                .OrderBy(x => ProviderOrder.Tier(x.State)).ThenBy(x => ProviderOrder.Tightness(x.State)).ThenBy(x => x.Index).Select(x => x.Id);
        }
        private Control OverviewLine(string id, bool name, int cells) {
            ProviderState state; if (!states.TryGetValue(id, out state)) state = new ProviderState { Id = id };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = name ? new GridLength(1, GridUnitType.Star) : new GridLength(0) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = name ? new GridLength(cells * 8) : new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            Control icon = Icon(id, 13); icon.Margin = new Thickness(0, 0, 7, 0); icon.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(icon);
            Quota main = AutomaticQuota(state);
            if (name) { var label = Label(ProviderCatalog.Name(id), 11.5, Ink); Grid.SetColumn(label, 1); grid.Children.Add(label); }
            string tip;
            if (main != null) {
                var bar = SegmentBar(main.Remaining, ProviderCatalog.Color(id), cells, 5); bar.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(bar, 2); grid.Children.Add(bar);
                var pct = Label(main.Remaining.ToString("0") + "%", 11.5, Ink); pct.FontWeight = FontWeight.SemiBold; pct.HorizontalAlignment = HorizontalAlignment.Right; Tabular(pct); Grid.SetColumn(pct, 3); grid.Children.Add(pct);
                tip = ProviderCatalog.Name(id) + " · " + main.Label + " 剩余 " + main.Remaining.ToString("0.#") + "% · " + Countdown(main.ResetUtc);
            } else {
                string text = state.Balances.Count > 0 ? state.Balances[0].Currency + " " + state.Balances[0].Amount.ToString("N2", CultureInfo.InvariantCulture) : ProviderCatalog.LocalOnly(id) || state.Status == "ready" ? "本机用量" : StatusWord(state);
                var note = Label(text, 10.5, NeedsConnect(state) ? WarnBrush : state.Status == "ready" ? InkDim : InkFaint); note.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(note, 2); Grid.SetColumnSpan(note, 2); grid.Children.Add(note);
                tip = ProviderCatalog.Name(id) + " · " + text + (NeedsConnect(state) ? "\n点击后可在这里连接" : "");
            }
            var button = new Button { Content = grid, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(-5, 0, -5, 0) };
            ToolTip.SetTip(button, tip);
            AutomationProperties.SetName(button, "查看 " + ProviderCatalog.Name(id));
            button.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; SelectCompact(id); };
            return button;
        }
        private Control MoreLine(int count) {
            var more = Label("另有 " + count + " 个平台 · 滚轮或 ←/→ 翻页", 10, InkFaint); more.Margin = new Thickness(0, 3, 0, 0);
            return more;
        }
        private Control Switcher(List<string> pages, string current, int max, double size) {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            List<string> shown = pages.Take(max).ToList();
            if (!shown.Contains(current) && pages.Contains(current) && shown.Count > 0) shown[shown.Count - 1] = current;
            foreach (string id in shown) {
                string captured = id; bool active = id == current;
                Control glyph = Icon(id, size - 8); glyph.Opacity = active ? 1 : .55;
                var button = new Button { Content = glyph, Width = size + 4, Height = size + 4, Padding = new Thickness(0), Margin = new Thickness(1, 0, 1, 0), Background = Brush(active ? "#1FFFFFFF" : "#00FFFFFF") };
                ToolTip.SetTip(button, id == "overview" ? "概览" : ProviderCatalog.Name(id) + " · " + (states.ContainsKey(id) ? StatusWord(states[id]) : ""));
                AutomationProperties.SetName(button, "切换到 " + (id == "overview" ? "概览" : ProviderCatalog.Name(id)));
                button.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; SelectCompact(captured); };
                row.Children.Add(button);
            }
            if (pages.Count > max) {
                var more = Styled(new Button(), "icon-button");
                more.Width = 20; more.Height = 22;
                more.Content = Icons.GlyphBound(more, Icons.ChevronRight, 9);
                ToolTip.SetTip(more, "下一个（滚轮或 ←/→ 也可以切换）");
                more.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; CycleCompact(1); }; row.Children.Add(more);
            }
            return row;
        }
        private Control Dots(List<string> pages, string current) {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (string id in pages.Take(14)) {
                string captured = id; bool active = id == current;
                string color = id == "overview" ? "#5CC8E0" : ProviderCatalog.Color(id);
                var dot = new Ellipse { Width = active ? 6 : 5, Height = active ? 6 : 5, Fill = Brush(active ? color : "#40FFFFFF") };
                if (id == "overview" && !active) { dot.Fill = Brushes.Transparent; dot.Stroke = Brush("#66FFFFFF"); dot.StrokeThickness = 1; }
                var hit = new Button { Content = dot, Width = 11, Height = 14, Padding = new Thickness(0) };
                ToolTip.SetTip(hit, id == "overview" ? "概览" : ProviderCatalog.Name(id));
                AutomationProperties.SetName(hit, "切换到 " + (id == "overview" ? "概览" : ProviderCatalog.Name(id)));
                hit.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; SelectCompact(captured); };
                row.Children.Add(hit);
            }
            return row;
        }
    }
}
