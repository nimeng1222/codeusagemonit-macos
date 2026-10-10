// Desktop 侧无头自测（Program.cs 在 --self-test 时经 SelfTests.DesktopTestSource 装配进
// 既有清单，与 Core 用例同一张 passed/failed 表）。不启动 Avalonia 平台：控件只是对象树，
// 构建与属性断言不需要运行中的 Application。MonitorPanel 的构造函数会创建窗口与托盘，
// 无头环境不可用，因此用 GetUninitializedObject 取未初始化实例、只经反射注入被测路径
// 需要的字段（先例：RevealHarness 反射访问私有成员）。
// 约束：数据目录经 Store.SwapData 指向临时目录（Core 经 InternalsVisibleTo 向本程序集
// 开放 internal；CODEUSAGEMONIT_DATA 环境变量不可用——Core 自测已先读过 Store.Data，
// 懒缓存选定后环境变量不再生效）、钥匙串经 Keychain.RunOverride 合成「条目不存在」，
// 并断言探测确实走这条缝；自测期间绝不触碰真实钥匙串、不写真实数据目录、不发起
// 网络请求，清单里不出现真实凭据。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace CodeUsageMonit {
    internal static class UiSelfTests {
        public static IEnumerable<KeyValuePair<string, Action>> Checks() {
            yield return new KeyValuePair<string, Action>("Desktop: the headless accounts page builds six enabled key boxes with placeholders and a detected-accounts row per platform", AccountsPageCheck);
            yield return new KeyValuePair<string, Action>("Desktop: credential panels build one collapsible panel per provider with status pills, default-open, toggle and per-kind bodies", PanelsStructureCheck);
            yield return new KeyValuePair<string, Action>("Desktop: the data page builds a null-safe WebDAV password box so save never dereferences null (regression: settings save NRE)", DataPagePasswordCheck);
            yield return new KeyValuePair<string, Action>("Desktop: Urgent orders overview providers by quota availability and filters nothing", UrgentOrderCheck);
            // D 期四项（hero 三档 / 构成细分 / 趋势卡 / 账户遮罩）：
            yield return new KeyValuePair<string, Action>("Desktop: hero range presets resolve this month and the 370-day horizon with their labels", RangePresetCheck);
            yield return new KeyValuePair<string, Action>("Desktop: account masking keeps only the first and last local characters before the @", AccountMaskCheck);
            yield return new KeyValuePair<string, Action>("Desktop: the trend card aggregates history.Days across agents into a peak and hides without data", TrendChartCheck);
            // D 期 Sessions 会话页：
            yield return new KeyValuePair<string, Action>("Desktop: the sessions page builds rows with tool, model and cost, expands breakdowns, pages and shows an empty state", SessionsPageCheck);
            // 窄条（edge）显示尺寸：
            yield return new KeyValuePair<string, Action>("Desktop: the edge dock lists quota providers by tightness with ring arcs, hides quota-less or disabled platforms and shows an empty state", EdgeDockCheck);
            yield return new KeyValuePair<string, Action>("Desktop: the edge dock summary reads dashes without data and today's tokens and cost once history exists", EdgeSummaryCheck);
            yield return new KeyValuePair<string, Action>("Desktop: the edge dock ring shows the shortest quota window, falls back to the tightest one without windows, skips Grok Bot and lists every window in its tooltip", EdgeQuotaPickCheck);
            yield return new KeyValuePair<string, Action>("Desktop: the edge size is registered last in the cycle, keeps its name, survives config normalization and sits between its resize limits", EdgeSizeRegistryCheck);
            // 手动调整边（任务1）：钳制纯函数与 ApplyLimits / Core 规则同口径。
            yield return new KeyValuePair<string, Action>("Desktop: the manual resize clamp matches the per-size limits (compact min and max scaled, full cap 8192) and treats an invalid scale as 1", ResizeClampCheck);
            // 概览平台卡「本机用量」小节（任务4）与所有页面默认当天（任务3）：
            yield return new KeyValuePair<string, Action>("Desktop: the overview card shows a local-usage peek with today's model split, a plus-more line and window estimates, and hides entirely without local data", ProviderCardLocalUsageCheck);
        }

        private static readonly BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        // 无头自测用的渲染接口只注册一次（进程内多次 Initialize 未必安全；失败不掩盖——
        // 后续 Geometry.Parse 抛“Unable to locate IPlatformRenderInterface”即可定位到这里）。
        private static bool renderInterfaceReady;

        private static void EnsureRenderInterface() {
            if (renderInterfaceReady) return;
            Avalonia.Skia.SkiaPlatform.Initialize();
            renderInterfaceReady = true;
        }

        // ── 认证页（需求3）：四个密钥平台 KeyBox 存在、可用、带占位符；检测区每平台一行 ──
        private static void AccountsPageCheck() {
            EnsureRenderInterface();   // 凭据面板折叠头的 chevron 走 Geometry.Parse
            string dir = Path.Combine(Path.GetTempPath(), "cum-ui-accounts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var keychainCalls = new List<string[]>();
            string previousData = Store.SwapData(dir);   // 数据目录隔离：无论此前是否已解析出真实目录
            Keychain.RunOverride = args => { keychainCalls.Add(args); return null; };   // 合成 exit 44：条目不存在，绝不触碰真实钥匙串
            try {
                var config = new AppConfig { Enabled = ProviderCatalog.DefaultEnabled };
                var states = ProviderCatalog.All.ToDictionary(id => id, id => new ProviderState { Id = id });
                var panel = Bare(config, states);
                var accounts = new StackPanel();
                Type keyFieldType = typeof(MonitorPanel).GetNestedType("KeyField", BindingFlags.NonPublic);
                var keyFields = (System.Collections.IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), keyFieldType));
                typeof(MonitorPanel).GetMethod("BuildAccountsPage", Hidden).Invoke(panel, new object[] { accounts, keyFields });
                // 隔离生效证明（进程内断言）：整页构建期间 Store.Data 已指向临时目录，
                // 引用文件/配置的读写都落在临时目录；claude 检测行的钥匙串存在性探测必须经
                // RunOverride 缝——拦截到调用本身即是「真实钥匙串未被触碰」的在证断言。
                Require(Store.Data == dir, "Store.Data not isolated to the temporary directory");
                Require(keychainCalls.Any(a => a.Length > 0 && a[0] == "find-generic-password"), "keychain probe bypassed the RunOverride seam");
                // 六个密钥平台的 KeyBox：存在、IsEnabled（非 demo）、Watermark 指向用途
                //（MiMo / StepFun 的占位符按凭据形态区分：Console Cookie / Oasis-Token）。
                var boxes = Find<KeyBox>(accounts).ToList();
                foreach (string id in new[] { "deepseek", "kimi", "opencode", "zcode" }) {
                    string expected = ProviderCatalog.Name(id) + " " + Accounts.DisplayName(config, id, Accounts.ActiveId(config, id)) + " API Key";
                    KeyBox box = boxes.FirstOrDefault(b => (AutomationProperties.GetName(b) ?? "") == expected);
                    Require(box != null, id + " KeyBox missing");
                    Require(box.IsEnabled, id + " KeyBox disabled");
                    Require((box.PlaceholderText ?? "") == I18n.T("粘贴 API Key"), id + " key box placeholder missing");
                    Require(keyFields.Contains(id), id + " not registered in keyFields");
                }
                foreach (string id in new[] { "mimo", "stepfun" }) {
                    string noun = MonitorPanel.CredentialNoun(id);
                    string expected = ProviderCatalog.Name(id) + " " + Accounts.DisplayName(config, id, Accounts.ActiveId(config, id)) + " " + noun;
                    KeyBox box = boxes.FirstOrDefault(b => (AutomationProperties.GetName(b) ?? "") == expected);
                    Require(box != null, id + " KeyBox missing");
                    Require(box.IsEnabled, id + " KeyBox disabled");
                    Require((box.PlaceholderText ?? "") == I18n.T("粘贴 " + noun), id + " key box placeholder missing");
                    Require(keyFields.Contains(id), id + " not registered in keyFields");
                }
                Require(boxes.Count == 6, "unexpected key box count: " + boxes.Count);
                // 「本机检测到的账户」区：全部已知平台各一行（含 CLI 登录平台），行上带文本。
                foreach (string id in ProviderCatalog.Ids) {
                    Visual row = FindByName(accounts, I18n.T("检测账户 {0}", ProviderCatalog.Name(id)));
                    Require(row != null, "detected row missing for " + id);
                    Require(Find<TextBlock>(row).Any(t => (t.Text ?? "").Length > 0), "row has no text: " + id);
                }
                // demo 模式：检测区只给提示、不探测，密钥框禁用（demo 语义不变）。
                var demoPanel = Bare(new AppConfig { Enabled = ProviderCatalog.DefaultEnabled }, ProviderCatalog.All.ToDictionary(id => id, id => new ProviderState { Id = id }), demo: true);
                var demoAccounts = new StackPanel();
                var demoFields = (System.Collections.IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), keyFieldType));
                typeof(MonitorPanel).GetMethod("BuildAccountsPage", Hidden).Invoke(demoPanel, new object[] { demoAccounts, demoFields });
                Require(FindByName(demoAccounts, I18n.T("检测账户 {0}", ProviderCatalog.Name("codex"))) == null, "demo must not probe");
                Require(Find<KeyBox>(demoAccounts).All(b => !b.IsEnabled), "demo key boxes must be disabled");
                // demo 的面板结构：保存并验证 / 清除一律禁用（演示模式禁用语义不变）；
                // 打开控制台只是个链接，六个 key 平台都在。
                foreach (string id in new[] { "deepseek", "kimi", "opencode", "zcode", "mimo", "stepfun" }) {
                    Button save = Find<Button>(demoAccounts).FirstOrDefault(b => (AutomationProperties.GetName(b) ?? "") == ProviderCatalog.Name(id) + " " + I18n.T("保存并验证"));
                    Button clear = Find<Button>(demoAccounts).FirstOrDefault(b => (AutomationProperties.GetName(b) ?? "") == ProviderCatalog.Name(id) + " " + I18n.T("清除已保存的密钥"));
                    Require(save != null && !save.IsEnabled, "demo save-verify must be disabled: " + id);
                    Require(clear != null && !clear.IsEnabled, "demo clear must be disabled: " + id);
                    Require(Find<Button>(demoAccounts).Any(b => (AutomationProperties.GetName(b) ?? "") == ProviderCatalog.Name(id) + " " + I18n.T("打开控制台")), "console link missing: " + id);
                }
            } finally {
                Keychain.RunOverride = null;   // 约定：自测缝用毕归 null（生产代码恒为 null）
                Store.SwapData(previousData);
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // ── 认证页凭据面板：每供应商一个折叠面板、状态胶囊、缺省只展开第一个未连接平台、
        // 折叠切换会话内记忆、key 型面板含「保存并验证」而 CLI 型面板不含 ──
        private static void PanelsStructureCheck() {
            EnsureRenderInterface();   // 面板折叠头的 chevron 走 Geometry.Parse
            string dir = Path.Combine(Path.GetTempPath(), "cum-ui-panels-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string previousData = Store.SwapData(dir);
            Keychain.RunOverride = args => null;   // 隔离：绝不触碰真实钥匙串（存在性探测只允许走这条缝）
            try {
                var config = new AppConfig { Enabled = ProviderCatalog.DefaultEnabled };
                var states = ProviderCatalog.All.ToDictionary(id => id, id => new ProviderState { Id = id });
                var panel = Bare(config, states);
                Type keyFieldType = typeof(MonitorPanel).GetNestedType("KeyField", BindingFlags.NonPublic);
                Func<StackPanel, System.Collections.IDictionary> newFields = host => {
                    var fields = (System.Collections.IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), keyFieldType));
                    typeof(MonitorPanel).GetMethod("BuildAccountsPage", Hidden).Invoke(panel, new object[] { host, fields });
                    return fields;
                };
                var accounts = new StackPanel();
                newFields(accounts);
                // 面板数量：除 pi（仅本机日志、无凭据）外的全部已知平台各一个折叠面板。
                //（正文行的名字是「凭据面板内容 …」，第 5 个字符不是空格，不会混进头部名单。）
                var heads = Find<Button>(accounts).Where(b => (AutomationProperties.GetName(b) ?? "").StartsWith(I18n.T("凭据面板 "), StringComparison.Ordinal)).ToList();
                int expected = ProviderCatalog.Ids.Count(id => id != "pi");
                Require(heads.Count == expected, "expected " + expected + " panel heads, got " + heads.Count);
                // 状态胶囊：每个面板一个（「<平台> 连接状态」），无读数时读作未连接。
                Func<string, StackPanel> bodyOf = id => (StackPanel)FindByName(accounts, I18n.T("凭据面板内容 {0}", ProviderCatalog.Name(id)));
                foreach (string id in ProviderCatalog.Ids.Where(id => id != "pi")) {
                    Visual pill = FindInContent(accounts, ProviderCatalog.Name(id) + " " + I18n.T("连接状态"));
                    Require(pill != null, "status pill missing for " + id);
                    Require(Find<TextBlock>(pill).Any(t => t.Text == I18n.T("未连接")), "pill must read 未连接 without state: " + id);
                    Require(bodyOf(id) != null, "panel body missing: " + id);
                }
                // 缺省只展开第一个未连接的平台（Ids 序第一个未连接 = codex），其余收起。
                Require(bodyOf("codex").IsVisible, "first unconnected panel must default open");
                Require(ProviderCatalog.Ids.Count(id => id != "pi" && bodyOf(id).IsVisible) == 1, "exactly one panel may default open");
                // 折叠切换：点头部收起 → 再点展开；状态在 credentialPanelOpen 里会话内记忆。
                var openState = (Dictionary<string, bool>)typeof(MonitorPanel).GetField("credentialPanelOpen", Hidden).GetValue(panel);
                Button codexHead = heads.First(h => (AutomationProperties.GetName(h) ?? "") == I18n.T("凭据面板 {0}", ProviderCatalog.Name("codex")));
                codexHead.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Require(!bodyOf("codex").IsVisible && !openState["codex"], "clicking the head must collapse the panel");
                codexHead.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Require(bodyOf("codex").IsVisible && openState["codex"], "clicking again must re-expand");
                // key 型面板正文：粘贴框 + 保存并验证（无已保存密钥时也可用：只验证环境变量兜底）
                // + 清除（未保存时禁用）+ 打开控制台 + 区域分段（kimi）。
                var deepseekBody = bodyOf("deepseek");
                Require(Find<KeyBox>(deepseekBody).Any(), "key panel must contain a paste box");
                Button save = Find<Button>(deepseekBody).FirstOrDefault(b => (AutomationProperties.GetName(b) ?? "") == "DeepSeek " + I18n.T("保存并验证"));
                Require(save != null && save.IsEnabled, "key panel must offer an enabled 保存并验证");
                Button clear = Find<Button>(deepseekBody).FirstOrDefault(b => (AutomationProperties.GetName(b) ?? "") == "DeepSeek " + I18n.T("清除已保存的密钥"));
                Require(clear != null && !clear.IsEnabled, "clear must be disabled without a saved key");
                Require(Find<Button>(deepseekBody).Any(b => (AutomationProperties.GetName(b) ?? "") == "DeepSeek " + I18n.T("打开控制台")), "key panel must offer 打开控制台");
                Require(Find<RadioButton>(bodyOf("kimi")).Any(), "kimi panel must keep the region segment");
                // CLI 型面板正文：有引导按钮，但没有粘贴框、没有「保存并验证」。
                var codexBody = bodyOf("codex");
                Require(!Find<KeyBox>(codexBody).Any(), "CLI panel must not contain a paste box");
                Require(!Find<Button>(codexBody).Any(b => (AutomationProperties.GetName(b) ?? "").EndsWith(I18n.T("保存并验证"), StringComparison.Ordinal)), "CLI panel must not offer 保存并验证");
                Require(Find<Button>(codexBody).Any(b => (AutomationProperties.GetName(b) ?? "").Length > 0), "CLI panel must keep its guidance buttons");
                // 连接后的胶囊口径：ready 读作已连接（重建一页验证）。
                states["deepseek"] = new ProviderState { Id = "deepseek", Status = "ready" };
                var accounts2 = new StackPanel();
                newFields(accounts2);
                Visual pill2 = FindInContent(accounts2, "DeepSeek " + I18n.T("连接状态"));
                Require(Find<TextBlock>(pill2).Any(t => t.Text == I18n.T("已连接")), "ready provider must read 已连接");
                // 多账户胶囊：两个账户时读作「N 个账户」。
                Accounts.Add(config, "kimi", "");
                var accounts3 = new StackPanel();
                newFields(accounts3);
                Visual pill3 = FindInContent(accounts3, "Kimi " + I18n.T("连接状态"));
                Require(Find<TextBlock>(pill3).Any(t => (t.Text ?? "").EndsWith(I18n.T("个账户"), StringComparison.Ordinal) && t.Text.StartsWith("2", StringComparison.Ordinal)), "multi-account pill must read 2 个账户");
            } finally {
                Keychain.RunOverride = null;   // 约定：自测缝用毕归 null（生产代码恒为 null）
                Store.SwapData(previousData);
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // ── 数据页（WebDAV 保存 NRE 回归）：未配置 WebDAV + 密码框未触碰时保存路径不遇 null ──
        // 曾因 KeyBox 未赋 Text（Avalonia 未输入过的 TextBox.Text == null）而保存必抛
        // NullReferenceException；修复 = 创建时 Text=""、读取统一走 MonitorPanel.BoxText。
        private static void DataPagePasswordCheck() {
            string dir = Path.Combine(Path.GetTempPath(), "cum-ui-data-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var keychainCalls = new List<string[]>();
            string previousData = Store.SwapData(dir);
            Keychain.RunOverride = args => { keychainCalls.Add(args); return null; };   // 隔离：绝不触碰真实钥匙串
            try {
                // 备份步进器经 Icons.Glyph → Geometry.Parse，需要已注册的渲染接口；无头环境
                // 只注册 Skia 渲染接口（不启动窗口平台），几何解析即可用，其余检查不受影响。
                EnsureRenderInterface();
                var panel = Bare(new AppConfig { Enabled = ProviderCatalog.DefaultEnabled }, ProviderCatalog.All.ToDictionary(id => id, id => new ProviderState { Id = id }));
                var page = new StackPanel();
                object fields;
                try { fields = typeof(MonitorPanel).GetMethod("BuildDataPage", Hidden).Invoke(panel, new object[] { page }); }
                catch (TargetInvocationException e) { throw e.InnerException ?? e; }   // 报告里要真实异常而不是反射包装
                Require(fields != null, "BuildDataPage returned nothing");
                Func<string, TextBox> field = name => (TextBox)typeof(MonitorPanel).GetNestedType("DataFields", BindingFlags.NonPublic).GetField(name).GetValue(fields);
                TextBox password = field("Password");
                Require(password != null, "WebDAV password box missing");
                // 根因回归：创建即空串。修复前这里是 null，Settings 保存路径 .Text.Length 直接 NRE。
                Require((password.Text ?? "") == "", "untouched password box must read as empty string, not null");
                Require(password is KeyBox, "password box must stay a KeyBox (masked input)");
                // 未配置 WebDAV（无已存密码）：Forget 开关不存在，保存的判定表达式求值为 false，不写不清钥匙串。
                CheckBox forget = (CheckBox)typeof(MonitorPanel).GetNestedType("DataFields", BindingFlags.NonPublic).GetField("Forget").GetValue(fields);
                Require(forget == null, "Forget switch must be absent without a saved WebDAV password");
                // 与 Settings 保存路径完全相同的判定（Settings.cs：勾选移除 / 非空写入）在此求值不得抛异常。
                Require(MonitorPanel.BoxText(password).Length == 0, "save guard must treat the untouched box as empty");
                Require(keychainCalls.All(a => !(a.Length > 0 && a.Contains("-w"))), "building the data page must never write the keychain");
                // BoxText 契约：null 框 / null 文本 / 正常文本。
                Require(MonitorPanel.BoxText(null) == "", "BoxText(null) must be empty");
                Require(MonitorPanel.BoxText(new TextBox()) == "", "BoxText must normalise a default (null-text) box");
                Require(MonitorPanel.BoxText(new TextBox { Text = " abc " }) == " abc ", "BoxText must pass text through verbatim");
            } finally {
                Keychain.RunOverride = null;   // 约定：自测缝用毕归 null（生产代码恒为 null）
                Store.SwapData(previousData);
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // ── 概览排序（需求2）：Urgent 与完整面板平台卡共用 ProviderOrder 口径 ──
        private static void UrgentOrderCheck() {
            var config = new AppConfig { Enabled = new[] { "codex", "claude", "cursor", "deepseek", "kimi", "zcode", "grok" } };
            Quota q(double remaining) { return new Quota { Label = "窗口", Used = 100 - remaining }; }
            var states = new Dictionary<string, ProviderState>();
            states["codex"] = new ProviderState { Id = "codex", Status = "ready" }; states["codex"].Quotas.Add(q(10));
            states["kimi"] = new ProviderState { Id = "kimi", Status = "cached" }; states["kimi"].Quotas.Add(q(70));   // cached 读数同样参与排序
            states["zcode"] = new ProviderState { Id = "zcode", Status = "ready" }; states["zcode"].Quotas.Add(q(40));
            states["deepseek"] = new ProviderState { Id = "deepseek", Status = "ready" };   // 仅本机用量：ready 无读数
            states["claude"] = new ProviderState { Id = "claude", Status = "ready" }; states["claude"].Balances.Add(new Balance { Currency = "USD", Amount = 5 }); // 余额读数 = Tier 0
            states["cursor"] = new ProviderState { Id = "cursor", Status = "error" };
            // grok 故意缺状态（null → Tier 2）。
            var panel = Bare(config, states);
            var urgent = (Func<IEnumerable<string>, IEnumerable<string>>)Delegate.CreateDelegate(typeof(Func<IEnumerable<string>, IEnumerable<string>>), panel, typeof(MonitorPanel).GetMethod("Urgent", Hidden));
            // 输入顺序刻意打乱（目录序）：claude, zcode, deepseek, codex, kimi, cursor, grok。
            List<string> ordered = urgent(new[] { "claude", "zcode", "deepseek", "codex", "kimi", "cursor", "grok" }).ToList();
            Require(String.Join(",", ordered) == "codex,zcode,kimi,claude,deepseek,cursor,grok",
                "unexpected order: " + String.Join(",", ordered));
            Require(ordered.Count == 7, "ordering must not filter providers");
        }

    // ── D 期：hero 三档 preset 解析（month = 本月 1 日零点起；total = HorizonDays 上限）──
    private static void RangePresetCheck() {
        var month = new UsageRange { Preset = "month" };
        month.Resolve(new DateTime(2026, 3, 15, 8, 30, 0), out DateTime monthStart, out DateTime monthEnd);
        Require(monthStart == new DateTime(2026, 3, 1), "month must start at local midnight of the 1st");
        Require(monthEnd == new DateTime(2026, 3, 15, 8, 30, 0), "month end must be now");
        Require(month.Label() == "本月", "month label");
        // 月初边界：起点与当前时刻重合时不得出现 start >= end（既有钳制语义）。
        month.Resolve(new DateTime(2026, 3, 1, 0, 0, 0), out DateTime edgeStart, out DateTime edgeEnd);
        Require(edgeStart < edgeEnd, "month clamp on the 1st must keep a non-empty window");
        var total = new UsageRange { Preset = "total" };
        DateTime now = new DateTime(2026, 10, 10, 23, 5, 0);
        total.Resolve(now, out DateTime totalStart, out DateTime totalEnd);
        Require(totalStart == now.Date.AddDays(-(UsageArchive.HorizonDays - 1)), "total must reach the HorizonDays lower bound");
        Require(totalEnd == now && totalStart < totalEnd, "total window must end at now");
        Require(total.Label() == "全部", "total label");
        // 其余既有 preset 不回退：today / 30d 的解析保持原语义。
        var today = new UsageRange { Preset = "today" };
        today.Resolve(now, out DateTime todayStart, out _);
        Require(todayStart == now.Date, "today preset must stay at local midnight");
        var d30 = new UsageRange();
        d30.Resolve(now, out DateTime d30Start, out _);
        Require(d30Start == now.Date.AddDays(-29), "default 30d preset must stay unchanged");
        // UsageRange 字段默认仍是 30d（任务3 只改 RangeFor 的新键播种，不动类型默认）。
        Require(new UsageRange().Preset == "30d", "UsageRange field default must stay 30d (only RangeFor seeding changes)");
        var panel1 = Bare(new AppConfig { Enabled = ProviderCatalog.DefaultEnabled }, ProviderCatalog.All.ToDictionary(id => id, id => new ProviderState { Id = id }));
        Set(panel1, "ranges", new Dictionary<string, UsageRange>());   // readonly 字段初始化器不经 GetUninitializedObject 运行
        var ranges = (Dictionary<string, UsageRange>)typeof(MonitorPanel).GetField("ranges", Hidden).GetValue(panel1);
        var normalize = typeof(MonitorPanel).GetMethod("NormalizeOverviewRange", Hidden);
        var heroRange = (UsageRange)normalize.Invoke(panel1, null);
        Require(heroRange.Preset == "today", "a fresh overview range must normalize to today before data is computed, got " + heroRange.Preset);
        Require(ranges["overview"].Preset == "today", "the seeded overview entry must be today");
        // 所有键新建即「当天」（任务3）：详情页 / 紧凑单平台页 / 第三方板共用 RangeFor，
        // 非 overview 键同样播种 today；已存在的键原样返回（用户选中的档位不被覆盖）。
        var rangeFor = typeof(MonitorPanel).GetMethod("RangeFor", Hidden);
        var codexRange = (UsageRange)rangeFor.Invoke(panel1, new object[] { "codex" });
        Require(codexRange.Preset == "today", "a fresh provider page range must seed today, got " + codexRange.Preset);
        Require(ranges["codex"].Preset == "today", "the seeded provider entry must be stored under its key");
        var boardRange = (UsageRange)rangeFor.Invoke(panel1, new object[] { ProviderCatalog.ThirdParty });
        Require(boardRange.Preset == "today", "the third-party board range must seed today");
        ranges["codex"] = new UsageRange { Preset = "14d" };
        Require(((UsageRange)rangeFor.Invoke(panel1, new object[] { "codex" })).Preset == "14d", "an existing range must be returned untouched");
        // 用户选中其它档位后原样保留（归一化只针对非 hero 预设）。
        ranges["overview"] = new UsageRange { Preset = "7d" };
        heroRange = (UsageRange)normalize.Invoke(panel1, null);
        Require(heroRange.Preset == "today", "a stale non-hero preset must be normalized to today");
        ranges["overview"] = new UsageRange { Preset = "month" };
        heroRange = (UsageRange)normalize.Invoke(panel1, null);
        Require(heroRange.Preset == "month", "hero presets (month) must be kept as chosen");
        ranges["overview"] = new UsageRange { Preset = "30d" };
        heroRange = (UsageRange)normalize.Invoke(panel1, null);
        Require(heroRange.Preset == "today", "30d left over from a compact session must be normalized to today");
    }

    // ── D 期：账户遮罩（@ 前只留首尾字符；不足 2 字符只留首字符；域名不遮）──
    private static void AccountMaskCheck() {
        string dir = Path.Combine(Path.GetTempPath(), "cum-ui-mask-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string previousData = Store.SwapData(dir);
        Keychain.RunOverride = args => null;   // 隔离：绝不触碰真实钥匙串
        try {
            var panel = Bare(new AppConfig { Enabled = ProviderCatalog.DefaultEnabled, HideAccounts = true }, ProviderCatalog.All.ToDictionary(id => id, id => new ProviderState { Id = id }));
            var label = (Func<string, string, string>)Delegate.CreateDelegate(typeof(Func<string, string, string>), panel, typeof(MonitorPanel).GetMethod("AccountLabel", Hidden));
            Require(label("codex", "longname8@gmail.com") == "l***8@gmail.com", "masking must keep first and last local characters");
            Require(label("codex", "ab@x.com") == "a***b@x.com", "two-character local part keeps both ends");
            Require(label("codex", "a@x.com") == "a***@x.com", "single-character local part keeps the first character only");
            Require(label("codex", "no-email-value") == "no-email-value", "values without @ stay verbatim");
            // 关闭遮挡（设置可关）：原文返回。
            typeof(MonitorPanel).GetField("config", Hidden).SetValue(panel, new AppConfig { Enabled = ProviderCatalog.DefaultEnabled, HideAccounts = false });
            Require(label("codex", "longname8@gmail.com") == "longname8@gmail.com", "masking off must keep the full address");
        } finally {
            Keychain.RunOverride = null;
            Store.SwapData(previousData);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ── D 期：Sessions 会话页（行渲染 / 展开明细 / 加载更多 / 空态 / 页签）──
    private static void SessionsPageCheck() {
        EnsureRenderInterface();   // 页签 Clock 字形走 Geometry.Parse
        string dir = Path.Combine(Path.GetTempPath(), "cum-ui-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string previousData = Store.SwapData(dir);
        try {
            var config = new AppConfig { Enabled = ProviderCatalog.DefaultEnabled };
            var panel = Bare(config, ProviderCatalog.All.ToDictionary(id => id, id => new ProviderState { Id = id }));
            Set(panel, "thirdParty", new ThirdPartySummary());   // RenderTabs 的 ThirdPartyVisible
            Set(panel, "sessionDetails", new HashSet<string>());
            Set(panel, "sessionLimit", 50);
            Set(panel, "sessionLedgers", new Dictionary<string, SessionLedger>());
            var content = (Func<Control>)Delegate.CreateDelegate(typeof(Func<Control>), panel, typeof(MonitorPanel).GetMethod("SessionsContent", Hidden));
            // 空态：给文案而不是空白。
            var empty = (StackPanel)content();
            Require(Find<TextBlock>(empty).Any(t => (t.Text ?? "").Contains("还没有会话记录")), "empty state must explain itself");
            // 两个合成会话：row1 两个模型（展开后可见），row2 更旧。
            var ledgers = new Dictionary<string, SessionLedger>();
            var ledger = new SessionLedger { Agent = "codex" };
            SessionRow row = new SessionRow { Id = "abc12345-6789-aaaa-bbbb-ccccddddeeee", First = DateTime.UtcNow.AddHours(-2).ToString("o"), Last = DateTime.UtcNow.AddMinutes(-5).ToString("o") };
            row.Models["gpt-5.2"] = new Bucket { I = 1000, O = 500, R = 12, D = 0.75 };
            row.Models["gpt-5.1"] = new Bucket { I = 200, O = 100, R = 3, D = 0.25 };
            ledger.Sessions[row.Id] = row;
            SessionRow old = new SessionRow { Id = "ffffffff-0000-0000-0000-000000000000", First = DateTime.UtcNow.AddDays(-3).ToString("o"), Last = DateTime.UtcNow.AddDays(-2).ToString("o") };
            old.Models["demo-model-a"] = new Bucket { I = 50, R = 1, D = 0.01 };
            ledger.Sessions[old.Id] = old;
            ledgers["codex"] = ledger;
            Set(panel, "sessionLedgers", ledgers);
            var page = (StackPanel)content();
            // 行名在 Run 内联里、行内容挂在 Button.Content 下：无窗口宿主时按钮模板不
            // 实例化，可视化树拿不到 Content，必须沿逻辑树走（FindInContent 同款前提）；
            // Text 与 Run 内联一起收集。
            Func<Visual, IEnumerable<string>> logTexts = null;
            logTexts = v => {
                var parts = new List<string>();
                if (v is TextBlock tb) {
                    parts.Add(tb.Text ?? "");
                    if (tb.Inlines != null) foreach (Avalonia.Controls.Documents.Run r in tb.Inlines.OfType<Avalonia.Controls.Documents.Run>()) parts.Add(r.Text ?? "");
                }
                if (v is Avalonia.LogicalTree.ILogical logical) foreach (object child in logical.GetLogicalChildren()) if (child is Visual cv) parts.AddRange(logTexts(cv));
                return parts;
            };
            Func<StackPanel, string> textOf = p => String.Join("\n", logTexts(p));
            string collapsed = textOf(page);
            Require(collapsed.Contains("Codex") && collapsed.Contains("gpt-5.2"), "row must name the tool and its dominant model");
            Require(collapsed.Contains("$1.00"), "row must show the session cost (total across its models)");
            Require(collapsed.Contains("abc12345"), "row must show the session id prefix");
            Require(!collapsed.Contains("gpt-5.1"), "the second model must stay inside the breakdown until expanded");
            Require(collapsed.IndexOf("abc12345", StringComparison.Ordinal) < collapsed.IndexOf("demo-model-a", StringComparison.Ordinal), "rows must be ordered by last activity (newest first)");
            // 展开明细：模型逐行 + 完整会话 ID。
            ((HashSet<string>)typeof(MonitorPanel).GetField("sessionDetails", Hidden).GetValue(panel)).Add("codex|" + row.Id);
            string expanded = textOf((StackPanel)content());
            Require(expanded.Contains("gpt-5.1"), "breakdown must list the second model");
            Require(expanded.Contains(row.Id), "breakdown must show the full session id");
            // 分页：limit=1 时旧会话藏起、「加载更多」按钮出现。
            Set(panel, "sessionLimit", 1);
            StackPanel paged = (StackPanel)content();
            Require(!textOf(paged).Contains("demo-model-a"), "sessions beyond the limit must stay hidden");
            Require(Find<Button>(paged).Any(b => (b.Content as string ?? "").Contains("加载更多")), "a load-more button must be offered");
            // 页签：RenderTabs 出现「会话」按钮（无 SVG，走 Clock 字形兜底）。
            Set(panel, "tabs", new WrapPanel());
            typeof(MonitorPanel).GetMethod("RenderTabs", Hidden).Invoke(panel, null);
            var tabsPanel = (WrapPanel)typeof(MonitorPanel).GetField("tabs", Hidden).GetValue(panel);
            Button sessionsTab = Find<Button>(tabsPanel).FirstOrDefault(b => (AutomationProperties.GetName(b) ?? "") == "会话");
            Require(sessionsTab != null, "the sessions tab must exist");
            Require(logTexts(tabsPanel).Any(t => t == "会话"), "the sessions tab must be labelled 会话");
        } finally {
            Store.SwapData(previousData);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ── 窄条（edge）：平台行只含有 Quotas 的启用平台、按最紧剩余%升序、环弧着色、空态 ──
    private static void EdgeDockCheck() {
        EnsureRenderInterface();   // RingArc 的 StreamGeometry 需要已注册的渲染接口
        string dir = Path.Combine(Path.GetTempPath(), "cum-ui-edge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string previousData = Store.SwapData(dir);
        try {
            var config = new AppConfig { Enabled = new[] { "codex", "claude", "kimi", "zcode" } };
            Quota q(double remaining) { return new Quota { Label = "窗口", Used = 100 - remaining }; }
            var states = ProviderCatalog.All.ToDictionary(id => id, id => new ProviderState { Id = id });
            states["zcode"].Quotas.Add(q(5));
            states["codex"].Quotas.Add(q(30));
            states["kimi"].Quotas.Add(q(70));
            states["claude"].Status = "ready";      // ready 但无 Quotas：不出现
            states["cursor"].Quotas.Add(q(50));     // 有读数但未启用：不出现
            var panel = Bare(config, states);
            Set(panel, "history", new UsageHistory());
            var build = (Func<Control>)Delegate.CreateDelegate(typeof(Func<Control>), panel, typeof(MonitorPanel).GetMethod("EdgeDockContent", Hidden));
            Control dock = build();
            string joined = String.Join("\n", LogicalTextsOf(dock));
            Require(joined.Contains("5%") && joined.Contains("30%") && joined.Contains("70%"), "each quota provider must show its remaining percentage");
            Require(!joined.Contains("Claude"), "a ready provider without quotas must not appear");
            Require(!joined.Contains("Cursor"), "a disabled provider must not appear");
            Require(joined.IndexOf("5%", StringComparison.Ordinal) < joined.IndexOf("30%", StringComparison.Ordinal) && joined.IndexOf("30%", StringComparison.Ordinal) < joined.IndexOf("70%", StringComparison.Ordinal),
                "rows must follow ProviderOrder (tightest remaining first)");
            // 每个平台一条环弧；剩余 <10% 的那条转暖色（#E6A083，与 SegmentBar 同阈值）。
            var arcs = LogicalFind<Avalonia.Controls.Shapes.Path>(dock).Where(p => p.Stroke is Avalonia.Media.SolidColorBrush).ToList();
            Require(arcs.Count == 3, "one ring arc per quota provider, got " + arcs.Count);
            Func<Avalonia.Controls.Shapes.Path, string> rgb = p => { var c = ((Avalonia.Media.SolidColorBrush)p.Stroke).Color; return c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); };
            Require(arcs.Count(p => rgb(p) == "E6A083") == 1, "only the sub-10% ring must turn warm, got " + arcs.Count(p => rgb(p) == "E6A083"));
            Require(arcs.Count(p => rgb(p) != "E6A083") == 2, "the other rings keep their provider colours");
            // 平台行的自动化名（点击跳转完整面板对应页签的入口）。
            foreach (string id in new[] { "zcode", "codex", "kimi" })
                Require(LogicalFind<Button>(dock).Any(b => (AutomationProperties.GetName(b) ?? "") == "查看 " + ProviderCatalog.Name(id)), "row missing for " + id);
            // 空态：全部平台都没有 Quotas 时给文案，不空白。
            foreach (string id in new[] { "zcode", "codex", "kimi" }) states[id].Quotas.Clear();
            Require(LogicalTextsOf(build()).Any(t => t == I18n.T("暂无额度读数")), "the empty state must explain itself");
        } finally {
            Store.SwapData(previousData);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ── 窄条顶部今日汇总：无历史读 “--”，有历史显示今日 Token（Compact 口径）与费用 ──
    private static void EdgeSummaryCheck() {
        EnsureRenderInterface();   // 与 EdgeDockCheck 同一条构建路径
        string dir = Path.Combine(Path.GetTempPath(), "cum-ui-edge-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string previousData = Store.SwapData(dir);
        try {
            var config = new AppConfig { Enabled = new[] { "codex", "claude" } };
            var panel = Bare(config, ProviderCatalog.All.ToDictionary(id => id, id => new ProviderState { Id = id }));
            Set(panel, "history", new UsageHistory());
            var build = (Func<Control>)Delegate.CreateDelegate(typeof(Func<Control>), panel, typeof(MonitorPanel).GetMethod("EdgeDockContent", Hidden));
            Require(LogicalTextsOf(build()).Any(t => (t ?? "").Contains("--")), "no data at all must read --");
            var history = (UsageHistory)typeof(MonitorPanel).GetField("history", Hidden).GetValue(panel);
            history.Updated = DateTime.UtcNow.ToString("o");
            history.Days.Add(new DayUsage { Day = HistoryService.DayKey(DateTime.Today), Agent = "codex", Tokens = 2500000, Cost = 1.25 });
            history.Days.Add(new DayUsage { Day = HistoryService.DayKey(DateTime.Today.AddDays(-1)), Agent = "codex", Tokens = 9000000, Cost = 4.5 });
            string withData = String.Join("\n", LogicalTextsOf(build()));
            Require(withData.Contains("2.5M"), "the summary must show today's tokens only (2.5M), got: " + withData);
            Require(withData.Contains("$1.25"), "the summary must show today's cost only ($1.25)");
            Require(!withData.Contains("9M") && !withData.Contains("$4.50"), "yesterday's usage must not leak into today's summary");
            Require(!withData.Contains("--"), "dashes must disappear once today has data");
        } finally {
            Store.SwapData(previousData);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ── 窄条额度挑选（任务5）：环取最短窗口（kimi 5h 优先于月度）、无窗口回退最紧剩余
    // （grok 账期类）、Cursor Grok Bot 不参与；tooltip 仍逐行列出全部窗口 ──
    private static void EdgeQuotaPickCheck() {
        EnsureRenderInterface();   // 与 EdgeDockCheck 同一条构建路径
        string dir = Path.Combine(Path.GetTempPath(), "cum-ui-edge-quota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string previousData = Store.SwapData(dir);
        try {
            var config = new AppConfig { Enabled = new[] { "kimi", "grok", "cursor" } };
            var states = ProviderCatalog.All.ToDictionary(id => id, id => new ProviderState { Id = id });
            states["kimi"].Status = "ready";
            states["kimi"].Quotas.Add(new Quota { Label = "每月", Used = 20, WindowSeconds = 2592000, ResetUtc = DateTime.UtcNow.AddDays(20).ToString("o") });
            states["kimi"].Quotas.Add(new Quota { Label = "5 小时", Used = 45, WindowSeconds = 18000, ResetUtc = DateTime.UtcNow.AddHours(2).ToString("o") });
            // grok：两个额度都没有 WindowSeconds（账期类）→ 回退 MainQuota = 最紧剩余 30%。
            states["grok"].Status = "ready";
            states["grok"].Quotas.Add(new Quota { Label = "账期", Used = 70 });
            states["grok"].Quotas.Add(new Quota { Label = "另一账期", Used = 10 });
            // cursor：Grok Bot 周窗口更短也不参与挑选 → 环取套餐总量（60%）；tooltip 列出 Grok 行。
            states["cursor"].Status = "ready";
            states["cursor"].Quotas.Add(new Quota { Label = "套餐总量", Used = 40, WindowSeconds = 2592000, ResetUtc = DateTime.UtcNow.AddDays(15).ToString("o") });
            states["cursor"].Quotas.Add(new Quota { Label = Parsers.CursorGrokLabel, Used = 80, WindowSeconds = 604800, ResetUtc = DateTime.UtcNow.AddDays(3).ToString("o") });
            var panel = Bare(config, states);
            Set(panel, "history", new UsageHistory());
            var build = (Func<Control>)Delegate.CreateDelegate(typeof(Func<Control>), panel, typeof(MonitorPanel).GetMethod("EdgeDockContent", Hidden));
            Control dock = build();
            Func<string, Button> row = name => LogicalFind<Button>(dock).FirstOrDefault(b => (AutomationProperties.GetName(b) ?? "") == "查看 " + name);
            string kimiName = ProviderCatalog.Name("kimi"), grokName = ProviderCatalog.Name("grok"), cursorName = ProviderCatalog.Name("cursor");
            Require(row(kimiName) != null && row(grokName) != null && row(cursorName) != null, "all three quota providers must have a row");
            Func<Button, string> rowText = b => String.Join("\n", LogicalTextsOf(b));
            Require(rowText(row(kimiName)).Contains("55%"), "kimi's ring must show the shortest (5h) window remaining, got: " + rowText(row(kimiName)));
            Require(rowText(row(grokName)).Contains("30%"), "without windows the ring must fall back to the tightest quota, got: " + rowText(row(grokName)));
            Require(rowText(row(cursorName)).Contains("60%"), "Grok Bot must not be picked for the ring, got: " + rowText(row(cursorName)));
            // tooltip 遍历全部 Quotas：kimi 两行、cursor 含 Grok Bot 行、grok 两行。
            string kimiTip = ToolTip.GetTip(row(kimiName)) as string ?? "";
            Require(kimiTip.Contains("每月") && kimiTip.Contains("5 小时"), "kimi's tooltip must list every window, got: " + kimiTip);
            string cursorTip = ToolTip.GetTip(row(cursorName)) as string ?? "";
            Require(cursorTip.Contains("套餐总量") && cursorTip.Contains(Parsers.CursorGrokLabel), "cursor's tooltip must include the Grok Bot row, got: " + cursorTip);
            string grokTip = ToolTip.GetTip(row(grokName)) as string ?? "";
            Require(grokTip.Contains("账期") && grokTip.Contains("另一账期"), "grok's tooltip must list every quota, got: " + grokTip);
        } finally {
            Store.SwapData(previousData);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ── 窄条档位注册：DisplaySizes 末尾（绿灯循环）、名称、配置规范化兼容、几何上下限 ──
    private static void EdgeSizeRegistryCheck() {
        Require(MonitorPanel.DisplaySizes.Last() == "edge", "edge must close the cycle (green light order)");
        Require(MonitorPanel.DisplaySizes.Length == 5, "five display sizes expected");
        string name = (string)typeof(MonitorPanel).GetMethod("SizeName", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { "edge" });
        Require(name == "窄条", "edge display name");
        var config = new AppConfig { UiVersion = 2, DisplaySize = "edge" };
        WindowFrameRules.Normalize(config);
        Require(config.DisplaySize == "edge", "a saved edge size must survive normalization");
        config = new AppConfig { UiVersion = 2, DisplaySize = "huge" };
        WindowFrameRules.Normalize(config);
        Require(config.DisplaySize == "full", "unknown sizes must still fall back to full");
        config.Layouts["edge"] = new WindowGeometry { Width = 84, Height = 320 };
        WindowFrameRules.Normalize(config);
        Require(config.Layouts.ContainsKey("edge"), "a saved edge layout must be kept");
        Size min = WindowFrameRules.MinSize("edge"), def = WindowFrameRules.DefaultSize("edge"), max = WindowFrameRules.MaxSize("edge");
        Require(def.Width == 84 && min.Width <= def.Width && def.Width <= max.Width && min.Height <= def.Height && def.Height <= max.Height, "edge default must sit between its resize limits");
    }

    // ── 手动调整边（任务1）：ClampSize 纯函数与 WindowFrame.ApplyLimits 同口径 ──
    // full 档只有下限（上限 8192，不钳 MaxSize）；Compact 档（mode != "full"）Min/Max ×
    // UiScale 同时生效；非法缩放（0 / NaN）按 1 处理，非法尺寸落到下限。
    private static void ResizeClampCheck() {
        Size fullMin = ResizeChrome.ClampSize("full", 1, 300, 200);
        Require(fullMin.Width == WindowFrame.MinSize("full").Width && fullMin.Height == WindowFrame.MinSize("full").Height,
            "full must clamp to its minimum, got " + fullMin.Width + "x" + fullMin.Height);
        Size fullMax = ResizeChrome.ClampSize("full", 1, 20000, 20000);
        Require(fullMax.Width == 8192 && fullMax.Height == 8192, "full must cap at 8192, got " + fullMax.Width + "x" + fullMax.Height);
        foreach (string mode in new[] { "small", "medium", "large", "edge" }) {
            Size min = ResizeChrome.ClampSize(mode, 1.25, 1, 1);
            Require(min.Width == WindowFrame.MinSize(mode).Width * 1.25 && min.Height == WindowFrame.MinSize(mode).Height * 1.25,
                mode + " minimum must scale with the interface, got " + min.Width + "x" + min.Height);
            Size max = ResizeChrome.ClampSize(mode, 1.25, 99999, 99999);
            Require(max.Width == WindowFrame.MaxSize(mode).Width * 1.25 && max.Height == WindowFrame.MaxSize(mode).Height * 1.25,
                mode + " maximum must scale with the interface, got " + max.Width + "x" + max.Height);
            double midW = (WindowFrame.MinSize(mode).Width + WindowFrame.MaxSize(mode).Width) / 2, midH = (WindowFrame.MinSize(mode).Height + WindowFrame.MaxSize(mode).Height) / 2;
            Size mid = ResizeChrome.ClampSize(mode, 1, midW, midH);
            Require(mid.Width == midW && mid.Height == midH, mode + " must keep an in-range size untouched, got " + mid.Width + "x" + mid.Height);
            // 与 Core 的 WindowFrameRules 数值对照：两半边不得漂移。
            Size core = ResizeChrome.ClampSize(mode, 1, 0, 0);
            Require(core.Width == WindowFrameRules.MinSize(mode).Width && core.Height == WindowFrameRules.MinSize(mode).Height, mode + " clamp must match the Core rule values");
        }
        Size badScale = ResizeChrome.ClampSize("small", 0, 300, 300);
        Require(badScale.Width == 300 && badScale.Height == 300, "an invalid scale must be treated as 1, got " + badScale.Width + "x" + badScale.Height);
        Size badSize = ResizeChrome.ClampSize("small", 1, Double.NaN, Double.NaN);
        Require(badSize.Width == WindowFrame.MinSize("small").Width && badSize.Height == WindowFrame.MinSize("small").Height, "NaN sizes must fall to the minimum");
    }

    // ── 概览平台卡「本机用量」小节（任务4）：今日模型拆分（前 3 + 「+N 个模型」）、
    // 逐窗口一行的订阅窗口估算与「下界」徽标；无本机数据（无记录且无逐小时索引）时
    // 整块不渲染。另覆盖：长模型名省略号、套餐卡脚注的归属速度（join 成一行的旧版
    // 会把后面的窗口截掉；kimi CLI 自己没跑过时旧速度口径一直是空）。
    private static void ProviderCardLocalUsageCheck() {
        EnsureRenderInterface();   // QuotaRow 折叠箭头等字形走 Geometry.Parse
        string dir = Path.Combine(Path.GetTempPath(), "cum-ui-card-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string previousData = Store.SwapData(dir);
        try {
            var config = new AppConfig { Enabled = ProviderCatalog.DefaultEnabled };
            var states = ProviderCatalog.All.ToDictionary(id => id, id => new ProviderState { Id = id });
            var panel = Bare(config, states);
            Set(panel, "refreshingIds", new HashSet<string>());   // readonly，ProviderRefreshButton 需要
            Set(panel, "expandedQuotas", new HashSet<string>());  // readonly，QuotaRow 需要
            Set(panel, "history", new UsageHistory());
            Set(panel, "usageIndexes", new Dictionary<string, LogIndex>());   // 无逐小时索引 → 每日口径（下界）
            states["kimi"].Status = "ready";
            states["kimi"].Quotas.Add(new Quota { Label = "5 小时", Used = 40, WindowSeconds = 18000, ResetUtc = DateTime.UtcNow.AddHours(2).ToString("o") });
            states["kimi"].Quotas.Add(new Quota { Label = "每周", Used = 10, WindowSeconds = 604800, ResetUtc = DateTime.UtcNow.AddDays(3).ToString("o") });
            states["kimi"].Quotas.Add(new Quota { Label = "无窗口", Used = 5 });   // WindowSeconds=0：不进估算行
            states["kimi"].Quotas.Add(new Quota { Label = "备用", Used = 0, WindowSeconds = 3600, ResetUtc = DateTime.UtcNow.AddHours(1).ToString("o") });   // 剩余 100% → 「未使用」
            var history = (UsageHistory)typeof(MonitorPanel).GetField("history", Hidden).GetValue(panel);
            string longModel = "kimi-k2-scaling-token-plan-preview-with-an-extra-long-suffix-0713";
            DayUsage day = new DayUsage { Day = HistoryService.DayKey(DateTime.Today), Agent = "kimi", Tokens = 3000, Cost = 0.3 };
            day.Models.Add(new ModelUsage { Model = "kimi-k2", Tokens = 2000, TimedOutput = 1500, TimedSeconds = 3 });   // 归属计时合计 → 速度 500 t/s
            day.Models.Add(new ModelUsage { Model = longModel, Tokens = 700 });
            day.Models.Add(new ModelUsage { Model = "kimi-late", Tokens = 200 });
            day.Models.Add(new ModelUsage { Model = "kimi-tiny", Tokens = 100 });
            history.Days.Add(day);
            var build = (Func<ProviderState, Control>)Delegate.CreateDelegate(typeof(Func<ProviderState, Control>), panel, typeof(MonitorPanel).GetMethod("ProviderCard", Hidden));
            Control card = build(states["kimi"]);
            string text = String.Join("\n", LogicalTextsOf(card));
            // 小节标题（kimi 非账户口径 → 本机用量）与「下界」徽标（无索引 → 每日口径）。
            Require(text.Contains("本机用量"), "the card must contain the local-usage section title, got: " + text);
            Require(text.Contains("只计完整自然日 · 下界"), "without a hourly index the badge must read the lower-bound tone");
            // 今日模型拆分：按 Token 降序前 3 行，第 4 个合并成「+1 个模型」；token Compact 口径。
            Require(text.Contains("kimi-k2") && text.Contains(longModel) && text.Contains("kimi-late"), "the top three models must each have a row, got: " + text);
            Require(!text.Contains("kimi-tiny"), "the fourth model must merge into the plus-more line");
            Require(text.Contains("+1 个模型"), "a fourth model must add the +1 more-models line");
            Require(text.Contains("2K") && text.Contains("700") && text.Contains("200"), "each model row must show its compact token count, got: " + text);
            // 长模型名：行内收省略号（Text 保留全名给 tooltip），不再挤压占比条与右列。
            TextBlock longName = LogicalFind<TextBlock>(card).FirstOrDefault(t => (t.Text ?? "") == longModel);
            Require(longName != null, "the long model row must exist with the full name as its text");
            Require(longName.TextTrimming == TextTrimming.CharacterEllipsis, "the long model name must ellipsize instead of squeezing the share bar");
            // 订阅窗口估算（套餐归属卡口径）：逐窗口一行（行数 = 窗口数），格式升级为
            // 「已用 X · 满额约 Y tok」；无窗口额度不进；剩余 100% 的窗口读「未使用」。
            // 每周窗口必然覆盖今天 → 已用 ≥3K 下界、满额 = 3K ÷ 10% = 30K；5 小时窗口
            // 是否计入今天取决于当时钟点（每日口径只计窗口内的整天：03:00 前窗口跨零点
            // → ≥3K 且满额 3K ÷ 40% = 7.5K，之后 → 已用 — 且满额 —）。
            List<TextBlock> windowBlocks = LogicalFind<TextBlock>(card).Where(t => { string s = t.Text ?? ""; return s.Contains(" 已用 ") || s.Contains("未使用"); }).ToList();
            List<string> windowLines = windowBlocks.Select(t => t.Text ?? "").ToList();
            Require(windowLines.Count == 3, "each window must get its own estimate line (3 windows), got " + windowLines.Count + ": " + String.Join(" | ", windowLines));
            string line5h = windowLines.First(x => x.StartsWith("5 小时 ", StringComparison.Ordinal));
            string lineWeek = windowLines.First(x => x.StartsWith("每周 ", StringComparison.Ordinal));
            string lineBackup = windowLines.Single(x => x.Contains("未使用"));
            string[] cells = line5h.Split(new[] { " · " }, StringSplitOptions.None);
            Require(cells[0] == "5 小时 已用 ≥3K" || cells[0] == "5 小时 已用 —", "the 5h cell must read the used amount (lower bound or an em dash, time-of-day dependent), got: " + line5h);
            Require(cells.Length > 1 && (cells[0].EndsWith("≥3K") ? cells[1] == "满额约 7.5K tok" : cells[1] == "满额约 —"), "the 5h full-quota estimate must follow the used amount, got: " + line5h);
            Require(lineWeek == "每周 已用 ≥3K · 满额约 30K tok", "the weekly estimate must read used ≥3K and a 30K full quota, got: " + lineWeek);
            Require(lineBackup == "备用 未使用", "a fully unused window must read 未使用 instead of 0, got: " + lineBackup);
            Require(windowLines.All(x => !x.Contains("无窗口")), "a quota without a window length must not enter the estimate lines");
            // 每行自带本窗口的 tooltip（满额推算说明 + 归属口径说明），行间不串行。
            foreach (TextBlock block in windowBlocks) {
                string tip = ToolTip.GetTip(block) as string;
                Require(tip != null && tip.Contains("口径：所有客户端里归属于本套餐的用量"), "each window line must carry its own scope tooltip, got: " + (tip ?? "<null>"));
            }
            Require(ToolTip.GetTip(windowBlocks[0]) != ToolTip.GetTip(windowBlocks[1]), "window tooltips must not be one shared string");
            // 套餐卡速度：从归属数据（近 7 天计时合计）算 —— kimi CLI 自己的按日行之外，
            // ZCode 等客户端里走 Kimi 套餐的计时也计入；1500 tok ÷ 3 s → 500 t/s。
            TextBlock speed = LogicalFind<TextBlock>(card).FirstOrDefault(t => (t.Text ?? "") == "500 t/s");
            Require(speed != null, "the plan card footer must show the attributed output speed, got: " + text);
            string speedTip = ToolTip.GetTip(speed) as string;
            Require(speedTip != null && speedTip.Contains("近 7 天输出速度") && speedTip.Contains("包含首字延迟"), "the speed tooltip must carry the timing definition, got: " + (speedTip ?? "<null>"));
            // 非套餐卡（codex）同样逐窗口一行（旧 join 口径两窗口同样超宽），速度仍按
            // harness 自己的近 7 天（当日 1200 tok ÷ 2 s → 600 t/s）。
            states["codex"].Status = "ready";
            states["codex"].Quotas.Add(new Quota { Label = "5 小时", Used = 20, WindowSeconds = 18000, ResetUtc = DateTime.UtcNow.AddHours(4).ToString("o") });
            states["codex"].Quotas.Add(new Quota { Label = "每周", Used = 30, WindowSeconds = 604800, ResetUtc = DateTime.UtcNow.AddDays(2).ToString("o") });
            history.Days.Add(new DayUsage { Day = HistoryService.DayKey(DateTime.Today), Agent = "codex", Tokens = 4000, Cost = 0.4, TimedOutput = 1200, TimedSeconds = 2 });
            Control codexCard = build(states["codex"]);
            // 非套餐行口径：WindowTokens 只给「≥X」（下界）或「—」（窗口不含完整一天，
            // 时间点相关）；倒计时 chips 同样以「5 小时 /」开头，用窗口值形态区分。
            Func<string, bool> isWindowLine = s => s.StartsWith("5 小时 ≥", StringComparison.Ordinal) || s.StartsWith("5 小时 —", StringComparison.Ordinal)
                || s.StartsWith("每周 ≥", StringComparison.Ordinal) || s.StartsWith("每周 —", StringComparison.Ordinal);
            List<TextBlock> codexBlocks = LogicalFind<TextBlock>(codexCard).Where(t => isWindowLine(t.Text ?? "")).ToList();
            List<string> codexLines = codexBlocks.Select(t => t.Text ?? "").ToList();
            Require(codexLines.Count == 2, "the codex card must also give each window its own line (2 windows), got " + codexLines.Count + ": " + String.Join(" | ", codexLines));
            Require(codexLines.Any(x => x == "每周 ≥4K"), "the codex weekly line must read the lower bound, got: " + String.Join(" | ", codexLines));
            string codex5h = codexLines.First(x => x.StartsWith("5 小时 ", StringComparison.Ordinal));
            Require(codex5h == "5 小时 ≥4K" || codex5h == "5 小时 —", "the codex 5h line must read the lower bound or an em dash, got: " + codex5h);
            foreach (TextBlock block in codexBlocks) {
                string tip = ToolTip.GetTip(block) as string;
                Require(tip != null && tip.Contains("保守下界"), "each codex window line must carry its own lower-bound tooltip, got: " + (tip ?? "<null>"));
            }
            Require(LogicalFind<TextBlock>(codexCard).Any(t => (t.Text ?? "") == "600 t/s"), "the non-plan card speed must stay on the harness's own last-7-days figures");
            // 无本机数据（清空历史、无索引）：整块不渲染 —— 标题与徽标都不出现。
            history.Days.Clear();
            Control bare = build(states["kimi"]);
            string empty = String.Join("\n", LogicalTextsOf(bare));
            Require(!empty.Contains("本机用量") && !empty.Contains("只计完整自然日"), "without local data the whole peek must stay hidden, got: " + empty);
        } finally {
            Store.SwapData(previousData);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ── D 期：趋势卡聚合（history.Days 按日跨平台求和 → Peak；无数据整卡不出现）──
    private static void TrendChartCheck() {
        EnsureRenderInterface();   // StreamGeometry 构建（折线 / 面积）需要已注册的渲染接口
        string dir = Path.Combine(Path.GetTempPath(), "cum-ui-trend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string previousData = Store.SwapData(dir);
        try {
            var config = new AppConfig { Enabled = new[] { "codex", "claude" } };
            var panel = Bare(config, ProviderCatalog.All.ToDictionary(id => id, id => new ProviderState { Id = id }));
            Set(panel, "history", new UsageHistory());
            Set(panel, "chartMetric", new Dictionary<string, string>());
            Set(panel, "chartPinned", new Dictionary<string, DateTime>());
            var trend = (Func<Control>)Delegate.CreateDelegate(typeof(Func<Control>), panel, typeof(MonitorPanel).GetMethod("TrendChart", Hidden));
            // 无数据：整卡不出现。
            Require(trend() == null, "empty history must hide the trend card");
            // 已知数据：今天 1.0M Token / $2.00；昨天两个平台各 0.6M（跨平台按日求和 = 1.2M）、
            // 费用 $1.50；40 天前关闭平台的 5M 不得计入。费用口径 Peak = 今天 $2.00。
            var history = (UsageHistory)typeof(MonitorPanel).GetField("history", Hidden).GetValue(panel);
            string todayKey = HistoryService.DayKey(DateTime.Today), yesterdayKey = HistoryService.DayKey(DateTime.Today.AddDays(-1));
            history.Days.Add(new DayUsage { Day = todayKey, Agent = "codex", Tokens = 1000000, Cost = 2.0 });
            history.Days.Add(new DayUsage { Day = yesterdayKey, Agent = "codex", Tokens = 600000, Cost = 1.0 });
            history.Days.Add(new DayUsage { Day = yesterdayKey, Agent = "claude", Tokens = 600000, Cost = 0.5 });
            history.Days.Add(new DayUsage { Day = HistoryService.DayKey(DateTime.Today.AddDays(-40)), Agent = "pi", Tokens = 5000000, Cost = 9.0 });
            var card = (Border)trend();
            Require(card != null, "known history must build the trend card");
            Require(card.Child != null, "trend card content missing");
            Require(Find<Avalonia.Controls.Shapes.Path>(card).Count() >= 2, "trend must draw the line and the area geometry");
            Func<string, bool> has = text => Find<TextBlock>(card).Any(t => (t.Text ?? "").Contains(text));
            // 默认口径 Token（任务2）：Peak = 昨天 1.2M（跨平台按日合计；今天单平台只有 1M）。
            Require(has("1.2M"), "token peak must be the default metric and aggregate both agents of the same day");
            Require(!has("5M"), "usage of disabled providers must not count");
            // 切到费用口径：Peak = 今天 $2.00（费用切换能力保留）。
            ((Dictionary<string, string>)typeof(MonitorPanel).GetField("chartMetric", Hidden).GetValue(panel))["trend"] = "cost";
            var costCard = (Border)trend();
            Func<string, bool> hasCost = text => Find<TextBlock>(costCard).Any(t => (t.Text ?? "").Contains(text));
            Require(hasCost("$2.00"), "cost peak must read today's summed cost after switching");
            // 切到 Token 口径：Peak 回到 1.2M（保留切换回归）。
            ((Dictionary<string, string>)typeof(MonitorPanel).GetField("chartMetric", Hidden).GetValue(panel))["trend"] = "tokens";
            var tokenCard = (Border)trend();
            Func<string, bool> has2 = text => Find<TextBlock>(tokenCard).Any(t => (t.Text ?? "").Contains(text));
            Require(has2("1.2M"), "token peak must aggregate both agents of the same day");
            // 指标切换片存在（复用 MetricToggle：Token 在前、费用在后）。
            Require(Find<Button>(card).Any(b => (AutomationProperties.GetName(b) ?? "") == I18n.T("图表显示费用")), "metric toggle missing");
            var toggles = Find<Button>(card).Where(b => (AutomationProperties.GetName(b) ?? "").StartsWith(I18n.T("图表显示"), StringComparison.Ordinal)).Select(b => AutomationProperties.GetName(b)).ToList();
            Require(toggles.Count >= 2 && toggles[0] == I18n.T("图表显示Token") && toggles[1] == I18n.T("图表显示费用"), "metric toggle must list tokens before cost, got: " + String.Join("/", toggles));
        } finally {
            Store.SwapData(previousData);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // 未初始化 MonitorPanel + 反射注入被测路径需要的字段。
    private static MonitorPanel Bare(AppConfig config, Dictionary<string, ProviderState> states, bool demo = false) {
        var panel = (MonitorPanel)RuntimeHelpers.GetUninitializedObject(typeof(MonitorPanel));
        Set(panel, "demo", demo);
        Set(panel, "config", config);
        Set(panel, "states", states);
        Set(panel, "icons", new Dictionary<string, string>());   // readonly，凭据面板折叠头的 Icon 需要（空表 → 首字母回退，无几何解析）
        Set(panel, "accountHosts", new Dictionary<string, StackPanel>());   // readonly，PopulateAccountSection 需要
        Set(panel, "credentialPanelOpen", new Dictionary<string, bool>());  // readonly，凭据面板折叠状态（会话内）
        Set(panel, "panelAccountHosts", new Dictionary<string, StackPanel>());   // readonly，面板账户区重建上下文（C 期）
        Set(panel, "panelClearButtons", new Dictionary<string, Button>());       // readonly，「清除」按钮就地重算可用态（C 期）
        Set(panel, "customListResult", new TextBlock { FontSize = 11 });    // readonly，RenderCustomList 需要
        return panel;
    }
        private static void Set(MonitorPanel panel, string field, object value) {
            typeof(MonitorPanel).GetField(field, Hidden).SetValue(panel, value);
        }
        private static void Require(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
        // 未挂到窗口的控件树：可视化子级在 Add 时就已维护，深度遍历即可。
        private static IEnumerable<T> Find<T>(Visual root) where T : Visual {
            if (root is T match) yield return match;
            foreach (Visual child in root.GetVisualChildren()) foreach (T found in Find<T>(child)) yield return found;
        }
    private static Visual FindByName(Visual root, string name) {
        if (root is Control control && AutomationProperties.GetName(control) == name) return root;
        foreach (Visual child in root.GetVisualChildren()) { Visual found = FindByName(child, name); if (found != null) return found; }
        return null;
    }
    // 折叠头里的状态胶囊挂在 Button.Content 下：无窗口宿主时按钮模板不实例化，
    // ContentPresenter 不进可视化树，这类内容需沿逻辑树查找。
    private static Visual FindInContent(Visual root, string name) {
        if (root is Control control && AutomationProperties.GetName(control) == name) return root;
        if (root is Avalonia.LogicalTree.ILogical logical) foreach (object child in logical.GetLogicalChildren()) { Visual found = FindInContent(child as Visual, name); if (found != null) return found; }
        return null;
    }
    // 窄条（edge）内容包在 ScrollViewer 里：无窗口宿主时模板不实例化，可视化树拿不到
    // Content，同样沿逻辑树遍历；Text 与 Run 内联一起收集（同 SessionsPageCheck 的做法）。
    private static IEnumerable<string> LogicalTextsOf(Visual root) {
        var parts = new List<string>();
        if (root is TextBlock tb) {
            parts.Add(tb.Text ?? "");
            if (tb.Inlines != null) foreach (Avalonia.Controls.Documents.Run r in tb.Inlines.OfType<Avalonia.Controls.Documents.Run>()) parts.Add(r.Text ?? "");
        }
        if (root is Avalonia.LogicalTree.ILogical logical) foreach (object child in logical.GetLogicalChildren()) if (child is Visual cv) parts.AddRange(LogicalTextsOf(cv));
        return parts;
    }
    private static IEnumerable<T> LogicalFind<T>(Visual root) where T : Visual {
        if (root is T match) yield return match;
        if (root is Avalonia.LogicalTree.ILogical logical) foreach (object child in logical.GetLogicalChildren()) if (child is Visual cv) foreach (T found in LogicalFind<T>(cv)) yield return found;
    }
    }
}
