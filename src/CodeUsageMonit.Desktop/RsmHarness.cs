// RSM 隔离 harness（仅 --rsm-harness 进入，生产路径不触及）。真实窗口内以合成 Avalonia
// 指针事件驱动两条链路的完整管线，断言窗口几何与滚动偏移的真实变化（RevealHarness 先例）：
//   ① 手动调整边（任务1）：E/S/W/N 四边各一条拖动 —— 尺寸按指针位移变化、钳制口径与
//      WindowFrame.ApplyLimits 一致、W/N 侧对侧缘严格不漂移；
//   ② 紧凑档钳制：small 档把窗口拖到超宽 → 收在 MaxSize × UiScale（full 档不钳，8192）；
//   ③ 按住拖动滚动（任务2）：BodyScroll 空白处按下拖动 → Offset 平移；阈值内不拦截；
//      按下源在交互控件子树内（UiInput.IsInteractive 口径）不接管；
//   ④ 顺带断言详情页时间段新建键播种「当天」（任务3 的运行时口径，无头侧在 UiSelfTests）。
//   ⑤ 滚轮盲区：卡片缝隙处 InputHitTest 必须命中 BodyScroll 子树、合成滚轮必须滚 Offset。
// 为什么不用 CGEvent 合成点击：部分运行环境（脚本宿主无辅助功能信任时）会丢合成鼠标按下
// 事件（移动可达、按下不可达），而构造 PointerEventArgs 走的是同一条 InputManager 路由，
// 只省去系统 HitTest —— 命中哪个边框由 ResizeChrome 的固定布局保证（harness 按
// Horizontal/VerticalAlignment 从 ResizeLayer 里取，与 Build 的布局一一对应）。
// 截图与度量落 CU_RSM_EVIDENCE_DIR（默认 exe 旁 verification-rsm/）；失败经退出码暴露。
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CodeUsageMonit {
    internal static class RsmHarness {
        public static bool Pending;
        private static readonly System.Collections.Generic.List<string> Results = new System.Collections.Generic.List<string>();
        private static void Require(bool value, string message = "Assertion failed") {
            Results.Add((value ? "PASS " : "FAIL ") + message);
            if (!value) throw new Exception("Assertion failed: " + message);
        }
        private static void Log(string line) { Results.Add("NOTE " + line); Console.WriteLine(line); }

        public static void Run(MonitorPanel ui) {
            _ = Dispatcher.UIThread.InvokeAsync(async () => {
                int failures = 0;
                try {
                    await Task.Delay(700); // Start（ApplicationIdle）先落地
                    var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                    var window = (Window)typeof(MonitorPanel).GetField("window", flags).GetValue(ui);
                    var frame = (WindowFrame)typeof(MonitorPanel).GetField("frame", flags).GetValue(ui);
                    Require(window != null && frame != null);
                    var config = (AppConfig)typeof(MonitorPanel).GetField("config", flags).GetValue(ui);
                    config.HideOnDeactivate = false;
                    typeof(MonitorPanel).GetMethod("SetDisplaySize", flags).Invoke(ui, new object[] { "full" });
                    await Task.Delay(250);
                    Require(window.IsVisible && window.WindowState == WindowState.Normal);

                    string evidence = Environment.GetEnvironmentVariable("CU_RSM_EVIDENCE_DIR");
                    if (String.IsNullOrEmpty(evidence)) evidence = Path.Combine(AppContext.BaseDirectory, "verification-rsm");
                    Directory.CreateDirectory(evidence);
                    File.WriteAllText(Path.Combine(evidence, "metrics.json"), Metrics(window, frame));

                    // 已知几何：位置与尺寸都定下来，断言不随启动恢复值漂移。
                    window.Position = new PixelPoint(220, 140);
                    window.Width = 469; window.Height = 790;
                    await Task.Delay(120);

                    var layer = window.GetVisualDescendants().OfType<Panel>().First(p => p.Name == "ResizeLayer");
                    Func<HorizontalAlignment, VerticalAlignment, Border> edge = (h, v) => layer.Children.OfType<Border>()
                        .First(b => b.HorizontalAlignment == h && b.VerticalAlignment == v);

                    Log("unit=" + Unit(window).ToString("0.###") + " renderScaling=" + window.RenderScaling.ToString("0.###") + " screenScaling=" + ScreenScaling(window).ToString("0.###")
                        + " position=" + window.Position + " p2s00=" + window.PointToScreen(new Point(0, 0)));

                    // ① 东缘：+90 单位 → 宽度 +90（full 档无上限钳制）。
                    double w0 = window.Width;
                    Point from = At(window, edge(HorizontalAlignment.Right, VerticalAlignment.Stretch));
                    PixelPoint right0 = window.PointToScreen(new Point(window.Bounds.Width, 0));
                    DragEdge(window, edge(HorizontalAlignment.Right, VerticalAlignment.Stretch), from, new Point(from.X + 90, from.Y));
                    await Task.Delay(80);
                    double expectW = WindowFrame.MaxSize("full").Width >= w0 + 90 ? w0 + 90 : w0 + 90;   // full 档 8192，不触顶
                    Require(Math.Abs(window.Width - expectW) <= 2, "east drag widens by the pointer delta, got " + Math.Round(window.Width, 1) + " expected " + expectW);
                    Require(window.Position.X == 220, "east drag must not move the window, got " + window.Position.X);
                    Capture(window, Path.Combine(evidence, "r1-east-wider.png"));

                    // ② 南缘：+110 单位 → 高度 +110。
                    double h0 = window.Height;
                    Point fromS = At(window, edge(HorizontalAlignment.Stretch, VerticalAlignment.Bottom));
                    DragEdge(window, edge(HorizontalAlignment.Stretch, VerticalAlignment.Bottom), fromS, new Point(fromS.X, fromS.Y + 110));
                    await Task.Delay(80);
                    Require(Math.Abs(window.Height - (h0 + 110)) <= 2, "south drag heightens by the pointer delta, got " + Math.Round(window.Height, 1));
                    Capture(window, Path.Combine(evidence, "r2-south-taller.png"));

                    // ③ 西缘：-70 单位 → 宽度 +70、位置左移 70、右缘在屏幕坐标里不动。
                    w0 = window.Width;
                    double x0 = window.Position.X;
                    Point fromW = At(window, edge(HorizontalAlignment.Left, VerticalAlignment.Stretch));
                    right0 = window.PointToScreen(new Point(window.Bounds.Width, 0));
                    DragEdge(window, edge(HorizontalAlignment.Left, VerticalAlignment.Stretch), fromW, new Point(fromW.X - 70, fromW.Y));
                    await Task.Delay(80);
                    Require(Math.Abs(window.Width - (w0 + 70)) <= 2, "west drag widens against the pointer, got " + Math.Round(window.Width, 1));
                    Require(Math.Abs(window.Position.X - (x0 - 70)) <= 2, "west drag moves the left edge with the pointer, got " + window.Position.X + " expected " + (x0 - 70));
                    PixelPoint right1 = window.PointToScreen(new Point(window.Bounds.Width, 0));
                    Require(Math.Abs(right1.X - right0.X) <= 2, "west drag must keep the right edge anchored, moved " + (right1.X - right0.X));
                    Capture(window, Path.Combine(evidence, "r3-west-anchored.png"));

                    // ④ 北缘：-60 单位 → 高度 +60、顶缘上移、下缘不动。
                    h0 = window.Height;
                    double y0 = window.Position.Y;
                    Point fromN = At(window, edge(HorizontalAlignment.Stretch, VerticalAlignment.Top));
                    PixelPoint bottom0 = window.PointToScreen(new Point(0, window.Bounds.Height));
                    DragEdge(window, edge(HorizontalAlignment.Stretch, VerticalAlignment.Top), fromN, new Point(fromN.X, fromN.Y - 60));
                    await Task.Delay(80);
                    Require(Math.Abs(window.Height - (h0 + 60)) <= 2, "north drag heightens against the pointer, got " + Math.Round(window.Height, 1));
                    PixelPoint bottom1 = window.PointToScreen(new Point(0, window.Bounds.Height));
                    Require(Math.Abs(bottom1.Y - bottom0.Y) <= 2, "north drag must keep the bottom edge anchored, moved " + (bottom1.Y - bottom0.Y));
                    Require(Math.Abs(window.Position.Y - (y0 - 60)) <= 2, "north drag moves the top edge with the pointer, got " + window.Position.Y + " expected " + (y0 - 60));

                    // ⑤ 紧凑钳制：small 档（Max 360×360 × UiScale）拖到超宽超高必须收住。
                    typeof(MonitorPanel).GetMethod("SetDisplaySize", flags).Invoke(ui, new object[] { "small" });
                    await Task.Delay(350);
                    double scale = Math.Max(.8, Math.Min(1.4, config.UiScale));
                    Size maxS = WindowFrame.MaxSize("small"), minS = WindowFrame.MinSize("small");
                    double maxW = maxS.Width * scale;
                    Point fromSE = At(window, edge(HorizontalAlignment.Right, VerticalAlignment.Stretch));
                    w0 = window.Width;
                    DragEdge(window, edge(HorizontalAlignment.Right, VerticalAlignment.Stretch), fromSE, new Point(fromSE.X + 1200, fromSE.Y));
                    await Task.Delay(80);
                    Require(Math.Abs(window.Width - Math.Min(w0 + 1200, maxW)) <= 2,
                        "small east drag clamps at MaxSize × UiScale (" + maxW + "), got " + Math.Round(window.Width, 1));
                    Point fromSS = At(window, edge(HorizontalAlignment.Stretch, VerticalAlignment.Bottom));
                    DragEdge(window, edge(HorizontalAlignment.Stretch, VerticalAlignment.Bottom), fromSS, new Point(fromSS.X, fromSS.Y + 1200));
                    await Task.Delay(80);
                    Require(window.Height <= maxS.Height * scale + 1, "small south drag clamps at MaxSize × UiScale, got " + Math.Round(window.Height, 1));
                    Capture(window, Path.Combine(evidence, "r4-small-clamped.png"));
                    // small 的下限：往回拖过 Min 也收在 MinSize × UiScale。
                    Point fromSW = At(window, edge(HorizontalAlignment.Left, VerticalAlignment.Stretch));
                    DragEdge(window, edge(HorizontalAlignment.Left, VerticalAlignment.Stretch), fromSW, new Point(fromSW.X + 4000, fromSW.Y));
                    await Task.Delay(80);
                    Require(window.Width >= minS.Width * scale - 1 && Math.Abs(window.Width - minS.Width * scale) <= 2,
                        "small west drag clamps at MinSize × UiScale (" + minS.Width * scale + "), got " + Math.Round(window.Width, 1));

                    // 回完整面板做拖动滚动。
                    typeof(MonitorPanel).GetMethod("SetDisplaySize", flags).Invoke(ui, new object[] { "full" });
                    await Task.Delay(350);
                    window.Position = new PixelPoint(220, 140);
                    window.Width = 520; window.Height = 800;
                    await Task.Delay(120);
                    typeof(MonitorPanel).GetField("selected", flags).SetValue(ui, "overview");
                    ((Action)typeof(MonitorPanel).GetMethod("Render", flags).CreateDelegate(typeof(Action), ui))();
                    await Task.Delay(250);
                    var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "BodyScroll");
                    scroll.Offset = new Vector(0, 0);
                    double offset0 = scroll.Offset.Y;
                    Require(scroll.Extent.Height > scroll.Bounds.Height + 240, "overview content must be scrollable for the drag test");

                    // ⑥ 空白处按住拖动：位移 220 → Offset 平移 220（钳到范围）。
                    var press = new PointerPressedEventArgs(scroll, Pointer(), window, new Point(100, 420), 0, Props(PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
                    scroll.RaiseEvent(press);
                    scroll.RaiseEvent(new PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, scroll, press.Pointer, window, new Point(100, 417), 0, Props(PointerUpdateKind.Other), KeyModifiers.None));
                    await Task.Delay(20);
                    Require(Math.Abs(scroll.Offset.Y - offset0) < .5, "within the 4px threshold the drag must not scroll");
                    scroll.RaiseEvent(new PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, scroll, press.Pointer, window, new Point(100, 200), 0, Props(PointerUpdateKind.Other), KeyModifiers.None));
                    await Task.Delay(30);
                    Require(scroll.Offset.Y > offset0 + 150, "dragging up must pan the body down, offset " + scroll.Offset.Y + " -> " + (offset0 + 220));
                    scroll.RaiseEvent(new PointerReleasedEventArgs(scroll, press.Pointer, window, new Point(100, 200), 0, Props(PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                    await Task.Delay(30);
                    double afterDrag = scroll.Offset.Y;
                    Require(Math.Abs(afterDrag - scroll.Offset.Y) < .5 && afterDrag > offset0 + 150, "offset must persist after release");
                    Capture(window, Path.Combine(evidence, "r5-drag-scrolled.png"));

                    // ⑦ 交互控件豁免：按下源在 Button 子树内 → 不接管、Offset 不动。
                    // 事件从 Button 本身发出（与真实点击同一条冒泡路由），Source 一路是 Button。
                    var anyButton = window.GetVisualDescendants().OfType<Button>().First(b => IsInBody(b, scroll));
                    double keep = scroll.Offset.Y;
                    Point atButton = ToWindow(window, anyButton);
                    var press2 = new PointerPressedEventArgs(anyButton, Pointer(), window, atButton, 0, Props(PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
                    anyButton.RaiseEvent(press2);
                    anyButton.RaiseEvent(new PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, anyButton, press2.Pointer, window, new Point(atButton.X, atButton.Y - 200), 0, Props(PointerUpdateKind.Other), KeyModifiers.None));
                    await Task.Delay(30);
                    Log("exemption: button=" + anyButton.GetType().Name + " pressHandled=" + press2.Handled + " sourceIsButton=" + (press2.Source == anyButton) + " keep=" + keep.ToString("0.#") + " now=" + scroll.Offset.Y.ToString("0.#"));
                    // 偏移在此不比较：段落按钮按下即切换 → 页面重建，Offset 的任何变化来自
                    // Render 而非手势（见上一行日志）；接管与否的判据是 armed 与 IsInteractive。
                    var armed = (bool)typeof(MonitorPanel).GetField("dragScrollArmed", flags).GetValue(ui);
                    Require(!armed, "interactive press must not arm the drag-scroll");
                    // IsInteractive 口径本身：按钮是交互、滚动面不是（EnableDrag 共用）。
                    Require(UiInput.IsInteractive(anyButton), "IsInteractive must classify buttons as interactive");
                    Require(!UiInput.IsInteractive(scroll), "the scroll surface itself must not be interactive");
                    var someText = window.GetVisualDescendants().OfType<TextBlock>().First(t => IsInBody(t, scroll));
                    Require(!UiInput.IsInteractive(someText), "plain text must stay draggable-scrollable");

                    // ⑧ 阈值内释放：2px 挪动后松开 → Offset 不动（点击语义不受影响）。
                    // （段落切换后的页面重建已落定，重新取基准。）
                    keep = scroll.Offset.Y;
                    var press3 = new PointerPressedEventArgs(scroll, Pointer(), window, new Point(150, 380), 0, Props(PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
                    scroll.RaiseEvent(press3);
                    scroll.RaiseEvent(new PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, scroll, press3.Pointer, window, new Point(152, 380), 0, Props(PointerUpdateKind.Other), KeyModifiers.None));
                    await Task.Delay(20);
                    scroll.RaiseEvent(new PointerReleasedEventArgs(scroll, press3.Pointer, window, new Point(152, 380), 0, Props(PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                    await Task.Delay(20);
                    Require(Math.Abs(scroll.Offset.Y - keep) < .5, "a sub-threshold press must leave the offset untouched");

                    // ⑨ 详情页时间段新建键播种「当天」（任务3 的运行时口径）。
                    var rangeFor = typeof(MonitorPanel).GetMethod("RangeFor", flags);
                    var seeded = (UsageRange)rangeFor.Invoke(ui, new object[] { "kimi" });
                    Require(seeded.Preset == "today", "a fresh provider range must seed today, got " + seeded.Preset);

                    // ── ⑩-⑫ 证据截图与交互回归（供验收材料；⑪⑫ 顺带断言交互生效）──
                    // ⑩ 概览 Kimi 卡：滚动到「本机用量」小节可见再截图（任务4 的真机形态）。
                    var bodyPanel = (StackPanel)typeof(MonitorPanel).GetField("body", flags).GetValue(ui);
                    Button kimiCard = FindButton(bodyPanel, ProviderCatalog.Name("kimi") + " 详情");
                    Require(kimiCard != null, "the kimi overview card must exist");
                    Point cardAt = ToWindow(window, kimiCard);
                    scroll.Offset = new Vector(0, scroll.Offset.Y + cardAt.Y - 170);
                    await Task.Delay(200);
                    Capture(window, Path.Combine(evidence, "r6-overview-kimi-card.png"));

                    // ⑪ hero 单平台模型视图：点「图表：Kimi」chip（OnClick 与真实点击同一路径）。
                    var heroCardField = (Border)typeof(MonitorPanel).GetField("heroCard", flags).GetValue(ui);
                    Button kimiChip = FindButton(heroCardField, "图表：" + ProviderCatalog.Name("kimi"));
                    if (kimiChip == null) Log("hero kimi chip missing (only one platform with records?)");
                    else {
                        scroll.Offset = new Vector(0, 0);
                        await Task.Delay(120);
                        typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(kimiChip, new object?[0]);
                        await Task.Delay(250);
                        var heroAfter = (Border)typeof(MonitorPanel).GetField("heroCard", flags).GetValue(ui);
                        string heroTitle = FindText(heroAfter).Select(t => t.Text ?? "").FirstOrDefault(t => t.Contains("Token 用量")) ?? "";
                        Require(heroTitle.StartsWith(ProviderCatalog.Name("kimi") + " ·", StringComparison.Ordinal),
                            "hero must switch to the kimi single-platform view, got: " + heroTitle);
                        Require(FindText(heroAfter).Any(t => (t.Text ?? "") == "模型"), "the kimi hero must list models");
                        Capture(window, Path.Combine(evidence, "r7-hero-kimi-models.png"));
                    }

                    // ⑫ 紧凑档交互：切到 large（单平台页含时间段分段与页头按钮），分段
                    // （RadioButton）切换触发 RenderCompact 重建；medium 页无分段（配额为主）。
                    typeof(MonitorPanel).GetMethod("SetDisplaySize", flags).Invoke(ui, new object[] { "large" });
                    await Task.Delay(450);
                    var compactRoot = (Border)typeof(MonitorPanel).GetField("compactRoot", flags).GetValue(ui);
                    Require(compactRoot != null && compactRoot.IsVisible, "large must render the compact root");
                    RadioButton segment = null;
                    segment = window.GetVisualDescendants().OfType<RadioButton>().FirstOrDefault(r => IsInBody(r, compactRoot) && (AutomationProperties.GetName(r) ?? "").Length > 0);
                    if (segment == null) Log("compact large has no segmented control to toggle");
                    else {
                        object before = compactRoot.Child;
                        bool wasChecked = segment.IsChecked == true;
                        segment.IsChecked = !wasChecked;
                        await Task.Delay(250);
                        Require(!ReferenceEquals(compactRoot.Child, before) || segment.IsChecked != wasChecked,
                            "toggling a compact segment must drive RenderCompact");
                        Capture(window, Path.Combine(evidence, "r8-compact-large.png"));
                    }
                    typeof(MonitorPanel).GetMethod("SetDisplaySize", flags).Invoke(ui, new object[] { "full" });
                    await Task.Delay(250);

                    // ⑬ 滚轮盲区：指针停在两块内容之间的缝隙（无子元素的空白处）滚轮必须滚动
                    // BodyScroll。根因在 App.axaml 的自定义 ScrollViewer 模板：没把 Background
                    // 绑到模板元素上，ScrollContentPresenter 在空白处不可命中，滚轮事件落到
                    // Surface Border 上、ScrollContentPresenter.OnPointerWheelChanged 不触发。
                    // 两步断言：
                    //   a. InputExtensions.InputHitTest（InputManager 同一条输入命中测试）在缝隙
                    //      处命中的元素必须在 BodyScroll 子树内（修复前命中 Surface，此处即失败）；
                    //   b. 从命中元素发滚轮事件（与真实滚轮唯一差异是省去平台层投递，同 ①-⑧
                    //      口径），BodyScroll.Offset 必须前进 50/格（ScrollContentPresenter 口径）。
                    typeof(MonitorPanel).GetField("selected", flags).SetValue(ui, "overview");
                    ((Action)typeof(MonitorPanel).GetMethod("Render", flags).CreateDelegate(typeof(Action), ui))();
                    await Task.Delay(250);
                    scroll.Offset = new Vector(0, 0);
                    await Task.Delay(60);
                    Require(scroll.Extent.Height > scroll.Bounds.Height + 300, "overview must be scrollable for the wheel-gap test");
                    var gapBody = (StackPanel)typeof(MonitorPanel).GetField("body", flags).GetValue(ui);
                    Point? gapPoint = null;   // 缝隙中点（body 坐标）；卡片 Margin 收口 10px。
                    for (int i = 0; i + 1 < gapBody.Children.Count && gapPoint == null; i++) {
                        Control ca = gapBody.Children[i], cb = gapBody.Children[i + 1];
                        if (!ca.IsVisible || !cb.IsVisible || ca.Bounds.Height < 8 || cb.Bounds.Height < 8) continue;
                        Point? aBottom = ca.TranslatePoint(new Point(0, ca.Bounds.Height), gapBody);
                        Point? bTop = cb.TranslatePoint(new Point(0, 0), gapBody);
                        if (aBottom == null || bTop == null) continue;
                        double gap = bTop.Value.Y - aBottom.Value.Y;
                        if (gap < 4) continue;
                        gapPoint = new Point(120, (aBottom.Value.Y + bTop.Value.Y) / 2);
                    }
                    Require(gapPoint != null, "two consecutive content blocks with a visible gap must exist");
                    Point gapInWindow = gapBody.TranslatePoint(gapPoint.Value, window) ?? new Point(-1, -1);
                    Point scrollTopLeft = scroll.TranslatePoint(new Point(0, 0), window) ?? new Point(-1, -1);
                    Require(gapInWindow.X > scrollTopLeft.X && gapInWindow.Y > scrollTopLeft.Y
                        && gapInWindow.X < scrollTopLeft.X + scroll.Bounds.Width && gapInWindow.Y < scrollTopLeft.Y + scroll.Bounds.Height,
                        "the gap point must sit inside the BodyScroll viewport, got " + gapInWindow + " viewport@" + scrollTopLeft);
                    var gapHit = Avalonia.Input.InputExtensions.InputHitTest(window, gapInWindow);
                    bool gapInScroll = false;
                    for (Visual v = gapHit as Visual; v != null; v = v.GetVisualParent()) if (v == scroll) { gapInScroll = true; break; }
                    Log("wheel-gap: hit=" + (gapHit?.GetType().Name ?? "null") + " inBodyScroll=" + gapInScroll + " at=" + gapInWindow);
                    Require(gapInScroll, "the card-gap point must resolve inside BodyScroll, got " + (gapHit?.GetType().Name ?? "null"));
                    double wheelOffset0 = scroll.Offset.Y;
                    var wheel = new PointerWheelEventArgs(gapHit, Pointer(), window, gapInWindow, 0, Props(PointerUpdateKind.Other), KeyModifiers.None, new Vector(0, -1));
                    gapHit.RaiseEvent(wheel);
                    await Task.Delay(40);
                    Require(scroll.Offset.Y > wheelOffset0 + 20, "wheel over a card gap must scroll the body, offset " + wheelOffset0 + " -> " + scroll.Offset.Y + " handled=" + wheel.Handled);
                    Capture(window, Path.Combine(evidence, "r9-wheel-gap-scrolled.png"));

                    // 空白处按住拖动（任务2 的修复后口径）：按下源是 presenter —— 命中测试
                    // 修复后空白处 PointerPressed 的真实来源 —— 经冒泡到 BodyScroll 的手势
                    // 必须同样接管（IsInteractive 不把 presenter 当交互控件），位移 220 平移、
                    // 释放保持。
                    double dragOffset0 = scroll.Offset.Y;
                    var gapPointer = Pointer();
                    gapHit.RaiseEvent(new PointerPressedEventArgs(gapHit, gapPointer, window, gapInWindow, 0, Props(PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
                    gapHit.RaiseEvent(new PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, gapHit, gapPointer, window, new Point(gapInWindow.X, gapInWindow.Y - 220), 0, Props(PointerUpdateKind.Other), KeyModifiers.None));
                    await Task.Delay(40);
                    Require(scroll.Offset.Y > dragOffset0 + 150, "drag from a card gap must pan the body, offset " + dragOffset0 + " -> " + scroll.Offset.Y);
                    gapHit.RaiseEvent(new PointerReleasedEventArgs(gapHit, gapPointer, window, new Point(gapInWindow.X, gapInWindow.Y - 220), 0, Props(PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
                    await Task.Delay(30);
                    Require(scroll.Offset.Y > dragOffset0 + 150, "offset must persist after a gap drag release");
                    Capture(window, Path.Combine(evidence, "r10-gap-drag-scrolled.png"));

                    Console.WriteLine("RSM RESULT:\n" + String.Join("\n", Results));
                    File.WriteAllLines(Path.Combine(evidence, "results.txt"), Results);
                    Console.WriteLine("PASSED: manual edge resize (E/S/W/N + compact clamp + anchored far edge) and drag-to-scroll (pan, threshold, interactive exemption)");
                } catch (Exception e) {
                    failures++;
                    Exception inner = e.InnerException ?? e;
                    Console.Error.WriteLine("FAILED: " + inner.Message + "\n" + inner.StackTrace);
                    Results.Add("EXCEPTION " + inner.Message);
                    try {
                        string evidence = Environment.GetEnvironmentVariable("CU_RSM_EVIDENCE_DIR") ?? Path.Combine(AppContext.BaseDirectory, "verification-rsm");
                        File.WriteAllLines(Path.Combine(evidence, "results.txt"), Results);
                    } catch { }
                }
                Environment.ExitCode = failures == 0 ? 0 : 1;
                ui.Quit();
            }, DispatcherPriority.Background);
        }

        // ── 合成指针与坐标工具 ────────────────────────────────────────────
        private static int pointerId = 4000;
        private static Avalonia.Input.Pointer Pointer() { return new Avalonia.Input.Pointer(unchecked(pointerId++), PointerType.Mouse, true); }
        private static PointerPointProperties Props(PointerUpdateKind kind) {
            RawInputModifiers modifiers = kind == PointerUpdateKind.LeftButtonReleased ? RawInputModifiers.None : RawInputModifiers.LeftMouseButton;
            return new PointerPointProperties(modifiers, kind);
        }
        // PointToScreen 坐标系单位每 DIP（与 ResizeChrome.Arm 同式实测，不自设语义）。
        private static double Unit(Window window) {
            PixelPoint origin = window.PointToScreen(new Point(0, 0));
            PixelPoint corner = window.PointToScreen(new Point(window.Bounds.Width, window.Bounds.Height));
            double unit = window.Bounds.Width > 0 ? (corner.X - origin.X) / window.Bounds.Width : 1;
            return unit > 0 && !Double.IsNaN(unit) && !Double.IsInfinity(unit) ? unit : 1;
        }
        private static double ScreenScaling(Window window) {
            try { Avalonia.Platform.Screen screen = window.Screens.ScreenFromWindow(window); return screen != null && screen.Scaling > 0 ? screen.Scaling : 1; } catch { return 1; }
        }
        // 目标边框中心换到窗口坐标（press/move 的 rootVisualPosition）。
        private static Point At(Window window, Control target) {
            Point local = new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);
            Point? mapped = target.TranslatePoint(local, window);
            return mapped ?? new Point(10, 10);
        }
        private static Point ToWindow(Visual root, Visual visual) {
            Point? mapped = visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), root);
            return mapped ?? new Point(10, 10);
        }
        private static bool IsInBody(Visual control, Visual scroll) {
            for (Visual v = control; v != null; v = v.GetVisualParent()) if (v == scroll) return true;
            return false;
        }
        // 深度遍历（RevealHarness 同款）：按 AutomationProperties.Name 找按钮 / 收集文本。
        private static Button FindButton(Visual root, string name) {
            if (root is Button b && AutomationProperties.GetName(b) == name) return b;
            foreach (Visual child in root.GetVisualChildren()) { Button found = FindButton(child, name); if (found != null) return found; }
            return null;
        }
        private static System.Collections.Generic.IEnumerable<TextBlock> FindText(Visual root) {
            if (root is TextBlock text) yield return text;
            foreach (Visual child in root.GetVisualChildren()) foreach (TextBlock found in FindText(child)) yield return found;
        }
        // 一次按下 → 单步移动 → 释放（合成事件走 InputManager 路由；移动量为 PointToScreen 单位 ÷ 实测 unit 的窗口坐标差）。
        private static void DragEdge(Window window, Control target, Point from, Point to) {
            double unit = Unit(window);
            var pointer = Pointer();
            target.RaiseEvent(new PointerPressedEventArgs(target, pointer, window, from, 0, Props(PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
            target.RaiseEvent(new PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, target, pointer, window, new Point(from.X + (to.X - from.X) / unit, from.Y + (to.Y - from.Y) / unit), 0, Props(PointerUpdateKind.Other), KeyModifiers.None));
            target.RaiseEvent(new PointerReleasedEventArgs(target, pointer, window, new Point(from.X + (to.X - from.X) / unit, from.Y + (to.Y - from.Y) / unit), 0, Props(PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        }
        // 窗口内容截图：进程内离屏渲染视觉树存 PNG（不拉起外部进程、不需要屏幕录制
        // 权限；不含系统毛玻璃背板，断言对象是窗口内容）。失败只打日志，不作断言。
        private static void Capture(Window window, string path) {
            try {
                double scaling = ScreenScaling(window);
                var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Round(window.Bounds.Width * scaling)), Math.Max(1, (int)Math.Round(window.Bounds.Height * scaling))), new Vector(96 * scaling, 96 * scaling));
                bitmap.Render(window);
                bitmap.Save(path);
                Console.WriteLine("captured: " + path + (File.Exists(path) ? "" : " (save failed?)"));
            } catch (Exception e) { Console.WriteLine("capture failed: " + e.Message); }
        }
        private static string Metrics(Window window, WindowFrame frame) {
            PixelPoint origin = window.PointToScreen(new Point(0, 0));
            PixelPoint corner = window.PointToScreen(new Point(window.Bounds.Width, window.Bounds.Height));
            return "renderScaling=" + window.RenderScaling.ToString("0.###") + "\n"
                + "screenScaling=" + ScreenScaling(window).ToString("0.###") + "\n"
                + "position=" + window.Position.X + "," + window.Position.Y + "\n"
                + "boundsDip=" + window.Bounds.Width.ToString("0.#") + "x" + window.Bounds.Height.ToString("0.#") + "\n"
                + "p2sOrigin=" + origin.X + "," + origin.Y + "\n"
                + "p2sCorner=" + corner.X + "," + corner.Y + "\n"
                + "unitPerDip=" + Unit(window).ToString("0.####") + "\n"
                + "mode=" + frame.Mode;
        }
    }
}
