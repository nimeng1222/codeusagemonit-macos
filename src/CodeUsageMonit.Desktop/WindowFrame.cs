// WindowFrame 的 Avalonia/平台半边（窗口 chrome、几何与背板；
// 纯逻辑半边在 Core/WindowFrame.cs）。映射：
//   WindowChrome(7px 调整边 + DragMove) → ResizeChrome（手动调整边 + BeginMoveDrag）
//   DWM Acrylic/Mica → TransparencyLevelHint=AcrylicBlur + tint×alpha 叠加（K3 拍板）
//   WinForms.Screen → Window.Screens；像素 ↔ DIP 用 screen.Scaling 换算
//   “钉在桌面层底部”（SetWindowPos + WM_WINDOWPOSCHANGING）→ 不实现，保留“置顶”开关（K3 拍板）
//   LayoutTransform 缩放 → LayoutTransformControl（ScaleRootHost / CompactScaleHost）
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaColor = Avalonia.Media.Color;
using AvaSize = CodeUsageMonit.Size;
using Avalonia.Platform;
using Avalonia.Layout;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;

namespace CodeUsageMonit {
    public sealed class WindowFrame {
        private readonly Window window;
        private readonly AppConfig config;
        private readonly Action save;
        private readonly LayoutTransformControl scaleHost, compactHost;
        private readonly ResizeChrome chrome;
        private readonly DispatcherTimer geometryTimer;
        private bool restored;
        private string mode = "full";
        public string MaterialStatus { get; private set; }

        public WindowFrame(Window window, AppConfig config, Action save, LayoutTransformControl scaleHost, LayoutTransformControl compactHost, Panel resizeLayer) {
            this.window = window; this.config = config; this.save = save;
            this.scaleHost = scaleHost; this.compactHost = compactHost;
            chrome = new ResizeChrome(window, resizeLayer);
            // 手动调整边的尺寸口径（MinSize/MaxSize(mode) × UiScale）从这里注入，
            // ResizeChrome 不自持配置 —— WindowFrame 仍是唯一尺寸口径。
            chrome.Attach(() => mode, () => config.UiScale);
            chrome.Build(7);
            window.Topmost = config.AlwaysOnTop;
            // No native caption: the panel header and the compact sizes are dragged with
            // BeginMoveDrag, so a right-click there opens our menu instead of the system menu.
            geometryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            geometryTimer.Tick += delegate { geometryTimer.Stop(); Capture(); save(); };
            window.PositionChanged += delegate { QueueSave(); };
            window.SizeChanged += delegate { QueueSave(); };
            ApplyLimits();
        }
        // ── 纯逻辑半边的本地副本 ────────────────────────────────────────
        // Desktop 程序集里的同名类会遮蔽 Core 的 WindowFrame（跨程序集 partial 不可行），
        // 因此 Core/WindowFrame.cs 的纯逻辑在此以 UiColor 提供一份实现（勿改动语义；
        // Core 侧仍保留原件供 SelfTests 回归，见 Core/WindowFrame.cs 头注）。
        public static void Normalize(AppConfig c) {
            c.WindowWidth = Finite(c.WindowWidth) ? Math.Max(360, Math.Min(4096, c.WindowWidth)) : 420;
            c.WindowHeight = Finite(c.WindowHeight) ? Math.Max(460, Math.Min(4096, c.WindowHeight)) : 790;
            c.UiScale = Finite(c.UiScale) ? Math.Max(.8, Math.Min(1.4, c.UiScale)) : 1;
            if (c.Material == "solid") c.SurfaceOpacity = 1;
            c.Material = "acrylic";
            if (c.BackgroundFit != "fit" && c.BackgroundFit != "tile") c.BackgroundFit = "fill";
            c.BackgroundDim = Finite(c.BackgroundDim) ? Math.Max(0, Math.Min(.85, c.BackgroundDim)) : .35;
            if (c.BackgroundImage == null) c.BackgroundImage = "";
            c.SurfaceOpacity = ClampOpacity(c.SurfaceOpacity);
            AvaColor tint; c.SurfaceColor = UiColor.TryParseHex(c.SurfaceColor, out tint) ? UiColor.Hex(tint) : UiColor.Hex(DefaultTint);
            if (c.DisplaySize != "small" && c.DisplaySize != "medium" && c.DisplaySize != "large" && c.DisplaySize != "edge") c.DisplaySize = "full";
            if (String.IsNullOrEmpty(c.CompactProvider)) c.CompactProvider = "overview";
            if (c.CompactLeft.HasValue && !Finite(c.CompactLeft.Value)) c.CompactLeft = null;
            if (c.CompactTop.HasValue && !Finite(c.CompactTop.Value)) c.CompactTop = null;
            if (c.WindowLeft.HasValue && !Finite(c.WindowLeft.Value)) c.WindowLeft = null;
            if (c.WindowTop.HasValue && !Finite(c.WindowTop.Value)) c.WindowTop = null;
            if (c.Layouts == null) c.Layouts = new Dictionary<string, WindowGeometry>();
            foreach (string key in new List<string>(c.Layouts.Keys)) {
                WindowGeometry g = c.Layouts[key];
                if (g == null || (key != "small" && key != "medium" && key != "large" && key != "edge") || !Finite(g.Width) || !Finite(g.Height)) { c.Layouts.Remove(key); continue; }
                if (g.Left.HasValue && !Finite(g.Left.Value)) g.Left = null;
                if (g.Top.HasValue && !Finite(g.Top.Value)) g.Top = null;
            }
            if (c.UiVersion < 2) {
                c.UiVersion = 2; c.AlwaysOnTop = false; c.HideOnDeactivate = false;
                c.PinWindow = false; c.WindowWidth = 420; c.WindowHeight = 790;
            }
        }
        private static bool Finite(double v) { return !Double.IsNaN(v) && !Double.IsInfinity(v); }
        public static AvaSize DefaultSize(string size) {
            return size == "small" ? new AvaSize(172, 172) : size == "medium" ? new AvaSize(360, 180) : size == "large" ? new AvaSize(360, 430) : size == "edge" ? new AvaSize(84, 320) : new AvaSize(420, 790);
        }
        public static AvaSize MinSize(string size) {
            return size == "small" ? new AvaSize(160, 160) : size == "medium" ? new AvaSize(300, 160) : size == "large" ? new AvaSize(320, 340) : size == "edge" ? new AvaSize(76, 150) : new AvaSize(360, 460);
        }
        public static AvaSize MaxSize(string size) {
            return size == "small" ? new AvaSize(360, 360) : size == "medium" ? new AvaSize(720, 360) : size == "large" ? new AvaSize(720, 1000) : size == "edge" ? new AvaSize(140, 1000) : new AvaSize(4096, 4096);
        }
        public string Mode { get { return mode; } }
        public bool Compact { get { return mode != "full"; } }
        // Compact sizes sit on the desktop layer unless "always on top" is on；
        // macOS 无桌面层窗口 API，置底不实现（K3 拍板），仅保留置顶。
        public bool PinnedToDesktop { get { return false; } }

        public void SetMode(string next) {
            if (restored) Capture(); // keep the geometry of the size being left
            mode = next;
            chrome.Build(Compact ? 6 : 7);
            ApplyLimits();
            restored = false; Restore();
            window.Topmost = config.AlwaysOnTop;
        }
        private void ApplyLimits() {
            double s = config.UiScale; Size min = MinSize(mode), max = MaxSize(mode);
            window.MinWidth = min.Width * s; window.MinHeight = min.Height * s;
            window.MaxWidth = Compact ? max.Width * s : 8192; window.MaxHeight = Compact ? max.Height * s : 8192;
        }
        private Screen ScreenNow {
            get {
                try {
                    var screens = window.Screens.All;
                    if (screens.Count == 0) return null;
                    Screen current = null;
                    try { current = window.Screens.ScreenFromWindow(window); } catch { }
                    if (current != null) return current;
                    PixelPoint at = window.Position;
                    if (at != default(PixelPoint)) { current = window.Screens.ScreenFromPoint(at); if (current != null) return current; }
                    return screens[0];
                } catch { return null; }
            }
        }
        // 屏幕工作区（DIP）。
        private Rect WorkingArea(Screen screen) {
            if (screen == null) return new Rect(0, 0, 1440, 900);
            double s = screen.Scaling <= 0 ? 1 : screen.Scaling;
            return new Rect(screen.WorkingArea.X / s, screen.WorkingArea.Y / s, screen.WorkingArea.Width / s, screen.WorkingArea.Height / s);
        }
        public void Restore() {
            if (restored) return;
            Normalize(config);
            Screen screen = ScreenNow;
            Rect area = WorkingArea(screen);
            double s = config.UiScale, width, height, left, top;
            if (Compact) {
                WindowGeometry saved; config.Layouts.TryGetValue(mode, out saved);
                Size design = DefaultSize(mode), min = MinSize(mode), max = MaxSize(mode);
                width = saved != null ? saved.Width : design.Width * s; height = saved != null ? saved.Height : design.Height * s;
                width = Math.Max(min.Width * s, Math.Min(Math.Min(max.Width * s, area.Width - 24), width));
                height = Math.Max(min.Height * s, Math.Min(Math.Min(max.Height * s, area.Height - 24), height));
                left = (saved != null ? saved.Left : null) ?? config.CompactLeft ?? area.Right - width - 24;
                top = (saved != null ? saved.Top : null) ?? config.CompactTop ?? area.Top + 24;
            } else {
                width = Math.Min(config.WindowWidth, area.Width - 24); height = Math.Min(config.WindowHeight, area.Height - 24);
                left = config.WindowLeft ?? area.Right - width - 16; top = config.WindowTop ?? area.Bottom - height - 16;
            }
            bool onScreen = false;
            foreach (Screen monitor in window.Screens.All) {
                Rect r = WorkingArea(monitor);
                if (left + 80 > r.Left && left < r.Right - 80 && top + 45 > r.Top && top < r.Bottom - 45) { onScreen = true; break; }
            }
            if (!onScreen) { left = area.Right - width - (Compact ? 24 : 16); top = Compact ? area.Top + 24 : area.Bottom - height - 16; }
            window.Width = width; window.Height = height;
            window.Position = DipToPixel(new Point(left, top), screen);
            SetScale(config.UiScale, false); restored = true;
        }
        private static PixelPoint DipToPixel(Point dip, Screen screen) {
            double s = screen != null && screen.Scaling > 0 ? screen.Scaling : 1;
            return new PixelPoint((int)Math.Round(dip.X * s), (int)Math.Round(dip.Y * s));
        }
        private static Point PixelToDip(PixelPoint pixel, Screen screen) {
            double s = screen != null && screen.Scaling > 0 ? screen.Scaling : 1;
            return new Point(pixel.X / s, pixel.Y / s);
        }
        public void ResetPosition() {
            if (Compact) { config.Layouts.Remove(mode); config.CompactLeft = null; config.CompactTop = null; }
            else { config.WindowLeft = null; config.WindowTop = null; config.WindowWidth = 420; config.WindowHeight = 790; }
            restored = false; Restore(); Capture(); save();
        }
        // Each display size saves its own geometry.
        public void Capture() {
            if (!restored || window.WindowState != WindowState.Normal) return;
            Screen screen = ScreenNow;
            Point at = PixelToDip(window.Position, screen);
            if (Compact) { config.Layouts[mode] = new WindowGeometry { Left = at.X, Top = at.Y, Width = window.Width, Height = window.Height }; return; }
            config.WindowLeft = at.X; config.WindowTop = at.Y;
            config.WindowWidth = window.Width; config.WindowHeight = window.Height;
        }
        private void QueueSave() { if (!restored) return; geometryTimer.Stop(); geometryTimer.Start(); }
        public void SetScale(double scale, bool persist) {
            double previous = config.UiScale;
            config.UiScale = Math.Max(.8, Math.Min(1.4, scale));
            var transform = new ScaleTransform(config.UiScale, config.UiScale);
            if (scaleHost != null) scaleHost.LayoutTransform = transform;
            if (compactHost != null) compactHost.LayoutTransform = transform;
            ApplyLimits();
            // A compact size grows and shrinks with its content; the full panel keeps its size.
            if (Compact && restored && Math.Abs(previous - config.UiScale) > 1e-6) { double ratio = config.UiScale / previous; window.Width *= ratio; window.Height *= ratio; }
            if (persist) save();
        }

        // ── Backdrop（材质与背板）────────────────────────────────────────
        public void ApplyMaterial() { MaterialStatus = ApplyBackdrop(window, FindSurface(), ClampOpacity(config.SurfaceOpacity), ParseTint(config.SurfaceColor)); }
        // Values other than the saved config are a preview; ApplyMaterial() restores them.
        public void ApplyMaterial(string material, double opacity, AvaColor tint) { MaterialStatus = ApplyBackdrop(window, FindSurface(), opacity, tint); }
        private Border FindSurface() { return window.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "Surface"); }
        public const double MinOpacity = 0, DefaultOpacity = .66;
        public static readonly AvaColor DefaultTint = AvaColor.FromRgb(0x15, 0x17, 0x1B);
        public static AvaColor ParseTint(string hex) { AvaColor tint; return UiColor.TryParseHex(hex, out tint) ? tint : DefaultTint; }
        public static double ClampOpacity(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value) ? Math.Max(MinOpacity, Math.Min(1, value)) : DefaultOpacity; }
        // 系统模糊（macOS AcrylicBlur，窗口在 XAML/构造时声明 TransparencyLevelHint）之上叠加
        // tint × alpha（alpha = 1 − 界面透明度）：保留模糊，又避免壁纸颜色浸染文字与量表。
        // 面板与设置窗口共用。返回 MaterialStatus 文案。
        public static string ApplyBackdrop(Window target, Border surface, string material, double opacity, AvaColor tint) { return ApplyBackdrop(target, surface, opacity, tint); }
        public static string ApplyBackdrop(Window target, Border surface, double opacity, AvaColor tint) {
            byte alpha = (byte)Math.Round(ClampOpacity(opacity) * 255);
            if (surface != null) surface.Background = new SolidColorBrush(AvaColor.FromArgb(alpha, tint.R, tint.G, tint.B));
            // macOS：把 NSVisualEffectView 的 state 固定 Active，失焦后透明度保持（任务4）。
            // 面板与设置窗口、外观预览都经过这里；幂等，非 macOS / 找不到效果视图时是空操作。
            if (OperatingSystem.IsMacOS()) MacApp.PinActiveMaterial(target);
            try {
                // Avalonia 12：实际生效级别经 PlatformImpl 查询不可靠，直接按已声明的 hint 报告。
                var hint = target.TransparencyLevelHint;
                return hint != null && hint.Contains(WindowTransparencyLevel.AcrylicBlur) ? "系统原生模糊 + 界面色调" : "不透明背景（系统不支持背景材质）";
            } catch { return "系统原生模糊 + 界面色调"; }
        }
        public void Dispose() { geometryTimer.Stop(); }
        // 拖拽移动（供面板标题区与紧凑页面使用；转发给 ResizeChrome）。
        public void EnableDrag(Avalonia.Controls.Control control) { chrome.EnableDrag(control); }
    }
}
