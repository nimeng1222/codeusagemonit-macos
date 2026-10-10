// 调色盘：预设色点、饱和度/亮度方块、色相条、透明度条（0 = 不透明）、
// hex 输入、对比度提示与实时预览。色彩数学（HSV/对比度）Core 已有（ColorMath）；指针拖拽
// 直接在自绘 Panel 上处理（Avalonia Canvas 定位）。
using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using AvaColor = Avalonia.Media.Color;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Automation;
using Avalonia.Controls.Documents;
using Avalonia.VisualTree;

namespace CodeUsageMonit {
    // Surface colour palette: presets, a saturation/brightness square, a hue strip, a
    // transparency strip (0 = opaque, the same value as 界面透明度) and a hex field. Every
    // change is reported at once for the live preview; the settings window's Save / Cancel
    // keeps or drops it. Text stays light, so the palette shows the text contrast.
    public sealed partial class MonitorPanel {
        private static readonly string[][] TintPresets = {
            new[] { "#15171B", "石墨（默认）" }, new[] { "#000000", "纯黑" }, new[] { "#262A30", "岩灰" }, new[] { "#0F1B2D", "深海蓝" }, new[] { "#161A36", "午夜蓝" },
            new[] { "#0D2226", "深青" }, new[] { "#0F211C", "墨绿" }, new[] { "#1E1629", "暗紫" }, new[] { "#2A1218", "酒红" }, new[] { "#231A14", "可可" }
        };
        private const double PaletteWidth = 264, SquareHeight = 138, StripHeight = 12;
        private static readonly AvaColor TextInk = AvaColor.FromRgb(0xF2, 0xF3, 0xF5);

        private void ShowColorPicker(Control anchor, AvaColor initial, double transparency, Action<AvaColor, double> changed) {
            double hue, sat, val; UiColor.ToHsv(initial, out hue, out sat, out val);
            AvaColor color = initial; double clear = transparency;
            var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.BottomEdgeAlignedLeft, IsLightDismissEnabled = true, VerticalOffset = 6 };
            var root = new StackPanel { Width = PaletteWidth };

            // Presets.
            root.Children.Add(Label("预设", 10.5, InkFaint).Also(t => t.Margin = new Thickness(0, 0, 0, 6)));
            var presetRow = new UniformGrid { Columns = TintPresets.Length, Rows = 1, Margin = new Thickness(0, 0, 0, 12) };
            root.Children.Add(presetRow);

            // Saturation (x) / brightness (y) square over the current hue.
            var hueLayer = new Border { CornerRadius = new CornerRadius(6) };
            var svThumb = new Grid { Width = 16, Height = 16, IsHitTestVisible = false };
            svThumb.Children.Add(new Ellipse { Stroke = Brush("#99000000"), StrokeThickness = 1 });
            svThumb.Children.Add(new Ellipse { Margin = new Thickness(1), Stroke = Brushes.White, StrokeThickness = 2 });
            var square = new Panel { Width = PaletteWidth, Height = SquareHeight, Cursor = new Cursor(StandardCursorType.Cross), Focusable = true, Background = Brushes.Transparent };
            square.Children.Add(hueLayer);
            square.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = LinearGradient(AvaColor.FromArgb(255, 255, 255, 255), AvaColor.FromArgb(0, 255, 255, 255), 0) });
            square.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = LinearGradient(AvaColor.FromArgb(0, 0, 0, 0), AvaColor.FromArgb(255, 0, 0, 0), 90), BorderBrush = Brush("#26FFFFFF"), BorderThickness = new Thickness(1) });
            square.Children.Add(svThumb);
            AutomationProperties.SetName(square, "饱和度与亮度");
            root.Children.Add(square);

            // Hue and transparency strips.
            var hueBrush = new LinearGradientBrush { StartPoint = new RelativePoint(0, .5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, .5, RelativeUnit.Relative) };
            for (int i = 0; i <= 6; i++) hueBrush.GradientStops.Add(new GradientStop(UiColor.FromHsv(i * 60, 1, 1), i / 6.0));
            Border hueThumb, hueTrack, alphaThumb, alphaTrack;
            Action update = null;
            Grid hueStrip = PaletteStrip(hueBrush, false, "色相", () => hue / 360, f => { hue = Math.Min(359.9, f * 360); color = UiColor.FromHsv(hue, sat, val); update(); }, out hueThumb, out hueTrack);
            Grid alphaStrip = PaletteStrip(null, true, "调色盘透明度", () => clear / 100, f => { clear = Math.Round(f * 100); update(); }, out alphaThumb, out alphaTrack);
            root.Children.Add(hueStrip); root.Children.Add(alphaStrip);

            // Preview (before | after), hex code and transparency value.
            var info = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); info.ColumnDefinitions.Add(new ColumnDefinition()); info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var swatch = new Grid { Width = 44, Height = 32, Margin = new Thickness(0, 0, 12, 0), ClipToBounds = true };
            swatch.ColumnDefinitions.Add(new ColumnDefinition()); swatch.ColumnDefinitions.Add(new ColumnDefinition());
            var checker = new Border { CornerRadius = new CornerRadius(6), Background = Checkerboard() }; Grid.SetColumnSpan(checker, 2); swatch.Children.Add(checker);
            var before = new Border { CornerRadius = new CornerRadius(6, 0, 0, 6), Background = new SolidColorBrush(AvaColor.FromArgb((byte)Math.Round((1 - transparency / 100) * 255), initial.R, initial.G, initial.B)) };
            ToolTip.SetTip(before, "原来的颜色");
            var after = new Border { CornerRadius = new CornerRadius(0, 6, 6, 0) };
            ToolTip.SetTip(after, "新的颜色"); Grid.SetColumn(after, 1);
            swatch.Children.Add(before); swatch.Children.Add(after);
            var swatchEdge = new Border { CornerRadius = new CornerRadius(6), BorderBrush = Brush("#33FFFFFF"), BorderThickness = new Thickness(1) };
            swatch.Children.Add(swatchEdge); Grid.SetColumnSpan(swatchEdge, 2);
            info.Children.Add(swatch);
            var hexBox = new TextBox { Width = 92, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Menlo, Monaco, monospace"), MaxLength = 7, MinHeight = 28 };
            AutomationProperties.SetName(hexBox, "颜色代码");
            Grid.SetColumn(hexBox, 1); info.Children.Add(hexBox);
            var clearValue = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            clearValue.Children.Add(Label("透明度", 10, InkFaint));
            var clearText = Label("", 13, Ink); clearText.FontWeight = FontWeight.SemiBold; Tabular(clearText); clearText.HorizontalAlignment = HorizontalAlignment.Right; clearValue.Children.Add(clearText);
            Grid.SetColumn(clearValue, 2); info.Children.Add(clearValue);
            root.Children.Add(info);
            var contrast = new TextBlock { FontSize = 10.5, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
            root.Children.Add(contrast);

            var actions = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var reset = Styled(new Button { Content = "恢复默认", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, 0, 0, 0) }, "link-button");
            AutomationProperties.SetName(reset, "恢复默认颜色");
            var done = Styled(new Button { Content = "完成" }, "primary");
            AutomationProperties.SetName(done, "完成颜色");
            done.Click += delegate { popup.IsOpen = false; };
            actions.Children.Add(reset); Grid.SetColumn(done, 1); actions.Children.Add(done);
            root.Children.Add(actions);

            Action<AvaColor> setColor = c => { color = c; UiColor.ToHsv(c, out hue, out sat, out val); update(); };
            reset.Click += delegate { clear = Math.Round((1 - WindowFrame.DefaultOpacity) * 100); setColor(WindowFrame.DefaultTint); };
            foreach (string[] preset in TintPresets) {
                AvaColor c = WindowFrame.ParseTint(preset[0]);
                var dot = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(c), BorderThickness = new Thickness(2) };
                var button = new Button { Content = dot, Padding = new Thickness(0), Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(button, preset[1] + "  " + preset[0]);
                AutomationProperties.SetName(button, "颜色 " + preset[1]);
                AvaColor captured = c; button.Click += delegate { setColor(captured); };
                button.Tag = c;
                presetRow.Children.Add(button);
            }
            Action<Point> pickSquare = p => {
                sat = Math.Max(0, Math.Min(1, p.X / PaletteWidth)); val = 1 - Math.Max(0, Math.Min(1, p.Y / SquareHeight));
                color = UiColor.FromHsv(hue, sat, val); update();
            };
            square.PointerPressed += delegate(object sender, PointerPressedEventArgs e) { square.Focus(); e.Pointer.Capture(square); pickSquare(e.GetPosition(square)); e.Handled = true; };
            square.PointerMoved += delegate(object sender, PointerEventArgs e) { if (e.Pointer.Captured == square) pickSquare(e.GetPosition(square)); };
            square.PointerReleased += delegate(object sender, PointerReleasedEventArgs e) { if (e.Pointer.Captured == square) e.Pointer.Capture(null); };
            square.KeyDown += delegate(object sender, KeyEventArgs e) {
                double step = e.KeyModifiers == KeyModifiers.Shift ? .1 : .02;
                if (e.Key == Key.Left) sat = Math.Max(0, sat - step); else if (e.Key == Key.Right) sat = Math.Min(1, sat + step);
                else if (e.Key == Key.Up) val = Math.Min(1, val + step); else if (e.Key == Key.Down) val = Math.Max(0, val - step); else return;
                color = UiColor.FromHsv(hue, sat, val); update(); e.Handled = true;
            };
            Action applyHex = () => { AvaColor parsed; if (UiColor.TryParseHex(hexBox.Text, out parsed)) { if (parsed != color) setColor(parsed); } else hexBox.Text = UiColor.Hex(color); };
            hexBox.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { applyHex(); e.Handled = true; } };
            hexBox.LostFocus += delegate { applyHex(); };

            update = () => {
                hueLayer.Background = new SolidColorBrush(UiColor.FromHsv(hue, 1, 1));
                Canvas.SetLeft(svThumb, sat * PaletteWidth - 8); Canvas.SetTop(svThumb, (1 - val) * SquareHeight - 8);
                Canvas.SetLeft(hueThumb, hue / 360 * (PaletteWidth - 8)); Canvas.SetLeft(alphaThumb, clear / 100 * (PaletteWidth - 8));
                alphaTrack.Background = LinearGradient(AvaColor.FromArgb(255, color.R, color.G, color.B), AvaColor.FromArgb(0, color.R, color.G, color.B), 0);
                after.Background = new SolidColorBrush(AvaColor.FromArgb((byte)Math.Round((1 - clear / 100) * 255), color.R, color.G, color.B));
                if (!hexBox.IsFocused) hexBox.Text = UiColor.Hex(color);
                clearText.Text = clear.ToString("0") + "%";
                foreach (Button b in presetRow.Children) ((Border)b.Content).BorderBrush = (AvaColor)b.Tag == color ? Brushes.White : Brush("#33FFFFFF");
                double ratio = UiColor.ContrastRatio(TextInk, color);
                contrast.Inlines.Clear();
                contrast.Inlines.Add(new Run("文字对比度 " + ratio.ToString("0.0", CultureInfo.InvariantCulture) + " : 1 · ") { Foreground = InkFaint });
                contrast.Inlines.Add(new Run(ratio >= 7 ? "清晰" : ratio >= 4.5 ? "可读" : "偏低，文字可能看不清，建议选深一些的颜色") { Foreground = ratio >= 4.5 ? GoodBrush : WarnBrush });
                if (clear >= 50) contrast.Inlines.Add(new Run("（透明度较高时还取决于桌面）") { Foreground = InkFaint });
                changed(color, clear);
            };
            update();

            var frameBorder = new Border { Background = PopupSurface(), BorderBrush = Brush("#33FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 14), Child = root };
            frameBorder.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; } };
            popup.Child = frameBorder;
            I18nUi.Localize(frameBorder);
            popup.IsOpen = true;
        }

        // A horizontal strip with a thumb; click or drag to set the fraction, arrow keys step it.
        private static Grid PaletteStrip(IBrush fill, bool checker, string name, Func<double> get, Action<double> set, out Border thumb, out Border track) {
            var strip = new Grid { Width = PaletteWidth, Height = StripHeight + 8, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Focusable = true, Margin = new Thickness(0, 10, 0, 0) };
            if (checker) strip.Children.Add(new Border { Height = StripHeight, CornerRadius = new CornerRadius(StripHeight / 2), VerticalAlignment = VerticalAlignment.Center, Background = Checkerboard() });
            track = new Border { Height = StripHeight, CornerRadius = new CornerRadius(StripHeight / 2), VerticalAlignment = VerticalAlignment.Center, Background = fill, BorderBrush = Brush("#26FFFFFF"), BorderThickness = new Thickness(1) };
            strip.Children.Add(track);
            var canvas = new Canvas { IsHitTestVisible = false }; strip.Children.Add(canvas);
            thumb = new Border { Width = 8, Height = StripHeight + 8, CornerRadius = new CornerRadius(4), Background = Brushes.White, BorderBrush = Brush("#99000000"), BorderThickness = new Thickness(1) };
            canvas.Children.Add(thumb);
            AutomationProperties.SetName(strip, name);
            strip.PointerPressed += delegate(object sender, PointerPressedEventArgs e) { strip.Focus(); e.Pointer.Capture(strip); set(Math.Max(0, Math.Min(1, (e.GetPosition(strip).X - 4) / (PaletteWidth - 8)))); e.Handled = true; };
            strip.PointerMoved += delegate(object sender, PointerEventArgs e) { if (e.Pointer.Captured == strip) set(Math.Max(0, Math.Min(1, (e.GetPosition(strip).X - 4) / (PaletteWidth - 8)))); };
            strip.PointerReleased += delegate(object sender, PointerReleasedEventArgs e) { if (e.Pointer.Captured == strip) e.Pointer.Capture(null); };
            strip.KeyDown += delegate(object sender, KeyEventArgs e) {
                double step = e.KeyModifiers == KeyModifiers.Shift ? .1 : .01;
                if (e.Key == Key.Left) set(Math.Max(0, get() - step)); else if (e.Key == Key.Right) set(Math.Min(1, get() + step)); else return;
                e.Handled = true;
            };
            return strip;
        }
        // 棋盘格（透明度底）：DrawingBrush 不存在，用两层渐变近似方格观感（1:1 的 8px 格）。
        private static IBrush Checkerboard() {
            return new LinearGradientBrush {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(AvaColor.Parse("#FF3A3D43"), 0), new GradientStop(AvaColor.Parse("#FF24272C"), .5), new GradientStop(AvaColor.Parse("#FF3A3D43"), .51), new GradientStop(AvaColor.Parse("#FF24272C"), 1) }
            };
        }
        private static LinearGradientBrush LinearGradient(AvaColor from, AvaColor to, double angle) {
            var brush = new LinearGradientBrush {
                StartPoint = angle == 0 ? new RelativePoint(0, .5, RelativeUnit.Relative) : new RelativePoint(.5, 0, RelativeUnit.Relative),
                EndPoint = angle == 0 ? new RelativePoint(1, .5, RelativeUnit.Relative) : new RelativePoint(.5, 1, RelativeUnit.Relative)
            };
            brush.GradientStops.Add(new GradientStop(from, 0));
            brush.GradientStops.Add(new GradientStop(to, 1));
            return brush;
        }
    }
}
