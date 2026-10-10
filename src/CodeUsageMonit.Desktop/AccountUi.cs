// 多账户（ROADMAP Phase 5）的桌面侧共用件：卡片上的账户标签与一键切换入口、
// 切换动作（落盘 + LimitsRuntime.ResetFor 定向刷新该平台），设置页的账户
// 管理区（活动账户切换、增删账户、密钥输入框绑定活动账户），以及认证页的
// 每供应商折叠面板：折叠头 + 状态胶囊 + key 型「保存并验证」/ CLI 型卡片引导两种正文。
// 额度缓存 / 刷新通道 / 健康状态保持 provider 粒度：切换活动账户就是凭据变更，
// 走既有 RefreshChanged 定向刷新，其余平台的通道与读数不动。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Automation;

namespace CodeUsageMonit {
    public sealed partial class MonitorPanel {
        // 该平台是否配置了多个账户（演示模式恒否：demo 不读也不改账户）。
        private bool MultipleAccounts(string id) { return !demo && Accounts.List(config, id).Count > 1; }
        // 卡片/紧凑页账户行的文本：单账户维持原样（只有 API 返回的邮箱）；多账户时
        // 前缀活动账户显示名，让「当前查的是哪个账户」一眼可读。
        // 遮罩口径（D 期对标）：@ 前只留首字符与尾字符（l***8@gmail.com）；@ 前不足
        // 2 字符时只留首字符 + ***（a***@x.com）。域名不遮。
        private string AccountLabel(string id, string apiAccount) {
            string account = apiAccount ?? "";
            if (config.HideAccounts && account.Contains("@")) {
                int at = account.IndexOf('@');
                string local = account.Substring(0, at);
                account = (local.Length >= 2 ? local.Substring(0, 1) + "***" + local.Substring(local.Length - 1) : local.Substring(0, 1) + "***") + account.Substring(at);
            }
            if (MultipleAccounts(id)) {
                string name = Accounts.DisplayName(config, id, Accounts.ActiveId(config, id));
                account = account.Length > 0 ? name + " · " + account : name;
            }
            return account;
        }
        // 卡片右上一键切换（Identity 与紧凑 Large 页头共用）：多账户时显示活动账户名，
        // 点击循环切到下一个账户并定向刷新该平台。单账户返回 null（不占位）。
        private Control AccountSwitchChip(string id) {
            List<ProviderAccount> list = Accounts.List(config, id);
            if (demo || list.Count < 2) return null;
            string active = Accounts.ActiveId(config, id);
            int index = Math.Max(0, list.FindIndex(a => a.Id == active));
            string next = list[(index + 1) % list.Count].Id;
            var chip = Styled(new Button { Content = Label(Accounts.DisplayName(config, id, active), 10.5, InkDim), Padding = new Thickness(7, 2, 7, 2) }, "secondary");
            chip.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(chip, I18n.T("切换账户：{0} → {1}", Accounts.DisplayName(config, id, active), Accounts.DisplayName(config, id, next)));
            AutomationProperties.SetName(chip, ProviderCatalog.Name(id) + " " + I18n.T("切换账户"));
            chip.Click += delegate(object sender, Avalonia.Interactivity.RoutedEventArgs e) { e.Handled = true; SwitchActiveAccount(id, next); };
            return chip;
        }
        // 切换活动账户：立即落盘，并按「凭据变更」语义只定向刷新这一个平台
        //（RefreshChanged 内部 ResetFor + 重放 Fetch，其余平台的通道与读数原样保留）。
        private async void SwitchActiveAccount(string id, string accountId) {
            if (demo) return;
            if (!Accounts.SetActive(config, id, accountId)) return;
            SaveConfig();
            keyDrafts.Remove(id);   // 卡片上未保存的密钥输入随账户一起作废
            if (settingsWindow == null && !ProviderCatalog.IsKnown(id)) return;
            try { await RefreshChanged(new HashSet<string> { id }, null); } catch { }
        }

        // ── 设置页账户管理区 ──────────────────────────────────────────────
        // keyFields（保存时逐平台落钥匙串）与 accountHosts（账户区就地重建）由
        // Settings.cs 的 BuildSettings 建立并传入；keyFields 以平台 id 为键，账户切换 /
        // 增删触发重建时同步更新（保存永远作用于「当前活动账户」）。
        private readonly Dictionary<string, StackPanel> accountHosts = new Dictionary<string, StackPanel>();
        // 面板上下文（C 期修正）：各平台当前面板正文的账户区宿主与「清除」按钮。每 id 一条、
        // 随面板重建覆盖，不累积；自定义编辑器的账户区不登记 → RebuildAccountSection 按宿主
        // 身份区分上下文：面板重建保持「清除」按钮模式（不重新注入「移除已保存的密钥」开关，
        // 并按新活动账户重算「清除」可用态），编辑器保持自己的开关语义。
        private readonly Dictionary<string, StackPanel> panelAccountHosts = new Dictionary<string, StackPanel>();
        private readonly Dictionary<string, Button> panelClearButtons = new Dictionary<string, Button>();
        // 密钥输入框的占位与无障碍命名按凭据形态区分：API Key / Console Cookie / Oasis-Token。
        internal static string CredentialNoun(string id) { return id == "mimo" ? "Console Cookie" : id == "stepfun" ? "Oasis-Token" : "API Key"; }
        // 账户区（活动账户切换 / 增删 / 密钥输入）：输入框绑定活动账户，占位符让空输入框的
        // 用途一眼可见（占位符不是值），凭据永不回显。removeSwitch=false 供认证页凭据面板用：
        // 那里以「清除」按钮即时移除（写空串恒成功），不再叠加「移除已保存的密钥」开关；
        // 自定义平台编辑器保持原有的保存时移除通道。
        private void PopulateAccountSection(string id, StackPanel host, Dictionary<string, KeyField> keyFields, bool removeSwitch = true) {
            host.Children.Clear();
            accountHosts[id] = host;
            List<ProviderAccount> list = Accounts.List(config, id);
            string active = Accounts.ActiveId(config, id);
            // 活动账户切换行：「活动账户」标签 + 分段（每个账户一项 + 「添加」）。切换立即生效
            // 并定向刷新该平台；密钥输入框始终编辑当前活动账户。
            var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            AddRow(row, Label(I18n.T("活动账户"), 12, InkDim), AccountSegment(id, list, active, keyFields));
            host.Children.Add(row);
            // 密钥输入（活动账户）。PlaceholderText 让空输入框的用途一眼可见（占位符，不是值）；凭据永不回显。
            string noun = CredentialNoun(id);
            var field = new KeyField { Id = id, AccountId = active, Box = new KeyBox { IsEnabled = !demo, Margin = new Thickness(0, 8, 0, 0), PlaceholderText = I18n.T("粘贴 " + noun) } };
            AutomationProperties.SetName(field.Box, ProviderCatalog.Name(id) + " " + Accounts.DisplayName(config, id, active) + " " + noun);
            host.Children.Add(field.Box);
            keyFields[id] = field;
            string env = demo ? "" : Store.KeyEnvironmentName(id, config);
            bool saved = !demo && Store.HasSavedKey(id, active);
            host.Children.Add(Hint(demo ? "演示模式不读取或保存密钥。"
                : env.Length > 0 ? I18n.T("正在使用环境变量 ") + env + I18n.T("（优先于这里保存的密钥）。")
                : saved ? I18n.T("「{0}」已保存密钥。留空则保留，填写新密钥会替换它。", Accounts.DisplayName(config, id, active))
                : I18n.T("「{0}」未设置密钥。", Accounts.DisplayName(config, id, active)), 5));
            if (saved && removeSwitch) {
                field.Remove = SwitchRow(I18n.T("移除已保存的密钥"), null, false);
                field.Remove.Margin = new Thickness(0, 8, 0, 0);
                host.Children.Add(field.Remove);
            }
            if (demo) return;
            // 删除当前活动账户（至少保留一个）：二次点击确认，立即生效并清理它的密钥。
            if (list.Count > 1) {
                var delete = Styled(new Button { Content = I18n.T("删除账户「{0}」", Accounts.DisplayName(config, id, active)), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, 6, 0, 0) }, "link-button");
                bool armed = false;
                delete.Click += delegate {
                    if (!armed) { armed = true; delete.Content = I18n.T("再次点击确认删除"); return; }
                    string before = Accounts.ActiveId(config, id);
                    if (!Accounts.Remove(config, id, active)) return;
                    SaveConfig();
                    if (before != Accounts.ActiveId(config, id) && config.Enabled.Contains(id) && ProviderCatalog.IsKnown(id)) { var ignored = RefreshChanged(new System.Collections.Generic.HashSet<string> { id }, null); }
                    RebuildAccountSection(id, keyFields);
                };
                host.Children.Add(delete);
            }
        }
        // ── 认证页凭据面板 ────────────────────────────────────────────────
        // 折叠状态：会话内记忆，不落盘；缺省值 = 仅「第一个未连接的平台」展开
        //（BuildAccountsPage 构建时把缺省展开项传进来）。
        private readonly Dictionary<string, bool> credentialPanelOpen = new Dictionary<string, bool>();
        // 连接口径（状态胶囊）：最近一次读数 ready / cached 即已连接；无状态 = 未连接。
        private bool ProviderConnected(string id) {
            ProviderState state;
            if (!states.TryGetValue(id, out state) || state == null) return false;
            return state.Status == "ready" || state.Status == "cached";
        }
        // 胶囊文案：多账户平台显示「N 个账户」，否则已连接 / 未连接。
        private string PanelStatusText(string id) {
            if (MultipleAccounts(id)) return I18n.T("{0} 个账户", Accounts.List(config, id).Count);
            return ProviderConnected(id) ? I18n.T("已连接") : I18n.T("未连接");
        }
        private Border StatusPill(string id) {
            bool connected = ProviderConnected(id);
            Border pill = Pill(PanelStatusText(id));
            if (connected) { pill.Background = Brush("#1F7AD3A8"); ((TextBlock)pill.Child).Foreground = GoodBrush; }
            AutomationProperties.SetName(pill, ProviderCatalog.Name(id) + " " + I18n.T("连接状态"));
            return pill;
        }
        // 胶囊就地刷新（验证 / 清除 / 导入后按同一口径重推已连接 / 未连接）。
        private void RefreshPill(Border pill, string id) {
            bool connected = ProviderConnected(id);
            pill.Background = connected ? Brush("#1F7AD3A8") : Brush("#14FFFFFF");
            var text = pill.Child as TextBlock;
            if (text != null) { text.Text = PanelStatusText(id); text.Foreground = connected ? GoodBrush : InkDim; }
        }
        // 每供应商一个可折叠面板：折叠头（图标 + 名称 + 状态胶囊 + chevron）点击展开 / 收起；
        // 正文按凭据类型分 key 型（控制台 / 步骤 / 粘贴框 / 保存并验证）与 CLI·桌面应用型（卡片引导）。
        private Control CredentialPanel(string id, Dictionary<string, KeyField> keyFields, string defaultOpen) {
            bool open;
            if (!credentialPanelOpen.TryGetValue(id, out open)) { open = id == defaultOpen; credentialPanelOpen[id] = open; }
            var root = new StackPanel();
            var body = new StackPanel { IsVisible = open };
            AutomationProperties.SetName(body, I18n.T("凭据面板内容 {0}", ProviderCatalog.Name(id)));
            // 折叠头：透明底按钮（悬停高亮来自默认模板的 Hover 层），整行可点。
            var head = new Button { Padding = new Thickness(2, 5, 2, 5), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var headGrid = new Grid();
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headGrid.ColumnDefinitions.Add(new ColumnDefinition());
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Control icon = Icon(id, 14); icon.Margin = new Thickness(0, 0, 8, 0); headGrid.Children.Add(icon);
            var chevronRotate = new RotateTransform(open ? 90 : 0);
            var chevron = Icons.Glyph(Icons.ChevronRight, 9, InkFaint, HorizontalAlignment.Center, VerticalAlignment.Center);
            chevron.Margin = new Thickness(0, 0, 9, 0); chevron.RenderTransform = chevronRotate;
            Grid.SetColumn(chevron, 1); headGrid.Children.Add(chevron);
            var name = Label(ProviderCatalog.Name(id), 12.5, Ink); name.FontWeight = FontWeight.SemiBold; name.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(name, 2); headGrid.Children.Add(name);
            Border pill = StatusPill(id);
            Grid.SetColumn(pill, 3); headGrid.Children.Add(pill);
            head.Content = headGrid;
            AutomationProperties.SetName(head, I18n.T("凭据面板 {0}", ProviderCatalog.Name(id)));
            head.Click += delegate {
                bool next = !body.IsVisible;
                credentialPanelOpen[id] = next;
                body.IsVisible = next;
                chevronRotate.Angle = next ? 90 : 0;
            };
            root.Children.Add(head);
            root.Children.Add(body);
            if (KeyProviders.Contains(id)) BuildKeyPanelBody(id, body, keyFields, pill);
            else BuildCliPanelBody(id, body);
            return Card(root);
        }
        // key / cookie / token 型面板正文：区域分段（kimi / zcode / stepfun）→ 控制台 /
        // 一键导入 → 编号步骤 → 账户区（粘贴框）→ 保存并验证 / 清除 → 结果行。
        private void BuildKeyPanelBody(string id, StackPanel body, Dictionary<string, KeyField> keyFields, Border pill) {
            var verify = new TextBlock { FontSize = 11, Foreground = InkDim, TextWrapping = TextWrapping.Wrap, LineHeight = 16, Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetName(verify, ProviderCatalog.Name(id) + " " + I18n.T("验证结果"));
            if (id == "kimi") body.Children.Add(RegionRow(new[] { new[] { "china", I18n.T("国内") }, new[] { "international", I18n.T("国际") } }, kimiRegionDraft, code => kimiRegionDraft = code));
            else if (id == "zcode") body.Children.Add(RegionRow(new[] { new[] { "china", I18n.T("国内") }, new[] { "global", I18n.T("国际") } }, zaiRegionDraft, code => zaiRegionDraft = code));
            else if (id == "stepfun") body.Children.Add(RegionRow(new[] { new[] { "china", I18n.T("国内") }, new[] { "global", I18n.T("国际") } }, stepfunRegionDraft, code => stepfunRegionDraft = code));
            var actions = ButtonRow();
            if (id == "kimi" || id == "zcode") {
                var import = ConnectButton(I18n.T("一键导入本机登录"), false);
                import.IsEnabled = !demo;
                AutomationProperties.SetName(import, ProviderCatalog.Name(id) + " " + I18n.T("一键导入本机登录"));
                import.Click += async delegate {
                    if (!ImportFromLocalCli(id)) { verify.Foreground = WarnBrush; verify.Text = I18n.T("未能从本机登录凭据导入，请填写 API Key。"); return; }
                    verify.Foreground = InkDim; verify.Text = I18n.T("正在连接…");
                    await RefreshOne(id);
                    RefreshPill(pill, id);
                    verify.Text = "";
                };
                actions.Children.Add(import);
            }
            var console = ConnectButton(I18n.T("打开控制台"), false);
            AutomationProperties.SetName(console, ProviderCatalog.Name(id) + " " + I18n.T("打开控制台"));
            console.Click += delegate { try { Process.Start(new ProcessStartInfo(ProviderCatalog.ConsoleUrl(id, RegionDraftSnapshot())) { UseShellExecute = true }); } catch { } };
            actions.Children.Add(console);
            body.Children.Add(actions);
            // 编号步骤指引：mimo / stepfun 给浏览器取 Cookie / Token 的具体路径；其余沿用 Help 文案。
            string[] steps = CredentialSteps(id);
            for (int i = 0; steps != null && i < steps.Length; i++) body.Children.Add(Hint((i + 1) + ". " + steps[i], i == 0 ? 6 : 2));
            if (steps == null) body.Children.Add(Hint(I18n.T(ProviderCatalog.Help(id)), 6));
            // 账户区（切换 / 增删 / 粘贴框）收进面板内，不散在面板外。
            var host = new StackPanel();
            body.Children.Add(host);
            panelAccountHosts[id] = host;   // 重建上下文：RebuildAccountSection 据此保持「清除」按钮模式
            PopulateAccountSection(id, host, keyFields, removeSwitch: false);
            // 「保存并验证」：写钥匙串 → 立即对该平台跑一次单平台 Fetch 验证；验证失败不回滚已保存的 key。
            var row = ButtonRow();
            var save = Styled(new Button { Content = I18n.T("保存并验证"), IsEnabled = !demo, Margin = new Thickness(0, 8, 8, 0) }, "primary");
            AutomationProperties.SetName(save, ProviderCatalog.Name(id) + " " + I18n.T("保存并验证"));
            var clear = Styled(new Button { Content = I18n.T("清除"), IsEnabled = !demo && Store.HasSavedKey(id, Accounts.ActiveId(config, id)), Margin = new Thickness(0, 8, 0, 0) }, "secondary");
            AutomationProperties.SetName(clear, ProviderCatalog.Name(id) + " " + I18n.T("清除已保存的密钥"));
            panelClearButtons[id] = clear;   // 账户切换 / 重建后就地重算可用态（见 RebuildAccountSection）
            save.Click += async delegate {
                if (demo) return;
                string active = Accounts.ActiveId(config, id);
                string key = BoxText(keyFields[id].Box).Trim();
                bool hasEnv = Store.KeyEnvironmentName(id, config).Length > 0;
                if (key.Length == 0 && !hasEnv && !Store.HasSavedKey(id, active)) { verify.Foreground = WarnBrush; verify.Text = I18n.T("请先粘贴 {0}。", CredentialNoun(id)); return; }
                save.IsEnabled = false; clear.IsEnabled = false;
                try {
                    // 区域草稿立即生效（端点与验证一致；与卡片保存的区域语义相同，设置保存路径重复赋同值幂等）。
                    ApplyRegionDraft(id);
                    if (key.Length > 0) {
                        // P2-3 同口径：钥匙串写入失败亮错误、不静默；输入保留可重试（不回滚也不清空）。
                        if (!Store.SetProviderKey(id, active, key)) { verify.Foreground = WarnBrush; verify.Text = I18n.T("密钥未能写入 macOS 钥匙串，保存未完成，请重试。"); return; }
                        // 凭据永不回显：账户区随已保存状态重建，输入框清零。
                        RebuildAccountSection(id, keyFields);
                    }
                    verify.Foreground = InkDim; verify.Text = I18n.T("正在验证…");
                    ProviderState outcome = await VerifyProvider(id);
                    if (outcome != null && outcome.Status == "loading") {
                        // 冲突 / 过期：key 已保存、后台刷新在途——中性提示，不算验证失败。
                        verify.Foreground = InkDim;
                        verify.Text = outcome.Message;
                    } else if (outcome != null && (outcome.Status == "ready" || outcome.Status == "cached")) {
                        string who = String.Join(" · ", new[] { outcome.Plan, AccountLabel(id, outcome.Account) }.Where(s => !String.IsNullOrEmpty(s)));
                        verify.Foreground = GoodBrush;
                        verify.Text = I18n.T("已连接") + (who.Length > 0 ? " · " + who : "");
                    } else {
                        // 验证失败：已保存的 key 不回滚，原因行内红字上屏。
                        verify.Foreground = WarnBrush;
                        verify.Text = I18n.T("验证失败：{0}", outcome == null || (outcome.Message ?? "").Length == 0 ? I18n.T("未知错误") : outcome.Message);
                    }
                    RefreshPill(pill, id);
                } finally {
                    save.IsEnabled = !demo;
                    clear.IsEnabled = !demo && Store.HasSavedKey(id, Accounts.ActiveId(config, id));
                }
            };
            clear.Click += delegate {
                if (demo) return;
                // 清除 = 钥匙串写空串（恒成功），作用于当前活动账户；没有环境变量兜底时
                // 连接随密钥失效（与 Copilot 退出登录同口径）。
                string active = Accounts.ActiveId(config, id);
                Store.SetProviderKey(id, active, "");
                if (Store.KeyEnvironmentName(id, config).Length == 0) states[id] = new ProviderState { Id = id };
                verify.Foreground = InkDim; verify.Text = "";
                RebuildAccountSection(id, keyFields);
                RefreshPill(pill, id);
                clear.IsEnabled = Store.HasSavedKey(id, Accounts.ActiveId(config, id));
            };
            row.Children.Add(save); row.Children.Add(clear);
            body.Children.Add(row);
            body.Children.Add(verify);
        }
        // CLI / 桌面应用型面板正文：沿用现有卡片引导（终端登录 / 打开应用 / 设备码流），不放粘贴框。
        private void BuildCliPanelBody(string id, StackPanel body) {
            var inner = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
            if (id == "copilot") inner.Children.Add(CopilotLoginRow());
            else if (id == "codex" || id == "claude" || id == "grok") CliConnect(inner, id);
            else if (id == "cursor" || id == "antigravity") AppConnect(inner, id);
            body.Children.Add(inner);
        }
        // 面板内的接口区域行（kimi / zcode / stepfun）：写草稿字段，设置保存时落 config；
        // 面板内「保存并验证」会先把该平台草稿落盘（ApplyRegionDraft），与卡片语义一致。
        private Control RegionRow(string[][] options, string current, Action<string> changed) {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            AddRow(row, Label(I18n.T("接口区域"), 11.5, InkDim), Segmented(options, current, changed));
            return row;
        }
        // mimo / stepfun 的取凭据具体步骤（编号上屏）；其余平台沿用 ProviderCatalog.Help 文案。
        private string[] CredentialSteps(string id) {
            if (id == "mimo") return new[] {
                I18n.T("浏览器登录 platform.xiaomimimo.com 并进入控制台。"),
                I18n.T("打开开发者工具（Cmd+Option+I）→ Application → Cookies。"),
                I18n.T("复制 api-platform_serviceToken 与 userId 两项的值，一起粘贴到下面。") };
            if (id == "stepfun") {
                string host = Parsers.StepFunHost(stepfunRegionDraft).Replace("https://", "");
                return new[] {
                    I18n.T("浏览器登录 {0}。", host),
                    I18n.T("打开开发者工具（Cmd+Option+I）→ Network。"),
                    I18n.T("找到 QueryStepPlanRateLimit 请求，从请求头复制 Oasis-Token 粘贴到下面。") };
            }
            return null;
        }
        // 面板内验证前把该平台的区域草稿落进 config 并持久化（端点选择与验证一致；与卡片
        // SaveKeyFromCard 的区域立即生效同语义，设置保存路径重复赋同值幂等）。
        private void ApplyRegionDraft(string id) {
            if (id == "kimi") { if (config.KimiRegion != kimiRegionDraft) { config.KimiRegion = kimiRegionDraft; SaveConfig(); } }
            else if (id == "zcode") { if (config.ZaiRegion != zaiRegionDraft) { config.ZaiRegion = zaiRegionDraft; SaveConfig(); } }
            else if (id == "stepfun") { if (config.StepfunRegion != stepfunRegionDraft) { config.StepfunRegion = stepfunRegionDraft; SaveConfig(); } }
        }
        // 用面板里选择的区域草稿解析控制台地址（保存前打开控制台也要去对站）。
        private AppConfig RegionDraftSnapshot() {
            return new AppConfig { KimiRegion = kimiRegionDraft, ZaiRegion = zaiRegionDraft, StepfunRegion = stepfunRegionDraft };
        }
        // 单平台验证（认证页面板「保存并验证」）：走 RefreshOne 同一条 ProviderService.Fetch 通道、
        // 同一套串号 / 通道记录语义（RecordOutcome / Accept），只是把结果返回给面板上屏。
        // Fetch 自身把异常折进 ProviderState（expired / error + 文案），这里只留兜底。
        // 两种「拿不到本次读数」的情况返回 Status="loading" 的中性结果——key 已写入、刷新在途，
        // 由调用方显示「已保存，正在后台刷新…」而不是误报「验证失败」：
        // 1) 发起时同平台已有刷新在途（refreshingIds 冲突）；2) Fetch 期间有更新的刷新接管
        //（串号过期：过期结果只记录不采纳，面板读数交给最新一次）。
        private async Task<ProviderState> VerifyProvider(string id) {
            if (demo || !ProviderCatalog.IsKnown(id)) return null;
            if (refreshingIds.Contains(id)) return new ProviderState { Id = id, Status = "loading", Message = I18n.T("已保存，正在后台刷新…") };
            refreshingIds.Add(id);
            try {
                int serial = BeginFetch(id);
                ProviderState result;
                using (var service = new ProviderService(config)) result = await Task.Run(() => service.Fetch(id));
                bool latest = Latest(id, serial);   // 先判串号再落通道：过期结果只记录、不采纳
                if (result != null) RecordOutcome(id, result);
                if (latest) Accept(id, result);
                try { Store.Write("quota-cache.json", states.Values.ToList()); UpdateTray(); } catch { }
                if (!latest) return new ProviderState { Id = id, Status = "loading", Message = I18n.T("已有更新的后台刷新，读数以最新为准。") };
                return result;
            } catch (Exception e) {
                return new ProviderState { Id = id, Status = "error", Message = e is ProviderException ? e.Message : I18n.T("网络错误：{0}", e.GetType().Name) };
            } finally { refreshingIds.Remove(id); Render(); }
        }

        // 账户分段（活动切换 + 添加）。代码即文档：切到现有账户 = 活动切换（立即生效、
        // 定向刷新）；「+」= 添加账户并把它设为活动（密钥输入随即作用于它）。演示模式
        // 只读：不提供添加入口（SwitchActiveAccount 在 demo 下也是空操作）。
        private Control AccountSegment(string id, List<ProviderAccount> list, string active, Dictionary<string, KeyField> keyFields) {
            var options = list.Select(a => new[] { a.Id, Accounts.DisplayName(config, id, a.Id) }).ToList();
            if (!demo) options.Add(new[] { "+add", "+ " + I18n.T("添加账户") });
            var segment = Segmented(options.ToArray(), active, code => {
                if (code == "+add") {
                    string added = Accounts.Add(config, id, "");
                    Accounts.SetActive(config, id, added);
                    SaveConfig();
                    RebuildAccountSection(id, keyFields);
                    return;
                }
                SwitchActiveAccount(id, code);
                // 切换已触发定向刷新与落盘；就地把输入框与提示切到新活动账户（整段重建）。
                RebuildAccountSection(id, keyFields);
            });
            segment.HorizontalAlignment = HorizontalAlignment.Right;
            return segment;
        }
        private void RebuildAccountSection(string id, Dictionary<string, KeyField> keyFields) {
            StackPanel host;
            if (!accountHosts.TryGetValue(id, out host) || host == null) return;
            // 面板上下文 = 注册的宿主仍是当前面板正文的账户区：不重新注入「移除已保存的密钥」
            // 开关（面板以「清除」按钮即时移除），并按（可能已切换的）新活动账户重算「清除」
            // 可用态。自定义编辑器的宿主不在 panelAccountHosts 里，保持原开关语义。
            bool panel = panelAccountHosts.TryGetValue(id, out StackPanel panelHost) && ReferenceEquals(panelHost, host);
            PopulateAccountSection(id, host, keyFields, removeSwitch: !panel);
            Button clear;
            if (panel && panelClearButtons.TryGetValue(id, out clear) && clear != null)
                clear.IsEnabled = !demo && Store.HasSavedKey(id, Accounts.ActiveId(config, id));
        }
        // 清空一个平台全部账户的密钥并移除账户结构（删除自定义平台、改名迁移后清旧 id）。
        private void ClearProviderKeys(string id) {
            try { foreach (ProviderAccount account in Accounts.List(config, id)) Store.SetProviderKey(id, account.Id, ""); } catch { }
            if (config.Accounts != null) config.Accounts.Remove(id);
        }
    }
}
