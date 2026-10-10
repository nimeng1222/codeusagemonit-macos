// MonitorPanel 的共享构件（Presentation.cs:20 与 588-612、Settings.cs:413-443、
// App.cs LoadIcons/Icon 432-448）。WPF → Avalonia 映射：Brush → 缓存的 SolidColorBrush、
// Style 资源 → Classes、图标字体字形 → Icons 内嵌 path、Typography.Tabular → 无（ Avalonia 无该 API，保持调用点）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using AvaColor = Avalonia.Media.Color;
using Path = System.IO.Path;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Avalonia.Automation;
using Avalonia.Interactivity;

namespace CodeUsageMonit {
    // 应用级小工具（供 Controls.cs 等非 MonitorPanel 代码使用）：颜色解析与强调色。
    internal static class Ui {
        private static readonly Dictionary<string, IBrush> brushes = new Dictionary<string, IBrush>(StringComparer.Ordinal);
        public static IBrush Brush(string color) {
            IBrush brush; if (brushes.TryGetValue(color, out brush)) return brush;
            brush = new SolidColorBrush(AvaColor.Parse(color));
            brushes[color] = brush;
            return brush;
        }
        public static readonly IBrush AccentBrush = Brush("#5CC8E0");
        public static readonly IBrush Ink = Brush("#F2F3F5"), InkDim = Brush("#A7ADB7"), InkFaint = Brush("#7D838D");
    }

    // 颜色数学（Avalonia.Media.Color 版）。Core/Compat/AvaColor.cs 的 ColorMath 是 internal 且
    // 面向 Core 自己的 AvaColor 结构；这里为 UI 另实现一份。
    internal static class UiColor {
        public static Avalonia.Media.Color FromHsv(double h, double s, double v) {
            h = ((h % 360) + 360) % 360; double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c, r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; } else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
            return Avalonia.Media.Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }
        public static void ToHsv(Avalonia.Media.Color color, out double h, out double s, out double v) {
            double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max; s = max <= 0 ? 0 : d / max;
            if (d <= 0) h = 0; else if (max == r) h = 60 * (((g - b) / d) % 6); else if (max == g) h = 60 * ((b - r) / d + 2); else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }
        public static string Hex(Avalonia.Media.Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }
        public static bool TryParseHex(string text, out Avalonia.Media.Color color) {
            color = Colors.Black; string t = (text ?? "").Trim().TrimStart('#');
            if (t.Length == 3) t = new string(new[] { t[0], t[0], t[1], t[1], t[2], t[2] });
            int value;
            if (t.Length != 6 || !Int32.TryParse(t, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value)) return false;
            color = Avalonia.Media.Color.FromRgb((byte)(value >> 16), (byte)(value >> 8 & 255), (byte)(value & 255)); return true;
        }
        // WCAG 2 contrast ratio between two opaque colours (1 … 21).
        public static double ContrastRatio(Avalonia.Media.Color a, Avalonia.Media.Color b) {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + .05) / (Math.Min(la, lb) + .05);
        }
        private static double Luminance(Avalonia.Media.Color c) {
            Func<byte, double> lin = x => { double s = x / 255.0; return s <= .03928 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); };
            return .2126 * lin(c.R) + .7152 * lin(c.G) + .0722 * lin(c.B);
        }
    }

    public sealed partial class MonitorPanel {
        private readonly HashSet<string> connectionDetails = new HashSet<string>();
        // Keep in sync with the palette at the top of App.axaml (原 Panel.xaml:10-14).
        internal static readonly IBrush Ink = Ui.Ink, InkDim = Ui.InkDim, InkFaint = Ui.InkFaint,
            CardFill = Brush("#0AFFFFFF"), CardLine = Brush("#12FFFFFF"), Hairline = Brush("#14FFFFFF"),
            AccentBrush = Ui.AccentBrush, WarnBrush = Brush("#F2B36B"), GoodBrush = Brush("#7AD3A8");

        internal static IBrush Brush(string color) { return Ui.Brush(color); }
        // 图标库：id → 合并后的 path data（来自 Assets/icons/*.svg，见 LoadIcons）。
        private readonly Dictionary<string, string> icons = new Dictionary<string, string>();
        private void LoadIcons() {
            foreach (string id in ProviderCatalog.Ids) {
                try {
                    var xml = new XmlDocument(); xml.Load(Path.Combine(Store.Root, "icons", id + ".svg"));
                    var paths = xml.GetElementsByTagName("path");
                    if (paths.Count == 0) continue;
                    // WPF/Avalonia path markup: "F0" selects the even-odd fill rule used by some logos.
                    string outer = xml.OuterXml;
                    bool evenOdd = outer.Contains("fill-rule=\"evenodd\"") || outer.Contains("fill-rule:evenodd");
                    string all = evenOdd ? "F0 " : "";
                    foreach (XmlNode p in paths) { if (p.Attributes != null && p.Attributes["d"] != null) all += p.Attributes["d"].Value + " "; }
                    icons[id] = all;
                } catch { }
            }
        }
        // 平台图标：SVG 路径优先；概览/第三方与无图标的平台退到字形或首字母。
        private Control Icon(string id, double size) {
            string data;
            if (icons.TryGetValue(id, out data)) {
                try {
                    return new Avalonia.Controls.Shapes.Path { Data = StreamGeometry.Parse(data), Fill = Brush(ProviderCatalog.Color(id)), Stretch = Stretch.Uniform, Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                } catch { }
            }
            string glyph = id == "overview" ? Icons.Overview : id == ProviderCatalog.ThirdParty ? Icons.Chart : id == "sessions" ? Icons.Clock : null;
            if (glyph != null) return Icons.Glyph(glyph, Math.Max(8, size - 2), Brush(ProviderCatalog.Color(id)), HorizontalAlignment.Center, VerticalAlignment.Center);
            string text = id == "antigravity" ? "Λ" : id == "grok" ? "𝕏" : ProviderCatalog.Name(id).Substring(0, 1);
            return new TextBlock { Text = text, FontSize = Math.Max(8, size - 2), Width = size, Height = size, TextAlignment = TextAlignment.Center, Foreground = Brush(ProviderCatalog.Color(id)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }

        // ── Building blocks（Presentation.cs:588-612）──────────────────────
        // Styled(key)：给控件挂 Avalonia Classes。
        internal static T Styled<T>(T button, string classes) where T : Control {
            foreach (string name in classes.Split(' ')) button.Classes.Add(name);
            return button;
        }
        internal static Border Card(Control child) { return new Border { Background = CardFill, BorderBrush = CardLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 14), Margin = new Thickness(0, 0, 0, 10), Child = child }; }
        internal static Border Pill(string text) {
            return new Border { Background = Brush("#14FFFFFF"), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = Label(text, 10.5, InkDim) };
        }
        internal static Control Notice(string text, bool warning) {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(Icons.Glyph(warning ? Icons.Warning : Icons.Info, 12, warning ? WarnBrush : InkDim, HorizontalAlignment.Left, VerticalAlignment.Top).Also(m => m.Margin = new Thickness(0, 2, 8, 0)));
            var body = new TextBlock { Text = text, FontSize = 11, Foreground = warning ? Brush("#F5D6AE") : InkDim, TextWrapping = TextWrapping.Wrap, LineHeight = 17 };
            Grid.SetColumn(body, 1); grid.Children.Add(body);
            return new Border { Background = Brush(warning ? "#14F2B36B" : "#0AFFFFFF"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 12, 0, 0), Child = grid };
        }
        internal static TextBlock Figure(string text, double size) { var t = Label(text, size, Ink); t.FontWeight = size >= 14 ? FontWeight.SemiBold : FontWeight.Normal; Tabular(t); return t; }
        internal static void Cell(Grid grid, int row, int column, Control element, string tip) {
            Grid.SetRow(element, row); Grid.SetColumn(element, column);
            element.Margin = new Thickness(0, row == 0 ? 0 : row == 1 ? 4 : 2, 6, 0); element.HorizontalAlignment = HorizontalAlignment.Left;
            if (tip != null) ToolTip.SetTip(element, tip);
            grid.Children.Add(element);
        }
        // Avalonia 无 OpenType 字形对齐（WPF Typography.SetNumeralAlignment）；保持调用点，等宽数字暂不可用。
        // Avalonia 12 的 IClipboard 只收 DataTransfer；文本快捷封装（全局共用）。
        internal static async System.Threading.Tasks.Task SetClipboardText(Avalonia.Controls.TopLevel top, string text) {
            if (top == null) return;
            var data = new Avalonia.Input.DataTransfer();
            data.Add(Avalonia.Input.DataTransferItem.CreateText(text));
            await top.Clipboard.SetDataAsync(data);
        }
        // Avalonia 12 无 ToggleButton.Checked 事件；IsCheckedChanged + IsChecked 判断等价。
        internal static void OnChecked(Avalonia.Controls.Primitives.ToggleButton box, Action changed) {
            box.IsCheckedChanged += delegate { if (box.IsChecked == true) changed(); };
        }
        internal static void Tabular(TextBlock text) { }
        internal static TextBlock Label(string text, double size, IBrush color) { return new TextBlock { Text = text, FontSize = size, Foreground = color, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }; }
        internal static Grid Row() { var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); return row; }
        internal static void AddRow(Grid row, Control left, Control right) { row.Children.Add(left); Grid.SetColumn(right, 1); var f = right as Control; if (f != null) f.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(right); }

        // ── Settings 构件（Settings.cs:413-443）───────────────────────────
        internal static TextBlock SectionTitle(string text) {
            var title = Label(text, 11, InkDim); title.FontWeight = FontWeight.SemiBold; title.Margin = new Thickness(2, 10, 0, 7); return title;
        }
        internal static TextBlock Hint(string text, double top) {
            return new TextBlock { Text = text, FontSize = 10.5, Foreground = InkFaint, TextWrapping = TextWrapping.Wrap, LineHeight = 16, Margin = new Thickness(0, top, 0, 0) };
        }
        internal static Control FieldLabel(string title, string hint) {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            // Wrapped rather than cut off: English runs longer than the Chinese.
            var head = Label(title, 12.5, Ink); head.TextWrapping = TextWrapping.Wrap; head.TextTrimming = TextTrimming.None; stack.Children.Add(head);
            if (hint != null) { var sub = Label(hint, 10.5, InkFaint); sub.Margin = new Thickness(0, 2, 0, 0); sub.TextWrapping = TextWrapping.Wrap; sub.TextTrimming = TextTrimming.None; stack.Children.Add(sub); }
            return stack;
        }
        internal CheckBox SwitchRow(string title, string hint, bool value) {
            var box = new CheckBox { Content = FieldLabel(title, hint), IsChecked = value };
            Styled(box, "switch");
            AutomationProperties.SetName(box, title);
            return box;
        }
        internal static Border Separator() { return new Border { Height = 1, Background = Hairline, Margin = new Thickness(0, 11, 0, 11) }; }
        // 分段控件：一组互斥 RadioButton，放在圆角浅槽里。
        internal Control Segmented(string[][] options, string current, Action<string> changed) {
            var grid = new UniformGrid { Rows = 1, Columns = options.Length };
            string group = "seg" + Guid.NewGuid().ToString("N");
            foreach (string[] option in options) {
                string code = option[0];
                var button = new RadioButton { GroupName = group, IsChecked = code == current };
                Styled(button, "segment");
                var content = new TextBlock { Text = option[1], TextTrimming = TextTrimming.CharacterEllipsis };
                button.Content = content;
                AutomationProperties.SetName(button, option[1]);
                OnChecked(button, () => changed(code));
                grid.Children.Add(button);
            }
            return new Border { Background = Brush("#0DFFFFFF"), BorderBrush = Brush("#14FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = grid, VerticalAlignment = VerticalAlignment.Center };
        }

        // ── Formatting（Presentation.cs:614-657）──────────────────────────
        private static string Today() { return HistoryService.DayKey(DateTime.Today); }
        private static string ShortDate(string day) { DateTime date; return DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ? date.ToString(I18n.T("M月d日"), I18n.Culture) : day; }
        private static string Usd(double value, bool cents) { return "$" + value.ToString(cents ? "N2" : value < 10 ? "0.##" : "N0", CultureInfo.InvariantCulture); }
        private static string Usd(double value) { return Usd(value, true); }
        private static string Money(IEnumerable<DayUsage> days) { var rows = days.Where(d => d.CostKnown).ToList(); return rows.Count == 0 ? "—" : Usd(rows.Sum(d => d.Cost)); }
        // A scanned history without a row for today means nothing was recorded today.
        private string TodayMoney(IEnumerable<DayUsage> days) { string today = Today(); var rows = days.Where(d => d.Day == today).ToList(); return rows.Count == 0 ? (history.Updated.Length > 0 ? "$0.00" : "—") : Money(rows); }
        private static string WindowName(Quota quota) { return quota.Label == Parsers.CursorGrokLabel ? "Grok Bot 本周" : quota.Label.Contains("每周") ? "本周额度" : quota.Label.Contains("5 小时") ? "5 小时窗口" : "当前周期"; }
        private static string WindowMoney(WindowUsage usage) {
            if (usage == null || !usage.HasCost) return "—";
            return (usage.Exact ? "≈" : "≥") + Usd(usage.Cost);
        }
        private static string WindowTokens(WindowUsage usage) {
            if (usage == null) return "—";
            if (usage.Exact) return Compact(usage.Tokens);
            return usage.Days.Count == 0 ? "—" : "≥" + Compact(usage.Tokens);
        }
        private static string WindowHint(WindowUsage usage) {
            if (usage == null) return "服务商尚未返回可计算的额度窗口起止时间。";
            string range = usage.StartUtc.ToLocalTime().ToString(I18n.T("M月d日 HH:mm"), I18n.Culture) + " – " + usage.EndUtc.ToLocalTime().ToString(I18n.T("M月d日 HH:mm"), I18n.Culture);
            if (usage.Exact) return "窗口 " + range + "（由重置时间和周期长度反推）。\nToken 按会话日志逐小时精确累计，只计官方订阅、不含第三方接口；费用按各自然日的 API 等价单价折算，因此标 ≈。";
            return "窗口 " + range + "（由重置时间和周期长度反推）。\n本地历史只有按日汇总，跨越窗口起点的那一天不计入，因此是保守下界（≥）。";
        }
        private static string PricingHint() {
            Pricing.EnsureLoaded();
            return "费用 = 本机会话日志里每次请求的 Token × 该模型的官方 API 单价（价目表 pricing.json 整理自公开的 LiteLLM / models.dev 价目，每天与本项目仓库同步一次，断网时用内置价目；当前价目 " + Pricing.Day + (Pricing.Source == "synced" ? "，已同步" : "，随软件内置") + "）。\n" +
                "逐次请求计价：已计入缓存折扣、缓存写入（1 小时缓存按 2 倍输入价）、长上下文分档（例如单次请求输入超过 272K 时整次按长上下文价）和 Codex 优先 / fast 档的倍率。日志里自带费用的记录（Grok、部分 Claude、OpenCode）直接使用记录值；Cursor 没有本机日志，用量和每次调用的价格取自 Cursor 账户后台。\n" +
                "订阅套餐（如 Pro）实际按月费计费，这里只是 API 等价参考。";
        }
        private static string Compact(double n) {
            var c = CultureInfo.InvariantCulture;
            return n >= 1000000000 ? (n / 1000000000).ToString("0.##", c) + "B" : n >= 1000000 ? (n / 1000000).ToString("0.##", c) + "M" : n >= 1000 ? (n / 1000).ToString("0.#", c) + "K" : n.ToString("N0", c);
        }
        private static string LocalTime(string iso) { DateTimeOffset date; return DateTimeOffset.TryParse(iso, out date) ? date.LocalDateTime.ToString("MM-dd HH:mm") : "时间未知"; }
        private static string Countdown(string iso) { DateTimeOffset date; if (!DateTimeOffset.TryParse(iso, out date)) return "重置时间未知"; var span = date - DateTimeOffset.UtcNow; if (span.TotalSeconds <= 0) return "等待新窗口"; return I18n.English ? "resets in " + Span(span) : Span(span) + "后重置"; }
        private static string UpdatedAgo(string iso) { DateTimeOffset date; if (!DateTimeOffset.TryParse(iso, out date)) return "更新时间未知"; double minutes = (DateTimeOffset.UtcNow - date).TotalMinutes; return minutes < 1 ? "刚刚更新" : minutes < 60 ? (int)minutes + (I18n.English ? " min ago" : " 分钟前更新") : "更新于 " + LocalTime(iso); }
        private static string Duration(double seconds) { if (seconds <= 0) return "现在"; return Span(TimeSpan.FromSeconds(seconds)); }
        // "6天 23小时" / "6d 23h".
        private static string Span(TimeSpan t) {
            if (I18n.English) return t.TotalDays >= 1 ? (int)t.TotalDays + "d " + t.Hours + "h" : t.TotalHours >= 1 ? (int)t.TotalHours + "h " + t.Minutes + "m" : Math.Max(1, (int)t.TotalMinutes) + " min";
            return t.TotalDays >= 1 ? (int)t.TotalDays + "天 " + t.Hours + "小时" : t.TotalHours >= 1 ? (int)t.TotalHours + "小时 " + t.Minutes + "分" : Math.Max(1, (int)t.TotalMinutes) + "分钟";
        }
        private static string ExpiryCountdown(string iso) { DateTimeOffset date; return DateTimeOffset.TryParse(iso, out date) ? Duration(Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds)) : "到期时间未知"; }
    }

    // 小工具：给构建代码一处链式设边距（替代 WPF 对象初始化器里直接赋 Margin 的写法）。
    internal static class UiExtensions {
        public static T Also<T>(this T control, Action<T> set) where T : Control { set(control); return control; }
    }
}
