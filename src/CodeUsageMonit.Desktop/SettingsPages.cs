// 设置 › 数据（设备、SQL 导入导出、备份、WebDAV）与 › 关于。
// 差异：文件对话框走 StorageProvider；恢复确认走自绘暗色对话框；explorer.exe → open；
// 密钥文案 DPAPI → 钥匙串。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Controls.Documents;
using Avalonia.VisualTree;
using Avalonia;
using Avalonia.Threading;

namespace CodeUsageMonit {
    // Settings › 数据 (other devices, SQL import / export, backups, WebDAV) and › 关于.
    public sealed partial class MonitorPanel {
        // What the 数据 page hands to Save; its buttons act at once.
        private sealed class DataFields {
            public TextBox Device, Url, User; public TextBox Password; public CheckBox Forget;
            public Func<int> BackupHours, BackupKeep, SyncMinutes; public Func<string> BackupFolder;
        }
        private static readonly int[] BackupChoices = { 0, 6, 24, 72, 168 }, SyncChoices = { 0, 15, 60, 360, 1440 };

        private DataFields BuildDataPage(StackPanel page) {
            var fields = new DataFields();
            DeviceIdentity self = demo ? new DeviceIdentity { Id = "demo", Name = Environment.MachineName } : Devices.Self();

            // Devices: this one (its name) and the ones imported here.
            page.Children.Add(SectionTitle(I18n.T("设备")));
            var deviceCard = new StackPanel();
            var nameRow = Row();
            fields.Device = new TextBox { Text = self.Name, Width = 170, MaxLength = 40, IsEnabled = !demo };
            AutomationProperties.SetName(fields.Device, I18n.T("本机名称"));
            AddRow(nameRow, FieldLabel(I18n.T("本机名称"), I18n.T("导出和同步时用它标识这台电脑")), fields.Device);
            deviceCard.Children.Add(nameRow);
            var deviceList = new StackPanel(); deviceCard.Children.Add(deviceList);
            Action renderDevices = null;
            renderDevices = () => {
                deviceList.Children.Clear(); deviceList.Children.Add(Separator());
                List<DeviceData> imported = demo ? new List<DeviceData>() : Devices.Imported();
                if (imported.Count == 0) { deviceList.Children.Add(Hint(I18n.T("还没有其他设备的用量。在另一台电脑上导出 SQL 后在下面导入，或两台都设置同一个 WebDAV 文件夹。"), 0)); return; }
                deviceList.Children.Add(Label(I18n.T("已导入的设备"), 11, InkDim));
                foreach (DeviceData d in imported) {
                    var row = Row(); row.Margin = new Thickness(0, 8, 0, 0);
                    var left = new StackPanel();
                    left.Children.Add(Label(d.Name, 12.5, Ink));
                    DateTime when; string at = LogIndex.Parse(d.Imported, out when) ? when.ToLocalTime().ToString("M/d HH:mm", CultureInfo.InvariantCulture) : "—";
                    var sub = Label(I18n.T("近一个月 {0} Token · {1} · {2} 更新", Compact(d.Tokens()), Usd(d.Cost()), at), 10.5, InkFaint); sub.Margin = new Thickness(0, 2, 0, 0); left.Children.Add(sub);
                    var remove = Styled(new Button { Content = I18n.T("移除"), Foreground = WarnBrush }, "link-button");
                    AutomationProperties.SetName(remove, I18n.T("移除") + " " + d.Name);
                    string id = d.Id; bool confirm = false;
                    remove.Click += delegate {
                        if (!confirm) { confirm = true; remove.Content = I18n.T("再次点击确认"); return; }
                        try { Devices.Remove(id); } catch { }
                        ReloadDevices(true); renderDevices();
                    };
                    AddRow(row, left, remove); deviceList.Children.Add(row);
                }
                deviceList.Children.Add(Hint(I18n.T("其他设备的用量计入概览、各平台和第三方页面。Cursor 的用量取自账户本身，不会重复计入。"), 10));
            };
            renderDevices();
            page.Children.Add(Card(deviceCard));

            // SQL import / export.
            page.Children.Add(SectionTitle(I18n.T("导入与导出")));
            var transfer = new StackPanel();
            transfer.Children.Add(Hint(I18n.T("把本机（和已导入设备）的每小时用量导出为 SQL 文件，在另一台电脑上导入即可合并查看；同一文件重复导入不会重复计算。文件只有 Token、请求数、费用和耗时，没有对话、密钥或账户，也可以直接导入 SQLite。"), 0));
            var transferButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var export = Styled(new Button { Content = I18n.T("导出 SQL…"), IsEnabled = !demo, Margin = new Thickness(0, 0, 8, 0) }, "secondary");
            var exportCsv = Styled(new Button { Content = I18n.T("导出 CSV…"), IsEnabled = !demo, Margin = new Thickness(0, 0, 8, 0) }, "secondary");
            var exportJson = Styled(new Button { Content = I18n.T("导出 JSON…"), IsEnabled = !demo, Margin = new Thickness(0, 0, 8, 0) }, "secondary");
            var import = Styled(new Button { Content = I18n.T("导入 SQL…"), IsEnabled = !demo }, "secondary");
            transferButtons.Children.Add(export); transferButtons.Children.Add(exportCsv); transferButtons.Children.Add(exportJson); transferButtons.Children.Add(import); transfer.Children.Add(transferButtons);
            var transferResult = Hint(demo ? I18n.T("演示模式不读写数据。") : "", 8); transfer.Children.Add(transferResult);
            Action<string, string, Func<string>> exportAs = null;
            exportAs = (extension, title, build) => {
                var button = extension == "sql" ? export : extension == "csv" ? exportCsv : exportJson;
                var ignored = ExportUsage(button, transferResult, extension, title, build);
            };
            export.Click += delegate { exportAs("sql", I18n.T("导出用量"), () => Devices.ExportSql(DateTime.UtcNow, true)); };
            // Phase 4.1：CSV / JSON 覆盖归档合并后的全量窗口（SQL 上传口径不变）。
            exportCsv.Click += delegate { exportAs("csv", I18n.T("导出用量（CSV）"), () => Devices.ExportCsv(Devices.ExportData(DateTime.UtcNow))); };
            exportJson.Click += delegate { exportAs("json", I18n.T("导出用量（JSON）"), () => Devices.ExportJson(Devices.ExportData(DateTime.UtcNow))); };
            import.Click += async delegate {
                string path = await Dialogs.PickOpenFile(settingsWindow ?? window, I18n.T("导入用量"), Dialogs.Sql);
                if (path == null) return;
                import.IsEnabled = false;
                try {
                    if (new FileInfo(path).Length > 256L * 1024 * 1024) throw new InvalidDataException(I18n.T("文件超过 256 MB。"));
                    ImportResult result = await Task.Run(() => Devices.ImportSql(File.ReadAllText(path), DateTime.UtcNow));
                    Say(transferResult, result.Rows + result.Same + result.Duplicate + result.Own > 0, result.Summary());
                    if (result.Rows > 0) { ReloadDevices(false); renderDevices(); }
                } catch (Exception e) { Say(transferResult, false, I18n.T("导入失败：{0}", e.Message)); }
                finally { import.IsEnabled = true; }
            };
            page.Children.Add(Card(transfer));

            // Backups.
            page.Children.Add(SectionTitle(I18n.T("备份与恢复")));
            var backupCard = new StackPanel();
            int backupHours = config.BackupHours, backupKeep = Math.Max(1, Math.Min(50, config.BackupKeep)); string backupFolder = config.BackupFolder ?? "";
            Func<AppConfig> backupConfig = () => new AppConfig { BackupFolder = backupFolder, BackupKeep = backupKeep, BackupHours = backupHours };
            var autoRow = Row();
            AddRow(autoRow, FieldLabel(I18n.T("自动备份"), I18n.T("到时间后在后台备份一次")), Segmented(BackupChoices.Select(h => new[] { h.ToString(CultureInfo.InvariantCulture), h == 0 ? I18n.T("关闭") : h < 24 ? I18n.T("{0} 小时", h) : h == 24 ? I18n.T("每天") : h == 168 ? I18n.T("每周") : I18n.T("{0} 天", h / 24) }).ToArray(), Nearest(BackupChoices, backupHours).ToString(CultureInfo.InvariantCulture), code => backupHours = Int32.Parse(code, CultureInfo.InvariantCulture)));
            backupCard.Children.Add(autoRow); backupCard.Children.Add(Separator());
            var keepRow = Row(); var keepStepper = new StackPanel { Orientation = Orientation.Horizontal };
            var keepMinus = Styled(new Button(), "icon-button");
            keepMinus.Width = 28; keepMinus.Height = 28;
            keepMinus.Content = Icons.GlyphBound(keepMinus, Icons.Minus, 11);
            ToolTip.SetTip(keepMinus, I18n.T("少保留一份"));
            var keepValue = Label(I18n.T("{0} 份", backupKeep), 12, Ink); keepValue.MinWidth = 48; keepValue.TextAlignment = TextAlignment.Center; Tabular(keepValue);
            var keepPlus = Styled(new Button(), "icon-button");
            keepPlus.Width = 28; keepPlus.Height = 28;
            keepPlus.Content = Icons.GlyphBound(keepPlus, Icons.Plus, 11);
            ToolTip.SetTip(keepPlus, I18n.T("多保留一份"));
            keepMinus.Click += delegate { backupKeep = Math.Max(1, backupKeep - 1); keepValue.Text = I18n.T("{0} 份", backupKeep); };
            keepPlus.Click += delegate { backupKeep = Math.Min(50, backupKeep + 1); keepValue.Text = I18n.T("{0} 份", backupKeep); };
            keepStepper.Children.Add(keepMinus); keepStepper.Children.Add(keepValue); keepStepper.Children.Add(keepPlus);
            AddRow(keepRow, FieldLabel(I18n.T("保留份数"), I18n.T("只删除较早的自动备份，手动备份不会被删除")), keepStepper);
            backupCard.Children.Add(keepRow); backupCard.Children.Add(Separator());
            var whereRow = Row();
            var whereLabel = FieldLabel(I18n.T("备份位置"), Backups.Folder(backupConfig()));
            var whereButtons = new StackPanel { Orientation = Orientation.Horizontal };
            var change = Styled(new Button { Content = I18n.T("更改…"), IsEnabled = !demo }, "secondary");
            var openBackups = Styled(new Button { Content = I18n.T("打开"), Margin = new Thickness(4, 0, 0, 0) }, "link-button");
            whereButtons.Children.Add(change); whereButtons.Children.Add(openBackups);
            AddRow(whereRow, whereLabel, whereButtons); backupCard.Children.Add(whereRow);
            var backupList = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            var backupResult = Hint(demo ? I18n.T("演示模式不读写数据。") : "", 8);
            Action renderBackups = null;
            Action<string> restore = path => {
                var ignored = Dialogs.Confirm(settingsWindow ?? window, "codeusagemonit", I18n.T("用这份备份覆盖当前的设置和用量数据吗？\n\n恢复前会先把当前状态另存一份备份；恢复后程序会重新启动。")).ContinueWith(t => {
                    if (!t.Result) return;
                    try { Backups.Restore(backupConfig(), path, DateTime.UtcNow); Dispatcher.UIThread.Post(RestartFresh); }
                    catch (Exception e) { Dispatcher.UIThread.Post(() => Say(backupResult, false, I18n.T("恢复失败：{0}", e.Message))); }
                });
            };
            renderBackups = () => {
                backupList.Children.Clear();
                var sub = ((StackPanel)whereLabel).Children.OfType<TextBlock>().ElementAtOrDefault(1);
                if (sub != null) sub.Text = Backups.Folder(backupConfig());
                List<BackupInfo> items;
                try { items = demo ? new List<BackupInfo>() : Backups.List(backupConfig()); } catch { items = new List<BackupInfo>(); }
                backupList.Children.Add(Separator());
                if (items.Count == 0) { backupList.Children.Add(Hint(I18n.T("还没有备份。"), 0)); return; }
                foreach (BackupInfo item in items.Take(5)) {
                    var row = Row(); row.Margin = new Thickness(0, 4, 0, 4);
                    var left = Label(item.Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "  ·  " + (item.Auto ? I18n.T("自动") : I18n.T("手动")) + "  ·  " + FileSize(item.Size), 11.5, Ink); Tabular(left);
                    var restoreButton = Styled(new Button { Content = I18n.T("恢复"), IsEnabled = !demo }, "link-button");
                    string path = item.Path; restoreButton.Click += delegate { restore(path); };
                    AddRow(row, left, restoreButton); backupList.Children.Add(row);
                }
                if (items.Count > 5) backupList.Children.Add(Hint(I18n.T("共 {0} 份，较早的在备份文件夹里", items.Count), 2));
            };
            backupCard.Children.Add(backupList);
            var backupButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var backupNow = Styled(new Button { Content = I18n.T("立即备份"), IsEnabled = !demo, Margin = new Thickness(0, 0, 8, 0) }, "secondary");
            var restoreFile = Styled(new Button { Content = I18n.T("从文件恢复…"), IsEnabled = !demo }, "secondary");
            backupButtons.Children.Add(backupNow); backupButtons.Children.Add(restoreFile); backupCard.Children.Add(backupButtons);
            backupCard.Children.Add(backupResult);
            backupCard.Children.Add(Hint(I18n.T("备份包含设置、自定义平台、接口切换记录、用量索引和已导入的设备，不含 API 密钥（它们只能由本机当前用户在钥匙串里解密，恢复时保持不变）。换电脑或多台电脑一起用，请用上面的导入导出或 WebDAV。"), 10));
            change.Click += async delegate {
                string folder = await Dialogs.PickFolder(settingsWindow ?? window, I18n.T("选择备份文件夹"), Backups.Folder(backupConfig()));
                if (folder == null) return;
                backupFolder = String.Equals(folder.TrimEnd('/'), Path.Combine(Store.Data, "backups"), StringComparison.Ordinal) ? "" : folder;
                renderBackups(); Say(backupResult, true, I18n.T("保存设置后生效。"));
            };
            openBackups.Click += delegate { string dir = Backups.Folder(backupConfig()); try { Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo("open", "\"" + dir + "\"") { UseShellExecute = false }); } catch { } };
            backupNow.Click += async delegate {
                backupNow.IsEnabled = false; AppConfig c = backupConfig();
                try { string path = await Task.Run(() => Backups.Create(c, false, DateTime.UtcNow)); Say(backupResult, true, I18n.T("已备份：{0}", Path.GetFileName(path))); renderBackups(); }
                catch (Exception e) { Say(backupResult, false, I18n.T("备份失败：{0}", e.Message)); }
                finally { backupNow.IsEnabled = true; }
            };
            restoreFile.Click += async delegate {
                string initial = Directory.Exists(Backups.Folder(backupConfig())) ? Backups.Folder(backupConfig()) : null;
                string path = await Dialogs.PickOpenFile(settingsWindow ?? window, I18n.T("选择备份文件"), Dialogs.Zip, initial);
                if (path != null) restore(path);
            };
            renderBackups();
            fields.BackupHours = () => backupHours; fields.BackupKeep = () => backupKeep; fields.BackupFolder = () => backupFolder;
            page.Children.Add(Card(backupCard));

            // WebDAV.
            page.Children.Add(SectionTitle(I18n.T("云同步（WebDAV）")));
            var syncCard = new StackPanel();
            syncCard.Children.Add(Hint(I18n.T("几台电脑填同一个 WebDAV 文件夹（坚果云、Nextcloud、群晖等）。每台只上传自己的用量、下载其他设备的用量，互不覆盖；上传的内容与 SQL 导出相同。"), 0));
            // 配置字段可能被手改的 settings.json 置成 null（反序列化直赋字段），输入框一律按空串起步；
            // KeyBox 不赋 Text 时 Avalonia 的 TextBox.Text 是 null（未输入过），保存路径会 NRE。
            syncCard.Children.Add(FieldStack(I18n.T("文件夹地址"), fields.Url = new TextBox { Text = config.WebDavUrl ?? "", IsEnabled = !demo }, I18n.T("例如 https://dav.jianguoyun.com/dav/codeusagemonit/")));
            syncCard.Children.Add(FieldStack(I18n.T("用户名"), fields.User = new TextBox { Text = config.WebDavUser ?? "", IsEnabled = !demo }, null));
            bool savedPassword = !demo && Store.HasSavedKey(WebDavSync.KeyId);
            syncCard.Children.Add(FieldStack(I18n.T("密码"), fields.Password = new KeyBox { Text = "", IsEnabled = !demo }, savedPassword ? I18n.T("已保存（钥匙串加密）。留空则保留。坚果云请用“应用密码”。") : I18n.T("坚果云请在“账户信息 › 安全选项”里生成应用密码。")));
            if (savedPassword) { fields.Forget = SwitchRow(I18n.T("移除已保存的密码"), null, false); fields.Forget.Margin = new Thickness(0, 8, 0, 0); syncCard.Children.Add(fields.Forget); }
            syncCard.Children.Add(Separator());
            int syncMinutes = Nearest(SyncChoices, config.SyncMinutes);
            var intervalRow = Row();
            AddRow(intervalRow, FieldLabel(I18n.T("自动同步"), null), Segmented(SyncChoices.Select(m => new[] { m.ToString(CultureInfo.InvariantCulture), m == 0 ? I18n.T("手动") : m < 60 ? I18n.T("{0} 分钟", m) : m < 1440 ? I18n.T("{0} 小时", m / 60) : I18n.T("每天") }).ToArray(), syncMinutes.ToString(CultureInfo.InvariantCulture), code => syncMinutes = Int32.Parse(code, CultureInfo.InvariantCulture)));
            syncCard.Children.Add(intervalRow);
            var syncButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var test = Styled(new Button { Content = I18n.T("测试连接"), IsEnabled = !demo, Margin = new Thickness(0, 0, 8, 0) }, "secondary");
            var syncNow = Styled(new Button { Content = I18n.T("立即同步"), IsEnabled = !demo }, "secondary");
            syncButtons.Children.Add(test); syncButtons.Children.Add(syncNow); syncCard.Children.Add(syncButtons);
            var syncResult = Hint("", 8); syncCard.Children.Add(syncResult);
            Action showState = () => {
                if (demo) { syncResult.Text = I18n.T("演示模式不读写数据。"); return; }
                SyncState state = WebDavSync.State(); DateTime last;
                if (!LogIndex.Parse(state.Last, out last)) { syncResult.Text = I18n.T("还没有同步过。"); syncResult.Foreground = InkFaint; return; }
                Say(syncResult, state.Ok, I18n.T("上次同步 {0} · {1}", last.ToLocalTime().ToString("M/d HH:mm", CultureInfo.InvariantCulture), state.Message));
            };
            // 密码框读数经 BoxText 判空：留空（含 null）→ 用已存密码；非空 → 用新输入。
            Func<string> password = () => { string own = BoxText(fields.Password); return own.Length > 0 ? own : Store.SavedKey(WebDavSync.KeyId); };
            test.Click += async delegate {
                test.IsEnabled = false; syncResult.Foreground = InkDim; syncResult.Text = I18n.T("正在连接…");
                string url = BoxText(fields.Url), user = BoxText(fields.User).Trim(), pass = password();
                try { Say(syncResult, true, await Task.Run(() => WebDavSync.Test(config, url, user, pass))); }
                catch (Exception e) { Say(syncResult, false, e is ProviderException ? e.Message : I18n.T("无法连接：{0}", e.Message)); }
                finally { test.IsEnabled = true; }
            };
            syncNow.Click += async delegate {
                syncNow.IsEnabled = false; syncResult.Foreground = InkDim; syncResult.Text = I18n.T("正在同步…");
                await SyncNow(BoxText(fields.Url), BoxText(fields.User).Trim(), password());
                showState(); renderDevices(); syncNow.IsEnabled = true;
            };
            showState();
            syncCard.Children.Add(Hint(I18n.T("密码以钥匙串加密保存在本机；同步只上传用量数字，不上传密钥、对话或账户。"), 10));
            fields.SyncMinutes = () => syncMinutes;
            page.Children.Add(Card(syncCard));
            return fields;
        }
        private static int Nearest(int[] choices, int value) { return choices.OrderBy(c => Math.Abs(c - value)).First(); }
        // Phase 4.1：SQL / CSV / JSON 三个导出按钮共用的落盘流程（保存对话框 + 后台生成）。
        private async Task ExportUsage(Button button, TextBlock result, string extension, string title, Func<string> build) {
            string safe = new String(Devices.Self().Name.Select(c => Char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
            string path = await Dialogs.PickSaveFile(settingsWindow ?? window, title, "codeusagemonit-" + safe + "-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture), extension);
            if (path == null) return;
            button.IsEnabled = false;
            try { await Task.Run(() => File.WriteAllText(path, build(), new UTF8Encoding(false))); Say(result, true, I18n.T("已导出到 {0}", path)); }
            catch (Exception e) { Say(result, false, I18n.T("导出失败：{0}", e.Message)); }
            finally { button.IsEnabled = true; }
        }
        private static string FileSize(long bytes) { return bytes >= 1 << 20 ? (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : Math.Max(1, bytes / 1024) + " KB"; }
        private static void Say(TextBlock text, bool ok, string message) { text.Foreground = ok ? GoodBrush : WarnBrush; text.Text = message; }
        // 未输入过的 TextBox.Text 是 null（Avalonia 语义），保存 / 同步路径统一按空串读取。
        internal static string BoxText(TextBox box) { return box == null ? "" : box.Text ?? ""; }
        private static Control FieldStack(string title, Control input, string hint) {
            var stack = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            stack.Children.Add(Label(title, 12, Ink));
            input.Margin = new Thickness(0, 6, 0, 0); AutomationProperties.SetName(input, title); stack.Children.Add(input);
            if (hint != null) stack.Children.Add(Hint(hint, 4));
            return stack;
        }
        // WebDAV sync with the given details (the 数据 page, before Save) or the saved ones.
        private async Task<bool> SyncNow(string url, string user, string password) {
            string message; bool ok; AppConfig c = config;
            SyncOutcome outcome = null;
            try { outcome = await Task.Run(() => WebDavSync.RunDetailed(c, url, user, password, DateTime.UtcNow, null)); message = outcome.Message; ok = true; }
            catch (Exception e) { message = e is ProviderException ? e.Message : I18n.T("同步失败：{0}", e.Message); ok = false; }
            WebDavSync.Record(ok, message, DateTime.UtcNow, outcome);   // Phase 4.4：带回 CAS 基线
            if (ok) ReloadDevices(false);
            return ok;
        }

        // ── 服务状态（Phase 4.3）────────────────────────────────────────
        // 各平台官方状态页的小条目：绿 / 黄 / 红圆点 + 名称 + 官方描述。设置页打开时才
        // 可能联网，缓存 10 分钟（service-status.json），不随 30 秒刷新走；请求失败
        // 静默沿用缓存，绝不影响额度通道。只点链接会打开官方页面，应用内不发其他请求。
        private Control BuildServiceStatusCard(bool demoMode) {
            var card = new StackPanel();
            var rows = new StackPanel(); card.Children.Add(rows);
            var footer = Hint("", 6); card.Children.Add(footer);
            Action<ServiceStatus.Cache> render = cache => {
                rows.Children.Clear(); rows.Children.Add(Separator());
                if (cache == null || cache.Readings.Count == 0) { rows.Children.Add(Hint(I18n.T("暂时查不到官方状态。"), 0)); footer.Text = ""; return; }
                foreach (ServiceStatus.Reading reading in cache.Readings) {
                    string level = ServiceStatus.Level(reading.Indicator);
                    var row = Row(); row.Margin = new Thickness(0, 7, 0, 0);
                    var left = new StackPanel { Orientation = Orientation.Horizontal };
                    left.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = level == "ok" ? GoodBrush : level == "warn" ? WarnBrush : Brush("#F08A7E"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
                    var name = Label(reading.Name, 12, Ink); left.Children.Add(name);
                    if (reading.Description.Length > 0) {
                        var desc = Label("  " + reading.Description, 11, InkDim); desc.MaxWidth = 250; left.Children.Add(desc);
                    }
                    var open = Styled(new Button { Content = I18n.T("状态页"), Margin = new Thickness(8, 0, 0, 0) }, "link-button");
                    string page = reading.Page;
                    open.Click += delegate { try { if (page.StartsWith("https://")) Process.Start(new ProcessStartInfo(page) { UseShellExecute = true }); } catch { } };
                    AddRow(row, left, open); rows.Children.Add(row);
                }
                DateTime at;
                footer.Text = LogIndex.Parse(cache.Updated, out at) ? I18n.T("缓存于 {0} · 10 分钟内不重复请求", at.ToLocalTime().ToString("M/d HH:mm", CultureInfo.InvariantCulture)) : "";
            };
            var refresh = Styled(new Button { Content = I18n.T("刷新"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, 10, 0, 0), IsEnabled = !demoMode }, "link-button");
            card.Children.Add(refresh);
            ServiceStatus.Cache cached = ServiceStatus.Load();
            render(demoMode ? cached : ServiceStatus.Stale(cached, DateTime.UtcNow) ? new ServiceStatus.Cache() : cached);
            if (!demoMode) {
                AppConfig c = config;
                Func<Task> update = async () => {
                    refresh.IsEnabled = false; footer.Foreground = InkDim; footer.Text = I18n.T("正在查询官方状态页…");
                    try {
                        ServiceStatus.Cache fresh = await Task.Run(() => ServiceStatus.Refresh(c));
                        Dispatcher.UIThread.Post(() => { render(fresh); refresh.IsEnabled = true; });
                    } catch { Dispatcher.UIThread.Post(() => { render(ServiceStatus.Load()); refresh.IsEnabled = true; }); }   // 失败静默
                };
                if (ServiceStatus.Stale(cached, DateTime.UtcNow)) { var ignored = update(); }
                refresh.Click += delegate { var ignored = update(); };
            }
            return card;
        }

        // ── 关于 ──────────────────────────────────────────────────────────
        // Returns the automatic update check switch (saved with the other settings).
        private CheckBox BuildAboutPage(StackPanel page) {
            page.Children.Add(SectionTitle(I18n.T("关于")));
            var card = new StackPanel();
            var name = Label("codeusagemonit " + AppInfo.Version, 14, Ink); name.FontWeight = FontWeight.SemiBold; card.Children.Add(name);
            card.Children.Add(Hint(I18n.T("code用量监控 · 开源免费 · MIT 许可 · macOS（Avalonia）"), 3));
            card.Children.Add(Separator());
            var updateRow = Row();
            var updateLabel = FieldLabel(I18n.T("检查更新"), "");
            var updateText = (TextBlock)((StackPanel)updateLabel).Children[1]; updateText.TextWrapping = TextWrapping.Wrap; updateText.TextTrimming = TextTrimming.None;
            var updateButtons = new StackPanel { Orientation = Orientation.Horizontal };
            var download = Styled(new Button { Content = I18n.T("前往下载"), Margin = new Thickness(0, 0, 8, 0), IsVisible = false }, "primary");
            var check = Styled(new Button { Content = I18n.T("检查更新") }, "secondary");
            updateButtons.Children.Add(download); updateButtons.Children.Add(check);
            AddRow(updateRow, updateLabel, updateButtons); card.Children.Add(updateRow);
            Action showUpdate = () => {
                UpdateState u = updateState; DateTime at; string when = LogIndex.Parse(u.Checked, out at) ? at.ToLocalTime().ToString("M/d HH:mm", CultureInfo.InvariantCulture) : "";
                bool newer = Updates.Newer(u);
                download.IsVisible = newer;
                updateText.Foreground = newer ? GoodBrush : u.Error.Length > 0 ? WarnBrush : InkFaint;
                updateText.Text = I18n.T(newer ? I18n.T("发现新版本 v{0}（当前 v{1}）", u.Latest, AppInfo.Version)
                    : u.Error.Length > 0 ? u.Error
                    : u.Latest.Length > 0 ? I18n.T("已是最新版本 · {0} 检查", when)
                    : I18n.T("当前 v{0}", AppInfo.Version));
            };
            download.Click += delegate { string url = updateState.Url.Length > 0 ? updateState.Url : Updates.Releases; try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } };
            check.Click += async delegate {
                check.IsEnabled = false; updateText.Foreground = InkDim; updateText.Text = I18n.T("正在检查…");
                updateState = await Updates.Check(config, DateTime.UtcNow);
                showUpdate(); UpdateStatus(); check.IsEnabled = true;
            };
            showUpdate();
            card.Children.Add(Separator());
            CheckBox auto = SwitchRow(I18n.T("每天自动检查更新"), I18n.T("只向 GitHub 查询最新版本号，不上传任何数据"), config.UpdateCheck);
            card.Children.Add(auto);
            page.Children.Add(Card(card));

            page.Children.Add(SectionTitle(I18n.T("链接")));
            var links = new StackPanel();
            foreach (var link in new[] {
                new { Title = I18n.T("项目主页（GitHub）"), Target = "https://github.com/nimeng1222/codeusagemonit-macos" } }) {
                string target = link.Target;
                var button = Styled(new Button { Content = link.Title, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, 0, 0, 2) }, "link-button");
                button.Click += delegate { try { if (target.StartsWith("https://") || File.Exists(target)) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { } };
                links.Children.Add(button);
            }
            links.Children.Add(Hint(I18n.T("数据目录 {0}", Store.Data), 6));
            links.Children.Add(Hint(I18n.T("与 OpenAI、Anthropic、Cursor 等被监控的服务均无关联；产品名称和图标归各自所有者。"), 4));
            page.Children.Add(Card(links));
            return auto;
        }

        // The language changed on Save: the tray menu and every page are rebuilt in it.
        private void LanguageChanged() {
            try { if (tray != null) { tray.Dispose(); } } catch { }
            trayStatusItem = null;
            CreateTray();
            UpdateTray(); SetTrayFirstItem(!window.IsVisible); Render();
        }
    }
}
