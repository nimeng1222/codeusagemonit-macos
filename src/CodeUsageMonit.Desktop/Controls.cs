// Avalonia 自绘基建：图标字形表（替代 Segoe Fluent Icons）、密码输入框、迷你滑杆、
// 暗色弹出菜单、窗口拖拽与调整边、文件/文件夹选择与确认对话框。
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaColor = Avalonia.Media.Color;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Automation;
using Avalonia.Controls.Documents;

namespace CodeUsageMonit {
    internal static class Icons {
        // Segoe Fluent Icons 字形 → 内嵌 path（macOS 无该字体）。常量是原字形码。
        public const string Refresh = "E72C";   // Panel.xaml RefreshButton
        public const string Size = "E73F";      // SizeButton
        public const string Pin = "E718";       // PinButton
        public const string Settings = "E713";  // SettingsButton
        public const string Close = "E8BB";     // HideButton
        public const string Copy = "E8C8";      // Presentation.CopySummary
        public const string ChevronDown = "E70D"; // QuotaRow 折叠箭头
        public const string ChevronUp = "E70E";
        public const string ChevronRight = "E76C"; // 第三方卡片 chevron
        public const string Info = "E946";      // Notice(info)
        public const string Warning = "E7BA";   // Notice(warning)
        public const string Calendar = "E787";  // RangePicker
        public const string Clock = "E823";     // RangePicker / RangeSelector 前缀
        public const string Check = "E73E";     // 菜单勾选
        public const string Minus = "E738";     // 设置步进器
        public const string Plus = "E710";
        public const string Overview = "E80F";  // 概览 tab 图形
        public const string Chart = "E9D2";     // 第三方 tab 图形

        private static readonly Dictionary<string, string> Data = new Dictionary<string, string> {
            { Refresh, "M13.408,3.2 A5.6,5.6 0 1 0 15.6,7.5 L13.9,7.5 A3.9,3.9 0 1 1 12.266,4.334 L10.5,6.1 L15.5,6.1 L15.5,1.1 L13.9,2.7 A5.59,5.59 0 0 0 13.408,3.2 Z" },
            { Size, "M2,2 L7,2 L7,3.5 L3.5,3.5 L3.5,7 L2,7 Z M9,2 L14,2 L14,7 L12.5,7 L12.5,3.5 L9,3.5 Z M2,9 L3.5,9 L3.5,12.5 L7,12.5 L7,14 L2,14 Z M12.5,9 L14,9 L14,14 L9,14 L9,12.5 L12.5,12.5 Z" },
            { Pin, "M9.5,1.5 L14.5,6.5 L12.4,7.2 L9.9,7.2 L6.9,10.2 L7.6,14 L6.5,14 L4.9,9.1 L1.7,12.3 L1,11.6 L4.2,8.4 L1.5,3 L2.8,2.4 L7.8,4.6 L9.2,1.5 Z" },
            { Settings, "M8,5.5 A2.5,2.5 0 1 0 8,10.5 A2.5,2.5 0 0 0 8,5.5 Z M8,7 A1,1 0 1 1 8,9 A1,1 0 0 1 8,7 Z M6.9,1 L9.1,1 L9.5,2.8 A5.3,5.3 0 0 1 11.1,3.7 L12.9,3 L14,5 L12.5,6.1 A5,5 0 0 1 12.5,8.9 L14,10 L12.9,12 L11.1,11.3 A5.3,5.3 0 0 1 9.5,12.2 L9.1,14 L6.9,14 L6.5,12.2 A5.3,5.3 0 0 1 4.9,11.3 L3.1,12 L2,10 L3.5,8.9 A5,5 0 0 1 3.5,6.1 L2,5 L3.1,3 L4.9,3.7 A5.3,5.3 0 0 1 6.5,2.8 Z" },
            { Close, "M3.4,2.34 L8,6.94 L12.6,2.34 L13.66,3.4 L9.06,8 L13.66,12.6 L12.6,13.66 L8,9.06 L3.4,13.66 L2.34,12.6 L6.94,8 L2.34,3.4 Z" },
            { ChevronDown, "M2,4.5 L8,10.5 L14,4.5 L14,6.6 L8,12.6 L2,6.6 Z" },
            { ChevronUp, "M2,10.5 L8,4.5 L14,10.5 L14,8.4 L8,2.4 L2,8.4 Z" },
            { ChevronRight, "M5,2 L11,8 L5,14 L5,11.9 L8.9,8 L5,4.1 Z" },
            { Copy, "M4,2 L12,2 L12,4 L14,4 L14,14 L6,14 L6,12 L4,12 Z M5.5,3.5 L5.5,10.5 L11,10.5 L11,3.5 Z M7.5,5.5 L12.5,5.5 L12.5,12.5 L7.5,12.5 Z" },
            { Info, "M8,1 A7,7 0 1 0 8,15 A7,7 0 0 0 8,1 Z M8,2.5 A5.5,5.5 0 1 1 8,13.5 A5.5,5.5 0 0 1 8,2.5 Z M7.3,6.5 L8.7,6.5 L8.7,12 L7.3,12 Z M7.3,4 L8.7,4 L8.7,5.4 L7.3,5.4 Z" },
            { Warning, "M8,1.2 L15,13.5 L1,13.5 Z M8,4 L3.8,12 L12.2,12 Z M7.3,6.5 L8.7,6.5 L8.7,9.5 L7.3,9.5 Z M7.3,10.2 L8.7,10.2 L8.7,11.4 L7.3,11.4 Z" },
            { Calendar, "M2,3 L14,3 L14,14 L2,14 Z M3.5,4.5 L3.5,12.5 L12.5,12.5 L12.5,4.5 Z M5,1.5 L6.5,1.5 L6.5,4 L5,4 Z M9.5,1.5 L11,1.5 L11,4 L9.5,4 Z M3.5,6 L12.5,6 L12.5,7 L3.5,7 Z" },
            { Clock, "M8,1 A7,7 0 1 0 8,15 A7,7 0 0 0 8,1 Z M8,2.5 A5.5,5.5 0 1 1 8,13.5 A5.5,5.5 0 0 1 8,2.5 Z M7.3,4 L8.7,4 L8.7,8.2 L11.5,9.8 L10.8,11 L7.3,9 Z" },
            { Check, "M13.7,3.6 L14.8,4.7 L6.5,13 L2.7,9.2 L3.8,8.1 L6.5,10.8 Z" },
            { Minus, "M3,7.25 L13,7.25 L13,8.75 L3,8.75 Z" },
            { Plus, "M7.25,3 L8.75,3 L8.75,7.25 L13,7.25 L13,8.75 L8.75,8.75 L8.75,13 L7.25,13 L7.25,8.75 L3,8.75 L3,7.25 L7.25,7.25 Z" },
            { Overview, "M2,2 L7.5,2 L7.5,7.5 L2,7.5 Z M8.5,2 L14,2 L14,7.5 L8.5,7.5 Z M2,8.5 L7.5,8.5 L7.5,14 L2,14 Z M8.5,8.5 L14,8.5 L14,14 L8.5,14 Z" },
            { Chart, "M2,13.5 L2,9 L5,9 L5,13.5 Z M6.5,13.5 L6.5,2.5 L9.5,2.5 L9.5,13.5 Z M11,13.5 L11,6 L14,6 L14,13.5 Z" },
        };
        public static Geometry Glyph(string name) {
            string data; return Data.TryGetValue(name, out data) ? Geometry.Parse(data) : Geometry.Parse(Data[Info]);
        }
        // 代码构建的小图标（Path 填充单色，等比缩放到 size）。
        public static Path Glyph(string name, double size, IBrush fill) {
            return new Path { Data = Glyph(name), Width = size, Height = size, Fill = fill, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        public static Path Glyph(string name, double size, IBrush fill, HorizontalAlignment h, VerticalAlignment v) {
            return new Path { Data = Glyph(name), Width = size, Height = size, Fill = fill, Stretch = Stretch.Uniform, HorizontalAlignment = h, VerticalAlignment = v };
        }
        // 图标按钮内的字形：Fill 绑定宿主按钮的 Foreground，悬停/禁用变色随按钮走。
        public static Path GlyphBound(Button host, string name, double size) {
            var path = Glyph(name, size, Ui.InkDim);
            path[!Path.FillProperty] = host[!TemplatedControl.ForegroundProperty];
            return path;
        }
    }

    // Avalonia 11+ 无独立 PasswordBox：TextBox.PasswordChar 承担。EditingKey() 靠此类型识别。
    // StyleKeyOverride 必须指回 TextBox：子类的默认 StyleKey 是自身类型，应用级 TextBox
    // 样式（MinHeight/背景/边框，App.axaml）与 Fluent 主题都会失配——控件以 0 高度渲染，
    // 卡片与认证页的密钥输入框完全不可见（此前实测 bounds=402x0）。
    internal sealed class KeyBox : TextBox {
        protected override Type StyleKeyOverride => typeof(TextBox);
        public KeyBox() { PasswordChar = '•'; }
    }

    // 迷你滑杆样式：4px 圆角轨道 + 强调色填充 + 14px 白点。
    // Avalonia 模板无法用 RepeatButton 实现填充段，这里以自绘 Panel 实现（值、吸附、键盘一致）。
    internal sealed class MiniSlider : Panel {
        private readonly Border fill;
        private readonly Ellipse thumb;
        private double minimum, maximum = 100, value;
        public double Minimum { get { return minimum; } set { minimum = value; Render(); } }
        public double Maximum { get { return maximum; } set { maximum = value; Render(); } }
        public double SmallChange { get; set; }
        public double? Snap { get; set; }
        public event Action ValueChanged;
        public double Value {
            get { return value; }
            set {
                double next = Math.Max(minimum, Math.Min(maximum, value));
                if (Snap.HasValue) next = Math.Round(next / Snap.Value) * Snap.Value;
                if (next == this.value) return;
                this.value = next; Render();
                var changed = ValueChanged; if (changed != null) changed();
            }
        }
        public MiniSlider() {
            Height = 22; Cursor = new Cursor(StandardCursorType.Hand); Focusable = true;
            Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Ui.Brush("#24FFFFFF"), VerticalAlignment = VerticalAlignment.Center });
            fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Ui.AccentBrush, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            Children.Add(fill);
            thumb = new Ellipse { Width = 14, Height = 14, Fill = Ui.Brush("#F2F3F5"), Stroke = Ui.Brush("#40000000"), StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            Children.Add(thumb);
            PointerPressed += (s, e) => { Focus(); e.Pointer.Capture(this); Set(e); e.Handled = true; };
            PointerMoved += (s, e) => { if (e.Pointer.Captured == this) Set(e); };
            PointerReleased += (s, e) => { if (e.Pointer.Captured == this) e.Pointer.Capture(null); };
            KeyDown += (s, e) => {
                double step = SmallChange * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1);
                if (e.Key == Key.Left || e.Key == Key.Down) { Value -= step; e.Handled = true; }
                else if (e.Key == Key.Right || e.Key == Key.Up) { Value += step; e.Handled = true; }
            };
            SizeChanged += (s, e) => Render();
        }
        private void Set(PointerEventArgs e) {
            Point p = e.GetPosition(this);
            Value = minimum + Math.Max(0, Math.Min(1, (p.X - 7) / Math.Max(1, Bounds.Width - 14))) * (maximum - minimum);
        }
        private void Render() {
            double fraction = maximum > minimum ? Math.Max(0, Math.Min(1, (value - minimum) / (maximum - minimum))) : 0;
            double width = Math.Max(0, Bounds.Width - 14);
            fill.Width = 7 + width * fraction;
            thumb.Margin = new Thickness(width * fraction, 0, 0, 0);
        }
    }

    // 暗色弹出菜单：平面样式、无子菜单。
    // 托盘菜单走 NativeMenu；面板内右键 / 尺寸按钮 / 更多按钮用这里。
    internal sealed class DarkMenu {
        private readonly StackPanel items = new StackPanel();
        private readonly Popup popup = new Popup { Placement = PlacementMode.BottomEdgeAlignedLeft, IsLightDismissEnabled = true };
        public DarkMenu() {
            var border = new Border { Background = Ui.Brush("#F51F2126"), BorderBrush = Ui.Brush("#33FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(4), MinWidth = 156 };
            border.Child = items;
            popup.Child = border;
        }
        // 一行菜单：勾选图标（22px 列）+ 文本。check == null 表示不可勾选。
        public void Item(string header, Action click, bool? check = null, bool enabled = true, IBrush foreground = null) {
            var checkIcon = Icons.Glyph(Icons.Check, 11, Ui.AccentBrush);
            checkIcon.IsVisible = check == true;
            var label = new TextBlock { Text = header, FontSize = 12, Foreground = foreground ?? Ui.Brush("#ECEEF1"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(checkIcon);
            Grid.SetColumn(label, 1); grid.Children.Add(label);
            var hover = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 6, 16, 6), Child = grid, IsEnabled = enabled };
            if (!enabled) hover.Opacity = 0.45;
            var row = new Button { Content = hover, Padding = new Thickness(0), Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            row.PointerEntered += delegate { if (enabled) hover.Background = Ui.Brush("#1FFFFFFF"); };
            row.PointerExited += delegate { hover.Background = Brushes.Transparent; };
            row.Click += delegate { popup.Close(); if (click != null) click(); };
            items.Children.Add(row);
        }
        public void Separator() { items.Children.Add(new Border { Height = 1, Background = Ui.Brush("#1FFFFFFF"), Margin = new Thickness(8, 4) }); }
        public void Open(Control anchor) { popup.PlacementTarget = anchor; popup.Placement = PlacementMode.BottomEdgeAlignedLeft; popup.IsOpen = true; }
        public void OpenAtPointer(Control anchor) { popup.PlacementTarget = anchor; popup.Placement = PlacementMode.Pointer; popup.IsOpen = true; }
    }

    // 无边框窗口的 7px 调整边（紧凑 6）与拖拽移动。
    // 挂到窗口根 Panel 的最上层。调整走手动实现：macOS 上 Avalonia 的 BeginResizeDrag 是
    // 空实现（拖了没反应），因此 PointerPressed 捕获指针、记录起始窗口几何与指针屏幕坐标
    //（PointToScreen），PointerMoved 把位移按实测换算系数（PointToScreen 单位每 DIP，见 Arm）
    // 换算后按边/角方向改 Width/Height 与 Position，尺寸钳制与 WindowFrame.ApplyLimits
    // 同口径（ClampSize 纯函数）。移动仍交给 BeginMoveDrag（macOS 有真实实现）。
    internal sealed class ResizeChrome {
        private readonly Window window;
        private readonly Panel layer;
        private double edge = 7;
        // 显示档与界面缩放由 WindowFrame 注入（WindowFrame 是唯一尺寸口径）。
        private Func<string> mode = () => "full";
        private Func<double> uiScale = () => 1;
        public ResizeChrome(Window window, Panel layer) { this.window = window; this.layer = layer; }
        public void Attach(Func<string> mode, Func<double> uiScale) { this.mode = mode; this.uiScale = uiScale; }
        public void Build(double edgeThickness) {
            edge = edgeThickness; layer.Children.Clear();
            Add(WindowEdge.West, new Thickness(0, edge, 0, edge), new Thickness(0, 0, 0, 0), edge, double.NaN, HorizontalAlignment.Left, VerticalAlignment.Stretch, StandardCursorType.LeftSide);
            Add(WindowEdge.East, new Thickness(0, edge, edge, edge), new Thickness(0, 0, edge, 0), edge, double.NaN, HorizontalAlignment.Right, VerticalAlignment.Stretch, StandardCursorType.RightSide);
            Add(WindowEdge.North, new Thickness(0), new Thickness(0, 0, 0, 0), double.NaN, edge, HorizontalAlignment.Stretch, VerticalAlignment.Top, StandardCursorType.TopSide);
            Add(WindowEdge.South, new Thickness(0, 0, 0, edge), new Thickness(0, 0, 0, edge), double.NaN, edge, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, StandardCursorType.BottomSide);
            Corner(WindowEdge.NorthWest, StandardCursorType.TopLeftCorner, HorizontalAlignment.Left, VerticalAlignment.Top);
            Corner(WindowEdge.NorthEast, StandardCursorType.TopRightCorner, HorizontalAlignment.Right, VerticalAlignment.Top);
            Corner(WindowEdge.SouthWest, StandardCursorType.BottomLeftCorner, HorizontalAlignment.Left, VerticalAlignment.Bottom);
            Corner(WindowEdge.SouthEast, StandardCursorType.BottomRightCorner, HorizontalAlignment.Right, VerticalAlignment.Bottom);
        }
        // 一次拖动的起始状态：指针屏幕坐标与窗口左上缘（PointToScreen 单位）、该坐标系
        // 换算系数（单位每 DIP，按下时实测，见 Arm）、窗口位置与尺寸（DIP）。
        private sealed class Drag {
            public WindowEdge Edge;
            public PixelPoint Pointer, Position;
            public double Left, Top, Width, Height, Unit, PositionRatio;
        }
        private Drag drag;
        private void Arm(Control target, WindowEdge edgeKind) {
            target.PointerPressed += (s, e) => {
                if (window.WindowState != WindowState.Normal || !e.GetCurrentPoint(target).Properties.IsLeftButtonPressed) return;
                try {
                    PixelPoint at = window.PointToScreen(e.GetPosition(target));
                    PixelPoint origin = window.PointToScreen(new Point(0, 0));
                    PixelPoint corner = window.PointToScreen(new Point(window.Bounds.Width, window.Bounds.Height));
                    // 换算系数实测而得（该坐标系单位每 DIP），不假设 RenderScaling 语义：
                    // PointToScreen 在 Windows 返回物理像素、在 macOS 返回屏幕点，按实测除，
                    // 高分屏（Retina 等）不会出现 2 倍漂移（任务1 的 HiDPI 坑）。
                    double unit = window.Bounds.Width > 0 ? (corner.X - origin.X) / window.Bounds.Width : 1;
                    if (!(unit > 0) || Double.IsNaN(unit) || Double.IsInfinity(unit)) unit = 1;
                    double ratio = Math.Abs(origin.X) > 0.5 ? window.Position.X / origin.X : 1;   // Position 单位 ÷ PointToScreen 单位
                    if (!(ratio > 0) || Double.IsNaN(ratio) || Double.IsInfinity(ratio)) ratio = 1;
                    drag = new Drag {
                        Edge = edgeKind,
                        Pointer = at,
                        Left = origin.X, Top = origin.Y,
                        Width = Double.IsNaN(window.Width) ? window.Bounds.Width : window.Width,
                        Height = Double.IsNaN(window.Height) ? window.Bounds.Height : window.Height,
                        Position = window.Position,
                        Unit = unit, PositionRatio = ratio
                    };
                    e.Pointer.Capture(target);
                    e.Handled = true;
                } catch { drag = null; }
            };
            target.PointerMoved += (s, e) => {
                if (drag == null || drag.Edge != edgeKind || e.Pointer.Captured != target) return;
                try { Apply(drag, window.PointToScreen(e.GetPosition(target))); e.Handled = true; } catch { }
            };
            target.PointerReleased += (s, e) => {
                if (drag != null && drag.Edge == edgeKind && e.Pointer.Captured == target) { drag = null; e.Pointer.Capture(null); e.Handled = true; }
            };
            target.PointerCaptureLost += (s, e) => { if (drag != null && drag.Edge == edgeKind) drag = null; };
        }
        // 把指针位移落到窗口几何上。W/N 侧：窗口左/上缘跟随指针、对侧缘保持不动 ——
        // 先把新尺寸钳到档位上下限，再由“对侧缘位置 − 新尺寸”反推新位置，这样钳制生效
        // 时窗口不会整体平移（右/下缘严格不漂移）；E/S 侧只改尺寸不动位置。
        private void Apply(Drag d, PixelPoint pointer) {
            double dx = (pointer.X - d.Pointer.X) / d.Unit, dy = (pointer.Y - d.Pointer.Y) / d.Unit;
            double width = d.Width, height = d.Height;
            bool west = d.Edge == WindowEdge.West || d.Edge == WindowEdge.NorthWest || d.Edge == WindowEdge.SouthWest;
            bool east = d.Edge == WindowEdge.East || d.Edge == WindowEdge.NorthEast || d.Edge == WindowEdge.SouthEast;
            bool north = d.Edge == WindowEdge.North || d.Edge == WindowEdge.NorthWest || d.Edge == WindowEdge.NorthEast;
            bool south = d.Edge == WindowEdge.South || d.Edge == WindowEdge.SouthWest || d.Edge == WindowEdge.SouthEast;
            if (west) width = ClampSize(mode(), uiScale(), d.Width - dx, d.Height).Width;
            else if (east) width = ClampSize(mode(), uiScale(), d.Width + dx, d.Height).Width;
            if (north) height = ClampSize(mode(), uiScale(), width, d.Height - dy).Height;
            else if (south) height = ClampSize(mode(), uiScale(), width, d.Height + dy).Height;
            double leftU = west ? d.Left + (d.Width - width) * d.Unit : d.Left;
            double topU = north ? d.Top + (d.Height - height) * d.Unit : d.Top;
            try {
                window.Position = new PixelPoint((int)Math.Round(d.PositionRatio * leftU), (int)Math.Round(d.PositionRatio * topU));
                window.Width = width;
                window.Height = height;
            } catch { }
        }
        // 纯函数（UiSelfTests 覆盖）：拖动目标尺寸钳到该显示档的上下限。与
        // WindowFrame.ApplyLimits 同一口径：MinSize/MaxSize(mode) × UiScale；
        // Compact（mode != "full"）才钳最大值，full 档上限 8192。
        internal static Size ClampSize(string mode, double uiScale, double width, double height) {
            double k = Double.IsNaN(uiScale) || Double.IsInfinity(uiScale) || uiScale <= 0 ? 1 : uiScale;
            Size min = WindowFrame.MinSize(mode), max = WindowFrame.MaxSize(mode);
            double maxW = mode == "full" ? 8192 : max.Width * k, maxH = mode == "full" ? 8192 : max.Height * k;
            return new Size(
                Math.Max(min.Width * k, Math.Min(maxW, Double.IsNaN(width) || Double.IsInfinity(width) ? min.Width * k : width)),
                Math.Max(min.Height * k, Math.Min(maxH, Double.IsNaN(height) || Double.IsInfinity(height) ? min.Height * k : height)));
        }
        private void Corner(WindowEdge edgeKind, StandardCursorType cursor, HorizontalAlignment h, VerticalAlignment v) {
            var corner = new Border { Background = Brushes.Transparent, Width = edge * 2, Height = edge * 2, HorizontalAlignment = h, VerticalAlignment = v, Cursor = new Cursor(cursor) };
            Arm(corner, edgeKind);
            layer.Children.Add(corner);
        }
        private void Add(WindowEdge edgeKind, Thickness hitMargin, Thickness margin, double width, double height, HorizontalAlignment h, VerticalAlignment v, StandardCursorType cursor) {
            var border = new Border { Background = Brushes.Transparent, Margin = margin, Width = width, Height = height, HorizontalAlignment = h, VerticalAlignment = v, Cursor = new Cursor(cursor) };
            Arm(border, edgeKind);
            layer.Children.Add(border);
        }
        // 拖拽移动：面板标题区与紧凑页面任意处。按下源在交互控件（按钮/输入框等，
        // UiInput.IsInteractive 口径）子树内时不接管：那些控件要吃点击，整窗拖动只属于空白区。
        public void EnableDrag(Control control) {
            control.PointerPressed += (s, e) => {
                if (e.Handled || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed || e.ClickCount != 1 || window.WindowState != WindowState.Normal) return;
                var source = e.Source as Visual;
                if (source != null && UiInput.IsInteractive(source)) return;
                try { window.BeginMoveDrag(e); e.Handled = true; } catch { }
            };
        }
    }

    // 指针手势的命中分类：事件源是否在交互控件子树内。Avalonia 12 无 ButtonBase：
    // ToggleButton 直接派生自 Button，因此 is Button 已覆盖按钮/切换/CheckBox/RadioButton
    // 全家族；再加输入框、下拉、滑杆、滚动条、列表与数字步进。
    // ResizeChrome.EnableDrag（整窗拖动）与 BodyScroll 的按住拖动滚动共用同一份口径，
    // 交互控件的行为不受拖动手势影响。
    internal static class UiInput {
        public static bool IsInteractive(Visual from) {
            for (Visual v = from; v != null; v = v.GetVisualParent()) {
                if (v is Button || v is TextBox || v is ComboBox || v is Slider || v is ScrollBar || v is ListBox || v is NumericUpDown) return true;
            }
            return false;
        }
    }

    // 文件 / 文件夹选择与确认对话框（替代 Microsoft.Win32.OpenFileDialog / SaveFileDialog、
    // FolderBrowserDialog 与 MessageBox 的使用点；走 Avalonia StorageProvider 与自绘暗色窗口）。
    internal static class Dialogs {
        public static readonly FilePickerFileType Images = new FilePickerFileType("图片") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp", "*.tif", "*.tiff" } };
        public static readonly FilePickerFileType Sql = new FilePickerFileType("SQL 文件") { Patterns = new[] { "*.sql" } };
        public static readonly FilePickerFileType Zip = new FilePickerFileType("codeusagemonit 备份") { Patterns = new[] { "*.zip" } };
        public static async Task<string> PickOpenFile(Window owner, string title, FilePickerFileType type, string initialFolder = null) {
            var options = new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = new[] { type, FilePickerFileTypes.All } };
            if (initialFolder != null && System.IO.Directory.Exists(initialFolder)) options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(initialFolder);
            var files = await owner.StorageProvider.OpenFilePickerAsync(options);
            return files.Count > 0 ? files[0].Path.LocalPath : null;
        }
        public static async Task<string> PickSaveFile(Window owner, string title, string suggestedName, string defaultExtension) {
            var options = new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedName, DefaultExtension = defaultExtension };
            var file = await owner.StorageProvider.SaveFilePickerAsync(options);
            return file != null ? file.Path.LocalPath : null;
        }
        public static async Task<string> PickFolder(Window owner, string title, string initialFolder = null) {
            var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
            if (initialFolder != null && System.IO.Directory.Exists(initialFolder)) options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(initialFolder);
            var folders = await owner.StorageProvider.OpenFolderPickerAsync(options);
            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
        }
        // 暗色确认对话框（恢复备份等需要 OK/Cancel 的场合）。true = 确认。
        public static Task<bool> Confirm(Window owner, string title, string text) {
            var done = new TaskCompletionSource<bool>();
            var w = new Window {
                Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, ShowInTaskbar = false,
                FontFamily = owner != null ? owner.FontFamily : null, FontSize = 12, Foreground = Ui.Brush("#F2F3F5"), MinWidth = 340, MaxWidth = 480
            };
            var stack = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
            var message = new TextBlock { Text = text, FontSize = 12, Foreground = Ui.Brush("#E6E8EB"), TextWrapping = TextWrapping.Wrap, MaxWidth = 430 };
            stack.Children.Add(message);
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = MonitorPanel.Styled(new Button { Content = I18n.T("取消"), Margin = new Thickness(0, 0, 8, 0) }, "secondary");
            var ok = MonitorPanel.Styled(new Button { Content = I18n.T("确定") }, "primary");
            cancel.Click += delegate { done.TrySetResult(false); w.Close(); };
            ok.Click += delegate { done.TrySetResult(true); w.Close(); };
            bar.Children.Add(cancel); bar.Children.Add(ok);
            stack.Children.Add(bar);
            w.Content = new Border { Background = Ui.Brush("#F515171B"), BorderBrush = Ui.Brush("#26FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = stack };
            w.Closed += delegate { done.TrySetResult(false); };
            w.Show(owner);
            w.Activate();
            return done.Task;
        }
    }
}
