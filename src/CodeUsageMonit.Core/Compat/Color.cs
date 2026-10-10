// Lightweight replacements for System.Windows.Media.Color and System.Windows.Size:
// the colour maths (from ColorPicker.cs / WindowFrame.cs) and the window geometry
// limits (WindowFrame.Normalize/DefaultSize/MinSize/MaxSize) are pure logic and live in
// Core; the UI layer converts to Avalonia types when it draws.
using System;
using System.Globalization;

namespace CodeUsageMonit {
    public struct Color {
        public byte R, G, B;
        public static Color FromRgb(byte r, byte g, byte b) { return new Color { R = r, G = g, B = b }; }
        public static Color White { get { return FromRgb(255, 255, 255); } }
        public static Color Black { get { return FromRgb(0, 0, 0); } }
        public bool Equals(Color other) { return R == other.R && G == other.G && B == other.B; }
        public override bool Equals(object obj) { return obj is Color other && Equals(other); }
        public override int GetHashCode() { return (R << 16) | (G << 8) | B; }
        public static bool operator ==(Color a, Color b) { return a.Equals(b); }
        public static bool operator !=(Color a, Color b) { return !a.Equals(b); }
    }

    public struct Size {
        public double Width, Height;
        public Size(double width, double height) { Width = width; Height = height; }
    }

    // Colour maths shared by WindowFrame.Normalize and the settings UI, on the Core Color struct.
    public static class ColorMath {
        internal static Color FromHsv(double h, double s, double v) {
            h = ((h % 360) + 360) % 360; double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c, r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; } else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }
        internal static void ToHsv(Color color, out double h, out double s, out double v) {
            double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max; s = max <= 0 ? 0 : d / max;
            if (d <= 0) h = 0; else if (max == r) h = 60 * (((g - b) / d) % 6); else if (max == g) h = 60 * ((b - r) / d + 2); else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }
        internal static string Hex(Color c) { return "#" + c.R.ToString("X2", CultureInfo.InvariantCulture) + c.G.ToString("X2", CultureInfo.InvariantCulture) + c.B.ToString("X2", CultureInfo.InvariantCulture); }
        internal static bool TryParseHex(string text, out Color color) {
            color = Color.Black; string t = (text ?? "").Trim().TrimStart('#');
            if (t.Length == 3) t = new string(new[] { t[0], t[0], t[1], t[1], t[2], t[2] });
            int value; if (t.Length != 6 || !Int32.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return false;
            color = Color.FromRgb((byte)(value >> 16), (byte)(value >> 8 & 255), (byte)(value & 255)); return true;
        }
        // WCAG 2 contrast ratio between two opaque colours (1 … 21).
        internal static double ContrastRatio(Color a, Color b) {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + .05) / (Math.Min(la, lb) + .05);
        }
        private static double Luminance(Color c) {
            Func<byte, double> lin = x => { double s = x / 255.0; return s <= .03928 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); };
            return .2126 * lin(c.R) + .7152 * lin(c.G) + .0722 * lin(c.B);
        }
    }
}
