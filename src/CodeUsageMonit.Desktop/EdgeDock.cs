// 窄条（edge）显示尺寸：竖直 dock 形态的第 5 档（Compact.cs 的 DisplaySizes 序列末尾）。
// 顶部今日汇总（Token 大字 + 今日 API 等价费用小字），下面每个有额度读数的平台一行环形
// 进度：弧长 = 本地挑选额度的剩余%——WindowSeconds>0 里窗口最短的一个（EdgeQuota，
// 最贴近「现在还能用多少」），没有带窗口的额度时回退 ProviderOrder.MainQuota 的最紧剩余；
// 环色 = 平台色、剩余 <10% 转暖色（与 SegmentBar 同阈值），环心放平台
// 图标，环下百分比数字，tooltip 逐行列出该平台全部额度窗口。点平台行切回完整面板并跳到该平台页签，点顶部汇总回概览；整条
// 可拖动、右键尺寸菜单（AppMonitor 对 compactRoot 挂的 EnableDrag/AttachRightClick 覆盖）。
// 无新定时器、无新磁盘 IO：每分钟 Render 重建与各额度刷新路径照常经 RenderCompact 走到这里。
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using AvaSize = Avalonia.Size;
using Path = Avalonia.Controls.Shapes.Path;

namespace CodeUsageMonit {
    public sealed partial class MonitorPanel {
        // 窄条布局常量：宽度 84；头部高度与行距只用于窗口高度自适应（内容实际排布说了算，
        // 这里决定进入 edge / 平台增减时的窗口初始高；超过 MaxSize 的部分交给 ScrollViewer）。
        internal const double EdgeWidth = 84, EdgeHeadHeight = 78, EdgeRowPitch = 60;
        // 上一次高度自适应时的平台行数（-1 = 尚未适配）。
        private int edgeFitRows = -1;
        // 窄条平台行清单：启用平台按 ProviderOrder（Urgent 同款）排序，只留有 Quotas 的。
        private List<string> EdgeQuotaIds() {
            return Urgent(EnabledIds()).Where(id => states.TryGetValue(id, out ProviderState s) && s.Quotas.Count > 0).ToList();
        }

        private Control EdgeDockContent() {
            var root = new StackPanel { Margin = new Thickness(5, 10, 5, 8) };
            root.Children.Add(EdgeSummaryButton());
            root.Children.Add(new Border { Height = 1, Background = Hairline, Margin = new Thickness(9, 8, 9, 3) });
            List<string> ids = EdgeQuotaIds();
            if (ids.Count == 0) {
                TextBlock empty = Label(I18n.T("暂无额度读数"), 9.5, InkFaint);
                empty.TextWrapping = TextWrapping.Wrap; empty.TextAlignment = TextAlignment.Center;
                empty.Margin = new Thickness(0, 16, 0, 6);
                root.Children.Add(empty);
            } else {
                var rows = new StackPanel();
                foreach (string id in ids) rows.Children.Add(EdgeRow(id));
                root.Children.Add(rows);
            }
            // 高度封顶时滚动：滚动条隐藏（窄条不放视觉杂讯），滚轮仍可滚。Background=透明
            // 让平台行之间的缝隙可命中（滚轮盲区修复，App.axaml 模板绑到 presenter）。
            return new ScrollViewer { Content = root, Background = Brushes.Transparent, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        }

        // 窄条高度按内容自适应（顶部汇总 + 行数 ×N，封顶 MaxSize）：只在进入 edge 或平台
        // 行数变化时重设窗口高，用户手动调过的宽高不会被每分钟渲染拉回去。
        private void FitEdgeHeight() {
            if (window == null || activeSize != "edge" || window.WindowState != WindowState.Normal) return;
            int rows = EdgeQuotaIds().Count;
            if (rows == edgeFitRows) return;
            edgeFitRows = rows;
            double s = config.UiScale;
            Size min = WindowFrame.MinSize("edge"), max = WindowFrame.MaxSize("edge");
            double desired = (EdgeHeadHeight + rows * EdgeRowPitch + 10) * s;
            double height = Math.Max(min.Height * s, Math.Min(max.Height * s, desired));
            if (Math.Abs(window.Height - height) > .5) window.Height = height;
        }

        // 顶部今日汇总：今日 Token（Compact 口径）+ 今日 API 等价费用小字；完全无历史数据
        // 时读 “--”。点击回完整面板概览（与尺寸菜单“在完整面板中查看”同款跳转）。
        private Control EdgeSummaryButton() {
            List<string> enabled = EnabledIds().ToList();
            string today = Today();
            List<DayUsage> todayRows = history.Days.Where(d => d.Day == today && enabled.Contains(d.Agent)).ToList();
            bool anyData = history.Days.Count > 0;
            string tokens = todayRows.Count > 0 ? Compact(todayRows.Sum(d => d.Tokens)) : anyData ? "0" : "--";
            string cost = anyData ? TodayMoney(history.Days.Where(d => enabled.Contains(d.Agent))) : "--";
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(Label(I18n.English ? "TODAY" : "今日", 8.5, InkFaint));
            var amount = BigValue(tokens, 14.5); amount.HorizontalAlignment = HorizontalAlignment.Center; amount.Margin = new Thickness(0); stack.Children.Add(amount);
            var costLabel = Label(cost, 9.5, InkDim); costLabel.HorizontalAlignment = HorizontalAlignment.Center; stack.Children.Add(costLabel);
            var button = new Button { Content = stack, Padding = new Thickness(0), Background = Brush("#00FFFFFF"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(button, I18n.T("在完整面板中查看") + " · " + I18n.T("今日汇总"));
            AutomationProperties.SetName(button, I18n.T("今日汇总"));
            button.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; SetDisplaySize("full"); SelectProvider("overview"); };
            return button;
        }

        // 窄条本地挑选（任务5）：WindowSeconds > 0 的额度按窗口时长升序取第一个——最短
        // 时间单位最贴近「现在还能用多少」（kimi/codex 的 5 小时 18000 优先于每周/每月；
        // 只有月度窗口的 mimo 2592000 同样命中）。没有带窗口的额度（grok/cursor 的账期类，
        // WindowSeconds=0）回退 ProviderOrder.MainQuota（最紧剩余口径）。
        // Cursor 的 Grok Bot 行不参与自动挑选，排除规则与 MainQuota 同一条；MainQuota 本身
        // 不改动（概览排序、紧凑 Headline、OverviewLine 三处共享）。
        private static Quota EdgeQuota(ProviderState state) {
            IEnumerable<Quota> pool = state.Id == "cursor" ? state.Quotas.Where(q => q.Label != Parsers.CursorGrokLabel) : state.Quotas;
            Quota timed = pool.Where(q => q.WindowSeconds > 0).OrderBy(q => q.WindowSeconds).FirstOrDefault();
            return timed ?? ProviderOrder.MainQuota(state);
        }

        // 平台行：环形进度（剩余%）+ 环下数字；点击切回完整面板并跳到该平台页签。
        // tooltip 遍历全部 Quotas 逐行列出（Label + 剩余% + 倒计时）：环上只放最短窗口，
        // 周/月等其它窗口悬停可见。
        private Control EdgeRow(string id) {
            ProviderState state = states[id];
            Quota main = EdgeQuota(state);
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(EdgeRing(id, main.Remaining, 36, 3, 16));
            var pct = Label(main.Remaining.ToString("0") + "%", 10, Ink);
            pct.HorizontalAlignment = HorizontalAlignment.Center; pct.Margin = new Thickness(0, 3, 0, 0);
            stack.Children.Add(pct);
            string tip = ProviderCatalog.Name(id) + "\n" + String.Join("\n", state.Quotas.Select(q => q.Label + " 剩余 " + q.Remaining.ToString("0.#") + "% · " + Countdown(q.ResetUtc)));
            var button = new Button { Content = stack, Padding = new Thickness(2, 5, 2, 3), Background = Brush("#00FFFFFF"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(button, tip);
            AutomationProperties.SetName(button, "查看 " + ProviderCatalog.Name(id));
            button.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; SetDisplaySize("full"); SelectProvider(id); };
            return button;
        }

        // 环形进度：底环（淡白）+ 剩余弧（平台色，<10% 转暖，同 SegmentBar 阈值），环心叠平台图标。
        internal Control EdgeRing(string id, double remaining, double size, double thickness, double iconSize) {
            var grid = new Grid { Width = size, Height = size };
            double diameter = size - thickness;   // 描边画在中线上：外缘正好贴着 size。
            grid.Children.Add(new Ellipse { Stroke = Brush("#30FFFFFF"), StrokeThickness = thickness, Width = diameter, Height = diameter });
            Path arc = RingArc(size / 2, size / 2, diameter / 2, Math.Max(0, Math.Min(100, remaining)) / 100 * 360, thickness, Brush(remaining < 10 ? "#E6A083" : ProviderCatalog.Color(id)));
            if (arc != null) grid.Children.Add(arc);
            grid.Children.Add(Icon(id, iconSize));
            return grid;
        }
        // 一段圆弧路径（从正上方顺时针扫 sweep 度，圆帽描边）。<0.5° 不画；≥359.5° 拆两段
        // 180° 弧拼整圆（弧终点与起点重合时零角度弧是退化路径，画不出来）。
        internal static Path RingArc(double cx, double cy, double radius, double sweep, double thickness, IBrush stroke) {
            sweep = Math.Max(0, Math.Min(360, sweep));
            if (sweep < 0.5) return null;
            var geo = new StreamGeometry();
            using (StreamGeometryContext ctx = geo.Open()) {
                Point top = new Point(cx, cy - radius);
                ctx.BeginFigure(top, false);
                if (sweep >= 359.5) {
                    ctx.ArcTo(new Point(cx, cy + radius), new AvaSize(radius, radius), 0, true, SweepDirection.Clockwise, true);
                    ctx.ArcTo(top, new AvaSize(radius, radius), 0, true, SweepDirection.Clockwise, true);
                } else {
                    double rad = (sweep - 90) * Math.PI / 180;
                    ctx.ArcTo(new Point(cx + radius * Math.Cos(rad), cy + radius * Math.Sin(rad)), new AvaSize(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true);
                }
                ctx.EndFigure(false);
            }
            return new Path { Data = geo, Stroke = stroke, StrokeThickness = thickness, StrokeLineCap = PenLineCap.Round, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        }
    }
}
