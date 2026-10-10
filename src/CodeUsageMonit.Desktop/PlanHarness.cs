// 套餐归属真机 harness（仅 --plan-harness 进入，生产路径不触及）。真实数据目录
// （CODEUSAGEMONIT_DATA 指向的副本）+ 真实窗口：等首轮扫描把 provider_id 写进 ZCode
// 小时索引后，断言概览套餐卡按归属渲染——
//   ① Kimi 卡今日 Token 非零（本会话走 Kimi 套餐的用量经 ZCode 日志归属到 Kimi 卡）；
//   ② Kimi 卡「本机用量」小节含满额推算行（已用 · 满额约 / 未使用），且逐窗口独立
//      成行（不再 join 成一行被截断）；
//   ③ Kimi / ZCode 卡脚注右侧显示归属输出速度（t/s）；
//   ④ ZCode 卡模型区只含 GLM 模型（kimi/mimo/step 套餐的行不落在 ZCode 卡）。
// 截图与结果落 CU_PLAN_EVIDENCE_DIR（默认 exe 旁 verification-plan/）；失败经退出码暴露。
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CodeUsageMonit {
    internal static class PlanHarness {
        private static readonly System.Collections.Generic.List<string> Results = new System.Collections.Generic.List<string>();
        private static BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
        private static void Require(bool value, string message) {
            Results.Add((value ? "PASS " : "FAIL ") + message);
            if (!value) throw new Exception("Assertion failed: " + message);
        }
        private static void Log(string line) { Results.Add("NOTE " + line); Console.WriteLine(line); }

        public static void Run(MonitorPanel ui) {
            _ = Dispatcher.UIThread.InvokeAsync(async () => {
                int failures = 0;
                try {
                    var window = (Window)typeof(MonitorPanel).GetField("window", Flags).GetValue(ui);
                    Require(window != null, "the panel window exists");
                    var config = (AppConfig)typeof(MonitorPanel).GetField("config", Flags).GetValue(ui);
                    config.HideOnDeactivate = false;
                    typeof(MonitorPanel).GetMethod("SetDisplaySize", Flags).Invoke(ui, new object[] { "full" });
                    window.Position = new PixelPoint(60, 60); window.Width = 520; window.Height = 900;
                    await Task.Delay(300);

                    string evidence = Environment.GetEnvironmentVariable("CU_PLAN_EVIDENCE_DIR");
                    if (String.IsNullOrEmpty(evidence)) evidence = Path.Combine(AppContext.BaseDirectory, "verification-plan");
                    Directory.CreateDirectory(evidence);

                    // 等首轮扫描（真实副本里的旧索引没有 provider_id，必须等 ScanHistory 整库
                    // 重建）与额度刷新落地：ZCode 索引出现非空 provider、且扫描/刷新都已收尾。
                    bool ready = false; DateTime deadline = DateTime.UtcNow.AddSeconds(240);
                    while (DateTime.UtcNow < deadline) {
                        await Task.Delay(2000);
                        bool scanning = (bool)typeof(MonitorPanel).GetField("scanning", Flags).GetValue(ui);
                        bool refreshing = (bool)typeof(MonitorPanel).GetField("refreshing", Flags).GetValue(ui);
                        var indexes = (System.Collections.Generic.Dictionary<string, LogIndex>)typeof(MonitorPanel).GetField("usageIndexes", Flags).GetValue(ui);
                        LogIndex zcode; bool zcodeProvider = indexes != null && indexes.TryGetValue("zcode", out zcode) && zcode != null && zcode.Entries().Any(e => (e.Provider ?? "").Length > 0);
                        if (!scanning && !refreshing && zcodeProvider) { ready = true; break; }
                    }
                    Require(ready, "the first scan rebuilt the zcode index with provider_id keys within the deadline");
                    typeof(MonitorPanel).GetField("selected", Flags).SetValue(ui, "overview");
                    ((Action)typeof(MonitorPanel).GetMethod("Render", Flags).CreateDelegate(typeof(Action), ui))();
                    await Task.Delay(400);

                    var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "BodyScroll");
                    var body = (StackPanel)typeof(MonitorPanel).GetField("body", Flags).GetValue(ui);
                    scroll.Offset = new Vector(0, 0);
                    await Task.Delay(150);

                    // ① Kimi 卡：今日 Token 非零 + 满额推算行 + k3-256k 模型行；
                    // 逐窗口一行（两行「已用 · 满额约」完整成行，不再 join 成一行被截断），
                    // 脚注右侧显示归属速度（t/s）。
                    await CheckCard(window, scroll, body, "kimi", new Func<string, bool>[] {
                        text => Regex.IsMatch(FindToday(text), @"今日 [^0][^ ]* Token"),
                        text => text.Contains("本机用量") && (text.Contains("已用 ") || text.Contains("未使用")) && text.Contains("满额约"),
                        text => text.Contains("k3-256k"),
                        text => WindowLines(text).Length >= 2 && WindowLines(text).All(l => !Regex.IsMatch(l, "(已用|未使用).* · (5 小时|每周|月度)")),
                        text => WindowLines(text).Where(l => l.Contains("满额约")).All(l => l.EndsWith("tok", StringComparison.Ordinal) || l.EndsWith("满额约 —", StringComparison.Ordinal)),
                        text => Regex.IsMatch(text, "[0-9]+(\\.[0-9]+)? t/s"),
                    }, Path.Combine(evidence, "kimi-card.png"));
                    // ② ZCode 卡：模型区只含 GLM —— k3-256k / mimo / step-5 行不得出现；
                    // 窗口估算同样逐窗口一行、脚注同样有归属速度。
                    await CheckCard(window, scroll, body, "zcode", new Func<string, bool>[] {
                        text => text.Contains("本机用量"),
                        text => text.Contains("GLM"),
                        text => !text.Contains("k3-256k") && !Regex.IsMatch(text, "mimo", RegexOptions.IgnoreCase) && !Regex.IsMatch(text, "step-5", RegexOptions.IgnoreCase),
                        text => WindowLines(text).Length >= 1 && WindowLines(text).All(l => !Regex.IsMatch(l, "(已用|未使用).* · (5 小时|每周|月度)")),
                        text => Regex.IsMatch(text, "[0-9]+(\\.[0-9]+)? t/s"),
                    }, Path.Combine(evidence, "zcode-card.png"));

                    Console.WriteLine("PLAN RESULT:\n" + String.Join("\n", Results));
                    File.WriteAllLines(Path.Combine(evidence, "results.txt"), Results);
                } catch (Exception e) {
                    failures++;
                    Exception inner = e.InnerException ?? e;
                    Console.Error.WriteLine("FAILED: " + inner.Message + "\n" + inner.StackTrace);
                    Results.Add("EXCEPTION " + inner.Message);
                    try {
                        string evidence = Environment.GetEnvironmentVariable("CU_PLAN_EVIDENCE_DIR") ?? Path.Combine(AppContext.BaseDirectory, "verification-plan");
                        Directory.CreateDirectory(evidence);
                        File.WriteAllLines(Path.Combine(evidence, "results.txt"), Results);
                    } catch { }
                }
                Environment.ExitCode = failures == 0 ? 0 : 1;
                ui.Quit();
            }, DispatcherPriority.Background);
        }

        // 找到平台卡（头按钮 → 上溯圆角卡片 Border），滚到可见，断言卡片文本，截图。
        private static async Task CheckCard(Window window, ScrollViewer scroll, StackPanel body, string id, Func<string, bool>[] asserts, string capture) {
            Button header = FindButton(body, ProviderCatalog.Name(id) + " 详情");
            Require(header != null, "the " + id + " overview card must exist");
            Border card = header.GetVisualAncestors().OfType<Border>().First(b => b.CornerRadius.TopLeft == 10);
            Point at = ToWindow(window, header);
            scroll.Offset = new Vector(0, scroll.Offset.Y + at.Y - 140);
            await Task.Delay(300);
            string text = String.Join("\n", FindText(card).Select(t => t.Text ?? ""));
            Log("card=" + id + " today=[" + FindToday(text) + "] models=[" + String.Join(", ", FindText(card).Select(t => t.Text ?? "").Where(IsModelRow).Distinct()) + "]");
            for (int i = 0; i < asserts.Length; i++) {
                bool ok = asserts[i](text);
                Require(ok, id + " card assertion #" + i + (ok ? " ok" : " failed; card text:\n" + text));
            }
            Capture(window, capture);
        }
        private static string FindToday(string text) {
            foreach (Match m in Regex.Matches(text, "今日 [0-9A-Za-z.≥—-]+ Token · 30 天 [0-9A-Za-z.≥—-]+")) return m.Value;
            return "";
        }
        // 窗口估算行（FindText 的每个 TextBlock 是 \n 分隔的一行）：订阅窗口估算现在
        // 逐窗口独立成行；若还有「5 小时 … · 每周 …」式的 join 行即回归。
        private static string[] WindowLines(string text) {
            return text.Split('\n').Where(l => (l.StartsWith("5 小时 ", StringComparison.Ordinal) || l.StartsWith("每周 ", StringComparison.Ordinal) || l.StartsWith("月度 ", StringComparison.Ordinal))
                && (l.Contains("已用") || l.Contains("未使用"))).ToArray();
        }
        // 模型行文本 = LocalModelRow 的模型名（截图断言用的粗筛：排除数字/费用类的短行）。
        private static bool IsModelRow(string s) { return s.Length > 2 && s.IndexOf(' ') < 0 && !s.StartsWith("+") && !s.EndsWith("Token") && !s.EndsWith("tok") && !s.Contains("·") && !Regex.IsMatch(s, "^[0-9.,KMB≥—$]+$"); }
        private static Point ToWindow(Visual root, Visual visual) {
            Point? mapped = visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), root);
            return mapped ?? new Point(10, 10);
        }
        private static Button FindButton(Visual root, string name) {
            if (root is Button b && AutomationProperties.GetName(b) == name) return b;
            foreach (Visual child in root.GetVisualChildren()) { Button found = FindButton(child, name); if (found != null) return found; }
            return null;
        }
        private static System.Collections.Generic.IEnumerable<TextBlock> FindText(Visual root) {
            if (root is TextBlock text) yield return text;
            foreach (Visual child in root.GetVisualChildren()) foreach (TextBlock found in FindText(child)) yield return found;
        }
        // 窗口内容截图（RsmHarness.Capture 同款：进程内离屏渲染，不需要屏幕录制权限）。
        private static void Capture(Window window, string path) {
            try {
                double scaling = 1;
                try { Avalonia.Platform.Screen screen = window.Screens.ScreenFromWindow(window); if (screen != null && screen.Scaling > 0) scaling = screen.Scaling; } catch { }
                var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Round(window.Bounds.Width * scaling)), Math.Max(1, (int)Math.Round(window.Bounds.Height * scaling))), new Vector(96 * scaling, 96 * scaling));
                bitmap.Render(window);
                bitmap.Save(path);
                Console.WriteLine("captured: " + path);
            } catch (Exception e) { Console.WriteLine("capture failed: " + e.Message); }
        }
    }
}
