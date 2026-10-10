// 卡片内连接流程：API Key（含区域切换）、GitHub 设备流登录、CLI 登录
// （macOS：osascript 打开 Terminal 执行命令）、桌面应用启动（macOS：/Applications 下的 .app）、
// 自定义平台编辑。渲染期间正在输入密钥时延迟重建（EditingKey/renderDeferred）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaColor = Avalonia.Media.Color;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Automation;
using Avalonia.Controls.Documents;

namespace CodeUsageMonit {
    // Connecting a provider from its own card (overview and detail page): API keys are
    // entered inline, GitHub uses the device login, CLI logins open a terminal, desktop
    // apps are launched, custom providers open their editor. Settings keep the same
    // options; both write the same saved keys (macOS 钥匙串) and config.
    public sealed partial class MonitorPanel {
        private static readonly string[] KeyProviders = { "deepseek", "kimi", "opencode", "zcode", "mimo", "stepfun" };
        // Typed-but-unsaved keys and regions survive page rebuilds (refresh, clock tick).
        private readonly Dictionary<string, string> keyDrafts = new Dictionary<string, string>(), regionDrafts = new Dictionary<string, string>(), connectNotes = new Dictionary<string, string>();
        private sealed class CopilotFlow { public ProviderService.DeviceLogin Pending; public bool Cancelled; }
        private CopilotFlow copilotFlow;
        private bool renderDeferred;

        // While a key is being typed into a card, rebuilding the page would drop focus and
        // the half-typed key; Render() waits until the box loses focus.
        private bool EditingKey() {
            var focused = FocusedElement();
            return focused is KeyBox && (IsVisualAncestor(bodyScroll, focused) || IsVisualAncestor(compactRoot, focused));
        }
        private static bool IsVisualAncestor(Visual ancestor, Visual node) {
            while (node != null) { if (node == ancestor) return true; node = (Visual)node.GetVisualParent(); }
            return false;
        }
        private void KeyBoxLostFocus(object sender, RoutedEventArgs e) {
            // Focus moving to a button in the panel: its click renders; rebuilding now would
            // swallow that click.
            if (renderDeferred && !(sender is Button)) Dispatcher.UIThread.Post(() => { if (renderDeferred) Render(); });
        }

        private Control ConnectPanel(ProviderState state) { return ConnectPanel(state, false); }
        // narrow: the compact sizes stack the key box above its button.
        private Control ConnectPanel(ProviderState state, bool narrow) {
            string id = state.Id;
            var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            if (ProviderCatalog.Custom.ContainsKey(id)) CustomConnect(panel, id);
            else if (KeyProviders.Contains(id)) KeyConnect(panel, state, narrow);
            else if (id == "copilot") CopilotConnect(panel, state);
            else if (id == "codex" || id == "claude" || id == "grok") CliConnect(panel, id);
            else if (id == "cursor" || id == "antigravity") AppConnect(panel, id);
            string note;
            if (connectNotes.TryGetValue(id, out note) && note.Length > 0) { TextBlock line = Hint(note, 6); line.Foreground = InkDim; panel.Children.Add(line); }
            return panel;
        }
        private Button ConnectButton(string text, bool primary) {
            return Styled(new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) }, primary ? "primary" : "secondary");
        }
        private Button CheckButton(string id) {
            var check = ConnectButton("我已登录，刷新", false);
            check.Click += delegate { connectNotes.Remove(id); var ignored = RefreshOne(id); };
            return check;
        }
        private static WrapPanel ButtonRow() { return new WrapPanel { Margin = new Thickness(0, 0, 0, 0) }; }

        // ── API key providers ─────────────────────────────────────────────
        private void KeyConnect(StackPanel panel, ProviderState state, bool narrow) {
            string id = state.Id;
            string env = demo ? "" : Store.KeyEnvironmentName(id, config);
            string noun = CredentialNoun(id);
            if (id == "kimi" || id == "zcode" || id == "stepfun") {
                string current; if (!regionDrafts.TryGetValue(id, out current)) current = id == "kimi" ? config.KimiRegion : id == "stepfun" ? config.StepfunRegion : config.ZaiRegion;
                string[][] options = id == "kimi" ? new[] { new[] { "china", "国内" }, new[] { "international", "国际" } } : new[] { new[] { "china", "国内" }, new[] { "global", "国际" } };
                var row = Row(); row.Margin = new Thickness(0, 0, 0, 8);
                AddRow(row, Label("接口区域", 11.5, InkDim), Segmented(options, current, code => regionDrafts[id] = code));
                panel.Children.Add(row);
            }
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (narrow) { line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }
            var box = new KeyBox { IsEnabled = !demo, VerticalAlignment = VerticalAlignment.Center, PlaceholderText = I18n.T("粘贴 " + noun) };
            ToolTip.SetTip(box, state.Status == "expired" ? "粘贴新的 " + noun : "粘贴 " + noun);
            AutomationProperties.SetName(box, ProviderCatalog.Name(id) + " " + noun);
            string draft; if (keyDrafts.TryGetValue(id, out draft)) box.Text = draft;
            box.TextChanged += delegate { keyDrafts[id] = box.Text; };
            box.LostFocus += KeyBoxLostFocus;
            box.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; SaveKeyFromCard(id, box.Text); } };
            line.Children.Add(box);
            if (narrow) panel.Children.Add(Label(state.Status == "expired" ? "粘贴新的 " + noun : "粘贴 " + noun, 10.5, InkDim));
            var save = Styled(new Button { Content = "保存并连接", IsEnabled = !demo, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch }, "primary");
            save.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(8, 0, 0, 0);
            AutomationProperties.SetName(save, ProviderCatalog.Name(id) + " 保存并连接");
            save.Click += delegate { SaveKeyFromCard(id, box.Text); };
            if (narrow) { Grid.SetColumnSpan(box, 2); Grid.SetRow(save, 1); } else Grid.SetColumn(save, 1);
            line.Children.Add(save);
            panel.Children.Add(line);
            if (id == "kimi" || id == "zcode") panel.Children.Add(LocalImportRow(id));
            else if (id == "mimo" || id == "stepfun") panel.Children.Add(ConsoleLinkRow(id));
            panel.Children.Add(Hint(demo ? "演示模式不读取或保存密钥。" : env.Length > 0 ? "正在使用环境变量 " + env + "（优先于这里保存的密钥）。" : "以 macOS 钥匙串加密保存在本机（data 目录只存引用），只有当前用户能解密。", 6));
        }
        // MiMo / StepFun 卡片：没有本机 CLI 登录可导入，只放一个打开官方控制台的按钮
        //（Cookie / Oasis-Token 都在控制台页面上获取）。
        private Control ConsoleLinkRow(string id) {
            var row = ButtonRow();
            var create = ConnectButton("去官网创建 Key", false);
            AutomationProperties.SetName(create, ProviderCatalog.Name(id) + " 去官网创建 Key");
            create.Click += delegate {
                try { Process.Start(new ProcessStartInfo(ProviderCatalog.ConsoleUrl(id, config)) { UseShellExecute = true }); } catch { }
            };
            row.Children.Add(create);
            return row;
        }
        // ZCode / Kimi 卡片的两个按钮：从本机 CLI 登录凭据一键导入（与 Fetch 的静默回退
        // 同一条路径，成功即经 SetProviderKey 存钥匙串并刷新卡片），以及打开官网创建 Key。
        private Control LocalImportRow(string id) {
            var row = ButtonRow();
            var import = ConnectButton("一键导入本机登录", false);
            import.IsEnabled = !demo;
            AutomationProperties.SetName(import, ProviderCatalog.Name(id) + " 一键导入本机登录");
            import.Click += delegate {
                // 卡片路径：离开聚焦的密钥框让页面重渲染；导入动作本体在 ImportFromLocalCli
                //（认证页凭据面板复用同一动作，那边等待刷新完成后就地更新状态胶囊）。
                if (ImportFromLocalCli(id)) { window.Focus(); var ignored = RefreshOne(id); }
                else { window.Focus(); Render(); }
            };
            var create = ConnectButton("去官网创建 Key", false);
            AutomationProperties.SetName(create, ProviderCatalog.Name(id) + " 去官网创建 Key");
            create.Click += delegate {
                string url = ProviderCatalog.ConsoleUrl(id, config);
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            };
            row.Children.Add(import); row.Children.Add(create);
            return row;
        }
        // 一键导入动作本体（卡片与认证页凭据面板共用）：从本机 CLI 登录凭据导入到活动账户
        //（Fetch 静默回退同口径）。返回是否成功；失败写 connectNotes（卡片展示），刷新由
        // 调用方发起——卡片 fire-and-forget，面板 await 后刷新状态胶囊。
        private bool ImportFromLocalCli(string id) {
            if (demo) return false;
            if (LocalCliCredentials.Import(id, Accounts.ActiveId(config, id)).Length == 0) { connectNotes[id] = "未能从本机登录凭据导入，请填写 API Key。"; return false; }
            keyDrafts.Remove(id); regionDrafts.Remove(id); connectNotes.Remove(id); connectionDetails.Remove(id);
            states[id] = new ProviderState { Id = id, Status = "loading", Message = "正在连接…" };
            return true;
        }
        private void SaveKeyFromCard(string id, string key) {
            if (demo) return;
            key = (key ?? "").Trim();
            string region; bool regionChanged = regionDrafts.TryGetValue(id, out region);
            // Phase 5：卡片输入写当前活动账户的钥匙串键。
            string active = Accounts.ActiveId(config, id);
            bool haveKey = Store.KeyEnvironmentName(id, config).Length > 0 || Store.HasSavedKey(id, active);
            if (key.Length == 0 && !(regionChanged && haveKey)) { connectNotes[id] = "请先粘贴 API Key。"; window.Focus(); Render(); return; }
            try {
                if (regionChanged) { if (id == "kimi") config.KimiRegion = region; else if (id == "zcode") config.ZaiRegion = region; else if (id == "stepfun") config.StepfunRegion = region; Store.Write("settings.json", config); }
                if (key.Length > 0 && !Store.SetProviderKey(id, active, key)) { connectNotes[id] = "密钥未能写入 macOS 钥匙串，保存未完成，请重试。"; window.Focus(); Render(); return; }
            } catch { connectNotes[id] = "未能保存，请检查 data 目录是否可写。"; window.Focus(); Render(); return; }
            keyDrafts.Remove(id); regionDrafts.Remove(id); connectNotes.Remove(id); connectionDetails.Remove(id);
            states[id] = new ProviderState { Id = id, Status = "loading", Message = "正在连接…" };
            window.Focus(); // leave the key box so the page can re-render
            var ignored = RefreshOne(id);
        }

        // ── GitHub Copilot: OAuth device flow ─────────────────────────────
        private void CopilotConnect(StackPanel panel, ProviderState state) {
            CopilotFlow flow = copilotFlow;
            if (flow != null && flow.Pending != null) {
                panel.Children.Add(Hint("在浏览器里输入下面的代码并授权：", 0));
                var code = new TextBox { Text = flow.Pending.UserCode, IsReadOnly = true, FontSize = 22, FontWeight = FontWeight.SemiBold, FontFamily = new FontFamily("Menlo, Monaco, monospace"), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Margin = new Thickness(0, 4, 0, 6), HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 0 };
                AutomationProperties.SetName(code, "GitHub 设备码");
                panel.Children.Add(code);
                var buttons = ButtonRow();
                var open = ConnectButton("复制代码并打开 GitHub", true);
                open.Click += delegate {
                    try { var ignored = SetClipboardText(window, flow.Pending.UserCode); } catch { }
                    try { Process.Start(new ProcessStartInfo(flow.Pending.VerificationUri) { UseShellExecute = true }); } catch { }
                };
                var cancel = Styled(new Button { Content = "取消" }, "link-button");
                cancel.Click += delegate { flow.Cancelled = true; copilotFlow = null; connectNotes["copilot"] = "已取消登录。"; Render(); };
                buttons.Children.Add(open); buttons.Children.Add(cancel); panel.Children.Add(buttons);
                return;
            }
            var row = ButtonRow();
            var login = ConnectButton(state.Status == "expired" ? "重新使用 GitHub 登录" : "使用 GitHub 登录", true);
            login.IsEnabled = !demo && flow == null;
            AutomationProperties.SetName(login, "使用 GitHub 登录");
            login.Click += delegate { var ignored = StartCopilotFlow(); };
            row.Children.Add(login); panel.Children.Add(row);
            panel.Children.Add(Hint(demo ? "演示模式不登录。" : "OAuth 设备码登录，只申请 read:user 权限，用于读取 Copilot 额度。", 6));
        }
        private async Task StartCopilotFlow() {
            if (demo || copilotFlow != null) return;
            var flow = new CopilotFlow(); copilotFlow = flow;
            connectNotes["copilot"] = "正在向 GitHub 申请设备码…"; connectionDetails.Add("copilot"); Render();
            try {
                using (var service = new ProviderService(config)) {
                    flow.Pending = await service.StartCopilotLogin();
                    if (flow.Cancelled) return;
                    connectNotes["copilot"] = "等待你在 GitHub 上授权，完成后会自动连接。"; Render();
                    DateTime until = DateTime.UtcNow.AddSeconds(flow.Pending.ExpiresIn);
                    while (!flow.Cancelled && DateTime.UtcNow < until) {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, flow.Pending.Interval)));
                        if (flow.Cancelled) return;
                        string token = await service.PollCopilotLogin(flow.Pending);
                        if (token == null) continue;
                        Store.SetProviderKey("copilot", token);
                        copilotFlow = null; connectNotes.Remove("copilot"); connectionDetails.Remove("copilot");
                        states["copilot"] = new ProviderState { Id = "copilot", Status = "loading", Message = "正在连接…" };
                        var ignored = RefreshOne("copilot");
                        return;
                    }
                    if (!flow.Cancelled) connectNotes["copilot"] = "设备码已过期，请重新登录。";
                }
            } catch (Exception e) { if (!flow.Cancelled) connectNotes["copilot"] = e is ProviderException ? e.Message : "无法连接 GitHub，请检查网络或代理设置。"; }
            finally { if (copilotFlow == flow) { copilotFlow = null; Render(); } }
        }

        // ── CLI logins: Codex / Claude Code / Grok ────────────────────────
        private void CliConnect(StackPanel panel, string id) {
            string command = id == "codex" ? "codex" : id == "claude" ? "claude" : "grok";
            string product = id == "codex" ? "Codex CLI" : id == "claude" ? "Claude Code" : "Grok Build CLI";
            string path = FindOnPath(command);
            var row = ButtonRow();
            if (path.Length > 0) {
                var login = ConnectButton(id == "claude" ? "在终端打开 Claude Code" : "在终端登录", true);
                login.IsEnabled = !demo;
                AutomationProperties.SetName(login, ProviderCatalog.Name(id) + " 登录");
                login.Click += delegate { connectNotes[id] = LaunchCli(path, id == "claude" ? new string[0] : new[] { "login" }) ? (id == "claude" ? "已打开终端。在 Claude Code 里输入 /login 完成登录，然后点“我已登录，刷新”。" : "已打开终端。按提示在浏览器完成登录，然后点“我已登录，刷新”。") : "无法打开终端。"; Render(); };
                row.Children.Add(login);
            }
            row.Children.Add(CheckButton(id));
            panel.Children.Add(row);
            string hint = demo ? "演示模式不启动登录。" : path.Length == 0 ? "未在 PATH 中找到 " + command + " 命令。请先安装 " + product + (id == "codex" ? "，或在 Codex 应用中登录" : "") + "，然后刷新。"
                : id == "codex" ? "将在新终端运行 codex login。" : id == "claude" ? "将在新终端运行 claude，然后输入 /login。" : "将在新终端运行 grok login。";
            panel.Children.Add(Hint(hint, 6));
        }
        // 用 osascript 在 Terminal.app 开新窗口执行命令，窗口保持可读。
        // Terminal 的 do script 走用户 shell：先按 POSIX 规则对整条命令逐参数 quoting
        // （空格、单引号、$、反引号等按字面传递，见 Shell.Quote / review #8），再对整串做
        // AppleScript 字符串转义（双引号与反斜杠）；参数经 ArgumentList 逐个传递
        // （UseShellExecute=false 时 Arguments 字符串按 Windows 规则解析，单引号不被识别，
        // 脚本会被拆碎）。读取退出码，失败让 UI 显示真实错误。arguments 是数组而非“空格
        // 分隔的串”：按空格 Split 再逐词 Quote 会把含空格的实参拆成多段（P3 复发面），
        // 现在每个元素原样 Quote，调用点显式传数组。
        private static bool LaunchCli(string path, string[] arguments) {
            try {
                string command = Shell.Quote(path);
                foreach (string argument in arguments) command += " " + Shell.Quote(argument);
                string escaped = command.Replace("\\", "\\\\").Replace("\"", "\\\"");
                var info = new ProcessStartInfo("osascript") { UseShellExecute = false, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
                info.ArgumentList.Add("-e");
                info.ArgumentList.Add("tell application \"Terminal\" to do script \"" + escaped + "\"");
                using (var process = Process.Start(info)) {
                    if (process == null) return false;
                    if (!process.WaitForExit(8000)) { try { process.Kill(); } catch { } return false; }
                    return process.ExitCode == 0;
                }
            } catch { return false; }
        }
        // PATH 取当前进程 + 登录 shell（zsh）的环境：安装晚于本程序启动的 CLI 也能找到。
        private static string SearchPath() {
            var parts = new List<string>();
            string value = Environment.GetEnvironmentVariable("PATH");
            if (!String.IsNullOrEmpty(value)) foreach (string part in value.Split(':')) { string p = part.Trim(); if (p.Length > 0 && !parts.Contains(p, StringComparer.Ordinal)) parts.Add(p); }
            foreach (string extra in new[] { "/usr/local/bin", "/opt/homebrew/bin", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/bin") }) if (!parts.Contains(extra, StringComparer.Ordinal)) parts.Add(extra);
            return String.Join(":", parts);
        }
        public static string FindOnPath(string command) {
            var folders = SearchPath().Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries).Select(Environment.ExpandEnvironmentVariables).ToList();
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/bin"));
            foreach (string folder in folders) {
                try { string candidate = Path.Combine(folder.Trim('"'), command); if (File.Exists(candidate)) return candidate; } catch { }
            }
            return "";
        }

        // ── Desktop apps: Cursor / Antigravity ────────────────────────────
        private void AppConnect(StackPanel panel, string id) {
            string name = ProviderCatalog.Name(id);
            string path = id == "antigravity" ? LocalAntigravity.FindApplication() : FindCursor();
            var row = ButtonRow();
            if (path.Length > 0) {
                var open = ConnectButton("打开 " + name, true);
                open.IsEnabled = !demo;
                open.Click += delegate {
                    try { Process.Start(new ProcessStartInfo("open", "\"" + path + "\"") { UseShellExecute = false }); connectNotes[id] = "已打开 " + name + "。登录后" + (id == "antigravity" ? "保持应用运行，" : "") + "点“我已登录，刷新”。"; }
                    catch { connectNotes[id] = "无法启动 " + name + "。"; }
                    Render();
                };
                row.Children.Add(open);
            }
            row.Children.Add(CheckButton(id));
            panel.Children.Add(row);
            panel.Children.Add(Hint(demo ? "演示模式不启动应用。" : path.Length == 0 ? "没有在默认位置找到 " + name + "，请手动打开并登录后刷新。" : "额度读取使用 " + name + " 自己的登录状态，本程序不保存它的密码。", 6));
        }
        private static string FindCursor() {
            return new[] { "/Applications/Cursor.app" }.FirstOrDefault(Directory.Exists) ?? "";
        }

        // ── Custom providers ──────────────────────────────────────────────
        private void CustomConnect(StackPanel panel, string id) {
            var row = ButtonRow();
            var edit = ConnectButton("编辑接口与密钥", true);
            edit.IsEnabled = !demo;
            edit.Click += delegate {
                CustomProvider provider; if (!ProviderCatalog.Custom.TryGetValue(id, out provider)) return;
                OpenSettings(); if (customEditor == null) OpenCustomEditor(provider);
            };
            var retry = ConnectButton("重试", false);
            retry.Click += delegate { var ignored = RefreshOne(id); };
            row.Children.Add(edit); row.Children.Add(retry);
            panel.Children.Add(row);
        }
    }
}
