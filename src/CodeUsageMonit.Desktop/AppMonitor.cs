// MonitorPanel 主体：监控页 UI、Pacing 节流扫描与连接流程的字段。
// WPF → Avalonia 映射：NotifyIcon → TrayIcon + NativeMenu（点击弹菜单，首项“显示/隐藏面板”）；
// Mutex/EventWaitHandle → Unix domain socket（Program + SingleInstance）；窗体事件同名迁移；
// Ctrl+滚轮经隧道路由；FileSystemWatcher 加 LastWriteTime 轮询兜底（macOS 事件粒度粗）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaColor = Avalonia.Media.Color;
using Path = System.IO.Path;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Platform;
using CodeUsageMonit.Desktop;
using Avalonia.Automation;
using Avalonia.Controls.Documents;

namespace CodeUsageMonit {
    public sealed partial class MonitorPanel : IDisposable {
        private readonly Application app;
        internal readonly bool demo;
        private readonly MainWindow window;
        private readonly WindowFrame frame;
        private readonly StackPanel body;
        private readonly WrapPanel tabs;
        private readonly Grid tabHost;
        private readonly LayoutTransformControl scaleRoot, compactScaleHost;
        private readonly Border compactRoot;
        private readonly HashSet<string> refreshingIds = new HashSet<string>();
        private readonly ScrollViewer bodyScroll;
        private readonly TextBlock status;
        private readonly Avalonia.Controls.Shapes.Path refreshGlyph;
        private readonly Ellipse statusDot;
        private readonly Button refreshButton, pinButton, settingsButton, sizeButton;
        private TrayIcon tray;
        private readonly DispatcherTimer refreshTimer, clockTimer;
        private AppConfig config;
        private Dictionary<string, ProviderState> states = new Dictionary<string, ProviderState>();
        // ROADMAP Phase 2：每平台的额度刷新通道（退避/凭据失效/自适应周期），见 LimitsRuntime.cs。
        private readonly LimitsRuntime limits = new LimitsRuntime();
        private UsageHistory history;
        private LogIndex codexLogs, claudeLogs;
        // Hourly usage index per agent (tokens by kind + API-equivalent cost), from local logs.
        private Dictionary<string, LogIndex> usageIndexes = new Dictionary<string, LogIndex>();
        // D 期 Sessions 会话视图：每 agent 一份 SessionLedger（data/sessions-<agent>.json 的
        // 只读投影）。启动装载一次，每轮扫描后随 usageIndexes 一起刷新。
        private Dictionary<string, SessionLedger> sessionLedgers = new Dictionary<string, SessionLedger>();
        private EndpointLog endpointLog;
        private ThirdPartySummary thirdParty = new ThirdPartySummary();
        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private DispatcherTimer endpointTimer, activityPollTimer, spinTimer;
        private string selected = "overview", statusNote = "";
        private bool refreshing, scanning, quitting;
        private double spinAngle;
        private DateTime lastRefresh = DateTime.MinValue;
        // 鼠标按住拖动滚动（BodyScroll）：按下时的指针位置（相对视口）与滚动偏移；
        // armed = 已记录起点（未超阈值），dragging = 位移超阈值、已开始平移。
        private Point dragScrollStart;
        private Vector dragScrollOffset;
        private bool dragScrollArmed, dragScrolling;

        public MonitorPanel(Application application, bool demoMode) {
            app = application; demo = demoMode; Directory.CreateDirectory(Store.Data);
            SourceHealth.ErrorSink = LogError;   // Phase 3.2：Core 侧错误登记落 errors.log 的通道
            config = Store.Read<AppConfig>("settings.json"); I18n.Use(config.Language); updateState = Updates.State();
            Accounts.Normalize(config);   // Phase 5：容错修复账户结构（对旧配置是空操作）
            WindowFrame.Normalize(config);
            config.RefreshMinutes = Math.Max(1, Math.Min(60, config.RefreshMinutes));
            if (config.Enabled == null || config.Enabled.Length == 0) config.Enabled = (string[])ProviderCatalog.DefaultEnabled.Clone();
            if (!demo) RegisterCustomProviders(CustomProviders.Load());
            window = new MainWindow();
            compactRoot = Named<Border>(window, "CompactRoot");
            compactScaleHost = new LayoutTransformControl();
            compactRoot.Child = compactScaleHost;
            frame = new WindowFrame(window, config, SaveConfig, Named<LayoutTransformControl>(window, "ScaleRootHost"), compactScaleHost, Named<Panel>(window, "ResizeLayer"));
            window.Opened += delegate { ApplyAppearance(); };
            // 失焦透明度保持（任务4）的兜底挂点：Avalonia 可能在尺寸/装饰变化时重建
            // NSVisualEffectView（state 回到 FollowsWindowActiveState），重新激活时补固定一次。
            window.Activated += delegate { if (OperatingSystem.IsMacOS()) MacApp.PinActiveMaterial(window); };
            body = Named<StackPanel>(window, "Body"); tabs = Named<WrapPanel>(window, "Tabs"); tabHost = Named<Grid>(window, "TabHost");
            bodyScroll = Named<ScrollViewer>(window, "BodyScroll");
            status = Named<TextBlock>(window, "StatusText"); statusDot = Named<Ellipse>(window, "StatusDot");
            refreshGlyph = Named<Avalonia.Controls.Shapes.Path>(window, "RefreshGlyph");
            scaleRoot = Named<LayoutTransformControl>(window, "ScaleRootHost");
            refreshButton = Named<Button>(window, "RefreshButton"); pinButton = Named<Button>(window, "PinButton");
            settingsButton = Named<Button>(window, "SettingsButton"); sizeButton = Named<Button>(window, "SizeButton");
            foreach (string id in ProviderCatalog.All) states[id] = new ProviderState { Id = id };
            try {
                foreach (ProviderState s in Store.Read<List<ProviderState>>("quota-cache.json").Select(Parsers.Normalize)) {
                    if (!states.ContainsKey(s.Id)) continue;
                    // Only entries that actually hold data are shown as the last good reading.
                    if (s.Quotas.Count == 0 && s.Balances.Count == 0) continue;
                    s.Stale = true; s.Status = "cached"; s.Message = "上次成功读取的数据"; states[s.Id] = s;
                }
            } catch { }
            history = Store.Read<UsageHistory>("history.json");
            if (!demo) { localIndexes = UsageScanner.Load(UsageScanner.Agents.Select(a => a.Id)); usageIndexes = WithArchive(WithDevices(localIndexes)); usageIndexes.TryGetValue("codex", out codexLogs); usageIndexes.TryGetValue("claude", out claudeLogs); sessionLedgers = SessionLedger.LoadAll(); }
            endpointLog = demo ? new EndpointLog() : Store.Read<EndpointLog>("endpoints.json");
            if (demo) SeedDemo(); else BuildThirdParty();
            LoadIcons();
            Named<Button>(window, "HideButton").Click += delegate { window.Hide(); };
            settingsButton.Click += delegate { OpenSettings(); };
            sizeButton.Click += delegate { SizeMenu().Open(sizeButton); };
            refreshButton.Click += delegate { var ignored = Refresh(); };
            // 标题区左侧的自绘 macOS 红绿灯：关闭 = 收起到托盘（与 HideButton 同义，程序驻留）；
            // 黄色 = 最小化；绿色 = 循环显示尺寸（small → medium → large → full → edge）。
            Named<Button>(window, "TrafficClose").Click += delegate { window.Hide(); };
            Named<Button>(window, "TrafficMinimize").Click += delegate { window.WindowState = WindowState.Minimized; };
            Named<Button>(window, "TrafficZoom").Click += delegate { CycleDisplaySize(); };
            // 钉子 = 置顶（用户心智里的“保持最前面”）：钉上总在最前，取消后是留在原处的
            // 普通窗口——失焦不消失，被盖住时点 Dock/托盘/Cmd-Tab 或点窗口本身唤回。
            // 「失焦自动收起」是设置里的独立选项（默认关），与钉子解耦。
            pinButton.Click += delegate { config.AlwaysOnTop = !config.AlwaysOnTop; SaveConfig(); UpdatePin(); };
            window.Closing += delegate(object sender, WindowClosingEventArgs e) {
                if (!quitting) { e.Cancel = true; window.Hide(); }
            };
            window.Deactivated += delegate {
                // Never auto-hide while settings are open: the user may be copying an API key.
                // 置顶（钉住）的窗口同样不自动收起——钉住就是为了让它一直在。
                if (config.HideOnDeactivate && !config.AlwaysOnTop && settingsWindow == null && !IsCompact) window.Hide();
            };
            // Ctrl + 滚轮缩放；紧凑尺寸滚轮翻页（隧道式挂接，保证子元素未处理时也能收到）。
            // 窄条（edge）不分页：滚轮留给条内的滚动。
            window.AddHandler(InputElement.PointerWheelChangedEvent, (EventHandler<PointerWheelEventArgs>)delegate(object sender, PointerWheelEventArgs e) {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) {
                    frame.SetScale(config.UiScale + (e.Delta.Y > 0 ? .05 : -.05), true); UpdateScaleLabel();
                    e.Handled = true;
                } else if (IsCompact && activeSize != "edge" && compactConnect == null) { CycleCompact(e.Delta.Y < 0 ? 1 : -1); e.Handled = true; }
            }, RoutingStrategies.Tunnel);
            window.KeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key == Key.Escape) { if (compactConnect != null) CloseCompactConnect(); else if (settingsWindow == null) window.Hide(); e.Handled = true; }
                if (e.Key == Key.F5) { var ignored = Refresh(); e.Handled = true; }
                // ← / → page through the compact sizes, unless a text box has the keys.
                // 窄条（edge）不分页，方向键不抢。
                if (IsCompact && activeSize != "edge" && compactConnect == null && (e.Key == Key.Left || e.Key == Key.Right) && !(FocusedElement() is TextBox)) { CycleCompact(e.Key == Key.Right ? 1 : -1); e.Handled = true; }
            };
            CreateTray();
            // Every 30 s: the platforms that are due (Pacing.cs), then backups / sync / update check.
            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            refreshTimer.Tick += async delegate { await RefreshDue(); await Housekeeping(); };
            clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            clockTimer.Tick += delegate { if (window.IsVisible && !refreshing) Render(); };
            dockWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            dockWatchTimer.Tick += WatchDockActivation;
            if (demo) Named<Border>(window, "DemoBadge").IsVisible = true;
            // Compact sizes drag from anywhere, the panel from its header. Right-click anywhere
            // opens the size menu (the window has no native caption, so no system menu).
            frame.EnableDrag(Named<Grid>(window, "TitleArea"));
            frame.EnableDrag(compactRoot);
            AttachDragScroll();
            AttachRightClick(Named<Grid>(window, "TitleArea"));
            AttachRightClick(compactRoot);
            AttachRightClick(scaleRoot);
            compactRoot.SizeChanged += delegate { QueueCompactRender(); };
            UpdatePin();
            ApplyDisplaySize(config.DisplaySize);
        }
        // 当前聚焦控件（EditingKey 与紧凑翻页判断用）。
        private Control FocusedElement() {
            try { return window.FocusManager?.GetFocusedElement() as Control; } catch { return null; }
        }
        // ── 鼠标按住拖动滚动（BodyScroll）───────────────────────────────
        // 在正文空白处按住拖动即可滚动（触控板滚轮不受影响；不加惯性，保持简单）。
        // 按下源在交互控件（按钮/输入框/滑杆/滚动条等，UiInput.IsInteractive 口径）子树内
        // 不接管；位移超过 ~4px 才开始平移并捕获指针 —— 阈值内完全不拦截，卡片头部、
        // 分段、按钮的点击保持原样。Offset setter 自带范围钳制，直接赋值即可。
        private void AttachDragScroll() {
            bodyScroll.PointerPressed += (s, e) => {
                if (e.Handled || !e.GetCurrentPoint(bodyScroll).Properties.IsLeftButtonPressed) return;
                var source = e.Source as Visual;
                if (source != null && UiInput.IsInteractive(source)) return;
                dragScrollStart = e.GetPosition(bodyScroll);
                dragScrollOffset = bodyScroll.Offset;
                dragScrollArmed = true; dragScrolling = false;
            };
            bodyScroll.PointerMoved += (s, e) => {
                if (!dragScrollArmed) return;
                if (!e.GetCurrentPoint(bodyScroll).Properties.IsLeftButtonPressed) { dragScrollArmed = dragScrolling = false; return; }
                Vector delta = e.GetPosition(bodyScroll) - dragScrollStart;
                if (!dragScrolling) {
                    if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4) return;
                    dragScrolling = true;
                    e.Pointer.Capture(bodyScroll);
                }
                bodyScroll.Offset = dragScrollOffset - delta;
                e.Handled = true;
            };
            bodyScroll.PointerReleased += (s, e) => {
                if (!dragScrollArmed) return;
                dragScrollArmed = dragScrolling = false;
                if (e.Pointer.Captured == bodyScroll) { e.Pointer.Capture(null); e.Handled = true; }
            };
            bodyScroll.PointerCaptureLost += (s, e) => { dragScrollArmed = dragScrolling = false; };
        }
        // 红绿灯绿键：沿 small → medium → large → full → edge 循环（与尺寸按钮/右键菜单共用 SetDisplaySize）。
        private void CycleDisplaySize() {
            int index = Array.IndexOf(DisplaySizes, activeSize);
            SetDisplaySize(DisplaySizes[(index + 1) % DisplaySizes.Length]);   // index = -1（未知）时落到 small
        }
        // 按 x:Name 找控件：窗口模板在显示前未展开，优先查 NameScope（XAML 加载即注册）。
        internal static T Named<T>(Control root, string name) where T : Control {
            var scope = Avalonia.Controls.NameScope.GetNameScope(root);
            if (scope != null) { var found = scope.Find(name) as T; if (found != null) return found; }
            return root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
        }
        // 右键弹尺寸菜单。
        private void AttachRightClick(Control control) {
            control.PointerReleased += delegate(object sender, PointerReleasedEventArgs e) {
                if (e.InitialPressMouseButton == MouseButton.Right) { SizeMenu().OpenAtPointer(control); e.Handled = true; }
            };
        }

        // ── Tray（菜单栏图标：点击弹菜单，首项“显示/隐藏面板”）──
        private void CreateTray() {
            var menu = new NativeMenu();
            var toggle = new NativeMenuItem { Header = window.IsVisible ? I18n.T("隐藏面板") : I18n.T("显示面板") };
            trayToggleItem = toggle; // 首项（显隐 toggle）引用单独保存，SetTrayFirstItem 只更新它（review #15）。
            toggle.Click += delegate { Dispatcher.UIThread.Post(ToggleFromTray); };
            menu.Add(toggle);
            // 窗口显隐时同步首项文案（“显示/隐藏面板”）。
            window.PropertyChanged += delegate(object sender, AvaloniaPropertyChangedEventArgs e) {
                if (e.Property == Visual.IsVisibleProperty) SetTrayFirstItem(!window.IsVisible);
            };
            var refreshItem = new NativeMenuItem { Header = I18n.T("刷新全部额度") };
            refreshItem.Click += delegate { Dispatcher.UIThread.Post(() => { var ignored = Refresh(); }); };
            menu.Add(refreshItem);
            var settingsItem = new NativeMenuItem { Header = I18n.T("设置…") };
            settingsItem.Click += delegate { Dispatcher.UIThread.Post(OpenSettings); };
            menu.Add(settingsItem);
            var sizes = new NativeMenuItem { Header = I18n.T("显示尺寸") };
            var sizesMenu = new NativeMenu();
            foreach (string size in DisplaySizes) {
                string captured = size;
                var item = new NativeMenuItem { Header = I18n.T(SizeName(size) + (size == "full" ? "面板" : "尺寸")), IsChecked = size == config.DisplaySize, ToggleType = MenuItemToggleType.CheckBox };
                item.Click += delegate { Dispatcher.UIThread.Post(() => SetDisplaySize(captured)); };
                sizesMenu.Add(item);
            }
            sizes.Menu = sizesMenu;
            menu.Add(sizes);
            var reset = new NativeMenuItem { Header = I18n.T("重置窗口位置") };
            reset.Click += delegate { Dispatcher.UIThread.Post(() => { frame.ResetPosition(); Reveal(); }); };
            menu.Add(reset);
            menu.Add(new NativeMenuItemSeparator());
            // 状态行（沿用 UpdateTray 的文本逻辑；菜单内只读展示）。
            var statusItem = new NativeMenuItem { Header = I18n.T("codeusagemonit · 正在读取"), IsEnabled = false };
            menu.Add(statusItem);
            trayStatusItem = statusItem;
            menu.Add(new NativeMenuItemSeparator());
            var quit = new NativeMenuItem { Header = I18n.T("退出 codeusagemonit") };
            quit.Click += delegate { Dispatcher.UIThread.Post(Quit); };
            menu.Add(quit);
            tray = new TrayIcon { Icon = MakeTrayIcon(), ToolTipText = I18n.T("codeusagemonit · 正在读取"), Menu = menu, IsVisible = true };
            TrayIcon.SetIcons(app, new TrayIcons { tray });
        }
        private NativeMenuItem trayStatusItem;
        // 首项（显隐 toggle）：窗口显隐时同步文案；额度状态行（trayStatusItem）只由
        // UpdateTray 维护，绝不能被显隐逻辑改写（review #15：旧实现误写 trayStatusItem，
        // toggle 标签停留在创建时的值，状态行被“显示/隐藏面板”覆盖）。
        private NativeMenuItem trayToggleItem;
        // 参数 = 面板当前是否隐藏（true → 首项显示“显示面板”）。调用方都以这个口径传值：
        // Reveal → false、ToggleFromTray 收起后 → true、PropertyChanged → !window.IsVisible。
        private void SetTrayFirstItem(bool panelHidden) {
            if (trayToggleItem != null) trayToggleItem.Header = I18n.T(panelHidden ? "显示面板" : "隐藏面板");
        }
        // 程序图标：三根小柱（Logo）程序化渲染，替代 app.ico。
        internal static WindowIcon MakeTrayIcon() {
            var bitmap = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
            using (var ctx = bitmap.CreateDrawingContext()) {
                ctx.FillRectangle(new SolidColorBrush(AvaColor.Parse("#15171B")), new Rect(0, 0, 32, 32), 6);
                ctx.FillRectangle(new SolidColorBrush(AvaColor.Parse("#6DE0D6")), new Rect(7, 16, 5, 10), 2);
                ctx.FillRectangle(new SolidColorBrush(AvaColor.Parse("#96D9EE")), new Rect(13.5, 7, 5, 19), 2);
                ctx.FillRectangle(new SolidColorBrush(AvaColor.Parse("#D6C1F9")), new Rect(20, 11, 5, 15), 2);
            }
            return new WindowIcon(bitmap);
        }

        public void Start(bool background) {
            if (!background) Reveal();
            clockTimer.Start();
            dockWatchTimer.Start();
            if (demo) return;
            ObserveEndpoints(); WatchEndpoints(); WatchActivity();
            refreshTimer.Start();
            var a = Refresh(); var b = ScanHistory(); var c = HousekeepingSoon();
        }
        // Dock 点击 / Cmd-Tab 唤回：Avalonia 的 applicationShouldHandleReopen 只把 app 激活，
        // 不会重新显示被 Hide 的面板（实测：app 已到前台、窗口仍然不可见）。macOS 惯例是
        // “激活一个没有可见窗口的 app 就亮出主窗口”，这里轮询 isActive 的**上升沿**补这件事：
        // 面板内隐藏按钮/托盘「隐藏面板」点击时 app 本就处于活跃态，没有 false→true 跳变，
        // 不会被误拉回；托盘菜单打开期间 app 也不会变成活跃（实测），不干扰菜单操作。
        private bool wasActive;
        private readonly DispatcherTimer dockWatchTimer;
        private void WatchDockActivation(object sender, EventArgs e) {
            bool active = MacApp.IsActive();
            if (active && !wasActive && !window.IsVisible) Reveal();
            wasActive = active;
        }
        // Automatic backup, WebDAV sync and the update check, when due (DataSync.cs, Updates.cs).
        private bool housekeeping;
        private UpdateState updateState = new UpdateState();
        private async Task HousekeepingSoon() { await Task.Delay(TimeSpan.FromSeconds(20)); await Housekeeping(); }
        private async Task Housekeeping() {
            if (demo || housekeeping) return;
            housekeeping = true;
            try {
                AppConfig c = config; DateTime now = DateTime.UtcNow;
                if (Backups.Due(c, now)) await Task.Run(() => { try { Backups.Create(c, true, now); Backups.Prune(c); } catch { } });
                if (WebDavSync.Due(c, now)) await SyncNow();
                if (Updates.Due(c, now)) { updateState = await Updates.Check(c, now); UpdateStatus(); }
            } finally { housekeeping = false; }
        }
        private Task<bool> SyncNow() { return SyncNow(config.WebDavUrl, config.WebDavUser, Store.SavedKey(WebDavSync.KeyId)); }
        // After a restore: start a fresh copy that waits for this one, and leave without saving
        // (the restored settings must not be overwritten by the ones in memory).
        private void RestartFresh() {
            try {
                string exe = Process.GetCurrentProcess().MainModule?.FileName;
                string arguments = "--restart " + Process.GetCurrentProcess().Id + (Program.SideBySide ? " --side-by-side" : "");
                if (exe != null && exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) Process.Start(new ProcessStartInfo("dotnet", "\"" + exe + "\" " + arguments) { UseShellExecute = false });
                else Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false });
            } catch { }
            quitting = true; if (tray != null) tray.IsVisible = false;
            if (settingsWindow != null) { Window w = settingsWindow; settingsWindow = null; w.Close(); }
            window.Close(); Shutdown();
        }
        private void Shutdown() {
            var lifetime = app.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            if (lifetime != null) lifetime.Shutdown();
        }
        // Endpoint switches (e.g. CC Switch rewriting settings.json / config.toml) are
        // recorded as they happen, so later usage can be attributed to the right relay.
        private void WatchEndpoints() {
            endpointTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            endpointTimer.Tick += delegate { endpointTimer.Stop(); ObserveEndpoints(); };
            var folders = ClaudeLogs.ConfigDirs().Take(1).Concat(new[] { ProviderService.CodexHome() });
            foreach (string folder in folders) {
                try {
                    if (!Directory.Exists(folder)) continue;
                    var watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                    FileSystemEventHandler changed = delegate(object sender, FileSystemEventArgs e) {
                        string name = Path.GetFileName(e.FullPath).ToLowerInvariant();
                        if (name == "settings.json" || name == "config.toml" || name == "auth.json") Dispatcher.UIThread.Post(() => { endpointTimer.Stop(); endpointTimer.Start(); });
                    };
                    watcher.Changed += changed; watcher.Created += changed;
                    watcher.Renamed += delegate(object sender, RenamedEventArgs e) { changed(sender, e); };
                    watcher.EnableRaisingEvents = true; watchers.Add(watcher);
                } catch { }
            }
        }
        private void ObserveEndpoints() {
            if (demo || !config.ShowThirdParty) return;
            try {
                if (Endpoints.Observe(endpointLog, DateTime.UtcNow, Endpoints.CcSwitchNames())) { Store.Write("endpoints.json", endpointLog); BuildThirdParty(); Render(); }
            } catch { }
        }
        private void BuildThirdParty() {
            if (demo) return;
            try { thirdParty = ThirdPartyReport.Build(codexLogs, claudeLogs, endpointLog, history, DateTime.UtcNow, TimeZoneInfo.Local); } catch { thirdParty = new ThirdPartySummary(); }
        }
        // macOS 上 window.Activate() 对非前台 app 不能越过当前活跃应用——只 Show 不够，
        // 窗口会留在浏览器等后面（IsVisible=true 却永远看不见，托盘反复点了也没反应）。
        // 所以 Reveal 无条件 Show + Activate，再经 NSApp 把整个 app 提到前台（MacApp）。
        public void Reveal() {
            if (!window.IsVisible) frame.Restore();
            window.Show();
            window.Activate();
            MacApp.ActivateForeground();
            Render();
            SetTrayFirstItem(false);
        }
        // 托盘点击 toggle：窗口可见且确实是当前活跃窗口 → 收起；其余一律显示（含“可见但
        // 被压在别的应用后面”）。旧实现的两个 600ms 守卫把“刚失焦自动隐藏”后的显式显示
        // 请求也吞掉，是托盘点击反复无效的另一半原因；失焦自动隐藏已由 Deactivated 直接
        // Hide（IsVisible=false），这里无需再区分来源。
        private void ToggleFromTray() {
            if (window.IsVisible && window.IsActive) { window.Hide(); SetTrayFirstItem(true); return; }
            Reveal();
        }
        private void UpdatePin() {
            window.Topmost = config.AlwaysOnTop;
            bool pinned = config.AlwaysOnTop;
            pinButton.Foreground = pinned ? AccentBrush : InkDim;
            ToolTip.SetTip(pinButton, I18n.T(pinned ? "已置顶 · 点击取消置顶（窗口可被遮住，但不会自动消失）" : "未置顶 · 点击后窗口总在最前"));
        }
        private void SaveConfig() { try { Store.Write("settings.json", config); } catch { statusNote = "设置保存失败，请检查数据目录是否可写"; UpdateStatus(); } }
        // errors.log（App.axaml.cs 未处理异常同款格式）：保存设置这类被 catch 吞掉的错误
        // 也落一份，远程诊断时才有第一现场。写入经 Core 的 ErrorLog（review P2-2）：
        // 超过 512KB 自动截断保留尾部，长跑进程日志有界。写日志自身失败再吞。
        internal static void LogError(string context, Exception e) { ErrorLog.Append(context, e); }
        // 刷新图标旋转（DispatcherTimer 步进，重建时保持相位）。
        private void SetSpinning(bool on) {
            if (refreshGlyph == null) return;
            if (on) {
                if (spinTimer == null) {
                    spinTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
                    spinTimer.Tick += delegate { spinAngle = DateTime.Now.TimeOfDay.TotalMilliseconds % 900 / 900 * 360; var rotate = refreshGlyph.RenderTransform as RotateTransform; if (rotate != null) rotate.Angle = spinAngle; };
                }
                if (refreshGlyph.RenderTransform is RotateTransform) { spinTimer.Start(); return; }
            }
            if (spinTimer != null) spinTimer.Stop();
            var transform = refreshGlyph.RenderTransform as RotateTransform;
            if (transform != null) transform.Angle = 0;
        }
        private async Task Refresh() {
            if (demo) { Render(); return; }
            if (refreshing) return; refreshing = true; statusNote = ""; SetSpinning(true); UpdateStatus();
            ObserveEndpoints();
            try {
                using (var service = new ProviderService(config)) {
                    var jobs = config.Enabled.Where(ProviderCatalog.IsKnown).Select(async id => {
                        int serial = BeginFetch(id); ProviderState result = await Task.Run(() => service.Fetch(id));
                        if (result != null) RecordOutcome(id, result);
                        if (Latest(id, serial)) Accept(id, result); Render();
                    }).ToArray();
                    await Task.WhenAll(jobs);
                }
                lastRefresh = DateTime.Now; Store.Write("quota-cache.json", states.Values.ToList()); UpdateTray();
            } catch (Exception e) { statusNote = e is ArgumentException ? e.Message : "刷新未完成，稍后重试"; }
            finally { refreshing = false; SetSpinning(false); Render(); }
            // At most once a day; newer prices re-price the history in the next scan.
            bool repriced = false; try { repriced = await Pricing.SyncAsync(config); } catch { }
            DateTime stamp;
            if (!scanning && (repriced || !DateTime.TryParse(history.Updated, out stamp) || DateTime.UtcNow - stamp.ToUniversalTime() > TimeSpan.FromMinutes(15))) { var ignored = ScanHistory(); }
        }
        // 一次 Fetch 的结果记进刷新通道（Phase 2）：成功复位退避与失败计数；expired/setup
        // 进入 auth 态不再自动重试；其余失败按 1→2→4→8 分钟退避。返回是否成功。
        private bool RecordOutcome(string id, ProviderState result) {
            DateTime now = DateTime.UtcNow;
            if (result.Status == "ready") { limits.RecordSuccess(id, result, now); return true; }
            limits.RecordFailure(id, result.Status, config, InUse(id), now);
            ScanTrace("fetch " + id + " " + result.Status + " -> " + limits.ChannelOf(id).Kind + (limits.ChannelOf(id).Kind == "backoff" ? " " + limits.ChannelOf(id).LastBackoffMinutes + "min" : ""));
            return false;
        }
        // A failed read keeps the last good values (marked stale) instead of blanking them.
        private void Accept(string id, ProviderState incoming) {
            ProviderState old;
            if (!states.TryGetValue(id, out old)) old = new ProviderState { Id = id };
            Parsers.RetainLastGood(incoming, old);
            states[id] = incoming;
        }
        // A full refresh and a single-provider refresh can overlap; only the newest fetch
        // of a provider is applied, so an older result never overwrites a newer one.
        private readonly Dictionary<string, int> fetchSerial = new Dictionary<string, int>();
        private int BeginFetch(string id) { int n; fetchSerial.TryGetValue(id, out n); fetchSerial[id] = ++n; return n; }
        private bool Latest(string id, int serial) { int n; return fetchSerial.TryGetValue(id, out n) && n == serial; }
        // Refreshes one provider (card / compact refresh buttons, after connecting).
        private async Task RefreshOne(string id) {
            if (demo) { refreshingIds.Add(id); Render(); await Task.Delay(600); refreshingIds.Remove(id); Render(); return; }
            // May overlap a full refresh: a key saved from a card must not wait for the next cycle.
            if (refreshingIds.Contains(id) || !ProviderCatalog.IsKnown(id)) return;
            refreshingIds.Add(id); Render();
            try {
                int serial = BeginFetch(id);
                using (var service = new ProviderService(config)) {
                    ProviderState result = await Task.Run(() => service.Fetch(id));
                    if (result != null) RecordOutcome(id, result);
                    if (Latest(id, serial)) Accept(id, result);
                }
                Store.Write("quota-cache.json", states.Values.ToList()); UpdateTray();
            } catch (Exception e) { statusNote = e is ArgumentException ? e.Message : ProviderCatalog.Name(id) + " 刷新未完成"; }
            finally { refreshingIds.Remove(id); Render(); }
        }
        // 定向刷新（Phase 2.1）：凭据/启用变化只影响到的平台——重置通道、立刻逐个 Fetch；
        // 其余平台的通道与读数原样保留。forget 是已停用/已删除的平台：只丢通道。
        // 手动路径与这里都不查询 AutoDue，退避永远不会挡住用户发起的刷新。
        private async Task RefreshChanged(HashSet<string> reset, HashSet<string> forget) {
            if (demo) return;
            foreach (string id in forget ?? new HashSet<string>()) { limits.Drop(id); fetchSerial.Remove(id); }
            var targets = (reset ?? new HashSet<string>()).Where(ProviderCatalog.IsKnown).ToList();
            foreach (string id in targets) limits.ResetFor(id);
            if (targets.Count == 0) { Render(); return; }
            if (refreshing) return;   // 全量刷新进行中：它已覆盖读数；通道已重置，下个 tick 兜底补查
            foreach (string id in targets) states[id] = new ProviderState { Id = id };
            refreshing = true; SetSpinning(true); UpdateStatus();
            try {
                using (var service = new ProviderService(config)) {
                    var jobs = targets.Select(async id => {
                        int serial = BeginFetch(id);
                        // Phase 3.2：fetch 抛出的异常也不再静默——记入该平台的最近错误（健康
                        // 摘要与诊断报告展示），通道照常进入退避。
                        try { ProviderState result = await Task.Run(() => service.Fetch(id)); if (result != null) RecordOutcome(id, result); if (Latest(id, serial)) Accept(id, result); } catch (Exception e) { limits.RecordFailure(id, "error", config, InUse(id), DateTime.UtcNow); SourceHealth.RecordError(id, e); }
                    }).ToArray();
                    await Task.WhenAll(jobs);
                }
                lastRefresh = DateTime.Now;
                try { Store.Write("quota-cache.json", states.Values.ToList()); } catch { }
                UpdateTray();
            } finally { refreshing = false; SetSpinning(false); Render(); }
        }
        // 30 秒 tick / 手动刷新 / 启动路径：默认口径全量。
        private async Task ScanHistory() { await ScanHistory(ScanableIds()); }
        private List<string> ScanableIds() {
            return UsageScanner.Agents.Select(a => a.Id).Where(ScanableId).ToList();
        }
        private bool ScanableId(string id) {
            return config.Enabled.Contains(id) || (config.ShowThirdParty && (id == "codex" || id == "claude"));
        }
        // 全量或定向扫描（调度器只传命中平台，Phase 0.1）。返回 false = 失败：某平台扫描
        // 抛错（UsageScanner.ScanAll 把单平台异常吞成 null 索引）或整体异常——调度器据此把
        // 下一轮升级为全量；其余失败语义不变：保留旧索引、状态行提示。
        private async Task<bool> ScanHistory(List<string> ids) {
            if (demo || scanning) return false;
            scanning = true; Render();
            bool ok = true;
            try {
                // Every enabled agent's local logs (Claude and Codex also for the third-party
                // page) are read incrementally into hourly indexes; the days derive from them.
                Dictionary<string, LogIndex> scanned = await Task.Run(() => UsageScanner.ScanAll(ids, DateTime.UtcNow, true));
                foreach (string id in ids) {
                    LogIndex index;
                    if (scanned.TryGetValue(id, out index) && index != null) localIndexes[id] = index;
                    else {
                        // Phase 3.2：该平台本轮没有返回索引。ScanAll 的 catch 已记了真实异常；
                        // 这里兜底覆盖「扫描器没抛但也没产出」的路径，失败语义（ok=false、
                        // 调度器升级全量）不变。
                        ok = false;
                        if (SourceHealth.LastErrorOf(id) == null) SourceHealth.RecordError(id, new Exception(I18n.T("本轮扫描未返回该平台的用量索引")));
                    }
                }
                usageIndexes = WithArchive(WithDevices(localIndexes));
                usageIndexes.TryGetValue("codex", out codexLogs); usageIndexes.TryGetValue("claude", out claudeLogs);
                sessionLedgers = SessionLedger.LoadAll();   // D 期：随扫描刷新会话账本投影
                // Phase 1：日历史滚动窗口 30 → 370 天（UsageArchive.HorizonDays）。派生侧
                // 靠合并层的归档投影补齐 33 天前的小时桶；Merge 的"历史最大值"语义不变，
                // 只放宽裁剪窗口，旧 history.json 的 30 天数据原样保留并继续增长。
                UsageHistory next = HistoryService.FromIndexes(usageIndexes, TimeZoneInfo.Local, DateTime.Today, UsageArchive.HorizonDays);
                history = HistoryService.Merge(history, next, HistoryService.DayKey(DateTime.Today.AddDays(-(UsageArchive.HorizonDays - 1))), HistoryService.DayKey(DateTime.Today));
                lastScanDay = DateTime.Today;   // 跨天判定基准（RunDirectedScan）
                Store.Write("history.json", history);
                BuildThirdParty();
            } catch (Exception e) { history.Error = "本地统计暂时不可用"; ok = false; SourceHealth.RecordError(SourceHealth.HistoryKey, e); }
            finally { scanning = false; Render(); }
            return ok;
        }
        // 调度器触发的一轮（Phase 0.1）。与 30 秒 tick 的全量扫描共用 scanning 互斥：
        // 全量进行中触发的定向轮直接让路（全量范围只会更大）。结束时必须通知调度器：
        // 失败 → 下一轮全量（ScanScheduler 置位不撤销）；跨天 → 本轮直接走全量。
        private async Task RunDirectedScan(ScanRequest request) {
            if (scanScheduler == null) return;
            bool failed = true;
            try {
                if (!demo && !scanning) {
                    bool full = request.Full || DateTime.Today != lastScanDay;
                    List<string> ids = full ? ScanableIds() : request.Agents.Where(ScanableId).ToList();
                    // 空定向（事件来自未启用平台）不跑扫描：空扫也会刷新 history.Updated，
                    // 会把 15 分钟 stale 全量兜底饿死（超限无 watcher 的平台只能靠它）。
                    if (!full && ids.Count == 0) { failed = false; return; }
                    ScanTrace("scan-fire " + (full ? "full" : String.Join("+", ids)) + (request.Full ? " (sticky-full)" : ""));
                    failed = !await ScanHistory(ids);
                } else {
                    failed = false;   // demo / 全量进行中：本轮范围已被覆盖
                }
            } catch (Exception e) {
                ScanTrace("scan-fire error " + e.GetType().Name);
            } finally {
                scanScheduler.NotifyScanCompleted(failed);
            }
        }
        // Local indexes plus the usage imported from other devices (DataSync.cs).
        private Dictionary<string, LogIndex> WithDevices(Dictionary<string, LogIndex> local) {
            try { List<DeviceData> devices = Devices.Imported(); importedDevices = devices.Count; return Devices.Attach(local, devices); } catch { importedDevices = 0; return local; }
        }
        // Phase 1 合并层：热索引（33 天）+ 归档只读投影 → 渲染与派生共用的索引视图。
        // 归档只回答热窗口之外的桶，写入持久化索引之前、读取不落地，是唯一入口——
        // 图表、日历史派生、额度窗口估算都经由 usageIndexes，归档逻辑不散进 UI。
        private Dictionary<string, LogIndex> WithArchive(Dictionary<string, LogIndex> indexes) {
            return UsageArchive.MergeAll(indexes, DateTime.UtcNow);
        }
        private int importedDevices;
        // This device's own indexes (what the scanners persist); usageIndexes adds the imported devices.
        private Dictionary<string, LogIndex> localIndexes = new Dictionary<string, LogIndex>();
        // After an import, a sync or removing a device: rebuild the totals from the indexes.
        // Merging keeps days whose local logs were deleted; a removed device must disappear.
        private void ReloadDevices(bool removed) {
            if (demo) return;
            usageIndexes = WithArchive(WithDevices(localIndexes)); usageIndexes.TryGetValue("codex", out codexLogs); usageIndexes.TryGetValue("claude", out claudeLogs);
            sessionLedgers = SessionLedger.LoadAll();   // D 期：保持会话投影与其余数据同批刷新
            UsageHistory next = HistoryService.FromIndexes(usageIndexes, TimeZoneInfo.Local, DateTime.Today, UsageArchive.HorizonDays);
            history = removed ? next : HistoryService.Merge(history, next, HistoryService.DayKey(DateTime.Today.AddDays(-(UsageArchive.HorizonDays - 1))), HistoryService.DayKey(DateTime.Today));
            try { Store.Write("history.json", history); } catch { }
            BuildThirdParty(); Render();
        }
        private void UpdateTray() {
            string text = "codeusagemonit";
            foreach (string id in new[] { "codex", "claude", "cursor" }) {
                ProviderState s; if (!states.TryGetValue(id, out s)) continue;
                Quota q = UsageDetails.Primary(s);
                if (s.Status == "ready" && q != null && config.Enabled.Contains(id)) text += "\n" + ProviderCatalog.Name(id) + " " + q.Remaining.ToString("0") + "%";
            }
            if (tray != null) tray.ToolTipText = text;
            if (trayStatusItem != null) {
                string[] lines = text.Split('\n');
                trayStatusItem.Header = lines.Length > 1 ? String.Join(" · ", lines.Skip(1)) : I18n.T("尚无读数");
            }
        }

        // ── 实时采集链路（ROADMAP Phase 0.1/0.2）：watcher 事件 → ScanScheduler → 定向扫描 ──
        private ScanScheduler scanScheduler;
        private DispatcherTimer schedulerTimer, degradedPollTimer;
        // key = agentId + "|" + folder。只在 UI 线程访问（FSW 的 Error 回调经 Post 回 UI 线程）。
        private readonly Dictionary<string, FileSystemWatcher> activityWatchers = new Dictionary<string, FileSystemWatcher>();
        private readonly HashSet<string> degradedAgents = new HashSet<string>();
        private readonly Dictionary<string, DateTime> lastDegradedPoll = new Dictionary<string, DateTime>();
        private readonly HashSet<string> overLimitFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const int MaxWatchEntries = 20000;
        private DateTime lastScanDay = DateTime.Today;
        // 上次尝试/上次成功的时间由 limits（LimitsRuntime）按平台记录，RefreshDue 据此判断到期。
        private readonly Dictionary<string, DateTime> lastActivity = new Dictionary<string, DateTime>();
        private DateTime lastQuickScan = DateTime.MinValue;
        private bool pacing;
        private bool InUse(string id) { lock (lastActivity) { DateTime at; return lastActivity.TryGetValue(id, out at) && DateTime.UtcNow - at < Pacing.InUseWindow; } }
        private List<string> InUseIds() { return EnabledIds().Where(InUse).ToList(); }
        // One watcher per platform folder; a write marks the platform as in use AND feeds the
        // scan scheduler（Phase 0.1）：事件经 1.5s debounce / 5s deadline 合并成一次定向扫描，
        // 只重扫命中的平台；归因不到 → 全量（ScanAttribution 前缀匹配，兜底 captured id）。
        // macOS 兜底：FileSystemWatcher 事件粒度粗，30 秒一次按 LastWriteTime 轮询同样的文件夹。
        // Phase 0.2 降级梯子：Error（含 InternalBufferOverflow）→ 该平台粘性切换 2 秒轮询；
        // 监视根条目数超 2 万不建 watcher，只靠 30 秒 tick 兜底；旧 watcher Dispose 完成
        // 后才启用新的。
        // Phase 0.4 冷启动：构造函数在建窗口前已 Store.Read history.json / quota-cache.json，
        // 首帧渲染即用持久化数据（随后启动扫描刷新），无“旧数据回退闪烁”，无需额外机制。
        private void WatchActivity() {
            scanScheduler = new ScanScheduler(() => DateTime.UtcNow, request => { var ignored = RunDirectedScan(request); });
            schedulerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            schedulerTimer.Tick += delegate { scanScheduler.OnTimer(); };
            schedulerTimer.Start();
            foreach (string id in ProviderCatalog.Ids) EnsureWatchers(id);
            activityPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            activityPollTimer.Tick += delegate { PollActivity(); };
            activityPollTimer.Start();
        }

        // 为平台建/补建 watcher：启动时、以及 30 秒对账发现新目录时（EnsureWatchers）。
        private void EnsureWatchers(string id) {
            if (degradedAgents.Contains(id)) return;
            foreach (string folder in Pacing.ActivityFolders(id)) {
                try {
                    if (!Directory.Exists(folder)) continue;
                    if (activityWatchers.ContainsKey(id + "|" + folder)) continue;
                    if (overLimitFolders.Contains(folder)) continue;
                    int entries = CountEntries(folder, MaxWatchEntries + 1);
                    if (entries > MaxWatchEntries) {
                        overLimitFolders.Add(folder);
                        ScanTrace("watch-skip " + id + " " + folder + " entries>=" + (MaxWatchEntries + 1) + " (只走 30s tick 兜底)");
                        continue;
                    }
                    CreateWatcher(id, folder);
                } catch { }
            }
        }

        // 同槽位旧 watcher Dispose 完成后才 EnableRaisingEvents 新的（Phase 0.2 重建顺序）。
        private void CreateWatcher(string id, string folder) {
            string key = id + "|" + folder;
            FileSystemWatcher stale;
            if (activityWatchers.TryGetValue(key, out stale)) { activityWatchers.Remove(key); try { stale.Dispose(); } catch { } }
            string captured = id;
            var watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, InternalBufferSize = 16384 };
            FileSystemEventHandler changed = delegate(object sender, FileSystemEventArgs e) {
                if (!Pacing.CountsAsWork(captured, e.FullPath)) return;   // Phase 0.3：-shm/-wal/自写 data/ 在这里挡下
                lock (lastActivity) lastActivity[captured] = DateTime.UtcNow;
                ScanTrace("event " + captured + " " + Path.GetFileName(e.FullPath));
                if (scanScheduler != null) scanScheduler.Report(ScanAttribution.AgentFor(e.FullPath) ?? captured);
            };
            watcher.Changed += changed; watcher.Created += changed;
            watcher.Error += delegate(object sender, ErrorEventArgs e) { DegradeWatcher(captured, e.GetException()); };
            try {
                watcher.EnableRaisingEvents = true;
                activityWatchers[key] = watcher; watchers.Add(watcher);
                ScanTrace("watch " + id + " " + folder);
            } catch (Exception e) {
                try { watcher.Dispose(); } catch { }
                DegradeWatcher(captured, e);
            }
        }

        // Phase 0.2 降级梯子：Error（含 InternalBufferOverflowException）→ 该平台粘性切换为
        // 2 秒 LastWriteTime 轮询（复用下方 RecentWrite 兜底路径），进程内不再回原生事件。
        // 可能从 FSW 的线程池线程触发：投递回 UI 线程处理（activityWatchers 的唯一写方）。
        private void DegradeWatcher(string id, Exception reason) {
            Dispatcher.UIThread.Post(() => {
                if (!degradedAgents.Add(id)) return;
                ScanTrace("watch-error " + id + " -> polling: " + (reason == null ? "unknown" : reason.GetType().Name + " " + reason.Message));
                if (reason != null) LogError("watcher " + id, reason);
                foreach (var pair in activityWatchers.Where(p => p.Key.StartsWith(id + "|", StringComparison.Ordinal)).ToList()) {
                    activityWatchers.Remove(pair.Key);
                    try { pair.Value.Dispose(); } catch { }   // 旧 watcher 全部停完才进入轮询态
                }
                lastDegradedPoll[id] = DateTime.UtcNow;
                if (degradedPollTimer == null) {
                    degradedPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    degradedPollTimer.Tick += delegate { PollDegraded(); };
                }
                degradedPollTimer.Start();
            });
        }

        private void PollActivity() {
            DateTime now = DateTime.UtcNow;
            foreach (string id in ProviderCatalog.Ids) {
                foreach (string folder in Pacing.ActivityFolders(id)) {
                    if (!Directory.Exists(folder)) continue;
                    if (RecentWrite(folder, now - Pacing.InUseWindow, 0)) {
                        lock (lastActivity) lastActivity[id] = DateTime.UtcNow;
                        // 降级平台没有原生事件：30 秒兜底轮询也喂调度器。普通平台只标记在用
                        //（原生事件驱动扫描；这里若再喂会与 tick 快速扫描叠加）。
                        if (degradedAgents.Contains(id) && scanScheduler != null) scanScheduler.Report(id);
                        break;
                    }
                }
            }
            // 新出现的会话目录（启动后才建、或丢事件）：30 秒对账时补建 watcher。
            foreach (string id in ProviderCatalog.Ids) EnsureWatchers(id);
        }

        // 降级平台的 2 秒自扫：与 PollActivity 同一条 LastWriteTime 路径，但窗口取“距上次
        // 轮询”（留 1 秒重叠防 mtime 粒度）——只有持续写入才喂调度器，不在用不扫。
        private void PollDegraded() {
            DateTime now = DateTime.UtcNow;
            foreach (string id in degradedAgents.ToList()) {
                DateTime since;
                if (!lastDegradedPoll.TryGetValue(id, out since)) since = now - Pacing.InUseWindow;
                lastDegradedPoll[id] = now;
                foreach (string folder in Pacing.ActivityFolders(id)) {
                    if (!Directory.Exists(folder)) continue;
                    if (RecentWrite(folder, since.AddSeconds(-1), 0)) {
                        lock (lastActivity) lastActivity[id] = DateTime.UtcNow;
                        if (scanScheduler != null) scanScheduler.Report(id);
                        break;
                    }
                }
            }
        }
        // 深度限制 20 层、跳过符号链接、数到 limit 即停：监视根条目数上限（Phase 0.2）的建前计数。
        private static int CountEntries(string folder, int limit) {
            int count = 0;
            var stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(folder, 0));
            while (stack.Count > 0 && count <= limit) {
                var top = stack.Pop(); string dir = top.Key; int depth = top.Value;
                try {
                    foreach (string file in Directory.EnumerateFiles(dir)) { count++; if (count > limit) return count; }
                    if (depth >= 20) continue;
                    foreach (string sub in Directory.EnumerateDirectories(dir)) {
                        count++;
                        if (count > limit) return count;
                        try { if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) stack.Push(new KeyValuePair<string, int>(sub, depth + 1)); } catch { }
                    }
                } catch { }
            }
            return count;
        }
        // Phase 0 验证/诊断跟踪（仅 Debug 构建进 stdout）：事件命中、调度触发、watcher 建/降级。
#if DEBUG
        private static void ScanTrace(string message) {
            try { Console.WriteLine("[phase0] " + DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message); } catch { }
        }
#else
        private static void ScanTrace(string message) { }
#endif
        // 深度限制 5 层、命中即返回：会话记录（.jsonl/.db）都在浅层目录里。
        private static bool RecentWrite(string folder, DateTime since, int depth) {
            try {
                if (depth > 5) return false;
                foreach (string file in Directory.EnumerateFiles(folder)) {
                    DateTime at;
                    try { at = File.GetLastWriteTimeUtc(file); } catch { continue; }
                    if (at >= since) return true;
                }
                foreach (string dir in Directory.EnumerateDirectories(folder)) if (RecentWrite(dir, since, depth + 1)) return true;
            } catch { }
            return false;
        }
        // The 30-second tick: read the platforms that are due, quietly (no spinner), and rescan
        // local usage more often while something is in use. Due judgment rides the per-platform
        // refresh channel (LimitsRuntime): a backing-off platform waits for its retry moment, an
        // auth-failed one waits for the user; manual refreshes never come through here.
        private async Task RefreshDue() {
            if (demo || pacing || refreshing) return;
            pacing = true;
            try {
                DateTime now = DateTime.UtcNow;
                List<string> due = config.Enabled.Where(ProviderCatalog.IsKnown).Where(id => limits.AutoDue(config, id, InUse(id), now)).ToList();
                if (due.Count > 0) {
                    using (var service = new ProviderService(config)) {
                        var jobs = due.Select(async id => {
                            int serial = BeginFetch(id);
                            try { ProviderState result = await Task.Run(() => service.Fetch(id)); if (result != null) RecordOutcome(id, result); if (Latest(id, serial)) Accept(id, result); }
                            catch (Exception e) { limits.RecordFailure(id, "error", config, InUse(id), DateTime.UtcNow); SourceHealth.RecordError(id, e); }
                        }).ToArray();
                        await Task.WhenAll(jobs);
                    }
                    lastRefresh = DateTime.Now;
                    try { Store.Write("quota-cache.json", states.Values.ToList()); } catch { }
                    UpdateTray(); Render();
                }
                List<string> busy = InUseIds();
                bool quick = config.FastInUse && busy.Count > 0 && now - lastQuickScan >= TimeSpan.FromMinutes(Math.Max(1, config.InUseMinutes));
                DateTime stamp; bool stale = !DateTime.TryParse(history.Updated, out stamp) || now - stamp.ToUniversalTime() > TimeSpan.FromMinutes(15);
                // stale 兜底与额度 due 解耦：全部平台处于 auth 态时 due 恒空，本地用量
                // 统计不能因此被饿死（15 分钟陈旧即扫一次，有 watcher 时很少走到）。
                if (!scanning && (quick || stale)) { lastQuickScan = now; await ScanHistory(); }
            } finally { pacing = false; }
        }

        // ── Demo data（App.cs:207-276 原样迁移）────────────────────────────
        private void SeedDemo() {
            config.Enabled = ProviderCatalog.Ids;
            history = new UsageHistory { Zone = TimeZoneInfo.Local.Id, Updated = DateTime.UtcNow.ToString("o") };
            double[] shape = { .5, .8, 1.1, .3, 0, .9, 1.4, .6, .2, 0, 1.2, 1.8, .7, .4, 1, 1.5, .5, 0, .8, 1.3, 1.9, .6, .3, 1.1, 1.6, .9, .2, 1.2, 1.7, 1.4 };
            for (int i = 0; i < 30; i++) {
                string day = HistoryService.DayKey(DateTime.Today.AddDays(i - 29));
                if (shape[i] > 0) history.Days.Add(DemoDay(day, "codex", shape[i] * 9000000, 2.05e-6, "demo-model-a"));
                if (i % 3 != 1) history.Days.Add(DemoDay(day, "claude", (1.2 - shape[i] / 2) * 1500000, 1.1e-6, "demo-model-b"));
            }
            foreach (string id in ProviderCatalog.Ids) {
                var state = new ProviderState { Id = id, Status = "ready", Message = "演示数据", Plan = id == "deepseek" ? "API 余额" : "演示账户", Account = "demo." + id + "@example.com", LastSuccess = DateTime.UtcNow.ToString("o") };
                if (id == "deepseek") state.Balances.Add(new Balance { Currency = "USD", Amount = 8.62 });
                else {
                    state.Quotas.Add(new Quota { Label = "每周", Used = id == "codex" ? 38 : id == "claude" ? 3 : 20, ResetUtc = DateTime.UtcNow.AddDays(3).AddHours(5).ToString("o"), WindowSeconds = 604800 });
                    if (id == "claude") state.Quotas.Insert(0, new Quota { Label = "5 小时", Used = 64, ResetUtc = DateTime.UtcNow.AddHours(1.5).ToString("o"), WindowSeconds = 18000 });
                }
                states[id] = state;
            }
            states["cursor"].Plan = "Cursor Pro";
            states["cursor"].Quotas.Clear();
            string cursorReset = DateTime.UtcNow.AddDays(3).AddHours(5).ToString("o");
            states["cursor"].Quotas.Add(new Quota { Label = "套餐总量", Used = 20, ResetUtc = cursorReset, WindowSeconds = 2592000 });
            states["cursor"].Quotas.Add(new Quota { Label = "Auto", Used = 12, ResetUtc = cursorReset, WindowSeconds = 2592000 });
            states["cursor"].Quotas.Add(new Quota { Label = "API / 手动模型", Used = 96, ResetUtc = cursorReset, WindowSeconds = 2592000 });
            states["cursor"].Quotas.Add(new Quota { Label = Parsers.CursorGrokLabel, Used = 69, ResetUtc = DateTime.UtcNow.AddDays(1).ToString("o"), WindowSeconds = 604800 });
            states["grok"] = new ProviderState { Id = "grok", Status = "setup", Message = ProviderCatalog.Help("grok") };
            states["copilot"].Plan = "Copilot Pro"; states["copilot"].Quotas.Clear();
            states["copilot"].Quotas.Add(new Quota { Label = "高级请求", Used = 42, ResetUtc = DateTime.UtcNow.AddDays(11).ToString("o"), WindowSeconds = 2592000 });
            states["kimi"].Plan = "Kimi Moderato"; states["kimi"].Quotas.Insert(0, new Quota { Label = "5 小时", Used = 55, ResetUtc = DateTime.UtcNow.AddHours(2.2).ToString("o"), WindowSeconds = 18000 });
            // One key provider stays unconnected so the demo shows connecting from a card.
            states["opencode"] = new ProviderState { Id = "opencode", Status = "setup", Message = ProviderCatalog.Help("opencode") };
            states["zcode"].Plan = "GLM Pro"; states["zcode"].Quotas.Insert(0, new Quota { Label = "5 小时", Used = 31, ResetUtc = DateTime.UtcNow.AddHours(1.1).ToString("o"), WindowSeconds = 18000 });
            states["pi"] = new ProviderState { Id = "pi", Status = "ready", Message = "仅本机用量", Plan = "本机日志", LastSuccess = DateTime.UtcNow.ToString("o") };
            for (int i = 0; i < 30; i++) {
                string day = HistoryService.DayKey(DateTime.Today.AddDays(i - 29));
                if (i % 2 == 0) history.Days.Add(DemoDay(day, "zcode", (0.4 + shape[i]) * 1800000, 0.6e-6, "demo-model-c"));
                if (i > 18 && i % 3 == 0) history.Days.Add(DemoDay(day, "pi", shape[i] * 900000, 1.4e-6, "demo-model-a"));
            }
            // A demo-only custom provider (kept in memory, never written to data/).
            CustomProvider sample = CustomProviders.Parse("{\"name\":\"示例网关\",\"id\":\"demo-gateway\",\"color\":\"#8FD1E8\",\"url\":\"https://gateway.example.com/v1/usage\",\"auth\":\"bearer\",\"windows\":[{\"label\":\"每日\",\"used\":\"daily.used\",\"limit\":\"daily.limit\"}],\"balance\":{\"amount\":\"balance\",\"currency\":\"CNY\"}}");
            RegisterCustomProviders(new[] { sample });
            states[sample.Id] = CustomProviders.Map(sample, J.Parse("{\"daily\":{\"used\":320,\"limit\":1000},\"balance\":46.8}"), DateTime.UtcNow);
            states[sample.Id].Status = "ready"; states[sample.Id].LastSuccess = DateTime.UtcNow.ToString("o"); states[sample.Id].Account = "demo@example.com";
            config.Enabled = ProviderCatalog.All.ToArray();
            states["codex"].Plan = "Pro 20x";
            states["codex"].ResetCreditsAvailable = 2;
            states["codex"].ResetCreditExpiries = new List<string> { DateTime.UtcNow.AddDays(8).AddHours(12).ToString("o"), DateTime.UtcNow.AddDays(26).AddHours(4).ToString("o") };
            var relayA = new EndpointUsage { App = Endpoints.ClaudeApp, Host = "relay-a.example.com", Key = "a1b2c3", Name = "示例中转 A", Current = true, MainModel = "demo-model-b", LastUsed = DateTime.UtcNow.AddMinutes(-12).ToString("o") };
            var relayB = new EndpointUsage { App = Endpoints.CodexApp, Host = "api.relay-b.example.org", Key = "d4e5f6", Name = "示例中转 B", MainModel = "demo-model-a", LastUsed = DateTime.UtcNow.AddDays(-3).ToString("o") };
            for (int i = 0; i < 30; i++) { relayA.Daily[i] = i >= 12 ? shape[i] * 2400000 : 0; relayB.Daily[i] = i < 27 && i % 4 != 0 ? shape[i] * 5200000 : 0; }
            foreach (var relay in new[] { new { Usage = relayA, Rate = 1.2e-6 }, new { Usage = relayB, Rate = 2.0e-6 } }) {
                EndpointUsage u = relay.Usage; u.Today = u.Daily[29]; u.Week = u.Daily.Skip(23).Sum(); u.Month = u.Daily.Sum(); u.Requests = Math.Round(u.Month / 180000);
                u.CostToday = u.Today * relay.Rate; u.CostWeek = u.Week * relay.Rate; u.CostMonth = u.Month * relay.Rate;
                u.TimedOutput = u.Month * .007; u.TimedSeconds = u.TimedOutput / (u == relayA ? 71 : 29);
            }
            thirdParty = new ThirdPartySummary {
                Endpoints = new List<EndpointUsage> { relayA, relayB },
                ClaudeNow = new EndpointMark { App = Endpoints.ClaudeApp, Host = relayA.Host, Key = relayA.Key, Name = relayA.Name },
                CodexNow = new EndpointMark { App = Endpoints.CodexApp, Official = true },
                ClaudeTrackedSince = DateTime.UtcNow.AddDays(-18).ToString("o"), ClaudeUnattributed = 4800000
            };
        }
        private static DayUsage DemoDay(string day, string agent, double tokens, double rate, string model) {
            double cached = Math.Round(tokens * .972), input = Math.Round(tokens * .021), output = tokens - cached - input;
            // Illustrative output speeds (t/s) for the agents whose logs carry durations.
            double speed = agent == "codex" ? 24 : agent == "claude" ? 88 : agent == "zcode" ? 48 : agent == "grok" ? 52 : agent == "opencode" ? 60 : 0;
            speed *= 1 + ((day.GetHashCode() & 0xFF) / 255.0 - .5) * .3;
            double timed = speed > 0 ? Math.Round(output * .9) : 0, seconds = speed > 0 ? timed / speed : 0;
            return new DayUsage { Day = day, Agent = agent, Tokens = tokens, CachedTokens = cached, InputTokens = input, OutputTokens = output, Cost = tokens * rate, TimedRequests = Math.Round(timed / 900), TimedOutput = timed, TimedSeconds = seconds, Models = new List<ModelUsage> { new ModelUsage { Model = model, Tokens = tokens, TimedOutput = timed, TimedSeconds = seconds } } };
        }
        private void RegisterCustomProviders(IEnumerable<CustomProvider> providers) {
            ProviderCatalog.Custom.Clear();
            foreach (CustomProvider provider in providers) ProviderCatalog.Custom[provider.Id] = provider;
            foreach (string id in ProviderCatalog.All) if (!states.ContainsKey(id)) states[id] = new ProviderState { Id = id };
        }

        // ── 生命周期（App.cs:451-452）─────────────────────────────────────
        public void Quit() {
            frame.Capture(); SaveConfig(); quitting = true;
            if (tray != null) tray.IsVisible = false;
            if (settingsWindow != null) settingsWindow.Close();
            window.Close();
            Shutdown();
            SingleInstance.Done();
        }
        public void Dispose() {
            quitting = true; frame.Dispose(); refreshTimer.Stop(); clockTimer.Stop();
            if (activityPollTimer != null) activityPollTimer.Stop();
            if (schedulerTimer != null) schedulerTimer.Stop();
            if (degradedPollTimer != null) degradedPollTimer.Stop();
            foreach (var watcher in watchers) watcher.Dispose();
            if (tray != null) { tray.IsVisible = false; tray.Dispose(); tray = null; }
        }
    }
}
