// Reveal 隔离 harness（仅 --reveal-harness 进入，生产路径不触及）：demo 模式拉起真实
// 面板窗口，等 Start 完成后在 UI 线程断言——
//   ① 隐藏后的 Reveal 让窗口恢复可见，且 NSApp 前台激活（MacApp.ActivateForeground）
//      确实被执行（ActivationCount 计数，可观测）；
//   ② 托盘 toggle 语义：活跃且可见 → 收起；隐藏 → 显示；
//   ③ DeepSeek Harness ready 态（无余额）的概览卡片渲染出内联 KeyBox（任务 3 分支）；
//   ⑥ hero 卡（ux-overhaul 需求1）：大数字 = Token 总数（26px Run），API 等价费用降为
//      小字副行，构成分组切换（按工具/按模型）切换后 hero 就地重建；
//   ⑦ 概览平台卡（需求2）：渲染顺序 = Urgent（ProviderOrder 共享口径）且不过滤；
//   ⑧ 认证页（需求3）：「本机检测到的账户」区存在（demo 下给提示行），4 个密钥平台
//      KeyBox 带占位符（demo 中禁用）。
// 关键截图经 screencapture -R 存 CU_UX_EVIDENCE_DIR（默认 exe 旁 verification-ux/）。
// 私有成员经反射访问（先例：LaunchCliHarness 调 LaunchCli）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CodeUsageMonit {
    internal static class RevealHarness {
        public static bool Pending;
        private static void Require(bool value) { if (!value) throw new Exception("Assertion failed"); }
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

        public static void Run(MonitorPanel ui) {
            _ = Dispatcher.UIThread.InvokeAsync(async () => {
                int failures = 0;
                try {
                    await Task.Delay(600); // Start 由 App 以 ApplicationIdle 投递，先让它落地
                    var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                    var window = (Window)typeof(MonitorPanel).GetField("window", flags).GetValue(ui);
                    var render = (Action)typeof(MonitorPanel).GetMethod("Render", flags).CreateDelegate(typeof(Action), ui);
                    var toggle = (Action)typeof(MonitorPanel).GetMethod("ToggleFromTray", flags).CreateDelegate(typeof(Action), ui);
                    var states = (Dictionary<string, ProviderState>)typeof(MonitorPanel).GetField("states", flags).GetValue(ui);
                    Require(window != null && states != null);
                    // demo-data 里可能残留上次手动演示的设置（DisplaySize=small、失焦自动
                    // 收起），会改变渲染分支并让断言随终端抢焦点而抖动：强制完整面板 + 关闭
                    // 自动收起，保证 harness 只测它要测的链路。
                    var config = (AppConfig)typeof(MonitorPanel).GetField("config", flags).GetValue(ui);
                    config.HideOnDeactivate = false;
                    typeof(MonitorPanel).GetMethod("SetDisplaySize", flags).Invoke(ui, new object[] { "full" });
                    await Task.Delay(200);
                    Require(window.IsVisible);

                    // ① 隐藏 → Reveal：窗口恢复可见 + 前台激活被执行。
                    window.Hide();
                    await Task.Delay(100);
                    Require(!window.IsVisible);
                    int before = MacApp.ActivationCount;
                    ui.Reveal();
                    await Task.Delay(100);
                    Require(window.IsVisible);
                    Require(MacApp.ActivationCount == before + 1);
                    Console.WriteLine("step1: hidden -> Reveal -> IsVisible=" + window.IsVisible + ", foreground activations +" + (MacApp.ActivationCount - before));

                    // ② 托盘 toggle：活跃且可见 → 收起；隐藏 → 显示。IsActive 依赖会话焦点
                    //（用户正在别的 Space/应用里时拿不到焦点），拿不到就跳过而非误报。
                    window.Activate();
                    MacApp.ActivateForeground();
                    await Task.Delay(400);
                    if (!window.IsActive) {
                        Console.WriteLine("step2: skipped (window could not take focus; another app/Space holds it)");
                    } else {
                        Require(window.IsVisible);
                        toggle();
                        await Task.Delay(100);
                        Require(!window.IsVisible);
                        toggle();
                        await Task.Delay(100);
                        Require(window.IsVisible);
                        Console.WriteLine("step2: tray toggle hides the active panel and reveals it again");
                    }

                    // ③ DeepSeek Harness ready 态（无余额）→ 概览卡片含内联 KeyBox。
                    typeof(MonitorPanel).GetField("selected", flags).SetValue(ui, "overview");
                    states["deepseek"] = new ProviderState { Id = "deepseek", Status = "ready", Message = "仅本机用量", Plan = "DeepSeek Harness", LastSuccess = DateTime.UtcNow.ToString("o") };
                    render();
                    await Task.Delay(100);
                    Require(FindKeyBox(window, "DeepSeek") != null);
                    KeyBox cardBox = FindKeyBox(window, "DeepSeek");
                    Require(cardBox.Bounds.Height >= 24, "card key box collapses to zero height (StyleKey regression)");
                    Console.WriteLine("step3: card keybox bounds=" + cardBox.Bounds.Width + "x" + cardBox.Bounds.Height);
                    Console.WriteLine("step3: ready DeepSeek card renders the inline API key box");

                    // ④ 头部时间段 Segmented（fix6 #4）：必须完整落在面板宽度内——
                    // 曾按内容计量把末段和复制按钮挤出窄窗口右缘。
                    var segments = FindByName(window, I18n.T("选择时间段"));
                    Require(segments != null);
                    double panelWidth = window.Bounds.Width;
                    Require(segments.Bounds.Left >= 0 && segments.Bounds.Right <= panelWidth - 12);
                    Console.WriteLine("step4: range segmented stays inside the panel (right=" + Math.Round(segments.Bounds.Right, 1) + " of " + Math.Round(panelWidth, 1) + ")");

                    // ⑤ 自定义平台简单表单（fix6 #5 + 返工）：生成必须过 CustomProviders.Parse，
                    // TryRead 只对能无损表达回表单的 JSON 放行；再驱动真实编辑器验证两态状态机——
                    // mode 必须跟随分段切换（保存取的是三格生成的定义，不是打开时求值的旧模式），
                    // 高级→简单对无法回读的 JSON 先提示回弹、再次点击才确认重置，显式 id 不丢。
                    var simple = typeof(MonitorPanel).GetNestedType("SimpleCustom", BindingFlags.NonPublic | BindingFlags.Static);
                    Require(simple != null);
                    var toJson = (Func<string, string, string, string, string>)Delegate.CreateDelegate(typeof(Func<string, string, string, string, string>), simple.GetMethod("ToJson", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static));
                    string url5 = "https://gw.example.com/api/usage";
                    string generated = toJson("三格网关", url5, "#8FD1E8", "");
                    CustomProvider parsedSimple = CustomProviders.Parse(generated); // 抛异常即失败
                    var tryRead = simple.GetMethod("TryRead", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                    var outArgs = new object[] { parsedSimple, null, null, null, null };
                    Require((bool)tryRead.Invoke(null, outArgs) && (string)outArgs[1] == "三格网关" && (string)outArgs[2] == url5 && (string)outArgs[3] == "#8FD1E8" && (string)outArgs[4] == "");
                    CustomProvider windowed = CustomProviders.Parse("{\"name\":\"映射平台\",\"url\":\"https://a.example.com/c\",\"auth\":\"bearer\",\"windows\":[{\"label\":\"每日\",\"usedPercent\":\"data.p\"}]}");
                    Require(!(bool)tryRead.Invoke(null, new object[] { windowed, null, null, null, null }));
                    // 驱动真实编辑器（demo 数据目录隔离；demo 预置的「示例网关」与本名不冲突）。
                    var member = BindingFlags.NonPublic | BindingFlags.Instance;
                    var openEditor = (Action)(() => typeof(MonitorPanel).GetMethod("OpenCustomEditor", member).Invoke(ui, new object[] { null }));
                    Func<Visual> editorRoot = () => (Visual)typeof(MonitorPanel).GetField("customEditor", member).GetValue(ui);
                    typeof(MonitorPanel).GetMethod("OpenSettings", member).Invoke(ui, null);
                    await Task.Delay(200);
                    openEditor(); await Task.Delay(200);
                    var editor5 = editorRoot(); Require(editor5 != null);
                    var json5 = (TextBox)FindByName(editor5, I18n.T("平台定义 JSON"));
                    Require(json5 != null && json5.Text == CustomProviders.Template); // 新建默认高级态
                    // 主断言（返工缺陷路径）：新建 → 切「简单」→ 填三格 → 保存，保存得到的是
                    // 用户三格生成的定义。旧实现 mode 只在打开时求值一次，这里会保存 Template。
                    ((RadioButton)FindByName(editor5, I18n.T("简单"))).IsChecked = true;
                    ((TextBox)FindByName(editor5, I18n.T("自定义平台名称"))).Text = "三格网关";
                    ((TextBox)FindByName(editor5, I18n.T("自定义平台接口地址"))).Text = url5;
                    ((TextBox)FindByName(editor5, I18n.T("自定义平台颜色"))).Text = "#8FD1E8";
                    var save5 = (Button)FindByName(editor5, I18n.T("保存自定义平台")); Require(save5 != null);
                    typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(save5, null);
                    await Task.Delay(200);
                    Require(editorRoot() == null); // 编辑器已关闭 = 保存流程走完
                    CustomProvider saved = ProviderCatalog.Custom.Values.FirstOrDefault(p => p.Name == "三格网关");
                    Require(saved != null && saved.Source == generated && saved.Url == url5 && saved.Color == "#8FD1E8");
                    // 高级 → 简单（无法回读）：第一次点击只提示并回弹到「高级」，JSON 原样保留；
                    // 再次点击才确认重置为简单表单，可读出的名称/地址保留。
                    openEditor(); await Task.Delay(200);
                    editor5 = editorRoot(); Require(editor5 != null);
                    json5 = (TextBox)FindByName(editor5, I18n.T("平台定义 JSON"));
                    string windowedText = "{\"name\":\"映射平台\",\"url\":\"https://a.example.com/c\",\"auth\":\"bearer\",\"windows\":[{\"label\":\"每日\",\"usedPercent\":\"data.p\"}]}";
                    json5.Text = windowedText;
                    var simpleRadio = (RadioButton)FindByName(editor5, I18n.T("简单"));
                    simpleRadio.IsChecked = true; await Task.Delay(100);
                    Require(json5.IsVisible && json5.Text == windowedText);
                    Require(((RadioButton)FindByName(editor5, I18n.T("高级"))).IsChecked == true && simpleRadio.IsChecked != true); // 回弹
                    simpleRadio.IsChecked = true; await Task.Delay(100); // 第二次点击：确认重置
                    var name5 = (TextBox)FindByName(editor5, I18n.T("自定义平台名称"));
                    Require(name5.IsVisible && name5.Text == "映射平台");
                    // 显式 id（P2）：带 id 的简单形态回读后物化回 JSON 仍带 id，保存不再丢 id。
                    var advancedRadio = (RadioButton)FindByName(editor5, I18n.T("高级"));
                    advancedRadio.IsChecked = true; await Task.Delay(100); // 物化当前三格（无 id）
                    json5.Text = toJson("带id平台", "https://b.example.com/c", "", "my-gateway");
                    simpleRadio.IsChecked = true; await Task.Delay(100); // 回读三格 + 显式 id
                    advancedRadio.IsChecked = true; await Task.Delay(100); // 再物化
                    Require(json5.Text.Contains("\"id\": \"my-gateway\"") && json5.Text.Contains("带id平台"));
                    Console.WriteLine("step5: simple-form JSON parses, round-trips; editor save commits the three fields; advanced->simple vetoes once then resets; explicit id survives");

                    // ── ux-overhaul：hero / 概览排序 / 认证页（真实渲染断言 + 截图）──
                    string evidence = Environment.GetEnvironmentVariable("CU_UX_EVIDENCE_DIR");
                    if (String.IsNullOrEmpty(evidence)) evidence = Path.Combine(AppContext.BaseDirectory, "verification-ux");
                    Directory.CreateDirectory(evidence);
                    // 收尾 ⑤ 遗留的编辑器与设置窗口，回到完整面板概览。
                    typeof(MonitorPanel).GetMethod("CloseCustomEditor", member).Invoke(ui, null);
                    typeof(MonitorPanel).GetMethod("CloseSettings", member).Invoke(ui, null);
                    typeof(MonitorPanel).GetField("selected", flags).SetValue(ui, "overview");
                    render();
                    await Task.Delay(200);

                    // ⑥ hero：大数字 = Token 总数；API 等价费用降为小字；分组切换就地重建 hero。
                    var heroCard = (Border)typeof(MonitorPanel).GetField("heroCard", flags).GetValue(ui);
                    Require(heroCard != null && heroCard.Child != null);
                    List<TextBlock> heroTexts = FindText(heroCard).ToList();
                    Require(heroTexts.Any(t => (t.Text ?? "").Contains("Token 用量")), "hero title still cost-based");
                    TextBlock costLine = heroTexts.FirstOrDefault(t => (t.Text ?? "").StartsWith("API 等价费用"));
                    Require(costLine != null && Math.Round(costLine.FontSize) <= 12, "cost line missing or not demoted");
                    Require(!heroTexts.Any(t => Math.Round(t.FontSize) >= 20 && (t.Text ?? "").StartsWith("$")), "cost still the hero figure");
                    TextBlock big = heroTexts.FirstOrDefault(t => t.Inlines != null && t.Inlines.OfType<Run>().Any(r => Math.Round(r.FontSize) == 26));
                    string bigText = big == null ? "" : String.Concat(big.Inlines.OfType<Run>().Select(r => r.Text ?? ""));
                    Require(big != null && bigText.EndsWith(" Token"), "big number is not the token total");
                    Console.WriteLine("step6: hero big number = \"" + bigText + "\"; cost demoted to \"" + costLine.Text + "\"");
                    Capture(window, Path.Combine(evidence, "10-hero-token-overview.png"));
                    var byTools = (RadioButton)FindByName(heroCard, I18n.T("按工具"));
                    var byModel = (RadioButton)FindByName(heroCard, I18n.T("按模型"));
                    Require(byTools != null && byModel != null, "hero group toggle missing");
                    Require(byTools.IsChecked == true, "default grouping should be by tools");
                    object heroChildBefore = heroCard.Child;
                    byModel.IsChecked = true;   // OnChecked → heroGroup=model → hero 就地重建
                    await Task.Delay(150);
                    Require((string)typeof(MonitorPanel).GetField("heroGroup", flags).GetValue(ui) == "model", "heroGroup not remembered");
                    Require(heroCard.Child != null && heroCard.Child != heroChildBefore, "hero not rebuilt on group change");
                    Require(FindText(heroCard).Any(t => (t.Text ?? "") == "模型"), "composition list not grouped by model");
                    Console.WriteLine("step6b: group switch to models rebuilds the hero in place");
                    Capture(window, Path.Combine(evidence, "11-hero-model-group.png"));
                    var byToolsAgain = (RadioButton)FindByName(heroCard, I18n.T("按工具"));
                    byToolsAgain.IsChecked = true;   // 还原为按工具
                    await Task.Delay(120);

                    // ⑦ 概览平台卡顺序 = Urgent（ProviderOrder）：有读数按最紧剩余%在前，
                    // ready 无读数随后，其余沉底；不过滤、不隐藏。
                    states["codex"] = new ProviderState { Id = "codex", Status = "ready" }; states["codex"].Quotas.Add(new Quota { Label = "每日", Used = 95 });     // 剩余 5%
                    states["claude"] = new ProviderState { Id = "claude", Status = "ready" }; states["claude"].Quotas.Add(new Quota { Label = "每周", Used = 20 });  // 剩余 80%
                    states["deepseek"] = new ProviderState { Id = "deepseek", Status = "ready", Message = "仅本机用量" };   // ready 无读数
                    states["cursor"] = new ProviderState { Id = "cursor", Status = "error" };
                    states["antigravity"] = new ProviderState { Id = "antigravity", Status = "loading" };
                    states["grok"] = new ProviderState { Id = "grok", Status = "setup" };
                    render();
                    await Task.Delay(200);
                    var urgent = (Func<IEnumerable<string>, IEnumerable<string>>)Delegate.CreateDelegate(typeof(Func<IEnumerable<string>, IEnumerable<string>>), ui, typeof(MonitorPanel).GetMethod("Urgent", flags));
                    List<string> orderedIds = urgent(config.Enabled.ToList()).ToList();
                    Require(orderedIds.Count == config.Enabled.Length && orderedIds.Distinct().Count() == orderedIds.Count, "ordering filtered or duplicated providers");
                    // 期望序 = ProviderOrder 同一口径直接推导（demo 各平台读数与状态各异）。
                    List<string> expected = config.Enabled.Select((id, i) => new { id, i, State = states.TryGetValue(id, out ProviderState s) ? s : null })
                        .OrderBy(x => ProviderOrder.Tier(x.State)).ThenBy(x => ProviderOrder.Tightness(x.State)).ThenBy(x => x.i).Select(x => x.id).ToList();
                    Require(orderedIds.SequenceEqual(expected), "urgent order " + String.Join(",", orderedIds) + " != provider-order " + String.Join(",", expected));
                    Require(orderedIds[0] == "codex", "tightest quota (5%) should be first, got " + orderedIds[0]);
                    Require(orderedIds.IndexOf("deepseek") > orderedIds.IndexOf("kimi"), "ready-without-readings should follow quota holders");
                    // DOM 顺序核对：卡片头部按钮（“<名> 详情”）在可视树里的先后 = Urgent 顺序。
                    var body = (StackPanel)typeof(MonitorPanel).GetField("body", flags).GetValue(ui);
                    List<string> domOrder = FindButtons(body).Select(b => AutomationProperties.GetName(b) ?? "").Where(n => n.EndsWith(" 详情", StringComparison.Ordinal))
                        .Select(n => n.Substring(0, n.Length - 3)).Select(n => ProviderCatalog.All.FirstOrDefault(id => ProviderCatalog.Name(id) == n)).Where(id => id != null).ToList();
                    Require(domOrder.Count == orderedIds.Count && domOrder.SequenceEqual(orderedIds), "DOM card order " + String.Join(",", domOrder) + " != urgent " + String.Join(",", orderedIds));
                    Console.WriteLine("step7: overview cards render in ProviderOrder: " + String.Join(" > ", domOrder));
                    Capture(window, Path.Combine(evidence, "12-overview-ordered-cards.png"));

                    // ⑧ 认证页：顶部「本机检测到的账户」区（demo 下是提示行）+ 4 个密钥平台
                    // KeyBox 带占位符（demo 中禁用；非 demo 的启用断言在无头 UiSelfTests）。
                    typeof(MonitorPanel).GetField("settingsTab", flags).SetValue(ui, "accounts");
                    typeof(MonitorPanel).GetMethod("OpenSettings", member).Invoke(ui, null);
                    await Task.Delay(300);
                    var settingsWin = (Window)typeof(MonitorPanel).GetField("settingsWindow", flags).GetValue(ui);
                    Require(settingsWin != null);
                    Require(FindText(settingsWin).Any(t => (t.Text ?? "").Contains("本机检测到的账户")), "detected-accounts section missing");
                    Require(FindText(settingsWin).Any(t => (t.Text ?? "").Contains("演示模式不读取本机凭据")), "demo guard hint missing");
                    foreach (string id in new[] { "deepseek", "kimi", "opencode", "zcode" }) {
                        KeyBox box = FindKeyBox(settingsWin, ProviderCatalog.Name(id));
                        Require(box != null, "key box missing for " + id);
                        Require(!box.IsEnabled, "demo key box should be disabled");
                        Require((box.PlaceholderText ?? "") == I18n.T("粘贴 API Key"), "placeholder missing on " + id);
                        Require(box.Bounds.Height >= 24, id + " key box collapses to zero height (StyleKey regression)");
                        Console.WriteLine("keybox " + id + ": bounds=" + box.Bounds.Width + "x" + box.Bounds.Height + " visible=" + box.IsVisible + " opacity=" + box.Opacity);
                    }
                    Console.WriteLine("step8: accounts page shows the detected-accounts section; four key boxes carry placeholders");
                    Capture(settingsWin, Path.Combine(evidence, "13-settings-detected-accounts.png"));
                    typeof(MonitorPanel).GetMethod("CloseSettings", member).Invoke(ui, null);
                    await Task.Delay(150);

                    // ⑨ 紧凑 Large 概览（需求1 紧凑侧同步）：大数字 = Token，费用降为小字副行。
                    typeof(MonitorPanel).GetMethod("SetDisplaySize", flags).Invoke(ui, new object[] { "large" });
                    await Task.Delay(400);
                    var compactRoot = (Border)typeof(MonitorPanel).GetField("compactRoot", flags).GetValue(ui);
                    Require(compactRoot != null && compactRoot.IsVisible, "compact root not visible");
                    List<TextBlock> compactTexts = FindText(compactRoot).ToList();
                    Require(compactTexts.Any(t => (t.Text ?? "") == "Token 用量"), "compact large hero is still cost-first");
                    Require(compactTexts.Any(t => (t.Text ?? "").Contains("API 等价费用")), "compact large lost the cost line");
                    Console.WriteLine("step9: compact large overview leads with tokens; cost demoted to the small line");
                    Capture(window, Path.Combine(evidence, "14-compact-large-overview.png"));
                    typeof(MonitorPanel).GetMethod("SetDisplaySize", flags).Invoke(ui, new object[] { "full" });
                    await Task.Delay(200);
                    Console.WriteLine("PASSED: reveal/foreground-activation chain, inline DeepSeek key card, range segmented bounds, custom editor two-mode state machine, hero token figure with group switch, provider order, detected-accounts section");
                } catch (Exception e) {
                    failures++;
                    Exception inner = e.InnerException ?? e;   // 反射调用会把真实异常包进 TargetInvocationException
                    Console.Error.WriteLine("FAILED: " + inner.Message + "\n" + inner.StackTrace);
                }
                Environment.ExitCode = failures == 0 ? 0 : 1;
                ui.Quit(); // 收尾走正常退出路径（托盘/套接字/几何全部清理）
            }, DispatcherPriority.Background);
        }
        // 深度遍历可视树找 AutomationProperties.Name 含关键字的 KeyBox（KeyConnect 命名 “<平台> API Key”）。
        private static KeyBox FindKeyBox(Visual root, string namePart) {
            if (root is KeyBox box && (AutomationProperties.GetName(box) ?? "").Contains(namePart)) return box;
            foreach (Visual child in root.GetVisualChildren()) {
                KeyBox found = FindKeyBox(child, namePart);
                if (found != null) return found;
            }
            return null;
        }
        // 深度遍历可视树找 AutomationProperties.Name 相等的控件（时间段 Segmented 等）。
        private static Visual FindByName(Visual root, string name) {
            if (root is Control control && AutomationProperties.GetName(control) == name) return control;
            foreach (Visual child in root.GetVisualChildren()) {
                Visual found = FindByName(child, name);
                if (found != null) return found;
            }
            return null;
        }
        // 深度遍历收集文本（hero 断言用）：Inlines 与 Text 都是实时的。
        private static System.Collections.Generic.IEnumerable<TextBlock> FindText(Visual root) {
            if (root is TextBlock text) yield return text;
            foreach (Visual child in root.GetVisualChildren()) foreach (TextBlock found in FindText(child)) yield return found;
        }
        // 深度遍历收集按钮（卡片顺序断言用）：可视树先后 = 渲染顺序。
        private static System.Collections.Generic.IEnumerable<Button> FindButtons(Visual root) {
            if (root is Button button) yield return button;
            foreach (Visual child in root.GetVisualChildren()) foreach (Button found in FindButtons(child)) yield return found;
        }
        // 窗口内容截图：进程内离屏渲染视觉树存 PNG（不拉起外部进程、不需要屏幕录制
        // 权限；不含系统毛玻璃背板，断言对象是窗口内容）。失败只打日志：
        // 截图是证据不是断言，harness 结论只取决于 Require。
        private static void Capture(Window window, string path) {
            try {
                double scaling = 1;
                try { Avalonia.Platform.Screen screen = window.Screens.ScreenFromWindow(window); if (screen != null) scaling = screen.Scaling; } catch { }
                var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Round(window.Bounds.Width * scaling)), Math.Max(1, (int)Math.Round(window.Bounds.Height * scaling))), new Vector(96 * scaling, 96 * scaling));
                bitmap.Render(window);
                bitmap.Save(path);
                Console.WriteLine("captured: " + path + (File.Exists(path) ? "" : " (save failed?)"));
            } catch (Exception e) { Console.WriteLine("capture failed: " + e.Message); }
        }
    }
}
