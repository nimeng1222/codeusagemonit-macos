// WindowFrame 的纯逻辑半边：配置规范化、每尺寸几何上下限、
// 表面色调默认值。窗口 chrome/背板的实例半边在 Desktop 程序集的 WindowFrame（Avalonia）。
// 不与 Desktop 同名的原因：跨程序集 partial 不可行，同名会互相遮蔽并产生 CS0436；
// SelfTests 仍直接回归这里的规则（Tests.cs）。
using System;
using System.Collections.Generic;

namespace CodeUsageMonit {
    public static class WindowFrameRules {
        public static void Normalize(AppConfig c) {
            c.WindowWidth = Finite(c.WindowWidth) ? Math.Max(360, Math.Min(4096, c.WindowWidth)) : 420;
            c.WindowHeight = Finite(c.WindowHeight) ? Math.Max(460, Math.Min(4096, c.WindowHeight)) : 790;
            c.UiScale = Finite(c.UiScale) ? Math.Max(.8, Math.Min(1.4, c.UiScale)) : 1;
            // Materials are no longer a choice: "opaque" became 0% transparency; Mica / Acrylic
            // both map to the system blur under the interface transparency.
            if (c.Material == "solid") c.SurfaceOpacity = 1;
            c.Material = "acrylic";
            if (c.BackgroundFit != "fit" && c.BackgroundFit != "tile") c.BackgroundFit = "fill";
            c.BackgroundDim = Finite(c.BackgroundDim) ? Math.Max(0, Math.Min(.85, c.BackgroundDim)) : .35;
            if (c.BackgroundImage == null) c.BackgroundImage = "";
            c.SurfaceOpacity = ClampOpacity(c.SurfaceOpacity);
            Color tint; c.SurfaceColor = ColorMath.TryParseHex(c.SurfaceColor, out tint) ? ColorMath.Hex(tint) : ColorMath.Hex(DefaultTint);
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

        // ── Display sizes ─────────────────────────────────────────────────
        // Sizes are at 100% interface scale; Ctrl + wheel scales them together with the content.
        // "edge" is the vertical dock (窄条): ~84 px wide, height follows its content.
        public static Size DefaultSize(string size) {
            return size == "small" ? new Size(172, 172) : size == "medium" ? new Size(360, 180) : size == "large" ? new Size(360, 430) : size == "edge" ? new Size(84, 320) : new Size(420, 790);
        }
        public static Size MinSize(string size) {
            return size == "small" ? new Size(160, 160) : size == "medium" ? new Size(300, 160) : size == "large" ? new Size(320, 340) : size == "edge" ? new Size(76, 150) : new Size(360, 460);
        }
        public static Size MaxSize(string size) {
            return size == "small" ? new Size(360, 360) : size == "medium" ? new Size(720, 360) : size == "large" ? new Size(720, 1000) : size == "edge" ? new Size(140, 1000) : new Size(4096, 4096);
        }
        public const double MinOpacity = 0, DefaultOpacity = .66;
        public static readonly Color DefaultTint = Color.FromRgb(0x15, 0x17, 0x1B);
        public static Color ParseTint(string hex) { Color tint; return ColorMath.TryParseHex(hex, out tint) ? tint : DefaultTint; }
        public static double ClampOpacity(double value) { return Finite(value) ? Math.Max(MinOpacity, Math.Min(1, value)) : DefaultOpacity; }
    }
}
