// 设置窗口与 通用 / 认证 / 高级 页 + 自定义平台编辑器。
// 差异：无边框窗口经 WindowDecorations=None（Avalonia 12 新名）+ ResizeChrome；Slider → MiniSlider；
// 文件/文件夹对话框 → StorageProvider；Explorer/cmd → open / osascript；DPAPI 文案改钥匙串。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using AvaColor = Avalonia.Media.Color;
using Avalonia.Automation;
using CodeUsageMonit.Desktop;

namespace CodeUsageMonit {
    // Settings open in their own window (same surface, cards and controls as the panel), so
    // the panel keeps whatever display size it has. Changes apply on Save; Esc / close cancels.
    public sealed partial class MonitorPanel {
        private Window settingsWindow;
        private Border settingsSurface;
        private Grid settingsHost;
        private TextBlock settingsTitle;
        private Button settingsBack;
        private Control settingsView;
        private static readonly int[] RefreshSteps = { 1, 2, 3, 5, 10, 15, 20, 30, 45, 60 };

        private void OpenSettings() {
            if (settingsWindow != null) {
                if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
                settingsWindow.Activate(); return;
            }
            settingsWindow = BuildSettingsWindow();
            settingsView = BuildSettings();
            settingsHost.Children.Add(settingsView);
            I18nUi.Localize(settingsWindow);
            settingsButton.Foreground = AccentBrush;
            settingsWindow.Show(); settingsWindow.Activate();
        }
        // Cancel / Esc / the window's close button: drop unsaved changes and previews.
        private void CloseSettings() {
            if (settingsWindow == null) return;
            Window closing = settingsWindow;
            settingsWindow = null; settingsView = null; customEditor = null; customList = null; settingsHost = null;
            accountHosts.Clear();   // Phase 5：账户区宿主随设置页关闭作废（下次打开重建）
            ApplyAppearance(); // drop an unsaved transparency / picture preview
            settingsButton.ClearValue(TemplatedControl.ForegroundProperty);
            closing.Close();
            if (activeSize != config.DisplaySize) ApplyDisplaySize(config.DisplaySize); else Render();
        }
        private Window BuildSettingsWindow() {
            double s = config.UiScale;
            var w = new Window {
                Title = "codeusagemonit 设置", Width = 460 * s, Height = 720 * s, MinWidth = 400 * s, MinHeight = 480 * s,
                // Avalonia 12：新属性 WindowDecorations（旧名 SystemDecorations 已过时）。
                WindowDecorations = Avalonia.Controls.WindowDecorations.None, CanResize = true, Background = Brushes.Transparent, ShowInTaskbar = true,
                TransparencyLevelHint = new[] { WindowTransparencyLevel.AcrylicBlur }, TransparencyBackgroundFallback = Brushes.Transparent,
                FontFamily = window.FontFamily, FontSize = window.FontSize, Foreground = window.Foreground
            };
            try { w.Icon = MakeTrayIcon(); } catch { }
            // Beside the panel when there is room, otherwise centred on the panel's screen.
            Screen screen = null; try { screen = window.Screens.ScreenFromWindow(window); } catch { }
            if (screen == null) { try { screen = window.Screens.All.FirstOrDefault(); } catch { } }
            double scaling = screen != null && screen.Scaling > 0 ? screen.Scaling : 1;
            Rect area = new Rect(screen != null ? screen.WorkingArea.X / scaling : 0, screen != null ? screen.WorkingArea.Y / scaling : 0,
                screen != null ? screen.WorkingArea.Width / scaling : 1440, screen != null ? screen.WorkingArea.Height / scaling : 900);
            double left = window.IsVisible ? window.Position.X / scaling - w.Width - 12 : area.Left + (area.Width - w.Width) / 2;
            if (left < area.Left + 8) left = window.IsVisible && window.Position.X / scaling + window.Width + 12 + w.Width <= area.Right ? window.Position.X / scaling + window.Width + 12 : area.Left + (area.Width - w.Width) / 2;
            double top = Math.Max(area.Top, area.Top + (area.Height - w.Height) / 2);
            if (w.Height > area.Height) w.Height = area.Height;
            w.Position = new PixelPoint((int)Math.Round(Math.Max(area.Left, left) * scaling), (int)Math.Round(top * scaling));

            settingsSurface = new Border { BorderBrush = Brush("#26FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(WindowFrame.ParseTint(config.SurfaceColor)) };
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) }); root.RowDefinitions.Add(new RowDefinition());
            var head = new Grid { Background = Brushes.Transparent, Margin = new Thickness(10, 0, 8, 0) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            settingsBack = Styled(new Button(), "icon-button");
            settingsBack.Content = Icons.GlyphBound(settingsBack, Icons.ChevronRight, 13);
            settingsBack.RenderTransform = new RotateTransform(180);
            settingsBack.IsVisible = false; ToolTip.SetTip(settingsBack, "返回（Esc）"); settingsBack.Margin = new Thickness(-4, 0, 2, 0);
            AutomationProperties.SetName(settingsBack, "返回");
            settingsBack.Click += delegate { BackFromSettings(); };
            head.Children.Add(settingsBack);
            settingsTitle = Label("设置", 12.5, Ink); settingsTitle.FontWeight = FontWeight.SemiBold; settingsTitle.Margin = new Thickness(4, 0, 0, 0);
            Grid.SetColumn(settingsTitle, 1); head.Children.Add(settingsTitle);
            var close = Styled(new Button(), "icon-button");
            close.Content = Icons.GlyphBound(close, Icons.Close, 11);
            ToolTip.SetTip(close, "关闭（不保存）");
            AutomationProperties.SetName(close, "关闭设置");
            close.Click += delegate { CloseSettings(); };
            Grid.SetColumn(close, 2); head.Children.Add(close);
            head.PointerPressed += delegate(object sender, PointerPressedEventArgs e) { if (e.GetCurrentPoint(head).Properties.IsLeftButtonPressed) { try { w.BeginMoveDrag(e); } catch { } } };
            root.Children.Add(head);
            root.Children.Add(new Border { Height = 1, Background = Hairline, VerticalAlignment = VerticalAlignment.Bottom });
            settingsHost = new Grid(); Grid.SetRow(settingsHost, 1); root.Children.Add(settingsHost);
            var windowRoot = new Panel();
            var scaleHost = new LayoutTransformControl { LayoutTransform = new ScaleTransform(s, s), Child = root };
            settingsSurface.Child = scaleHost;
            windowRoot.Children.Add(settingsSurface);
            var resizeLayer = new Panel();
            windowRoot.Children.Add(resizeLayer);
            w.Content = windowRoot;
            var settingsChrome = new ResizeChrome(w, resizeLayer);
            settingsChrome.Build(7);
            w.Opened += delegate { WindowFrame.ApplyBackdrop(w, settingsSurface, config.SurfaceOpacity, WindowFrame.ParseTint(config.SurfaceColor)); };
            w.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { BackFromSettings(); e.Handled = true; } };
            // Closed from outside (Cmd+W, taskbar): same as cancel.
            w.Closed += delegate { if (settingsWindow == w) CloseSettings(); };
            return w;
        }
        // Colour / transparency / picture preview until Save or Cancel (the settings window
        // follows the colour and transparency, the picture is shown on the panel only).
        private void PreviewAppearance(double alpha, string picture, string fit, double dim, AvaColor tint) {
            ApplyAppearance(alpha, picture, fit, dim, tint);
            if (settingsWindow != null) WindowFrame.ApplyBackdrop(settingsWindow, settingsSurface, "acrylic", alpha, tint);
        }
        // Five pages share one Save / Cancel bar: 通用 (language, start-up, platforms, window,
        // look), 认证 (accounts, keys, custom platforms), 数据 (devices, SQL, backups, WebDAV),
        // 高级 (refresh, proxy, prices, maintenance) and 关于 (version, updates, links).
        // Buttons on the 数据 page (export, backup, sync…) act at once; everything else on Save.
        private string settingsTab = "general";
        private static readonly string[][] SettingsTabs = { new[] { "general", "通用" }, new[] { "accounts", "认证" }, new[] { "data", "数据" }, new[] { "advanced", "高级" }, new[] { "about", "关于" } };
        private Control BuildSettings() {
            // Enabled 空值防御：settings.json 里 "Enabled": null 会让构建期的
            // config.Enabled.Contains(id)（errors.log 2026-10-09 的 NullReferenceException）
            // 直接崩掉设置窗口。归位到构建入口（首次使用 config.Enabled 之前），保存
            // 回调经本入口打开窗口时看到的一定是归位后的集合，无需重复防御。
            if (config.Enabled == null) config.Enabled = (string[])ProviderCatalog.DefaultEnabled.Clone();
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var pages = SettingsTabs.ToDictionary(t => t[0], t => new StackPanel());
            if (!pages.ContainsKey(settingsTab)) settingsTab = "general";
            // Background=透明：设置页卡片之间的缝隙可命中，滚轮在空白处同样生效（滚轮盲区
            // 修复，App.axaml ScrollViewer 模板把它绑到 PART_ContentPresenter）。
            var scroll = new ScrollViewer { Content = pages[settingsTab], Background = Brushes.Transparent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(14, 2, 8, 8), Margin = new Thickness(0, 0, 4, 0) };
            Grid.SetRow(scroll, 1); root.Children.Add(scroll);
            Control tabs = Segmented(SettingsTabs.Select(t => new[] { t[0], I18n.T(t[1]) }).ToArray(), settingsTab, code => { settingsTab = code; scroll.Content = pages[code]; scroll.Offset = new Vector(0, 0); });
            tabs.Margin = new Thickness(14, 10, 14, 4); root.Children.Add(tabs);
            StackPanel general = pages["general"], accounts = pages["accounts"], dataPage = pages["data"], advanced = pages["advanced"], aboutPage = pages["about"];

            // ── 通用 ── Language and start-up
            general.Children.Add(SectionTitle(I18n.T("语言与启动")));
            var startCard = new StackPanel();
            string language = config.Language == "zh" || config.Language == "en" ? config.Language : "auto";
            var languageRow = Row();
            AddRow(languageRow, FieldLabel(I18n.T("界面语言"), I18n.T("“跟随系统”按 macOS 的界面语言")), Segmented(new[] { new[] { "auto", I18n.T("跟随系统") }, new[] { "zh", "中文" }, new[] { "en", "English" } }, language, code => language = code));
            startCard.Children.Add(languageRow); startCard.Children.Add(Separator());
            bool startupOn = demo || Program.SideBySide ? false : AutoStart.IsEnabled();
            CheckBox autoStart = SwitchRow(I18n.T("登录时自动运行"), demo ? I18n.T("演示模式下不可更改") : Program.SideBySide ? I18n.T("并行运行的副本不可更改") : I18n.T("启动后只驻留菜单栏"), startupOn); autoStart.IsEnabled = !demo && !Program.SideBySide;
            CheckBox hidden = SwitchRow(I18n.T("隐藏账户邮箱的部分字符"), I18n.T("截图或分享时更安全"), config.HideAccounts);
            startCard.Children.Add(autoStart); startCard.Children.Add(Separator()); startCard.Children.Add(hidden);
            general.Children.Add(Card(startCard));

            // Providers
            general.Children.Add(SectionTitle(I18n.T("显示的平台")));
            var providers = new StackPanel();
            var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, -6) };
            var choices = new Dictionary<string, CheckBox>();
            foreach (string id in ProviderCatalog.All) {
                var chipContent = new StackPanel { Orientation = Orientation.Horizontal };
                Control icon = Icon(id, 13); icon.Margin = new Thickness(0, 0, 6, 0); chipContent.Children.Add(icon);
                chipContent.Children.Add(new TextBlock { Text = ProviderCatalog.Name(id), VerticalAlignment = VerticalAlignment.Center });
                var chip = new CheckBox { Content = chipContent, IsChecked = config.Enabled.Contains(id) };
                Styled(chip, "chip");
                AutomationProperties.SetName(chip, ProviderCatalog.Name(id));
                choices[id] = chip; chips.Children.Add(chip);
            }
            providers.Children.Add(chips);
            providers.Children.Add(Hint(I18n.T("关闭的平台不再联网查询，也不出现在标签栏。"), 10));
            providers.Children.Add(Separator());
            CheckBox thirdPartySwitch = SwitchRow(I18n.T("统计第三方 API 用量"), I18n.T("按本机日志统计经中转站的 Token；服务商的限额无法得知"), config.ShowThirdParty);
            providers.Children.Add(thirdPartySwitch);
            general.Children.Add(Card(providers));

            // Window
            general.Children.Add(SectionTitle(I18n.T("窗口")));
            var windowCard = new StackPanel();
            // One window, four sizes: small / medium / large sit by the desktop edge; full is the panel.
            string displaySize = config.DisplaySize, initialSize = config.DisplaySize;
            var sizeRow = Row();
            AddRow(sizeRow, FieldLabel(I18n.T("显示尺寸"), I18n.T("拖动边缘可调整每种尺寸的大小")), Segmented(DisplaySizes.Select(sz => new[] { sz, SizeName(sz) }).ToArray(), displaySize, code => displaySize = code));
            windowCard.Children.Add(sizeRow); windowCard.Children.Add(Separator());
            CheckBox onTop = SwitchRow(I18n.T("置于其他窗口上方"), I18n.T("默认关闭；macOS 不模拟“桌面层”"), config.AlwaysOnTop);
            CheckBox autoHide = SwitchRow(I18n.T("点击其他窗口时自动收起"), I18n.T("仅完整面板，默认关闭；收起后可从托盘或 Dock 唤回"), config.HideOnDeactivate);
            windowCard.Children.Add(onTop); windowCard.Children.Add(Separator()); windowCard.Children.Add(autoHide); windowCard.Children.Add(Separator());
            var zoomRow = Row(); var zoomValue = Label((config.UiScale * 100).ToString("0") + "%", 12, InkDim); Tabular(zoomValue);
            AddRow(zoomRow, FieldLabel(I18n.T("界面缩放"), I18n.T("也可以在面板上按 Ctrl + 滚轮")), zoomValue); windowCard.Children.Add(zoomRow);
            var zoom = new MiniSlider { Minimum = .8, Maximum = 1.4, Snap = .05, SmallChange = .05 };
            zoom.Value = config.UiScale;
            AutomationProperties.SetName(zoom, I18n.T("界面缩放"));
            zoom.ValueChanged += delegate { zoomValue.Text = (Math.Round(zoom.Value * 100)).ToString("0") + "%"; };
            zoom.Margin = new Thickness(0, 8, 0, 0);
            windowCard.Children.Add(zoom); windowCard.Children.Add(Separator());
            var placeRow = Row(); var reset = Styled(new Button { Content = I18n.T("重置") }, "secondary");
            reset.Click += delegate { frame.ResetPosition(); };
            AddRow(placeRow, FieldLabel(I18n.T("窗口位置与尺寸"), I18n.T("当前显示尺寸回到默认位置和大小")), reset); windowCard.Children.Add(placeRow);
            general.Children.Add(Card(windowCard));

            // Appearance: surface colour and interface transparency (0 = opaque) — together in
            // the palette, transparency also as a slider — and an optional background picture.
            general.Children.Add(SectionTitle(I18n.T("外观")));
            var look = new StackPanel();
            double transparency = Math.Round((1 - config.SurfaceOpacity) * 100);
            AvaColor tint = WindowFrame.ParseTint(config.SurfaceColor);
            string picture = SavedBackgroundPath(config) ?? "", fit = config.BackgroundFit; double dim = config.BackgroundDim; bool pictureChanged = false;
            Action preview = () => PreviewAppearance(1 - transparency / 100, picture, fit, dim, tint);
            var colorRow = Row();
            var swatchFill = new Border { CornerRadius = new CornerRadius(5) };
            var swatch = new Grid { Width = 40, Height = 22, Margin = new Thickness(0, 0, 8, 0) };
            swatch.Children.Add(new Border { CornerRadius = new CornerRadius(5), Background = Checkerboard() }); swatch.Children.Add(swatchFill);
            swatch.Children.Add(new Border { CornerRadius = new CornerRadius(5), BorderBrush = Brush("#33FFFFFF"), BorderThickness = new Thickness(1) });
            var swatchText = Label("", 11.5, Ink); Tabular(swatchText); swatchText.VerticalAlignment = VerticalAlignment.Center;
            var swatchContent = new StackPanel { Orientation = Orientation.Horizontal }; swatchContent.Children.Add(swatch); swatchContent.Children.Add(swatchText);
            swatchContent.Children.Add(Icons.Glyph(Icons.ChevronDown, 8, InkFaint, HorizontalAlignment.Left, VerticalAlignment.Center).Also(g => g.Margin = new Thickness(8, 1, 0, 0)));
            var colorButton = Styled(new Button { Content = swatchContent, Padding = new Thickness(6, 4, 10, 4) }, "secondary");
            ToolTip.SetTip(colorButton, I18n.T("打开调色盘：颜色与透明度"));
            AutomationProperties.SetName(colorButton, I18n.T("背景颜色"));
            AddRow(colorRow, FieldLabel(I18n.T("背景颜色"), I18n.T("调色盘选颜色和透明度，深色最清楚")), colorButton);
            look.Children.Add(colorRow);
            var transparencyValue = Label(transparency.ToString("0") + "%", 12, InkDim); Tabular(transparencyValue);
            var transparencyRow = Row(); transparencyRow.Margin = new Thickness(0, 12, 0, 0);
            AddRow(transparencyRow, FieldLabel(I18n.T("界面透明度"), I18n.T("0 为不透明，数值越大越能看到后面的桌面")), transparencyValue);
            look.Children.Add(transparencyRow);
            var transparencySlider = new MiniSlider { Minimum = 0, Maximum = 100, SmallChange = 1 };
            transparencySlider.Value = transparency;
            AutomationProperties.SetName(transparencySlider, I18n.T("界面透明度"));
            Action refreshSwatch = () => { swatchFill.Background = new SolidColorBrush(AvaColor.FromArgb((byte)Math.Round((1 - transparency / 100) * 255), tint.R, tint.G, tint.B)); swatchText.Text = UiColor.Hex(tint); };
            transparencySlider.Margin = new Thickness(0, 8, 0, 0);
            transparencySlider.ValueChanged += delegate { transparency = Math.Round(transparencySlider.Value); transparencyValue.Text = transparency.ToString("0") + "%"; refreshSwatch(); preview(); };
            colorButton.Click += delegate {
                ShowColorPicker(colorButton, tint, transparency, (c, t) => {
                    tint = c;
                    if (Math.Abs(t - transparency) > .1) transparencySlider.Value = t; // previews through the slider
                    else { refreshSwatch(); preview(); }
                });
            };
            refreshSwatch();
            look.Children.Add(transparencySlider); look.Children.Add(Separator());
            var pictureRow = new Grid();
            pictureRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); pictureRow.ColumnDefinitions.Add(new ColumnDefinition()); pictureRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var thumb = new Border { Width = 64, Height = 40, CornerRadius = new CornerRadius(6), BorderBrush = Brush("#26FFFFFF"), BorderThickness = new Thickness(1), Background = Brush("#0AFFFFFF"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            var pictureLabel = FieldLabel(I18n.T("背景图片"), picture.Length > 0 ? I18n.T("已设置 · 复制保存在 data 目录") : I18n.T("未设置 · 支持 PNG / JPG / BMP / GIF / WebP"));
            var pictureButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var choose = Styled(new Button { Content = I18n.T("选择图片…") }, "secondary");
            var clear = Styled(new Button { Content = I18n.T("移除"), Margin = new Thickness(4, 0, 0, 0) }, "link-button");
            AutomationProperties.SetName(choose, I18n.T("选择背景图片")); AutomationProperties.SetName(clear, I18n.T("移除背景图片"));
            pictureButtons.Children.Add(choose); pictureButtons.Children.Add(clear);
            pictureRow.Children.Add(thumb); Grid.SetColumn(pictureLabel, 1); pictureRow.Children.Add(pictureLabel); Grid.SetColumn(pictureButtons, 2); pictureRow.Children.Add(pictureButtons);
            look.Children.Add(pictureRow);
            var fitRow = Row(); fitRow.Margin = new Thickness(0, 12, 0, 0);
            AddRow(fitRow, FieldLabel(I18n.T("填充方式"), null), Segmented(new[] { new[] { "fill", I18n.T("填满") }, new[] { "fit", I18n.T("适应") }, new[] { "tile", I18n.T("平铺") } }, fit, code => { fit = code; preview(); }));
            look.Children.Add(fitRow);
            var dimValue = Label((dim * 100).ToString("0") + "%", 12, InkDim); Tabular(dimValue);
            var dimRow = Row(); dimRow.Margin = new Thickness(0, 12, 0, 0);
            AddRow(dimRow, FieldLabel(I18n.T("图片压暗"), I18n.T("让图片上的文字更清楚")), dimValue); look.Children.Add(dimRow);
            var dimSlider = new MiniSlider { Minimum = 0, Maximum = 85, SmallChange = 1 };
            dimSlider.Value = dim * 100;
            AutomationProperties.SetName(dimSlider, I18n.T("图片压暗"));
            dimSlider.Margin = new Thickness(0, 8, 0, 0);
            dimSlider.ValueChanged += delegate { dim = Math.Round(dimSlider.Value) / 100; dimValue.Text = (dim * 100).ToString("0") + "%"; preview(); };
            look.Children.Add(dimSlider);
            Action refreshPicture = () => {
                var image = LoadPicture(picture);
                thumb.Child = image == null ? null : new Image { Source = image, Width = 62, Height = 38, Stretch = Stretch.UniformToFill };
                thumb.Background = image == null ? Brush("#0AFFFFFF") : Brushes.Transparent;
                clear.IsVisible = picture.Length > 0;
                fitRow.IsVisible = dimRow.IsVisible = dimSlider.IsVisible = picture.Length > 0;
                var sub = ((StackPanel)pictureLabel).Children.OfType<TextBlock>().ElementAtOrDefault(1);
                if (sub != null) sub.Text = I18n.T(picture.Length == 0 ? "未设置 · 支持 PNG / JPG / BMP / GIF / WebP" : pictureChanged ? "预览中 · 保存后复制到 data 目录" : "已设置 · 复制保存在 data 目录");
                if (image != null) image.Dispose();
            };
            choose.Click += async delegate {
                string file = await Dialogs.PickOpenFile(settingsWindow, I18n.T("选择背景图片"), Dialogs.Images);
                if (file == null) return;
                if (LoadPicture(file) == null) { await Dialogs.Confirm(settingsWindow, "codeusagemonit", I18n.T("无法读取这张图片（格式不支持或超过 40 MB）。")); return; }
                picture = file; pictureChanged = true; refreshPicture(); preview();
            };
            clear.Click += delegate { picture = ""; pictureChanged = true; refreshPicture(); preview(); };
            refreshPicture();
            general.Children.Add(Card(look));

            // ── 认证 ── 本机检测到的账户 + 账号与密钥 + 自定义平台（抽出为 BuildAccountsPage，
            // 与保存动作只通过 keyFields / 区域草稿字段耦合；无头自测直接构建认证页）。
            kimiRegionDraft = config.KimiRegion; zaiRegionDraft = config.ZaiRegion; stepfunRegionDraft = config.StepfunRegion;
            var keyFields = new Dictionary<string, KeyField>();
            BuildAccountsPage(accounts, keyFields);

            // ── 数据 ── devices, SQL, backups, WebDAV (SettingsPages.cs)
            DataFields dataFields = BuildDataPage(dataPage);

            // ── 高级 ── Refresh and network
            advanced.Children.Add(SectionTitle(I18n.T("刷新与网络")));
            var netCard = new StackPanel();
            // Phase 2.2：刷新模式——自适应（默认）按额度消耗快慢伸缩各平台间隔；固定即原行为。
            string refreshMode = config.RefreshMode == "fixed" ? "fixed" : "adaptive";
            var modeRow = Row();
            AddRow(modeRow, FieldLabel(I18n.T("刷新模式"), I18n.T("自适应按各平台额度的消耗快慢伸缩刷新间隔（2–30 分钟）")), Segmented(new[] { new[] { "adaptive", I18n.T("自适应") }, new[] { "fixed", I18n.T("固定") } }, refreshMode, code => refreshMode = code));
            netCard.Children.Add(modeRow); netCard.Children.Add(Separator());
            int minutes = config.RefreshMinutes;
            var intervalRow = Row(); var stepper = new StackPanel { Orientation = Orientation.Horizontal };
            var minus = Styled(new Button(), "icon-button");
            minus.Width = 28; minus.Height = 28;
            // − = 更频繁、+ = 更少。
            minus.Content = Icons.GlyphBound(minus, Icons.Minus, 11);
            ToolTip.SetTip(minus, I18n.T("更频繁"));
            var value = Label(I18n.T("{0} 分钟", minutes), 12, Ink); value.MinWidth = 58; value.TextAlignment = TextAlignment.Center; Tabular(value);
            var plus = Styled(new Button(), "icon-button");
            plus.Width = 28; plus.Height = 28;
            plus.Content = Icons.GlyphBound(plus, Icons.Plus, 11);
            ToolTip.SetTip(plus, I18n.T("更少"));
            List<int> steps = RefreshSteps.Concat(new[] { minutes }).Distinct().OrderBy(n => n).ToList();
            minus.Click += delegate { int i = steps.IndexOf(minutes); if (i > 0) minutes = steps[i - 1]; value.Text = I18n.T("{0} 分钟", minutes); };
            plus.Click += delegate { int i = steps.IndexOf(minutes); if (i < steps.Count - 1) minutes = steps[i + 1]; value.Text = I18n.T("{0} 分钟", minutes); };
            stepper.Children.Add(minus); stepper.Children.Add(value); stepper.Children.Add(plus);
            AddRow(intervalRow, FieldLabel(I18n.T("自动刷新"), I18n.T("各平台默认的额度刷新间隔；下面可以单独设置")), stepper); netCard.Children.Add(intervalRow); netCard.Children.Add(Separator());
            // A platform in use (its client writing logs) is read more often.
            CheckBox fastInUse = SwitchRow(I18n.T("正在使用的平台更快刷新"), I18n.T("某个工具 10 分钟内写过本机会话记录，就单独按下面的间隔刷新它的额度和本机用量"), config.FastInUse);
            netCard.Children.Add(fastInUse);
            int inUseMinutes = Pacing.InUseChoices.Contains(config.InUseMinutes) ? config.InUseMinutes : 2;
            var inUseRow = Row(); inUseRow.Margin = new Thickness(0, 10, 0, 0);
            AddRow(inUseRow, FieldLabel(I18n.T("使用中的刷新间隔"), I18n.T("1 分钟最及时，但个别平台的额度接口可能限流")), Segmented(Pacing.InUseChoices.Select(m => new[] { m.ToString(CultureInfo.InvariantCulture), I18n.T("{0} 分钟", m) }).ToArray(), inUseMinutes.ToString(CultureInfo.InvariantCulture), code => inUseMinutes = Int32.Parse(code, CultureInfo.InvariantCulture)));
            netCard.Children.Add(inUseRow); netCard.Children.Add(Separator());
            // Platforms with their own interval.
            var ownMinutes = new Dictionary<string, int>(config.ProviderMinutes ?? new Dictionary<string, int>());
            var ownList = new StackPanel { IsVisible = ownMinutes.Count > 0 };
            var ownToggle = Styled(new Button { Content = I18n.T(ownMinutes.Count > 0 ? "收起按平台设置" : "按平台单独设置…"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, 0, 0, 0) }, "link-button");
            ownToggle.Click += delegate { bool open = !ownList.IsVisible; ownList.IsVisible = open; ownToggle.Content = I18n.T(open ? "收起按平台设置" : "按平台单独设置…"); I18nUi.Localize(ownToggle); };
            foreach (string id in ProviderCatalog.All.Where(p => config.Enabled.Contains(p) && !ProviderCatalog.LocalOnly(p))) {
                string captured = id; int current; ownMinutes.TryGetValue(id, out current); if (!Pacing.OwnChoices.Contains(current)) current = 0;
                var row = Row(); row.Margin = new Thickness(0, 8, 0, 0);
                var who = new StackPanel { Orientation = Orientation.Horizontal }; Control icon = Icon(id, 14); icon.Margin = new Thickness(0, 0, 8, 0); who.Children.Add(icon); who.Children.Add(Label(ProviderCatalog.Name(id), 12, Ink));
                var own = new StackPanel { Orientation = Orientation.Horizontal };
                var less = Styled(new Button(), "icon-button");
                less.Width = 24; less.Height = 24;
                less.Content = Icons.GlyphBound(less, Icons.Minus, 10);
                ToolTip.SetTip(less, I18n.T("更频繁"));
                var shown = Label("", 11.5, Ink); shown.MinWidth = 64; shown.TextAlignment = TextAlignment.Center; Tabular(shown);
                var more = Styled(new Button(), "icon-button");
                more.Width = 24; more.Height = 24;
                more.Content = Icons.GlyphBound(more, Icons.Plus, 10);
                ToolTip.SetTip(more, I18n.T("更少"));
                Action show = () => { int m; ownMinutes.TryGetValue(captured, out m); shown.Text = m > 0 ? I18n.T("{0} 分钟", m) : I18n.T("跟随默认"); shown.Foreground = m > 0 ? Ink : InkFaint; };
                // − / + from “default” start at the default interval and move one step from there.
                Action<int> step = delta => {
                    int m; ownMinutes.TryGetValue(captured, out m); if (m == 0) m = minutes;
                    int at = 1; for (int k = 1; k < Pacing.OwnChoices.Length; k++) if (Math.Abs(Pacing.OwnChoices[k] - m) < Math.Abs(Pacing.OwnChoices[at] - m)) at = k;
                    ownMinutes[captured] = Pacing.OwnChoices[Math.Max(1, Math.Min(Pacing.OwnChoices.Length - 1, at + delta))]; show();
                };
                less.Click += delegate { step(-1); };
                more.Click += delegate { step(1); };
                var useDefault = Styled(new Button { Content = I18n.T("默认"), Margin = new Thickness(2, 0, -6, 0) }, "link-button");
                ToolTip.SetTip(useDefault, I18n.T("跟随默认间隔"));
                useDefault.Click += delegate { ownMinutes[captured] = 0; show(); };
                own.Children.Add(less); own.Children.Add(shown); own.Children.Add(more); own.Children.Add(useDefault);
                show(); AddRow(row, who, own); ownList.Children.Add(row);
            }
            ownList.Children.Add(Hint(I18n.T("比默认间隔更长的平台会少查几次；手动刷新（F5）总是查询全部平台。"), 8));
            netCard.Children.Add(ownToggle); netCard.Children.Add(ownList); netCard.Children.Add(Separator());
            string proxyMode = config.Proxy == "direct" ? "direct" : config.Proxy == "auto" || String.IsNullOrWhiteSpace(config.Proxy) ? "auto" : "custom";
            var proxyBox = new TextBox { Text = proxyMode == "custom" ? config.Proxy : "", Margin = new Thickness(0, 8, 0, 0), IsVisible = proxyMode == "custom" };
            AutomationProperties.SetName(proxyBox, I18n.T("代理地址"));
            var proxyHint = Hint(ProxyHint(proxyMode), 6);
            var proxyRow = Row();
            AddRow(proxyRow, FieldLabel(I18n.T("网络代理"), null), Segmented(new[] { new[] { "auto", I18n.T("自动") }, new[] { "direct", I18n.T("直连") }, new[] { "custom", I18n.T("自定义") } }, proxyMode, code => {
                proxyMode = code; proxyBox.IsVisible = code == "custom"; proxyHint.Text = ProxyHint(code);
                if (code == "custom" && proxyBox.Text.Length == 0) { proxyBox.Text = "http://127.0.0.1:7890"; proxyBox.Focus(); proxyBox.SelectAll(); }
            }));
            netCard.Children.Add(proxyRow); netCard.Children.Add(proxyBox); netCard.Children.Add(proxyHint);
            advanced.Children.Add(Card(netCard));
            // Phase 4.3：官方状态页（白名单 https + 10 分钟缓存 + 失败静默，不随刷新走）。
            advanced.Children.Add(SectionTitle(I18n.T("服务状态")));
            advanced.Children.Add(Card(BuildServiceStatusCard(demo)));
            // Prices
            advanced.Children.Add(SectionTitle(I18n.T("价目")));
            Pricing.EnsureLoaded();
            CheckBox priceSyncSwitch = SwitchRow(I18n.T("每天同步官方价目"), I18n.T("从本项目 GitHub 仓库取最新 pricing.json，只下载文件、不上传任何数据；当前价目 {0}", Pricing.Day) + (Pricing.Source == "synced" ? I18n.T("（已同步）") : I18n.T("（随软件内置）")), config.PriceSync);
            advanced.Children.Add(Card(priceSyncSwitch));
            // Maintenance
            advanced.Children.Add(SectionTitle(I18n.T("维护")));
            var maintenance = new StackPanel();
            var scanRow = Row(); var scanNow = Styled(new Button { Content = I18n.T("立即扫描"), IsEnabled = !demo }, "secondary");
            scanNow.Click += async delegate { scanNow.IsEnabled = false; await ScanHistory(); scanNow.IsEnabled = true; };
            AddRow(scanRow, FieldLabel(I18n.T("本机用量"), I18n.T("增量读取各平台日志；平时会每 15 分钟自动扫描")), scanNow); maintenance.Children.Add(scanRow); maintenance.Children.Add(Separator());
            var folderRow = Row(); var openData = Styled(new Button { Content = I18n.T("打开") }, "secondary");
            openData.Click += delegate { try { Process.Start(new ProcessStartInfo("open", "\"" + Store.Data + "\"") { UseShellExecute = false }); } catch { } };
            AddRow(folderRow, FieldLabel(I18n.T("数据目录"), Store.Data), openData); maintenance.Children.Add(folderRow);
            // Phase 3.3：一键导出脱敏诊断报告（Core/Diagnostics.cs 与 CLI doctor 共用构建器）。
            maintenance.Children.Add(Separator());
            var diagRow = Row(); var diagExport = Styled(new Button { Content = I18n.T("导出诊断报告…"), IsEnabled = !demo }, "secondary");
            var diagResult = Hint("", 6);
            diagExport.Click += async delegate {
                string path = await Dialogs.PickSaveFile(settingsWindow ?? window, I18n.T("导出诊断报告"), "codeusagemonit-diagnostic-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture), "md");
                if (path == null) return;
                diagExport.IsEnabled = false; diagResult.Foreground = InkDim; diagResult.Text = I18n.T("正在生成…");
                try {
                    AppConfig c = config;
                    List<ProviderState> snapshot = states.Values.ToList();   // 后台线程读，先在 UI 线程拍快照（刷新会改 states）
                    string report = await Task.Run(() => Diagnostics.BuildReport(c, snapshot, id => limits.ChannelOf(id), DateTime.UtcNow));
                    File.WriteAllText(path, report, new System.Text.UTF8Encoding(false));
                    Say(diagResult, true, I18n.T("已导出到 {0}", Diagnostics.MaskPath(path)));
                } catch (Exception e) { Say(diagResult, false, I18n.T("导出失败：{0}", e.Message)); }
                finally { diagExport.IsEnabled = true; }
            };
            AddRow(diagRow, FieldLabel(I18n.T("诊断报告"), I18n.T("版本、平台健康、最近错误与配置指纹；自动脱敏，不含密钥")), diagExport);
            maintenance.Children.Add(diagRow); maintenance.Children.Add(diagResult);
            advanced.Children.Add(Card(maintenance));

            // ── 关于 ── (SettingsPages.cs)
            CheckBox updateSwitch = BuildAboutPage(aboutPage);

            // Action bar
            var bar = new Grid { Margin = new Thickness(14, 0, 14, 10) };
            bar.ColumnDefinitions.Add(new ColumnDefinition()); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var line = new Border { Height = 1, Background = Hairline, Margin = new Thickness(-14, 0, -14, 10) }; Grid.SetColumnSpan(line, 3); bar.Children.Add(line);
            var error = new TextBlock { FontSize = 11, Foreground = WarnBrush, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            Grid.SetRow(error, 1); bar.Children.Add(error);
            var cancel = Styled(new Button { Content = I18n.T("取消"), Margin = new Thickness(0, 0, 8, 0) }, "secondary");
            cancel.Click += delegate { CloseSettings(); };
            Grid.SetRow(cancel, 1); Grid.SetColumn(cancel, 1); bar.Children.Add(cancel);
            var save = Styled(new Button { Content = I18n.T("保存") }, "primary");
            AutomationProperties.SetName(save, I18n.T("保存设置"));
            Grid.SetRow(save, 1); Grid.SetColumn(save, 2); bar.Children.Add(save);
            Grid.SetRow(bar, 2); root.Children.Add(bar);

            save.Click += async delegate {
                Action<string, string> fail = (tab, message) => { error.Text = message; if (settingsTab != tab) { settingsTab = tab; scroll.Content = pages[tab]; } };
                if (!choices.Any(p => p.Value.IsChecked == true)) { fail("general", I18n.T("请至少保留一个平台。")); return; }
                string proxyValue = proxyMode == "custom" ? proxyBox.Text.Trim() : proxyMode;
                if (proxyMode == "custom" && proxyValue.Length == 0) { fail("advanced", I18n.T("请填写代理地址，例如 http://127.0.0.1:7890")); return; }
                string davUrl = BoxText(dataFields.Url).Trim();
                if (davUrl.Length > 0) { string davError = WebDavSync.Validate(davUrl); if (davError != null) { fail("data", davError); return; } }
                // 保存前主动探测数据目录可写：把真实路径和原因亮给用户（权限 / 磁盘 / Translocation），
                // 而不是等 Write 失败后只剩一句泛化文案。
                string probe = Store.ProbeWritable();
                if (probe != null) { fail("advanced", probe); LogError("保存设置：数据目录探测", new IOException(probe)); return; }
                try {
                    ProviderService.ResolveProxy(proxyValue);
                    // Phase 2：保存前快照密钥/启用集合/端点区域/代理，保存后据此只重置并刷新
                    // 真正变化的平台（LimitsRuntime.PlanCredentialRefresh），其余平台状态不动。
                    Dictionary<string, string> keysBefore = null; string[] enabledBefore = null;
                    Dictionary<string, string> activeBefore = null;   // Phase 5：各平台活动账户快照
                    string kimiRegionBefore = null, zaiRegionBefore = null, stepfunRegionBefore = null, proxyBefore = null;
                    if (!demo) {
                        // Phase 5：密钥快照按「活动账户」取（env 之外 Fetch 实际使用的那把）。
                        keysBefore = ProviderCatalog.All.ToDictionary(id => id, id => Store.SavedKey(id, Accounts.ActiveId(config, id)));
                        activeBefore = ProviderCatalog.All.ToDictionary(id => id, id => Accounts.ActiveId(config, id));
                        enabledBefore = config.Enabled.ToArray();
                        kimiRegionBefore = config.KimiRegion; zaiRegionBefore = config.ZaiRegion; stepfunRegionBefore = config.StepfunRegion; proxyBefore = config.Proxy;
                    }
                    bool languageChanged = I18n.Resolve(language) != I18n.Language;
                    config.Language = language; I18n.Use(language);
                    config.Proxy = proxyValue; config.RefreshMinutes = minutes; config.HideAccounts = hidden.IsChecked == true;
                    config.Enabled = ProviderCatalog.All.Where(id => choices.ContainsKey(id) ? choices[id].IsChecked == true : config.Enabled.Contains(id)).ToArray();
                    config.KimiRegion = kimiRegionDraft; config.ZaiRegion = zaiRegionDraft; config.StepfunRegion = stepfunRegionDraft;
                    config.AlwaysOnTop = onTop.IsChecked == true; config.HideOnDeactivate = autoHide.IsChecked == true; config.SurfaceOpacity = WindowFrame.ClampOpacity(1 - transparency / 100); config.SurfaceColor = UiColor.Hex(tint);
                    config.BackgroundFit = fit; config.BackgroundDim = dim;
                    if (pictureChanged) { if (picture.Length == 0) { RemoveBackground(); config.BackgroundImage = ""; } else config.BackgroundImage = StoreBackground(picture); }
                    if (displaySize != initialSize) config.DisplaySize = displaySize; // applied by CloseSettings below
                    bool thirdPartyOn = thirdPartySwitch.IsChecked == true && !config.ShowThirdParty; config.ShowThirdParty = thirdPartySwitch.IsChecked == true;
                    config.PriceSync = priceSyncSwitch.IsChecked == true; config.UpdateCheck = updateSwitch.IsChecked == true;
                    config.BackupHours = dataFields.BackupHours(); config.BackupKeep = dataFields.BackupKeep(); config.BackupFolder = dataFields.BackupFolder();
                    config.WebDavUrl = davUrl; config.WebDavUser = BoxText(dataFields.User).Trim(); config.SyncMinutes = dataFields.SyncMinutes();
                    if (!demo) {
                        // 密码框三条路径（BoxText 判空，未触碰的框 Text 可能为 null）：勾选移除 → 清空；
                        // 填了新密码 → 写入钥匙串；留空 → 保留已存密码。
                        if (dataFields.Forget != null && dataFields.Forget.IsChecked == true) Store.SetProviderKey(WebDavSync.KeyId, "");
                        else if (BoxText(dataFields.Password).Length > 0) Store.SetProviderKey(WebDavSync.KeyId, BoxText(dataFields.Password));
                        if (BoxText(dataFields.Device).Trim() != Devices.Self().Name) Devices.Rename(BoxText(dataFields.Device));
                    }
                    // P2-3：钥匙串写入失败不再静默——记录失败平台，保存流程照常完成
                    //（其余设置生效、其他平台照常定向刷新），但失败平台不进定向刷新范围
                    //（key 没存进去，刷新了也用不上），设置窗口留在认证页亮出错误。
                    var failedKeyProviders = new HashSet<string>();
                    foreach (KeyField field in keyFields.Values) {
                        if (demo) break;
                        // 只落钥匙串，不复位读数：真正的复位与刷新按“密钥是否变化”在下方定向进行
                        //（RefreshChanged），填了同一个 Key 或没动的平台不再被全量刷新打断。
                        // 写入目标 = 该输入框绑定的活动账户（Phase 5）。
                        bool saved = true;
                        if (field.Remove != null && field.Remove.IsChecked == true) Store.SetProviderKey(field.Id, field.AccountId, "");   // 清除恒成功
                        else if (!String.IsNullOrWhiteSpace(field.Box.Text)) saved = Store.SetProviderKey(field.Id, field.AccountId, field.Box.Text);
                        if (!saved) failedKeyProviders.Add(field.Id);
                    }
                    if (!demo && !Program.SideBySide) AutoStart.SetEnabled(autoStart.IsChecked == true);
                    frame.SetScale(zoom.Value, false); UpdatePin(); UpdateScaleLabel();
                    config.FastInUse = fastInUse.IsChecked == true; config.InUseMinutes = inUseMinutes; config.ProviderMinutes = ownMinutes.Where(p => p.Value > 0).ToDictionary(p => p.Key, p => p.Value);
                    config.RefreshMode = refreshMode == "fixed" ? "fixed" : "adaptive";
                    Accounts.Normalize(config);   // Phase 5：保存前修复账户结构
                    Store.Write("settings.json", config);
                    bool keySaveFailed = failedKeyProviders.Count > 0;
                    if (!keySaveFailed) CloseSettings();   // P2-3：密钥写入失败时窗口留在认证页亮错误
                    if (languageChanged) LanguageChanged();
                    if (!demo) {
                        // Phase 2 定向刷新：算出密钥/启用/端点真正变化的平台，只动它们。
                        //（密钥快照与保存一致：活动账户的钥匙串键。）
                        var keysAfter = ProviderCatalog.All.ToDictionary(id => id, id => Store.SavedKey(id, Accounts.ActiveId(config, id)));
                        var endpoints = new HashSet<string>();
                        if (kimiRegionBefore != config.KimiRegion) endpoints.Add("kimi");       // 区域切换改请求端点
                        if (zaiRegionBefore != config.ZaiRegion) endpoints.Add("zcode");
                        if (stepfunRegionBefore != config.StepfunRegion) endpoints.Add("stepfun");
                        LimitsRuntime.RefreshPlan plan = LimitsRuntime.PlanCredentialRefresh(keysBefore, keysAfter, endpoints, enabledBefore, config.Enabled);
                        // Phase 5：设置会话期间切换过活动账户的平台一并重置（切换入口自己已定向刷新；
                        // 若那次刷新与全量刷新重叠让路，这里兜底补一次，保证读数一定用新凭据重取）。
                        plan.Reset.UnionWith(Accounts.ChangedProviders(activeBefore, ProviderCatalog.All.Select(id => new KeyValuePair<string, string>(id, Accounts.ActiveId(config, id)))));
                        plan.Reset.ExceptWith(failedKeyProviders);   // P2-3：key 未存入的平台刷了也白刷
                        plan.Reset.ExceptWith(plan.Forget);
                        if (proxyBefore != config.Proxy) await Refresh();   // 代理影响所有平台的连通性：保持全量
                        else if (plan.Reset.Count > 0 || plan.Forget.Count > 0) await RefreshChanged(plan.Reset, plan.Forget);
                    }
                    if (thirdPartyOn) { var scan = ScanHistory(); }
                    if (keySaveFailed) fail("accounts", I18n.T("密钥保存失败：钥匙串不可用"));
                } catch (Exception ex) {
                    // 异常类型与消息直接上屏 + errors.log：泛化文案把真实原因（权限/磁盘/路径）吞掉了，
                    // 远程排查只能靠猜。ArgumentException 的消息本来就面向用户，保持原样。
                    if (ex is ArgumentException) error.Text = I18n.T(ex.Message);
                    else { error.Text = I18n.T("设置未能保存：{0} — {1}", ex.GetType().Name, ex.Message); LogError("保存设置失败", ex); }
                }
            };
            foreach (StackPanel page in pages.Values) I18nUi.Localize(page);
            I18nUi.Localize(bar);
            return root;
        }
        private static string ProxyHint(string mode) {
            return mode == "auto" ? "使用 macOS 系统代理，其次读取 HTTPS_PROXY / HTTP_PROXY。只作用于本程序。" : mode == "direct" ? "不使用任何代理直接连接。" : "填写 http://主机:端口，例如 http://127.0.0.1:7890。";
        }

        // ── 认证页（BuildAccountsPage）───────────────────────────────────
        // Kimi / ZCode 接口区域的草稿：分段回调写入字段，保存时落 config（抽出认证页后
        // 不能再用 BuildSettings 的局部变量捕获）。
        private string kimiRegionDraft, zaiRegionDraft, stepfunRegionDraft;
        // 认证页 = 顶部「本机检测到的账户」只读区 + 每供应商一个折叠面板（折叠头 = 图标 +
        // 名称 + 状态胶囊；key 型正文 = 控制台 / 步骤指引 / 粘贴框 / 保存并验证，CLI·桌面
        // 应用型正文沿用卡片引导）+ 自定义平台列表。
        // keyFields 由调用方创建、保存动作读取；单独成方法让无头自测可以直接构建并断言面板结构。
        private void BuildAccountsPage(StackPanel accounts, Dictionary<string, KeyField> keyFields) {
            accounts.Children.Add(SectionTitle(I18n.T("本机检测到的账户")));
            accounts.Children.Add(Card(BuildDetectedAccounts()));
            accounts.Children.Add(SectionTitle(I18n.T("账号与密钥")));
            // 缺省只展开第一个未连接的平台；折叠状态会话内记忆（credentialPanelOpen，不落盘）。
            string defaultOpen = ProviderCatalog.Ids.Where(id => id != "pi" && !ProviderConnected(id)).FirstOrDefault();
            foreach (string id in ProviderCatalog.Ids.Where(id => id != "pi")) accounts.Children.Add(CredentialPanel(id, keyFields, defaultOpen));
            accounts.Children.Add(Hint(I18n.T("密钥以 macOS 钥匙串加密保存在本机，只有当前用户能解密；同名环境变量优先。"), 12));
            // Custom providers
            accounts.Children.Add(SectionTitle(I18n.T("自定义平台")));
            customList = new StackPanel();
            RenderCustomList();
            accounts.Children.Add(Card(customList));
        }
        // 「本机检测到的账户」：全部已知平台各一行（含 CLI 登录平台），显示凭据来源与
        // 最近一次额度读取拿到的账户/套餐。只读：探测只看本机文件与钥匙串条目是否存在，
        // 不读密钥值、不发起网络请求；账户信息来自 states 缓存（邮箱按设置遮挡）。
        private Control BuildDetectedAccounts() {
            var card = new StackPanel();
            if (demo) { card.Children.Add(Hint(I18n.T("演示模式不读取本机凭据。"), 0)); return card; }
            foreach (string id in ProviderCatalog.Ids) {
                ProviderState state; states.TryGetValue(id, out state);
                string source = LocalCredentialSource.Describe(id, config);
                string who = state == null ? "" : String.Join(" · ", new[] { state.Plan, AccountLabel(id, state.Account) }.Where(s => !String.IsNullOrEmpty(s)));
                var row = Row(); row.Margin = new Thickness(0, 7, 0, 0);
                var left = new StackPanel { Orientation = Orientation.Horizontal };
                left.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = Brush(ProviderCatalog.Color(id)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
                left.Children.Add(Label(ProviderCatalog.Name(id), 12, Ink));
                var right = Label((who.Length > 0 ? who + " · " : "") + (source.Length > 0 ? source : "未检测到"), 10.5, source.Length > 0 ? InkDim : InkFaint);
                right.MaxWidth = 250; right.HorizontalAlignment = HorizontalAlignment.Right;
                AddRow(row, left, right);
                AutomationProperties.SetName(row, I18n.T("检测账户 {0}", ProviderCatalog.Name(id)));
                ToolTip.SetTip(row, I18n.T("{0}：凭据来源「{1}」。只读展示，不发起网络请求。", ProviderCatalog.Name(id), source.Length > 0 ? source : "未检测到"));
                card.Children.Add(row);
            }
            card.Children.Add(Hint(I18n.T("来源为本机文件、钥匙串与环境变量的存在性；账户与套餐来自最近一次额度读取，只读展示。"), 8));
            return card;
        }

        // ── Accounts and keys ─────────────────────────────────────────────
        // AccountId（Phase 5）：该输入框当前绑定的账户（始终是活动账户，账户区重建时刷新）。
        private sealed class KeyField { public string Id; public string AccountId; public TextBox Box; public CheckBox Remove; }
        // GitHub device flow: show a one-time code, open github.com/login/device, poll.
        private Control CopilotLoginRow() {
            var stack = new StackPanel();
            var head = Row(); var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            string existing = demo ? "" : ProviderService.CopilotToken();
            var status = Hint(demo ? "演示模式不登录。" : existing.Length > 0 ? (Store.HasSavedKey("copilot") ? "已登录（本程序保存的 GitHub 授权）。" : "已发现 Copilot 客户端保存的 GitHub 授权，可直接使用。") : "未登录。只申请 read:user 权限，用于读取 Copilot 额度。", 4);
            var login = Styled(new Button { Content = existing.Length > 0 ? "重新登录" : "使用 GitHub 登录", IsEnabled = !demo }, "secondary");
            var logout = Styled(new Button { Content = "退出", IsVisible = Store.HasSavedKey("copilot") && !demo, Margin = new Thickness(0, 0, 4, 0) }, "link-button");
            buttons.Children.Add(logout); buttons.Children.Add(login);
            AddRow(head, FieldLabel("GitHub Copilot", "OAuth 设备码登录"), buttons);
            stack.Children.Add(head); stack.Children.Add(status);
            var device = new StackPanel { IsVisible = false, Margin = new Thickness(0, 10, 0, 0) };
            var code = new TextBlock { FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Ink, FontFamily = new FontFamily("Menlo, Monaco, monospace") };
            var open = Styled(new Button { Content = "复制代码并打开 GitHub", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) }, "primary");
            var cancel = Styled(new Button { Content = "取消", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, 4, 0, 0) }, "link-button");
            device.Children.Add(Hint("在浏览器里输入下面的代码并授权：", 0)); device.Children.Add(code); device.Children.Add(open); device.Children.Add(cancel);
            stack.Children.Add(device);
            bool cancelled = false; ProviderService.DeviceLogin pending = null;
            open.Click += delegate {
                if (pending == null) return;
                try { var ignored = SetClipboardText(window, pending.UserCode); } catch { }
                try { Process.Start(new ProcessStartInfo(pending.VerificationUri) { UseShellExecute = true }); } catch { }
            };
            cancel.Click += delegate { cancelled = true; device.IsVisible = false; login.IsEnabled = true; status.Text = I18n.T("已取消登录。"); };
            logout.Click += delegate { Store.SetProviderKey("copilot", ""); states["copilot"] = new ProviderState { Id = "copilot" }; logout.IsVisible = false; login.Content = I18n.T("使用 GitHub 登录"); status.Text = I18n.T("已退出。" + (ProviderService.CopilotToken().Length > 0 ? "仍会使用 Copilot 客户端保存的授权。" : "")); };
            login.Click += async delegate {
                cancelled = false; login.IsEnabled = false; status.Text = I18n.T("正在向 GitHub 申请设备码…");
                try {
                    using (var service = new ProviderService(config)) {
                        pending = await service.StartCopilotLogin();
                        code.Text = pending.UserCode; device.IsVisible = true; status.Text = I18n.T("等待你在 GitHub 上授权…");
                        DateTime until = DateTime.UtcNow.AddSeconds(pending.ExpiresIn);
                        while (!cancelled && DateTime.UtcNow < until) {
                            await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, pending.Interval)));
                            if (cancelled) break;
                            string token = await service.PollCopilotLogin(pending);
                            if (token == null) continue;
                            Store.SetProviderKey("copilot", token); states["copilot"] = new ProviderState { Id = "copilot" };
                            device.IsVisible = false; status.Text = I18n.T("登录成功。保存设置并启用 Copilot 后即可显示额度。"); logout.IsVisible = true; login.Content = I18n.T("重新登录");
                            break;
                        }
                        if (!cancelled && device.IsVisible) { device.IsVisible = false; status.Text = I18n.T("设备码已过期，请重新登录。"); }
                    }
                } catch (Exception e) { device.IsVisible = false; status.Text = I18n.T(e is ProviderException ? e.Message : "无法连接 GitHub，请检查网络或代理设置。"); }
                finally { login.IsEnabled = true; pending = null; }
            };
            return stack;
        }

        // ── Custom providers ──────────────────────────────────────────────
        private StackPanel customList;
        private Control customEditor;
        private void RenderCustomList() {
            if (customList == null) return;
            customList.Children.Clear();
            customList.Children.Add(Hint("用一个返回 JSON 的 GET 接口接入任意服务（中转站后台、自建网关、公司内部额度接口等），用字段路径映射额度和余额。", 0));
            foreach (CustomProvider provider in ProviderCatalog.Custom.Values.OrderBy(p => p.Name)) {
                CustomProvider captured = provider;
                var row = Row(); row.Margin = new Thickness(0, 10, 0, 0);
                var left = new StackPanel { Orientation = Orientation.Horizontal };
                Control icon = Icon(provider.Id, 14); icon.Margin = new Thickness(0, 0, 8, 0); left.Children.Add(icon);
                var text = new TextBlock { FontSize = 12.5, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230 };
                text.Inlines.Add(new Run(provider.Name)); text.Inlines.Add(new Run("  " + provider.Host) { Foreground = InkFaint, FontSize = 10.5 });
                left.Children.Add(text);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var testRow = Styled(new Button { Content = "测试", IsEnabled = !demo, Margin = new Thickness(0, 0, 10, 0) }, "link-button");
                // 列表行快捷测试（此前只有编辑器里能测）：用活动账户已保存的密钥请求一次接口。
                testRow.Click += async delegate { await RunCustomTest(captured, Store.SavedKey(captured.Id, Accounts.ActiveId(config, captured.Id)), customListResult, testRow); };
                var edit = Styled(new Button { Content = "编辑", IsEnabled = !demo }, "link-button");
                edit.Click += delegate { OpenCustomEditor(captured); };
                buttons.Children.Add(testRow); buttons.Children.Add(edit);
                AddRow(row, left, buttons); customList.Children.Add(row);
            }
            // customListResult 是跨重建复用的共享 TextBlock；WPF 会静默换父，Avalonia 11+
            // 则直接抛“already has a visual parent”——先脱离旧宿主再挂（第二次打开设置页
            // 时的崩溃路径）。
            if (customListResult.Parent is Avalonia.Controls.Panel previousHost) previousHost.Children.Remove(customListResult);
            customList.Children.Add(customListResult);
            var add = Styled(new Button { Content = "添加自定义平台", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0), IsEnabled = !demo }, "secondary");
            add.Click += delegate { OpenCustomEditor(null); };
            customList.Children.Add(add);
            if (demo) customList.Children.Add(Hint("演示模式不保存自定义平台。", 6));
        }
        // 列表与编辑器共用的测试结果行；RenderCustomList 重建时按上次文本恢复。
        private readonly TextBlock customListResult = new TextBlock { FontSize = 11, Foreground = InkDim, TextWrapping = TextWrapping.Wrap, LineHeight = 17, Margin = new Thickness(2, 10, 2, 0) };
        // 编辑器与列表行共用：请求一次自定义接口并把可读结果（✓ 明细或失败原因）写进 result。
        // 返回是否成功，调用方据此追加提示；button 在请求期间禁用防重复点击。
        private async Task<bool> RunCustomTest(CustomProvider parsed, string key, TextBlock result, Button button) {
            if (button != null) button.IsEnabled = false;
            result.Foreground = InkDim; result.Text = I18n.T("正在请求 " + parsed.Host + " …");
            try {
                ProviderState state;
                using (var service = new ProviderService(config)) state = await service.FetchCustom(parsed, key);
                var lines = state.Quotas.Select(q => "✓ " + q.Label + "：剩余 " + q.Remaining.ToString("0.#") + "%" + (q.ResetUtc.Length > 0 ? "，" + Countdown(q.ResetUtc) : "")).ToList();
                lines.AddRange(state.Balances.Select(b => "✓ 余额：" + b.Currency + " " + b.Amount.ToString("N2", System.Globalization.CultureInfo.InvariantCulture)));
                if (state.Plan.Length > 0 && state.Plan != "自定义") lines.Add("✓ 套餐：" + state.Plan);
                if (state.Account.Length > 0) lines.Add("✓ 账户：" + state.Account);
                int missing = parsed.Windows.Count - state.Quotas.Count;
                if (missing > 0) lines.Add("有 " + missing + " 个窗口在响应中找不到对应字段，将不会显示。");
                result.Foreground = GoodBrush; result.Text = String.Join("\n", lines);
                return true;
            } catch (Exception e) {
                result.Foreground = WarnBrush;
                result.Text = I18n.T(e is ProviderException ? e.Message : e is TaskCanceledException ? "请求超时（15 秒）" : "请求失败：" + e.GetType().Name);
                return false;
            } finally { if (button != null) button.IsEnabled = true; }
        }
        // Back/Esc: leave the custom editor first, then the settings page.
        private void BackFromSettings() { if (customEditor != null) CloseCustomEditor(); else CloseSettings(); }
        private void CloseCustomEditor() {
            if (customEditor == null) return;
            customEditor = null; settingsHost.Children.Clear(); settingsHost.Children.Add(settingsView); settingsTitle.Text = I18n.T("设置"); settingsBack.IsVisible = false; RenderCustomList(); I18nUi.Localize(customList);
        }
        // 简单表单 ↔ JSON 描述符（fix6 #5）。简单态面向最常见的 OpenAI 兼容余额端点：
        // bearer 认证、余额读 data.balance、币种 USD、无窗口映射。生成后必须能过
        // CustomProviders.Parse 校验；只有能无损表达回表单的 JSON 才进简单态。
        // JSON 里显式写明的 id 同样属于简单形态（fix6 返工 P2）：原样带回，保存时
        // 不被名称推导覆盖。
        private static class SimpleCustom {
            public static string ToJson(string name, string url, string color, string id) {
                Func<string, string> quote = text => J.Serializer().Serialize((text ?? "").Trim());
                string c = (color ?? "").Trim();
                var fields = new List<string> {
                    "\"name\": " + quote(name),
                    "\"url\": " + quote(url),
                    "\"auth\": \"bearer\"",
                    "\"balance\": { \"amount\": \"data.balance\", \"currency\": \"USD\" }"
                };
                if (c.Length > 0) fields.Insert(1, "\"color\": " + quote(c));
                if ((id ?? "").Trim().Length > 0) fields.Insert(1, "\"id\": " + quote(id));
                return "{\n  " + String.Join(",\n  ", fields) + "\n}";
            }
            // JSON 是否被简单态无损表达（回读三格 + 可选颜色 + 可选显式 id）；窗口映射、
            // 其他 auth、自定义币种或余额路径都会落进高级态。
            public static bool TryRead(CustomProvider p, out string name, out string url, out string color, out string id) {
                name = p.Name; url = p.Url; color = p.Color; id = ExplicitId(p);
                return p.Windows.Count == 0 && p.Auth == "bearer" && p.Header.Length == 0
                    && p.Plan.Length == 0 && p.Account.Length == 0
                    && p.Balance == "data.balance" && p.CurrencyPath.Length == 0
                    && (p.Currency == "USD" || p.Currency.Length == 0);
            }
            // JSON 原文里写明的 id；没写或原文解析不了时为空串，id 继续由名称推导。
            // Parse 会再做一次小写归一与校验，这里只取原文。
            public static string ExplicitId(CustomProvider p) {
                try { return J.Str(J.Parse(p.Source), "id").Trim(); } catch { return ""; }
            }
        }
        private void OpenCustomEditor(CustomProvider existing) {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var content = new StackPanel();
            root.Children.Add(new ScrollViewer { Content = content, Background = Brushes.Transparent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(14, 6, 8, 8), Margin = new Thickness(0, 0, 4, 0) });   // Background 透明：缝隙可命中（滚轮盲区修复，同 App.axaml 模板口径）
            content.Children.Add(SectionTitle(existing == null ? "添加自定义平台" : "编辑 · " + existing.Name));
            var card = new StackPanel();
            // 简单 / 高级两态（fix6 #5）：简单态三个字段 + 可选颜色，提交时生成等价 JSON；
            // 高级态保留完整 JSON 文本域。编辑既有平台时，能无损表达回表单才默认简单态。
            string readName = "", readUrl = "", readColor = "", readId = "";
            string mode = existing != null && SimpleCustom.TryRead(existing, out readName, out readUrl, out readColor, out readId) ? "simple" : "advanced";
            string simpleId = readId; // 简单态要保留的显式 id；空串表示 id 由名称推导，ToJson 不输出
            var nameBox = new TextBox { Text = readName, Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetName(nameBox, "自定义平台名称");
            var urlBox = new TextBox { Text = readUrl, Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetName(urlBox, "自定义平台接口地址");
            var colorBox = new TextBox { Text = readColor, Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetName(colorBox, "自定义平台颜色");
            var json = new TextBox { Text = existing != null ? existing.Source : CustomProviders.Template, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Menlo, Monaco, monospace"), FontSize = 11.5, Height = 290, VerticalContentAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(10, 8, 10, 8) };
            AutomationProperties.SetName(json, "平台定义 JSON");
            // 测试/保存与切换提示共用的结果行，提前声明供分段回调写提示。
            var result = new TextBlock { FontSize = 11, Foreground = InkDim, TextWrapping = TextWrapping.Wrap, LineHeight = 17, Margin = new Thickness(2, 2, 2, 8) };
            var simplePanel = new StackPanel { IsVisible = mode == "simple" };
            var advancedPanel = new StackPanel { IsVisible = mode != "simple" };
            // mode 必须跟随分段实时同步（fix6 返工）：此前只在打开时求值一次，切到「简单」
            // 后测试/保存仍按旧模式取值——新建平台的三格输入被静默丢弃，高级改过的 JSON
            // 也被三格覆盖。高级→简单先 TryRead 回读；读不回简单形态时第一次点击只提示
            // 并回弹到「高级」（JSON 原样保留），再次点击才确认重置为简单表单（能读出的
            // 名称/地址/颜色与显式 id 保留），绝不静默丢弃用户的 JSON。新建未改动的
            // Template 占位除外——直接放行切换，否则新建用户永远进不了简单态。
            bool simpleArmed = false, reverting = false;
            RadioButton[] segments = null;
            Control modeSwitch = Segmented(new[] { new[] { "simple", I18n.T("简单") }, new[] { "advanced", I18n.T("高级") } }, mode, code => {
                if (reverting) return; // 回弹选中态触发的回调：模式与面板保持原状
                bool simple = code == "simple";
                if (simple) {
                    CustomProvider current = null; try { current = CustomProviders.Parse(json.Text); } catch { }
                    string n, u, c, id;
                    if (existing == null && json.Text == CustomProviders.Template) {
                        simpleArmed = false; // 新建未改动的 Template 只是占位，直接切简单（无可丢失内容）；改动过就走下面的回读保护
                    } else if (current != null && SimpleCustom.TryRead(current, out n, out u, out c, out id)) {
                        simpleArmed = false;
                        nameBox.Text = n; urlBox.Text = u; colorBox.Text = c; simpleId = id; // 回读无损，切换不丢字段
                    } else if (!simpleArmed) {
                        simpleArmed = true; // 第一次点击：不切换，提示后回弹
                        result.Foreground = WarnBrush;
                        result.Text = I18n.T("当前 JSON 无法在简单模式编辑（含窗口映射等字段）；再次点击「简单」将重置为简单表单。");
                        // 回弹不能同步做：分段点击的值管线会在事件处理器返回后“互斥清掉其它项”，
                        // 把这里刚设回的「高级」又清掉；投递到下一轮派发再回弹。
                        var advancedButton = segments[1];
                        Dispatcher.UIThread.Post(() => { reverting = true; advancedButton.IsChecked = true; reverting = false; }, DispatcherPriority.Background);
                        return;
                    } else { // 确认重置：可读出的字段保留，其余高级字段丢弃（已提示过一次）
                        simpleArmed = false;
                        result.Foreground = InkDim; result.Text = "";
                        nameBox.Text = current != null ? current.Name : ""; urlBox.Text = current != null ? current.Url : ""; colorBox.Text = current != null ? current.Color : "";
                        simpleId = current != null ? SimpleCustom.ExplicitId(current) : "";
                    }
                } else {
                    simpleArmed = false;
                    json.Text = SimpleCustom.ToJson(nameBox.Text, urlBox.Text, colorBox.Text, simpleId); // 简单态字段物化进 JSON，切换不丢输入
                }
                mode = code;
                simplePanel.IsVisible = simple; advancedPanel.IsVisible = !simple;
            });
            segments = ((UniformGrid)((Border)modeSwitch).Child).Children.OfType<RadioButton>().ToArray(); // Segmented 的内部结构：Border > UniformGrid > 两个 RadioButton（按声明顺序）
            modeSwitch.HorizontalAlignment = HorizontalAlignment.Left; modeSwitch.Margin = new Thickness(0, 0, 0, 4);
            card.Children.Add(modeSwitch);
            simplePanel.Children.Add(FieldLabel("名称", "1–40 个字符").Also(v => v.Margin = new Thickness(0, 8, 0, 0)));
            simplePanel.Children.Add(nameBox);
            simplePanel.Children.Add(FieldLabel("接口地址（Base URL）", "返回 JSON 的 GET 接口；公网必须 https，本机或局域网可以用 http").Also(v => v.Margin = new Thickness(0, 10, 0, 0)));
            simplePanel.Children.Add(urlBox);
            simplePanel.Children.Add(FieldLabel("颜色（可选）", "#RRGGBB，留空用默认").Also(v => v.Margin = new Thickness(0, 10, 0, 0)));
            simplePanel.Children.Add(colorBox);
            simplePanel.Children.Add(Hint("简单模式按最常见的形态生成定义：bearer 认证、余额读 data.balance、币种 USD；需要映射额度窗口或其他字段时切换到高级模式。", 8));
            advancedPanel.Children.Add(json);
            advancedPanel.Children.Add(Hint("字段：name、url、auth（none / bearer / x-api-key / token / header + header 名称）、windows[]（label + usedPercent 或 remainingPercent 或 used/remaining + limit；可选 ratio、resetsAt、resetInSeconds、windowHours）、balance（amount + currency）、plan、account。路径写法：data.items[0].used。", 8));
            card.Children.Add(simplePanel);
            card.Children.Add(advancedPanel);
            card.Children.Add(Separator());
            // ── 密钥（Phase 5 多账户）──
            // 已有平台：走共用账户区（活动账户切换 / 增删账户，密钥输入绑定活动账户）。
            // 新建平台：id 尚未产生，保留单输入框，保存时落到默认账户。
            var editorKeyFields = new Dictionary<string, KeyField>();
            TextBox secret;
            if (existing != null) {
                var secretHead = Row();
                AddRow(secretHead, FieldLabel("密钥", "按 auth 方式放进请求头，钥匙串加密保存；支持多账户"), Label("", 11, InkFaint));
                card.Children.Add(secretHead);
                var accountHost = new StackPanel();
                card.Children.Add(accountHost);
                PopulateAccountSection(existing.Id, accountHost, editorKeyFields);
                secret = editorKeyFields[existing.Id].Box;
            } else {
                var secretHead = Row();
                AddRow(secretHead, FieldLabel("密钥", "按 auth 方式放进请求头，钥匙串加密保存"), Label("未保存", 11, InkFaint));
                card.Children.Add(secretHead);
                secret = new KeyBox { Margin = new Thickness(0, 8, 0, 0) };
                AutomationProperties.SetName(secret, "自定义平台密钥");
                card.Children.Add(secret);
                card.Children.Add(Hint("auth 为 none 时可以留空。", 5));
            }
            content.Children.Add(Card(card));
            content.Children.Add(result);

            var bar = new Grid { Margin = new Thickness(14, 0, 14, 10) };
            bar.ColumnDefinitions.Add(new ColumnDefinition()); for (int i = 0; i < 3; i++) bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var line = new Border { Height = 1, Background = Hairline, Margin = new Thickness(-14, 0, -14, 10) }; Grid.SetColumnSpan(line, 4); bar.Children.Add(line);
            var delete = Styled(new Button { Content = "删除此平台", Foreground = WarnBrush, HorizontalAlignment = HorizontalAlignment.Left, IsVisible = existing != null, Margin = new Thickness(-6, 0, 0, 0) }, "link-button");
            Grid.SetRow(delete, 1); bar.Children.Add(delete);
            var test = Styled(new Button { Content = "测试", Margin = new Thickness(0, 0, 8, 0) }, "secondary");
            var cancel = Styled(new Button { Content = "取消", Margin = new Thickness(0, 0, 8, 0) }, "secondary");
            var save = Styled(new Button { Content = "保存" }, "primary");
            AutomationProperties.SetName(save, "保存自定义平台");
            foreach (var pair in new[] { new { B = test, C = 1 }, new { B = cancel, C = 2 }, new { B = save, C = 3 } }) { Grid.SetRow(pair.B, 1); Grid.SetColumn(pair.B, pair.C); bar.Children.Add(pair.B); }
            Grid.SetRow(bar, 1); root.Children.Add(bar);

            cancel.Click += delegate { CloseCustomEditor(); };
            test.Click += async delegate {
                if (mode == "simple") json.Text = SimpleCustom.ToJson(nameBox.Text, urlBox.Text, colorBox.Text, simpleId); // 生成结果留在文本域，便于切到高级态继续调
                CustomProvider parsed;
                try { parsed = CustomProviders.Parse(json.Text); } catch (ArgumentException e) { result.Foreground = WarnBrush; result.Text = e.Message; return; }
                // Phase 5：测试用「输入框内容，否则活动账户已保存的密钥」。
                string typedKey = (secret.Text ?? "").Trim();
                string key = typedKey.Length > 0 ? typedKey : existing != null ? Store.SavedKey(existing.Id, Accounts.ActiveId(config, existing.Id)) : ""; // Avalonia 12 未输入的 TextBox.Text 是 null
                bool ok = await RunCustomTest(parsed, key, result, test);
                if (!ok && mode == "simple") result.Text += "\n" + I18n.T("字段映射不匹配？切换到高级模式调整。");
            };
            save.Click += delegate {
                if (mode == "simple") json.Text = SimpleCustom.ToJson(nameBox.Text, urlBox.Text, colorBox.Text, simpleId);
                CustomProvider parsed;
                try { parsed = CustomProviders.Parse(json.Text); } catch (ArgumentException e) { result.Foreground = WarnBrush; result.Text = e.Message; return; }
                if (ProviderCatalog.Ids.Contains(parsed.Id) || (ProviderCatalog.Custom.ContainsKey(parsed.Id) && (existing == null || existing.Id != parsed.Id))) { result.Foreground = WarnBrush; result.Text = I18n.T("已有同样 id 的平台（" + parsed.Id + "），请在 JSON 里指定不同的 id。"); return; }
                try {
                    var list = ProviderCatalog.Custom.Values.Where(p => existing == null || p.Id != existing.Id).ToList(); list.Add(parsed);
                    CustomProviders.Save(list);
                    string typed = (secret.Text ?? "").Trim();
                    bool keyWriteFailed = false;   // P2-3 同类：钥匙串写入失败不静默（编辑器留在原地，输入保留可重试）
                    if (existing != null && existing.Id != parsed.Id) {
                        // Renamed (Phase 5)：整份账户结构迁到新 id——每个账户的钥匙串键逐一
                        // 复制，全部成功才清空旧 id（个别失败保留旧条目：宁可残留也不丢密钥）。
                        // 输入了新密钥时，它覆盖新 id 的活动账户。
                        string activeOld = Accounts.ActiveId(config, existing.Id);
                        List<ProviderAccount> migrated = Accounts.List(config, existing.Id).Select(a => new ProviderAccount { Id = a.Id, Name = a.Name }).ToList();
                        ProviderAccounts entry = Accounts.Ensure(config, parsed.Id);
                        entry.Items = migrated;
                        entry.Active = migrated.Any(a => a.Id == activeOld) ? activeOld : migrated[0].Id;
                        bool allCopied = true;
                        foreach (ProviderAccount account in migrated) {
                            string oldKey = Store.SavedKey(existing.Id, account.Id);
                            if (oldKey.Length > 0 && !Store.SetProviderKey(parsed.Id, account.Id, oldKey)) allCopied = false;
                        }
                        if (typed.Length > 0 && !Store.SetProviderKey(parsed.Id, entry.Active, typed)) keyWriteFailed = true;
                        if (allCopied) ClearProviderKeys(existing.Id);
                        states.Remove(existing.Id); config.Enabled = config.Enabled.Where(id => id != existing.Id).ToArray();
                    } else if (existing != null) {
                        // 未改名：编辑器输入（与「移除已保存的密钥」勾选）作用于活动账户。
                        KeyField field;
                        if (editorKeyFields.TryGetValue(existing.Id, out field)) {
                            if (field.Remove != null && field.Remove.IsChecked == true) Store.SetProviderKey(existing.Id, field.AccountId, "");
                            else if (typed.Length > 0 && !Store.SetProviderKey(existing.Id, field.AccountId, typed)) keyWriteFailed = true;
                        }
                    } else if (typed.Length > 0) {
                        // 新建平台：落到默认账户。
                        if (!Store.SetProviderKey(parsed.Id, typed)) keyWriteFailed = true;
                    }
                    RegisterCustomProviders(list);
                    states[parsed.Id] = new ProviderState { Id = parsed.Id };
                    if (!config.Enabled.Contains(parsed.Id)) config.Enabled = config.Enabled.Concat(new[] { parsed.Id }).ToArray();
                    Store.Write("settings.json", config);
                    if (keyWriteFailed) { result.Foreground = WarnBrush; result.Text = I18n.T("密钥保存失败：钥匙串不可用"); return; }   // 平台定义已保存；密钥留在输入框可重试
                    CloseCustomEditor();
                    // Phase 2 定向刷新：只有这个平台被编辑（改名时旧 id 一并停用），其余平台
                    // 的通道与读数原样保留——不再全量 Refresh。
                    var forget = new HashSet<string>();
                    if (existing != null && existing.Id != parsed.Id) forget.Add(existing.Id);
                    var ignored = RefreshChanged(new HashSet<string> { parsed.Id }, forget);
                } catch (Exception e) { result.Foreground = WarnBrush; result.Text = I18n.T("保存失败：" + e.Message); }
            };
            bool confirm = false;
            delete.Click += delegate {
                if (!confirm) { confirm = true; delete.Content = I18n.T("再次点击确认删除"); return; }
                var list = ProviderCatalog.Custom.Values.Where(p => p.Id != existing.Id).ToList();
                try { CustomProviders.Save(list); ClearProviderKeys(existing.Id); } catch { }   // Phase 5：清掉全部账户的密钥与结构
                RegisterCustomProviders(list); states.Remove(existing.Id);
                limits.Drop(existing.Id);   // Phase 2：删掉的平台不再保留刷新通道
                config.Enabled = config.Enabled.Where(id => id != existing.Id).ToArray(); SaveConfig();
                CloseCustomEditor();
            };
            customEditor = root;
            settingsHost.Children.Clear(); settingsHost.Children.Add(root); settingsTitle.Text = I18n.T("自定义平台"); settingsBack.IsVisible = true;
            I18nUi.Localize(root);
        }
    }
}
