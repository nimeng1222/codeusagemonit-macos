// 背景图与表面外观：表面 = 系统模糊上的颜色×不透明度；可选背景图
// （data/background.*）+ 压暗层。WPF BitmapImage → Avalonia Bitmap（文件读取后立即释放）。
using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaColor = Avalonia.Media.Color;
using Avalonia.Layout;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace CodeUsageMonit {
    // Window surface: a colour and an "interface transparency" (0 = opaque) over the system
    // blur, plus an optional background picture. The picture is a copy in data/ (background.*); nothing
    // else is stored and no service is involved.
    public sealed partial class MonitorPanel {
        public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };
        private const long MaxImageBytes = 40L * 1024 * 1024;

        private static string SavedBackgroundPath(AppConfig c) {
            if (String.IsNullOrEmpty(c.BackgroundImage)) return null;
            string path = Path.Combine(Store.Data, Path.GetFileName(c.BackgroundImage));
            return File.Exists(path) ? path : null;
        }
        // Decoded and released immediately（读入后立即释放文件）。
        public static Bitmap LoadPicture(string path) {
            if (String.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try {
                if (new FileInfo(path).Length > MaxImageBytes) return null;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return new Bitmap(stream);
            } catch { return null; }
        }
        private void ApplyAppearance() { ApplyAppearance(config.SurfaceOpacity, SavedBackgroundPath(config), config.BackgroundFit, config.BackgroundDim, WindowFrame.ParseTint(config.SurfaceColor)); }
        // The last decoded picture, so dragging a slider or the palette does not decode again.
        private string cachedPicturePath; private DateTime cachedPictureStamp; private Bitmap cachedPicture;
        private Bitmap CachedPicture(string path) {
            if (String.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            DateTime stamp; try { stamp = File.GetLastWriteTimeUtc(path); } catch { return null; }
            if (path != cachedPicturePath || stamp != cachedPictureStamp) { if (cachedPicture != null) cachedPicture.Dispose(); cachedPicture = LoadPicture(path); cachedPicturePath = path; cachedPictureStamp = stamp; }
            return cachedPicture;
        }
        // alpha = 1 − transparency. The picture fades with the same transparency; the dim
        // layer (in a darker shade of the surface colour) keeps text readable on busy pictures.
        private void ApplyAppearance(double alpha, string picture, string fit, double dim, AvaColor tint) {
            frame.ApplyMaterial("acrylic", alpha, tint);
            var layer = Named<Border>(window, "BackdropImage"); var scrim = Named<Border>(window, "BackdropScrim");
            if (layer == null || scrim == null) return;
            Bitmap image = CachedPicture(picture);
            if (image == null) { layer.IsVisible = false; scrim.IsVisible = false; layer.Background = null; return; }
            var brush = new ImageBrush(image) { Stretch = fit == "fit" ? Stretch.Uniform : fit == "tile" ? Stretch.None : Stretch.UniformToFill };
            layer.Background = brush; layer.Opacity = Math.Max(0, Math.Min(1, alpha)); layer.IsVisible = true;
            scrim.Background = new SolidColorBrush(AvaColor.FromArgb((byte)Math.Round(Math.Max(0, Math.Min(.85, dim)) * 255), (byte)(tint.R * .6), (byte)(tint.G * .6), (byte)(tint.B * .6)));
            scrim.Opacity = layer.Opacity; scrim.IsVisible = true;
        }
        // Opaque background for popups (period picker, palette): the surface colour, a shade lighter.
        private IBrush PopupSurface() {
            AvaColor c = WindowFrame.ParseTint(config.SurfaceColor);
            Func<byte, byte> lift = x => (byte)Math.Round(x + (255 - x) * .03);
            return new SolidColorBrush(AvaColor.FromRgb(lift(c.R), lift(c.G), lift(c.B)));
        }
        // Copies the chosen picture into data/ and removes an older one.
        private static string StoreBackground(string source) {
            string ext = (Path.GetExtension(source) ?? "").ToLowerInvariant(); if (!ImageExtensions.Contains(ext)) ext = ".png";
            string name = "background" + ext, target = Path.Combine(Store.Data, name);
            if (!String.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.Ordinal)) File.Copy(source, target, true);
            foreach (string old in Directory.GetFiles(Store.Data, "background.*")) if (!String.Equals(old, target, StringComparison.Ordinal)) { try { File.Delete(old); } catch { } }
            return name;
        }
        private static void RemoveBackground() { try { foreach (string old in Directory.GetFiles(Store.Data, "background.*")) File.Delete(old); } catch { } }
    }
}
