// Windows provider adapters, written for this project after studying how CodexBar (MIT)
// and CodeZeno/Claude-Code-Usage-Monitor (MIT) talk to each provider; no code is shared.
// See THIRD-PARTY-NOTICES.md. Credentials remain in their owning applications.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
// System.Web.Script.Serialization: the Compat/JavaScriptSerializer.cs compatibility layer
// (System.Text.Json implementation preserving the JavaScriptSerializer member surface
// and the Dictionary<string, object> / object[] boxing shape).
using System.Web.Script.Serialization;

namespace CodeUsageMonit {
    public sealed class Quota {
        public string Label, ResetUtc;
        public double WindowSeconds;
        public double Used;
        public double Remaining { get { return Math.Max(0, 100 - Used); } }
    }
    public sealed class Balance { public string Currency; public double Amount; }
    public sealed class ProductConsumption {
        public string Product;
        // Percentage points taken from the shared allowance, not a quota of its own.
        public double UsedPercent;
        [ScriptIgnore]
        public string DisplayName {
            get {
                if (String.IsNullOrEmpty(Product)) return "";
                if (Product.IndexOf("build", StringComparison.OrdinalIgnoreCase) >= 0) return "Build";
                if (Product.IndexOf("chat", StringComparison.OrdinalIgnoreCase) >= 0) return "Chat";
                return Product;
            }
        }
    }
    public sealed class ProviderState {
        public string Id, Status = "loading", Message = "等待首次读取", Plan = "", Account = "", LastSuccess = "", LastAttempt = "";
        public List<Quota> Quotas = new List<Quota>();
        public List<Balance> Balances = new List<Balance>();
        public List<ProductConsumption> ProductUsage = new List<ProductConsumption>();
        public bool Stale;
        public int? ResetCreditsAvailable;
        public List<string> ResetCreditExpiries = new List<string>();
        public string ResetCreditsUpdated = "", ResetCreditsError = "";
    }
    public sealed class ModelUsage { public string Model; public double Tokens, Cost, TimedOutput, TimedSeconds; }
    public sealed class DayUsage {
        public string Day, Agent;
        // Tokens = fresh input + output + cache read + cache write (+ other counted tokens).
        // CachedTokens is the cache-read subset; InputTokens excludes cached input.
        public double Cost, Tokens, CachedTokens, InputTokens, OutputTokens, CacheCreationTokens, Requests, UnpricedTokens;
        // Requests with a measured duration: output tokens / seconds = output speed (OutputTiming).
        public double TimedRequests, TimedOutput, TimedSeconds;
        public bool CostKnown = true;
        public List<ModelUsage> Models = new List<ModelUsage>();
    }
    public sealed class UsageHistory {
        public string Updated = "", Error = "", Zone = "", Engine = "";
        public List<DayUsage> Days = new List<DayUsage>();
    }
    public sealed class AppConfig {
        public string Proxy = "auto";
        public int RefreshMinutes = 5;
        public bool PinWindow = false;
        public bool HideAccounts = true;
        public string[] Enabled = (string[])ProviderCatalog.DefaultEnabled.Clone();
        public int UiVersion;
        public bool AlwaysOnTop = false;
        public bool HideOnDeactivate = false;
        public double? WindowLeft, WindowTop;
        public double WindowWidth = 420, WindowHeight = 790, UiScale = 1;
        public string Material = "acrylic";
        public bool ShowThirdParty = true;
        // Fetch the repository's pricing.json once a day (Pricing.SyncAsync).
        public bool PriceSync = true;
        public string KimiRegion = "china", ZaiRegion = "china";
        // StepFun 开放平台的接口区域："global"（国际站 platform.stepfun.ai，默认）| "china"（国内站 platform.stepfun.com）。
        public string StepfunRegion = "global";
        // Alpha of the smoke layer over Acrylic/Mica (0–1). Settings show it as
        // transparency = 1 − alpha, so 0% transparency is fully opaque.
        public double SurfaceOpacity = .66;
        // Colour of that layer (#RRGGBB); text stays light, so dark colours read best.
        public string SurfaceColor = "#15171B";
        // Optional background picture: a file in data/ (background.*), how it fills the
        // window (fill | fit | tile) and how much it is dimmed for legibility (0–0.85).
        public string BackgroundImage = "", BackgroundFit = "fill";
        public double BackgroundDim = .35;
        // One window, four sizes: "small" | "medium" | "large" | "full" (the panel).
        public string DisplaySize = "full";
        // Page shown by the compact sizes: "overview" or a provider id.
        public string CompactProvider = "overview";
        // Position of the compact sizes before each size kept its own geometry (read once).
        public double? CompactLeft, CompactTop;
        // Size and position of each compact size; the full panel uses WindowLeft/Top/Width/Height.
        public Dictionary<string, WindowGeometry> Layouts = new Dictionary<string, WindowGeometry>();
        // Interface language: "auto" (follows Windows) | "zh" | "en".
        public string Language = "auto";
        // Automatic backups (Backups in DataSync.cs): every BackupHours (0 = off), keeping the
        // newest BackupKeep automatic ones, in BackupFolder (empty = data\backups).
        public int BackupHours = 24, BackupKeep = 7;
        public string BackupFolder = "";
        // WebDAV sync of usage between devices: folder URL, user name (the password is a
        // DPAPI key, "webdav"), and how often to sync automatically (0 = only on demand).
        public string WebDavUrl = "", WebDavUser = "";
        public int SyncMinutes = 0;
        // Look for a newer release on GitHub once a day.
        public bool UpdateCheck = true;
        // Per-platform refresh (Pacing.cs): a platform whose client is writing its logs is read at
        // least every InUseMinutes; ProviderMinutes holds platforms with their own interval.
        public bool FastInUse = true;
        public int InUseMinutes = 2;
        public Dictionary<string, int> ProviderMinutes = new Dictionary<string, int>();
        // Refresh cadence (ROADMAP Phase 2): "adaptive" scales each platform's interval with how
        // fast its quota burns (LimitsRuntime, 2–30 min); "fixed" keeps the configured periods.
        public string RefreshMode = "adaptive";
        // ROADMAP Phase 5（每平台多账户）：每平台一条 ProviderAccounts；字典缺省 = 单个
        // 隐式默认账户（Accounts.cs）。旧配置没有这一字段，反序列化后为空字典，行为不变。
        public Dictionary<string, ProviderAccounts> Accounts = new Dictionary<string, ProviderAccounts>();
    }
    public sealed class WindowGeometry { public double Width, Height; public double? Left, Top; }
    public static class AppInfo {
        public const string ShortVersion = "1.0";
        public const string UserAgent = "codeusagemonit/" + ShortVersion;
        // Full version from the assembly ("1.0.0").
        public static string Version { get { System.Version v = typeof(AppInfo).Assembly.GetName().Version; return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build); } }
    }
    public static class ProviderCatalog {
        public static readonly string[] Ids = { "codex", "claude", "cursor", "antigravity", "deepseek", "grok", "copilot", "kimi", "opencode", "zcode", "pi", "mimo", "stepfun" };
        // New installs start with the original six; the rest are opt-in in Settings.
        // 共享的 static readonly 数组：赋给任何 config.Enabled 都必须 Clone（各消费点见
        // AppConfig 字段初始化器 / Cli / AppMonitor / Settings），否则全部实例别名同一数组。
        public static readonly string[] DefaultEnabled = { "codex", "claude", "cursor", "antigravity", "deepseek", "grok" };
        // Not a quota provider: a page of usage that went through third-party endpoints.
        public const string ThirdParty = "thirdparty";
        // User-defined HTTP JSON providers (CustomProviders.cs), keyed by "custom-…" id.
        public static readonly Dictionary<string, CustomProvider> Custom = new Dictionary<string, CustomProvider>();
        public static IEnumerable<string> All { get { return Ids.Concat(Custom.Keys.OrderBy(k => k, StringComparer.Ordinal)); } }
        public static bool IsKnown(string id) { return Ids.Contains(id) || Custom.ContainsKey(id); }
        // Providers without an account quota: only local session logs.
        public static bool LocalOnly(string id) { return id == "pi"; }
        // Usage read from the account (Cursor's dashboard) rather than this machine's logs:
        // it covers every device signed in to the account.
        public static bool AccountUsage(string id) { return id == "cursor"; }
        // The local client a provider's usage is read from, when it is not the obvious one.
        public static string UsageSource(string id) { return id == "deepseek" ? "DeepSeek Harness" : null; }
        public static string UsageTitle(string id) { return AccountUsage(id) ? "账户用量" : "本机用量"; }
        public static string Name(string id) {
            CustomProvider custom; if (id != null && Custom.TryGetValue(id, out custom)) return custom.Name;
            switch (id) { case "codex": return "Codex"; case "claude": return "Claude"; case "cursor": return "Cursor"; case "antigravity": return "Antigravity"; case "deepseek": return "DeepSeek"; case "grok": return "Grok"; case "copilot": return "Copilot"; case "kimi": return "Kimi"; case "opencode": return "OpenCode"; case "zcode": return "ZCode"; case "pi": return "Pi"; case "mimo": return "MiMo"; case "stepfun": return "StepFun"; case ThirdParty: return "第三方"; default: return "概览"; }
        }
        public static string Color(string id) {
            CustomProvider custom; if (id != null && Custom.TryGetValue(id, out custom)) return custom.Color.Length > 0 ? custom.Color : "#A9B4C6";
            switch (id) { case "codex": return "#63D5E5"; case "claude": return "#E8AB8C"; case "cursor": return "#80DDAB"; case "antigravity": return "#B8A0F5"; case "deepseek": return "#7FA9FF"; case "grok": return "#D8DEE9"; case "copilot": return "#E58FD0"; case "kimi": return "#F4C95D"; case "opencode": return "#A6D96A"; case "zcode": return "#F2874E"; case "pi": return "#A9B4C6"; case "mimo": return "#FF6900"; case "stepfun": return "#6E8BFF"; case ThirdParty: return "#9DB4CF"; default: return "#8CB9FF"; }
        }
        public static string Help(string id) {
            if (id != null && Custom.ContainsKey(id)) return "检查接口地址、字段路径和密钥（设置 → 自定义平台），然后刷新。";
            switch (id) {
                case "codex": return "使用 Codex 应用或 CLI 登录后，点击刷新。";
                case "claude": return "使用 Claude Code 登录后，点击刷新。";
                case "cursor": return "使用 Cursor 编辑器登录后，点击刷新。";
                case "antigravity": return "打开 Antigravity 并完成登录，保持应用运行后刷新。也可使用已登录的 agy CLI。";
                case "deepseek": return "填写 DeepSeek API Key 即可查询 API 账户余额（卡片或设置中都可以填写），也可设置环境变量 DEEPSEEK_API_KEY。";
                case "grok": return "使用 Grok Build CLI 执行 grok login，完成登录后刷新。";
                case "copilot": return "使用 GitHub 设备码登录：在浏览器输入一次性代码授权，完成后自动连接。";
                case "kimi": return "填写 Kimi Code API Key（在 kimi.com/code/console 创建）、设置环境变量 KIMI_CODE_API_KEY，或先用 Kimi Code 客户端登录再点击「一键导入本机登录」（支持 OAuth 登录）。";
                case "opencode": return "OpenCode Go 订阅：填写 OpenCode API Key，或设置环境变量 OPENCODE_API_KEY。";
                case "zcode": return "填写智谱 / Z.ai 的 API Key（GLM 编码套餐），或设置环境变量 Z_AI_API_KEY。";
                case "pi": return "Pi 没有账户额度，这里只统计本机日志中的用量。";
                case "mimo": return "已登录小米 MiMo 桌面客户端时自动续期凭据，无需手动操作；也可在浏览器登录 MiMo 控制台（platform.xiaomimimo.com）后复制完整 Cookie 粘贴到这里（需包含 api-platform_serviceToken 与 userId），或设置环境变量 MIMO_COOKIE。";
                case "stepfun": return "填写阶跃星辰 StepFun 开放平台的 Oasis-Token（platform.stepfun.ai 国际站 / platform.stepfun.com 国内站），或设置环境变量 STEPFUN_TOKEN。";
                default: return "";
            }
        }
        // Environment variables checked before the key saved in Settings.
        public static string[] KeyEnv(string id, AppConfig config) {
            switch (id) {
                case "deepseek": return new[] { "DEEPSEEK_API_KEY" };
                case "kimi": return new[] { "KIMI_CODE_API_KEY" };
                case "opencode": return new[] { "OPENCODE_API_KEY" };
                case "zcode": return config != null && config.ZaiRegion == "global" ? new[] { "Z_AI_API_KEY" } : new[] { "Z_AI_API_KEY", "BIGMODEL_API_KEY", "ZHIPU_API_KEY", "ZHIPUAI_API_KEY", "GLM_API_KEY" };
                case "mimo": return new[] { "MIMO_COOKIE" };
                case "stepfun": return new[] { "STEPFUN_TOKEN" };
                default: return new string[0];
            }
        }
        // 「打开控制台」的单一注册点（Connect 卡片与设置认证页的凭据面板共用）：按平台给出
        // 取 Key / Cookie / Token 的控制台地址；没有控制台入口的平台返回空串。StepFun 跟随
        // 接口区域（config.StepfunRegion）选站，复用 Fetch 的同一份 host 映射（StepFunHost）。
        public static string ConsoleUrl(string id, AppConfig config) {
            return id == "zcode" ? "https://www.z.ai/"
                : id == "kimi" ? "https://www.kimi.com/code/console"
                : id == "mimo" ? "https://platform.xiaomimimo.com/console/plan-manage"
                : id == "stepfun" ? Parsers.StepFunHost(config != null ? config.StepfunRegion : null) + "/"
                : id == "deepseek" ? "https://platform.deepseek.com/api_keys"
                : id == "opencode" ? "https://opencode.ai/zen"
                : "";
        }
    }
    public static class J {
        public static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 100 }; }
        public static object Parse(string text) { return Serializer().DeserializeObject(text); }
        public static object Get(object obj, params string[] keys) {
            foreach (string key in keys) { var d = obj as IDictionary<string, object>; if (d == null || !d.TryGetValue(key, out obj)) return null; }
            return obj;
        }
        public static string Str(object obj, params string[] keys) { object v = keys.Length == 0 ? obj : Get(obj, keys); return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture); }
        public static double? Num(object obj, params string[] keys) {
            object v = keys.Length == 0 ? obj : Get(obj, keys); if (v == null || v is bool) return null;
            double n; return Double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out n) && !Double.IsNaN(n) && !Double.IsInfinity(n) ? (double?)n : null;
        }
        public static IEnumerable<object> Arr(object obj) { var a = obj as IEnumerable; if (a == null || obj is string || obj is IDictionary) yield break; foreach (object v in a) yield return v; }
        public static Dictionary<string, object> Dict(object obj) { return obj as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        public static object File(string path) { if (!System.IO.File.Exists(path)) return null; return Parse(System.IO.File.ReadAllText(path)); }
        public static string Iso(object value) {
            double? seconds = Num(value);
            if (seconds.HasValue && seconds > 100000000) {
                // Some endpoints return epoch milliseconds; treat 12+ digit values as ms.
                double s = seconds.Value > 100000000000 ? seconds.Value / 1000 : seconds.Value;
                try { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s).ToString("o"); } catch { return ""; }
            }
            DateTimeOffset date; return DateTimeOffset.TryParse(Str(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date) ? date.UtcDateTime.ToString("o") : "";
        }
        public static object Jwt(string token) { try { string[] p = token.Split('.'); if (p.Length < 2) return null; string s = p[1].Replace('-', '+').Replace('_', '/'); s += new string('=', (4 - s.Length % 4) % 4); return Parse(Encoding.UTF8.GetString(Convert.FromBase64String(s))); } catch { return null; } }
    }
    public static class Store {
        // Windows 安装器的注册表键名（仅跨平台口径记录，macOS 不使用注册表）。
        public const string InstallRegistryKey = @"Software\codeusagemonit";
        public const string InstallRegistryValue = "InstallPath";
        public static readonly string Root = NormalizeDirectory(AppDomain.CurrentDomain.BaseDirectory);
        // Chosen on first read. EnableDemoMode assigns this before anything reads it, so
        // --demo never migrates or opens the real data directory.
        private static string dataPath;
        public static string Data {
            get {
                if (dataPath == null) dataPath = ChooseDataFor(Root, ReadInstallPath(), Environment.GetEnvironmentVariable("CODEUSAGEMONIT_DATA"), UserDataDirectory());
                return dataPath;
            }
            set { dataPath = value; }
        }
        public static void EnableDemoMode() { Data = Path.Combine(Root, "verification", "demo-data"); }
        // Self-tests point the data directory at a temporary folder and back, without choosing
        // (or migrating) the real one.
        internal static string SwapData(string path) { string old = dataPath; dataPath = path; return old; }
        // Zip, Scoop and install.ps1 keep using <exe>\data. setup.exe writes installed.txt
        // (and HKCU\Software\codeusagemonit\InstallPath); those copies use
        // %LOCALAPPDATA%\codeusagemonit so Program Files can stay read-only.
        // portable.txt next to the exe forces <exe>\data even for an installed copy.
        // CODEUSAGEMONIT_DATA overrides the directory unless portable.txt is present.
        public static string UserDataDirectory() {
            // Windows: %LOCALAPPDATA%\codeusagemonit；macOS 惯例: ~/Library/Application Support/codeusagemonit
            //（.NET 在 macOS 上 SpecialFolder.ApplicationData 即 ~/Library/Application Support）。
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "codeusagemonit");
        }
        public static string ResolveData(string root, string installPath, string envData, bool installedMarker, string userData) {
            string beside = Path.Combine(root ?? "", "data");
            try { if (File.Exists(Path.Combine(root ?? "", "portable.txt"))) return beside; } catch { }
            if (!String.IsNullOrWhiteSpace(envData)) { try { return Path.GetFullPath(envData.Trim()); } catch { } }
            if (installedMarker || PathsEqual(installPath, root)) return String.IsNullOrEmpty(userData) ? beside : userData;
            // macOS：.app 包内不可写、GUI 与 CLI 要共用数据，默认落用户目录（portable.txt 仍强制便携模式）。
            if (OperatingSystem.IsMacOS() && !String.IsNullOrEmpty(userData)) return userData;
            return beside;
        }
        public static string ChooseDataFor(string root, string installPath, string envData, string userData) {
            bool marker = false;
            try { marker = File.Exists(Path.Combine(root, "installed.txt")); } catch { }
            string chosen = ResolveData(root, installPath, envData, marker, userData);
            if (!String.IsNullOrEmpty(userData) && PathsEqual(chosen, userData)) TryMigratePortableData(Path.Combine(root, "data"), chosen);
            return chosen;
        }
        public static string ReadInstallPath() {
            // Windows 安装器把安装路径写在 HKCU\Software\codeusagemonit；macOS 没有注册表，
            // 这里保持签名并返回空（行为等价于「未安装」，数据目录选在 exe 旁或用户目录）。
            return "";
        }
        public static bool PathsEqual(string a, string b) {
            if (String.IsNullOrWhiteSpace(a) || String.IsNullOrWhiteSpace(b)) return false;
            try { return String.Equals(NormalizeDirectory(a), NormalizeDirectory(b), StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }
        // First launch of an installed copy: if the per-user folder is still empty and a
        // real data directory sits beside the exe (zip or the PowerShell installer), copy it
        // across and remove the old folder. A junction (Scoop's persist link) is copied and left in place.
        public static bool TryMigratePortableData(string from, string to) {
            string staging = null;
            try {
                if (String.IsNullOrEmpty(from) || String.IsNullOrEmpty(to) || PathsEqual(from, to)) return false;
                if (IsNested(from, to) || IsNested(to, from)) return false;
                if (!Directory.Exists(from)) return false;
                DirectoryInfo source = new DirectoryInfo(from);
                bool link = (source.Attributes & FileAttributes.ReparsePoint) != 0;
                if (Directory.GetFileSystemEntries(from).Length == 0) return false;
                if (Directory.Exists(to) && Directory.GetFileSystemEntries(to).Length > 0) return false;
                string parent = Path.GetDirectoryName(NormalizeDirectory(to));
                if (String.IsNullOrEmpty(parent)) return false;
                Directory.CreateDirectory(parent);
                staging = Path.Combine(parent, Path.GetFileName(NormalizeDirectory(to).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + ".migrating");
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                CopyAll(from, staging);
                if (Directory.Exists(to)) Directory.Delete(to, true);
                Directory.Move(staging, to);
                staging = null;
                if (!link) { try { Directory.Delete(from, true); } catch { } }
                return true;
            } catch {
                if (staging != null && Directory.Exists(staging)) { try { Directory.Delete(staging, true); } catch { } }
                return false;
            }
        }
        private static bool IsNested(string parent, string child) {
            try {
                string p = NormalizeDirectory(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string c = NormalizeDirectory(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return c.StartsWith(p, StringComparison.OrdinalIgnoreCase) && !PathsEqual(parent, child);
            } catch { return false; }
        }
        private static void CopyAll(string from, string to) {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), false);
            foreach (string dir in Directory.GetDirectories(from)) {
                DirectoryInfo info = new DirectoryInfo(dir);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                CopyAll(dir, Path.Combine(to, info.Name));
            }
        }
        public static string NormalizeDirectory(string path) {
            if (String.IsNullOrEmpty(path)) return "";
            try { path = Path.GetFullPath(path); } catch { }
            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + Path.DirectorySeparatorChar;
            return trimmed.Length == 0 ? path : trimmed;
        }
        public static void Write(string name, object value) {
            Directory.CreateDirectory(Data); string path = Path.Combine(Data, name); string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, J.Serializer().Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        // 保存前的写探测（设置保存失败曾只报泛化文案，真实原因被吞）：向数据目录写一个
        // 临时文件再删除，与 Store.Write 同目录同权限；失败返回带真实路径与系统原因的
        // 文案（App Translocation、权限、磁盘满都能现形），成功返回 null。
        public static string ProbeWritable() {
            try {
                Directory.CreateDirectory(Data);
                string probe = Path.Combine(Data, ".write-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return null;
            } catch (Exception e) { return I18n.T("数据目录 {0} 不可写：{1}", Data, e.Message); }
        }
        public static T Read<T>(string name) where T : new() { try { string path = Path.Combine(Data, name); return File.Exists(path) ? J.Serializer().Deserialize<T>(File.ReadAllText(path)) : new T(); } catch { return new T(); } }
        // Secrets live in the macOS keychain (service "codeusagemonit"); the data/*.key
        // file holds only the "keychain:<account>" reference, so backups keep containing
        // no keys. Environment variables (ProviderCatalog.KeyEnv) take precedence over
        // saved keys.
        //
        // ROADMAP Phase 5（多账户）：密钥存储键 = "<providerId>:<accountId>"，钥匙串
        // account 同名；引用文件为 data/<providerId>_<accountId>.key（合法 id 不含 '_'，
        // 无歧义；避免 ':' 进文件名——Windows 血统上它是盘符分隔符）。每平台隐含一个
        // 默认账户 "default"（Accounts.cs）；data/<id>.key 与钥匙串 <id> 是它的遗留位置。
        //
        // 惰性迁移（SavedKey(LegacyMigrate)）：默认账户首次读到旧位置且新位置不存在时，
        // 把密钥复制到新键并落新引用文件；旧文件与旧钥匙串条目一律保留（无删除路径，
        // 迁移失败也不丢 key，可无限重试）。新位置生效后旧位置不再被读，幂等。
        private const string KeychainMarker = "keychain:";
        public static string ProviderKey(string id, string accountId, AppConfig config = null) {
            foreach (string name in ProviderCatalog.KeyEnv(id, config)) { string fromEnv = Environment.GetEnvironmentVariable(name); if (!String.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim(); }
            return SavedKey(id, accountId);
        }
        // 活动账户的密钥（env 优先语义不变：环境变量按平台判断，与账户无关）。
        public static string ProviderKey(string id, AppConfig config = null) {
            return ProviderKey(id, Accounts.ActiveId(config, id), config);
        }
        public static string SavedKey(string id) { return SavedKey(id, Accounts.DefaultId); }
        public static string SavedKey(string id, string accountId) {
            try {
                string file = KeyRefPath(id, accountId);
                if (File.Exists(file)) return KeychainValue(file);
                return accountId == Accounts.DefaultId ? LegacyMigrate(id) : "";
            } catch { return ""; }
        }
        public static bool HasSavedKey(string id) { return HasSavedKey(id, Accounts.DefaultId); }
        public static bool HasSavedKey(string id, string accountId) {
            try {
                if (File.Exists(KeyRefPath(id, accountId))) return true;
                return accountId == Accounts.DefaultId && File.Exists(KeyPath(id));
            } catch { return false; }
        }
        public static string KeyEnvironmentName(string id, AppConfig config) { return ProviderCatalog.KeyEnv(id, config).FirstOrDefault(n => !String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(n))) ?? ""; }
        // 返回是否保存成功：钥匙串写入失败时返回 false，并且不写 .key 引用文件
        //（HasSavedKey 只看文件存在；先写引用会把失败当成成功，留下读不出密钥的
        // 无效引用，更新失败时还会让旧密钥"顶替"刚输入的密钥）。清除（key 为空）恒成功。
        // 调用方 LocalCliCredentials.Import 保留"尽力持久化"语义：忽略返回值、失败仍用导入值。
        public static bool SetProviderKey(string id, string key) { return SetProviderKey(id, Accounts.DefaultId, key); }
        public static bool SetProviderKey(string id, string accountId, string key) {
            Directory.CreateDirectory(Data);
            string path = KeyRefPath(id, accountId), account = KeyAccount(id, accountId);
            if (String.IsNullOrWhiteSpace(key)) {
                Keychain.Delete(account);
                if (File.Exists(path)) File.Delete(path);
                // 显式清除默认账户时一并清掉遗留位置，避免旧条目让密钥"复活"。
                if (accountId == Accounts.DefaultId) { Keychain.Delete(id); string legacy = KeyPath(id); if (File.Exists(legacy)) File.Delete(legacy); }
                return true;
            }
            if (!Keychain.Set(account, key.Trim())) return false;
            WriteKeyReference(path, KeychainMarker + account);
            return true;
        }
        // 引用文件原子写（review P2-1）：与 Store.Write 同语义——先写同目录临时文件，
        // 再 File.Replace / File.Move(overwrite) 覆盖。任何时刻磁盘上要么是完整的旧
        // 引用、要么是完整的新引用，崩溃不会留下读不出 account 的半截文件。
        private static void WriteKeyReference(string path, string content) {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        private static string KeychainValue(string referenceFile) {
            string marker = File.ReadAllText(referenceFile, new UTF8Encoding(false)).Trim();
            return marker.StartsWith(KeychainMarker, StringComparison.Ordinal) ? Keychain.Get(marker.Substring(KeychainMarker.Length)) : "";
        }
        // 默认账户的惰性迁移：读旧 data/<id>.key → 钥匙串 <id>，成功读到密钥才复制到
        // 新键 <id>:default 并落新引用文件（原子写，失败回滚新钥匙串条目）。旧位置永不
        // 删除。迁移退化为纯读时返回值照常可用，下次调用再试。
        // marker 撕裂（半截文件，只可能来自外部破坏）时按旧版布局的固定 account <id>
        // 直读钥匙串——迁移不会因此被永久跳过，旧文件本身不改动。
        private static string LegacyMigrate(string id) {
            string legacy = KeyPath(id);
            if (!File.Exists(legacy)) return "";
            string secret = KeychainValue(legacy);
            if (String.IsNullOrEmpty(secret)) secret = Keychain.Get(id);
            if (String.IsNullOrEmpty(secret)) return "";
            string account = KeyAccount(id, Accounts.DefaultId);
            if (!Keychain.Set(account, secret)) return secret;
            try { WriteKeyReference(KeyRefPath(id, Accounts.DefaultId), KeychainMarker + account); }
            catch { try { Keychain.Delete(account); } catch { } }
            return secret;
        }
        // 旧版（单账户）引用文件：<id>.key，钥匙串 account 即 <id>。
        private static string KeyPath(string id) {
            if (String.IsNullOrEmpty(id) || id.Any(c => !(Char.IsLetterOrDigit(c) && c < 128) && c != '-')) throw new ArgumentException("invalid key id");
            return Path.Combine(Data, id + ".key");
        }
        // 多账户引用文件：<id>_<accountId>.key；存储键（钥匙串 account）= <id>:<accountId>。
        private static string KeyRefPath(string id, string accountId) {
            if (String.IsNullOrEmpty(id) || id.Any(c => !(Char.IsLetterOrDigit(c) && c < 128) && c != '-')) throw new ArgumentException("invalid key id");
            if (String.IsNullOrEmpty(accountId) || accountId.Any(c => !(Char.IsLetterOrDigit(c) && c < 128) && c != '-')) throw new ArgumentException("invalid account id");
            return Path.Combine(Data, id + "_" + accountId + ".key");
        }
        private static string KeyAccount(string id, string accountId) { return id + ":" + KeyRefAccountId(accountId); }
        private static string KeyRefAccountId(string accountId) {
            if (String.IsNullOrEmpty(accountId) || accountId.Any(c => !(Char.IsLetterOrDigit(c) && c < 128) && c != '-')) throw new ArgumentException("invalid account id");
            return accountId;
        }
        public static string DeepSeekKey() { return ProviderKey("deepseek"); }
        public static void SetDeepSeekKey(string key) { SetProviderKey("deepseek", key); }
    }
    public sealed class ProviderException : Exception {
        public string State;
        public ProviderException(string message, string state) : base(message) { State = state; }
    }
    public static class Parsers {
        public static void Add(ProviderState s, string label, object used, object reset, double windowSeconds = 0) {
            double? n = J.Num(used); if (!n.HasValue) return;
            s.Quotas.Add(new Quota { Label = label, Used = Math.Max(0, Math.Min(100, n.Value)), ResetUtc = J.Iso(reset), WindowSeconds = windowSeconds });
        }
        public static string WindowLabel(double seconds) { if (seconds >= 2500000) return "每月"; if (seconds >= 432000) return "每周"; if (seconds >= 86400) return "每日"; if (seconds > 0) return (seconds / 3600).ToString("0.#") + " 小时"; return "当前窗口"; }
        public static ProviderState Codex(object root) {
            var s = new ProviderState { Id = "codex", Plan = UsageDetails.PlanName(J.Str(root, "plan_type")) };
            foreach (string key in new[] { "primary_window", "secondary_window" }) { object w = J.Get(root, "rate_limit", key); double seconds = J.Num(w, "limit_window_seconds") ?? 0; if (w != null) Add(s, WindowLabel(seconds), J.Get(w, "used_percent"), J.Get(w, "reset_at"), seconds); }
            foreach (object extra in J.Arr(J.Get(root, "additional_rate_limits"))) {
                string model = J.Str(extra, "limit_name"); if (model.Length == 0) model = J.Str(extra, "metered_feature");
                foreach (string key in new[] { "primary_window", "secondary_window" }) { object w = J.Get(extra, "rate_limit", key); double seconds = J.Num(w, "limit_window_seconds") ?? 0; if (w != null) Add(s, model + " · " + WindowLabel(seconds), J.Get(w, "used_percent"), J.Get(w, "reset_at"), seconds); }
            }
            return s;
        }
        public static ProviderState Claude(object root) {
            var s = new ProviderState { Id = "claude", Plan = "Claude Code" };
            foreach (var pair in new[] { new[] { "five_hour", "5 小时" }, new[] { "seven_day", "每周" }, new[] { "seven_day_sonnet", "Sonnet · 每周" }, new[] { "seven_day_opus", "Opus · 每周" } }) {
                object w = J.Get(root, pair[0]); if (w != null) Add(s, pair[1], J.Get(w, "utilization"), J.Get(w, "resets_at"), pair[0] == "five_hour" ? 18000 : 604800);
            }
            foreach (object w in J.Arr(J.Get(root, "limits"))) {
                string kind = J.Str(w, "kind"), model = J.Str(w, "model"); string label = J.Str(w, "display_name");
                if (label.Length == 0) label = kind.Contains("weekly") ? (model.Length == 0 ? "每周" : model + " · 每周") : kind.Contains("five") || kind.Contains("session") ? "5 小时" : kind;
                object used = J.Get(w, "utilization") ?? J.Get(w, "used_percent");
                if (!s.Quotas.Any(q => q.Label == label)) Add(s, label, used, J.Get(w, "resets_at") ?? J.Get(w, "reset_at"));
            }
            return s;
        }
        public static ProviderState Cursor(object root) {
            var s = new ProviderState { Id = "cursor", Plan = J.Str(root, "membershipType") };
            object p = J.Get(root, "individualUsage", "plan"); object reset = J.Get(root, "billingCycleEnd");
            DateTimeOffset cycleStart, cycleEnd; double duration = 0;
            if (DateTimeOffset.TryParse(J.Str(root, "billingCycleStart"), out cycleStart) && DateTimeOffset.TryParse(J.Str(root, "billingCycleEnd"), out cycleEnd) && cycleEnd > cycleStart) duration = (cycleEnd - cycleStart).TotalSeconds;
            Add(s, "套餐总量", J.Get(p, "totalPercentUsed"), reset, duration); Add(s, "Auto", J.Get(p, "autoPercentUsed"), reset, duration); Add(s, "API / 手动模型", J.Get(p, "apiPercentUsed"), reset, duration);
            s.Plan = s.Plan.Length == 0 ? "Cursor" : "Cursor " + CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.Plan); return s;
        }
        // Shown under the plan meters. Not labeled "每周" on its own, so headline pickers
        // that look for that exact label keep 套餐总量.
        public const string CursorGrokLabel = "Grok Bot · 每周";
        // POST cursor.com/api/dashboard/get-sand-usage-status. Appended only when the
        // account has a non-zero included Grok Bot allowance and a numeric usage percent.
        public static void CursorGrokBot(ProviderState state, object root) {
            if (state == null || root == null) return;
            object included = J.Get(root, "hasNonZeroIncludedLimit");
            if (!(included is bool) || !(bool)included) return;
            if (!J.Num(root, "usagePercent").HasValue) return;
            object reset = J.Get(root, "nextResetTimestampUtc");
            double window = 604800;
            DateTimeOffset start, end;
            if (DateTimeOffset.TryParse(J.Str(root, "currentPeriodStart"), out start) && DateTimeOffset.TryParse(J.Str(reset), out end) && end > start) window = (end - start).TotalSeconds;
            Add(state, CursorGrokLabel, J.Get(root, "usagePercent"), reset, window);
        }
        public static ProviderState DeepSeek(object root) {
            var s = new ProviderState { Id = "deepseek", Plan = "API 余额" };
            foreach (object b in J.Arr(J.Get(root, "balance_infos"))) { double? n = J.Num(b, "total_balance"); string c = J.Str(b, "currency"); if (n.HasValue && c.Length > 0) s.Balances.Add(new Balance { Currency = c, Amount = n.Value }); }
            return s;
        }
        public static ProviderState Grok(object root) {
            var s = new ProviderState { Id = "grok", Plan = J.Str(root, "subscriptionTier") }; object c = J.Get(root, "config");
            object end = J.Get(c, "currentPeriod", "end"); if (J.Get(end, "seconds") != null) end = J.Get(end, "seconds");
            // One shared allowance. productUsage (Build / Chat) splits what was spent from it;
            // a product's share is not a quota of its own, so it gets no remaining meter.
            Add(s, "当前账期", J.Get(c, "creditUsagePercent"), end);
            double? sharedUsed = J.Num(c, "creditUsagePercent");
            var products = new List<ProductConsumption>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool valid = sharedUsed.HasValue && sharedUsed >= 0 && sharedUsed <= 100;
            foreach (object product in J.Arr(J.Get(c, "productUsage"))) {
                string name = J.Str(product, "product").Trim(); double? used = J.Num(product, "usagePercent");
                if (name.Length == 0 || !used.HasValue || used < 0 || used > 100 || !names.Add(name)) { valid = false; break; }
                products.Add(new ProductConsumption { Product = name, UsedPercent = used.Value });
            }
            // Allow one percentage point for independently rounded product shares.
            if (valid && products.Count > 0 && Math.Abs(products.Sum(p => p.UsedPercent) - sharedUsed.Value) <= 1)
                s.ProductUsage = products;
            if (s.Plan.Length == 0) s.Plan = "Grok Build"; return s;
        }
        // Readings cached by older versions: before 1.2 Grok's Build share was saved as a quota.
        // Its complement (for example "Build 剩余 45%") is not spendable allowance.
        public static ProviderState Normalize(ProviderState s) {
            if (s == null) return null;
            if (s.ProductUsage == null) s.ProductUsage = new List<ProductConsumption>();
            if (s.Id == "grok" && s.Quotas != null) s.Quotas.RemoveAll(q => q.Label == "Build 占比");
            return s;
        }
        // A failed refresh keeps the last good reading, including the consumption breakdown.
        public static void RetainLastGood(ProviderState incoming, ProviderState old) {
            if (incoming == null || (incoming.Status != "error" && incoming.Status != "expired")) return;
            if (old == null) old = new ProviderState();
            incoming.Quotas = old.Quotas; incoming.Balances = old.Balances; incoming.ProductUsage = old.ProductUsage ?? new List<ProductConsumption>();
            incoming.LastSuccess = old.LastSuccess; incoming.Plan = old.Plan; incoming.Account = old.Account;
            incoming.ResetCreditsAvailable = old.ResetCreditsAvailable; incoming.ResetCreditExpiries = old.ResetCreditExpiries; incoming.ResetCreditsUpdated = old.ResetCreditsUpdated;
            incoming.Stale = (old.Quotas != null && old.Quotas.Count > 0) || (old.Balances != null && old.Balances.Count > 0);
        }
        // GET api.github.com/copilot_internal/user. Snapshots report percent_remaining; the
        // monthly quota resets on quota_reset_date. Unlimited pools have no meter.
        public static ProviderState Copilot(object root) {
            string plan = J.Str(root, "copilot_plan");
            var s = new ProviderState { Id = "copilot", Plan = plan.Length == 0 ? "Copilot" : "Copilot " + CultureInfo.InvariantCulture.TextInfo.ToTitleCase(plan.Replace('_', ' ')) };
            string reset = J.Iso(J.Get(root, "quota_reset_date")); DateTime resetAt; double window = 0;
            if (DateTime.TryParse(reset, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out resetAt)) window = (resetAt - resetAt.AddMonths(-1)).TotalSeconds;
            object snapshots = J.Get(root, "quota_snapshots");
            var unlimited = new List<string>();
            foreach (var pair in new[] { new[] { "premium_interactions", "高级请求" }, new[] { "chat", "对话" }, new[] { "completions", "代码补全" } }) {
                object snap = J.Get(snapshots, pair[0]); if (snap == null) continue;
                if (J.Get(snap, "unlimited") is bool && (bool)J.Get(snap, "unlimited")) { unlimited.Add(pair[1]); continue; }
                double? remaining = J.Num(snap, "percent_remaining"), entitlement = J.Num(snap, "entitlement"), left = J.Num(snap, "remaining");
                if (!remaining.HasValue && entitlement > 0 && left.HasValue) remaining = left.Value / entitlement.Value * 100;
                // entitlement 0 + remaining 0 + percent 0 is a placeholder, not an exhausted quota.
                if (!remaining.HasValue || (entitlement == 0 && left == 0 && remaining == 0)) continue;
                Add(s, pair[1], 100 - remaining.Value, reset, window);
            }
            if (s.Quotas.Count == 0 && unlimited.Count > 0) s.Plan += " · " + String.Join("、", unlimited) + "不限量";
            return s;
        }
        // GET api.kimi.com/coding/v1/usages (Kimi Code). Ratio pools (0–1) take precedence;
        // legacy count responses are mapped when no ratio is present for that window.
        public static ProviderState Kimi(object root) {
            var s = new ProviderState { Id = "kimi", Plan = KimiLevel(J.Str(root, "user", "membership", "level")) };
            object pools = J.Get(root, "usages");
            foreach (var pool in new[] { new { Key = "limit_5h", Label = "5 小时", Seconds = 18000.0 }, new { Key = "limit_7d", Label = "每周", Seconds = 604800.0 }, new { Key = "limit_month_total", Label = "每月总量", Seconds = 0.0 } }) {
                object item = J.Get(pools, pool.Key); double? ratio = J.Num(item, "used_ratio");
                if (ratio.HasValue && ratio >= 0) Add(s, pool.Label, Math.Min(1, ratio.Value) * 100, KimiReset(item), pool.Seconds);
            }
            object usage = J.Get(root, "usage");
            if (usage != null && !s.Quotas.Any(q => q.Label == "每周")) AddCount(s, "每周", usage, 604800);
            foreach (object limit in J.Arr(J.Get(root, "limits"))) {
                double duration = J.Num(limit, "window", "duration") ?? 0; string unit = J.Str(limit, "window", "timeUnit");
                double seconds = duration * (unit.Contains("MINUTE") ? 60 : unit.Contains("HOUR") ? 3600 : unit.Contains("DAY") ? 86400 : 0);
                string label = seconds == 18000 ? "5 小时" : seconds > 0 ? WindowLabel(seconds) : "速率限制";
                if (!s.Quotas.Any(q => q.Label == label)) AddCount(s, label, J.Get(limit, "detail"), seconds);
            }
            return s;
        }
        private static void AddCount(ProviderState s, string label, object detail, double seconds) {
            double? limit = J.Num(detail, "limit"), used = J.Num(detail, "used"), remaining = J.Num(detail, "remaining");
            if (!used.HasValue && limit.HasValue && remaining.HasValue) used = limit - remaining;
            if (limit > 0 && used.HasValue) Add(s, label, used.Value / limit.Value * 100, KimiReset(detail), seconds);
        }
        private static object KimiReset(object item) { return J.Get(item, "resetTime") ?? J.Get(item, "resetAt") ?? J.Get(item, "reset_time") ?? J.Get(item, "reset_at"); }
        private static string KimiLevel(string level) {
            switch (level) { case "LEVEL_FREE": return "Kimi Adagio"; case "LEVEL_TRIAL": return "Kimi Andante"; case "LEVEL_BASIC": return "Kimi Moderato"; case "LEVEL_INTERMEDIATE": return "Kimi Allegretto"; case "LEVEL_ADVANCED": return "Kimi Allegro"; default: return "Kimi Code"; }
        }
        // GET opencode.ai/zen/go/v1/usage (OpenCode Go). Percentages are 0–100; resets are
        // absolute (resetAt…) or relative (resetInSec…).
        public static ProviderState OpenCode(object root, DateTime nowUtc) {
            var s = new ProviderState { Id = "opencode", Plan = "OpenCode Go" };
            object usage = J.Get(root, "usage");
            if (J.Get(usage, "rolling") == null) throw new ProviderException("OpenCode 接口未返回用量字段", "error");
            foreach (var window in new[] { new { Key = "rolling", Label = "5 小时", Seconds = 18000.0 }, new { Key = "weekly", Label = "每周", Seconds = 604800.0 }, new { Key = "monthly", Label = "每月", Seconds = 0.0 } }) {
                object item = J.Get(usage, window.Key); if (item == null) continue;
                object percent = new[] { "usagePercent", "usedPercent", "percentUsed", "percent", "usage_percent", "used_percent", "utilization" }.Select(k => J.Get(item, k)).FirstOrDefault(v => J.Num(v).HasValue);
                if (percent == null) continue;
                object reset = new[] { "resetAt", "resetsAt", "reset_at", "resets_at", "nextReset", "next_reset" }.Select(k => J.Get(item, k)).FirstOrDefault(v => v != null);
                string resetIso = reset != null ? J.Iso(reset) : "";
                if (resetIso.Length == 0) { double? inSeconds = new[] { "resetInSec", "resetInSeconds", "resetSeconds", "reset_in_sec", "resetIn" }.Select(k => J.Num(item, k)).FirstOrDefault(v => v.HasValue); if (inSeconds.HasValue) resetIso = nowUtc.AddSeconds(inSeconds.Value).ToString("o"); }
                Add(s, window.Label, percent, resetIso, window.Seconds);
            }
            return s;
        }
        // GET {api.z.ai | open.bigmodel.cn}/api/monitor/usage/quota/limit (GLM Coding Plan,
        // used by ZCode). unit: 1 day, 3 hour, 5 minute, 6 week; nextResetTime is epoch ms.
        // TIME_LIMIT is the monthly MCP tool allowance, not a time window.
        public static ProviderState Zai(object root) {
            if (J.Get(root, "success") is bool && !(bool)J.Get(root, "success")) throw new ProviderException("智谱接口返回错误：" + J.Str(root, "msg"), "error");
            object data = J.Get(root, "data");
            string plan = new[] { "planName", "plan", "plan_type", "packageName", "level" }.Select(k => J.Str(data, k)).FirstOrDefault(v => v.Length > 0) ?? "";
            var s = new ProviderState { Id = "zcode", Plan = plan.Length > 0 ? "GLM " + plan : "GLM 编码套餐" };
            foreach (object limit in J.Arr(J.Get(data, "limits"))) {
                string type = J.Str(limit, "type");
                if (type != "TOKENS_LIMIT" && type != "CREDIT_LIMIT" && type != "TIME_LIMIT") continue;
                double? percent = J.Num(limit, "percentage"), usage = J.Num(limit, "usage"), current = J.Num(limit, "currentValue"), remaining = J.Num(limit, "remaining");
                if (usage > 0) {
                    double? used = remaining.HasValue ? Math.Max(usage.Value - remaining.Value, current ?? usage.Value - remaining.Value) : current;
                    if (used.HasValue) percent = Math.Max(0, Math.Min(usage.Value, used.Value)) / usage.Value * 100;
                }
                if (!percent.HasValue) continue;
                int unit = (int)(J.Num(limit, "unit") ?? 0); double number = J.Num(limit, "number") ?? 0;
                double minutes = number * (unit == 1 ? 1440 : unit == 3 ? 60 : unit == 5 ? 1 : unit == 6 ? 10080 : 0);
                string label = type == "TIME_LIMIT" ? "MCP 工具调用（每月）" : minutes == 300 ? "5 小时" : minutes > 0 ? WindowLabel(minutes * 60) : "编码套餐";
                if (type == "CREDIT_LIMIT") label += " · 积分";
                Add(s, label, percent, J.Get(limit, "nextResetTime"), type == "TIME_LIMIT" ? 0 : minutes * 60);
            }
            return s;
        }
        public static ProviderState Antigravity(object root) {
            var s = new ProviderState { Id = "antigravity", Plan = "Google AI" };
            foreach (object group in J.Arr(J.Get(root, "groups"))) {
                string name = J.Str(group, "displayName");
                foreach (object b in J.Arr(J.Get(group, "buckets"))) { double? fraction = J.Num(b, "remainingFraction"); string window = J.Str(b, "window"); string label = name + " · " + (window == "weekly" ? "每周" : window == "5h" ? "5 小时" : J.Str(b, "displayName")); if (fraction.HasValue) Add(s, label.Trim(' ', '·'), (1 - fraction.Value) * 100, J.Get(b, "resetTime"), window == "weekly" ? 604800 : window == "5h" ? 18000 : 0); }
            }
            if (s.Quotas.Count == 0) foreach (var model in J.Dict(J.Get(root, "models"))) { double? fraction = J.Num(model.Value, "quotaInfo", "remainingFraction"); if (fraction.HasValue) Add(s, model.Key, (1 - fraction.Value) * 100, J.Get(model.Value, "quotaInfo", "resetTime")); }
            return s;
        }

        // ── MiMo（小米 MiMo 开放平台，Console Cookie 通道）──────────────────
        // 两个通道共用的未授权提示：信封错误与 HTTP 状态映射（ProviderService.CloudRequest）
        // 都指向同一条「重新复制凭据」的指引。
        internal const string MimoExpiredMessage = "MiMo 的 Cookie 已失效或未登录，请重新从浏览器复制 Console Cookie。";
        internal const string StepFunExpiredMessage = "StepFun 的 Oasis-Token 无效或已过期，请重新填写。";
        // camelCase / snake_case 双写兼容：返回第一个有值的键（OpenCode 的字段别名探测同款写法）。
        private static object Pick(object obj, params string[] keys) { return keys.Select(k => J.Get(obj, k)).FirstOrDefault(v => v != null); }
        private static double? PickNum(object obj, params string[] keys) { return keys.Select(k => J.Num(obj, k)).FirstOrDefault(v => v.HasValue); }
        private static string PickStr(object obj, params string[] keys) { return keys.Select(k => J.Str(obj, k)).FirstOrDefault(v => v.Length > 0) ?? ""; }
        // Console Cookie 归一化：用户整段粘贴浏览器 Cookie，这里只保留通道需要的四个名称
        //（serviceToken / userId 必填，ph / slh 可选），按名称排序拼成 "name=value; …" 后
        // 整体作为 provider key 存钥匙串——其余条目（跟踪、UI 状态）一律不落盘。缺必填项
        // 直接报缺哪一个，不做静默猜测。
        public static string MimoCookie(string raw) {
            string[] wanted = { "api-platform_serviceToken", "userId", "api-platform_ph", "api-platform_slh" };
            var kept = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string part in (raw ?? "").Split(';')) {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string name = part.Substring(0, eq).Trim(), value = part.Substring(eq + 1).Trim().Trim('"');
                if (wanted.Contains(name) && value.Length > 0 && !kept.ContainsKey(name)) kept[name] = value;
            }
            string missing = string.Join("、", wanted.Take(2).Where(name => !kept.ContainsKey(name)));
            if (missing.Length > 0) throw new ProviderException("Cookie 缺少 " + missing + "：请在浏览器登录 MiMo 控制台后重新复制完整 Cookie。", "setup");
            return string.Join("; ", kept.Select(p => p.Key + "=" + p.Value));
        }
        // MiMo 信封：code===0 → data；401/403 → 未授权（与 HTTP 层同一条提示）；其他 code → 服务不可用。
        public static object MimoEnvelope(object root) {
            double? code = J.Num(root, "code");
            if (!code.HasValue) throw new ProviderException("MiMo 接口返回了无法识别的数据格式", "error");
            if (code.Value == 401 || code.Value == 403) throw new ProviderException(MimoExpiredMessage, "expired");
            if (code.Value != 0) throw new ProviderException("MiMo 接口返回错误（code " + J.Str(root, "code") + "）", "error");
            return J.Get(root, "data");
        }
        // 钱包：balance 优先；缺失时才退回 cash + gift 之和，绝不把两者与 balance 相加。
        public static void MimoWallet(ProviderState s, object envelope) {
            object data = MimoEnvelope(envelope); if (data == null) return;
            double? cash = PickNum(data, "cashBalance", "cash_balance"), gift = PickNum(data, "giftBalance", "gift_balance");
            double? balance = PickNum(data, "balance") ?? (cash.HasValue || gift.HasValue ? (cash ?? 0) + (gift ?? 0) : (double?)null);
            if (!balance.HasValue) return;
            s.Balances.Add(new Balance { Currency = PickStr(data, "currency").Length > 0 ? PickStr(data, "currency") : "USD", Amount = balance.Value });
        }
        // 套餐名与周期末；返回周期末 ISO 串（无则空串），供 MimoUsage 当作月度窗口的重置时间。
        public static string MimoPlan(ProviderState s, object envelope) {
            if (envelope == null) return "";
            object data = MimoEnvelope(envelope); if (data == null) return "";
            string plan = PickStr(data, "planCode", "plan_code", "planName", "plan_name");
            if (plan.Length > 0) s.Plan = plan;
            return J.Iso(Pick(data, "currentPeriodEnd", "current_period_end"));
        }
        // 月度 Token 窗口：monthUsage.items[] 里 name（大小写不敏感）== month_total_token 的条目。
        // percent 是 0–1 的已用比例且超额可大于 1：一律用 used/limit 重算，percent 只在没有
        // used/limit 时兜底；Parsers.Add 再统一夹进 [0,100]。
        public static void MimoUsage(ProviderState s, object envelope, string periodEndIso) {
            object data = MimoEnvelope(envelope);
            object month = Pick(data, "monthUsage", "month_usage");
            foreach (object item in J.Arr(J.Get(month, "items"))) {
                if (!String.Equals(J.Str(item, "name"), "month_total_token", StringComparison.OrdinalIgnoreCase)) continue;
                double? used = J.Num(item, "used"), limit = J.Num(item, "limit");
                double percent;
                if (used.HasValue && limit.HasValue && limit.Value > 0) percent = used.Value / limit.Value * 100;
                else {
                    double? ratio = PickNum(item, "percent");
                    if (!ratio.HasValue) continue;
                    percent = ratio.Value * 100;
                }
                Add(s, "每月 Token", percent, periodEndIso, 2592000);
                return;
            }
        }
        public static string MimoAccount(object envelope) {
            object data = MimoEnvelope(envelope); if (data == null) return "";
            return PickStr(data, "email", "nickName", "nick_name");
        }

        // ── StepFun（阶跃星辰开放平台，Oasis-Token 通道）────────────────────
        // Oasis-Token：接受纯值或 "Oasis-Token=xxx" 整段 Cookie（按 ; 分隔提取）。提取后
        // 仍含控制字符，或既不是纯值（含 ;）也没有 Oasis-Token= 标记的输入一律拒绝（空串）。
        public static string StepFunToken(string raw) {
            string token = (raw ?? "").Trim();
            Match match = Regex.Match(token, "(?:^|;)\\s*Oasis-Token=([^;]+)");
            if (match.Success) token = match.Groups[1].Value.Trim();
            else if (token.IndexOf(';') >= 0) return "";
            return token.Length == 0 || token.Any(Char.IsControl) ? "" : token;
        }
        // oasis-webid 派生：Oasis-Token 是若干段 JWT 用 "…"（三个点）连接的串；从最后一段
        // 向前找 payload（中间段）里 device_id claim 合法（^[A-Za-z0-9_-]{1,128}$）的段。
        // 找不到（含垃圾输入）时回退官方 Web 端使用的常量 webid。
        public const string StepFunFallbackWebId = "c8a1002d2c457e758785a9979832217c7c0b884c";
        public static string StepFunWebId(string token) {
            if (!String.IsNullOrWhiteSpace(token)) {
                foreach (string part in token.Split(new[] { "..." }, StringSplitOptions.None).Reverse()) {
                    string device = J.Str(J.Jwt(part), "device_id");
                    if (Regex.IsMatch(device, "^[A-Za-z0-9_-]{1,128}$")) return device;
                }
            }
            return StepFunFallbackWebId;
        }
        // oasis-appid：与 token 所属的 Oasis 应用配对（国际站实测是 20700，国内站是 10300）
        // ——带错 appid 携有效 token 请求会被平台判为盗用（embezzled）。
        // token 各段 JWT payload 里带 app_id claim，优先从 token 自身提取；提取不到按区域回退。
        public static string StepFunAppId(string token, string region) {
            string extracted = StepFunTokenAppId(token);
            if (extracted.Length > 0) return extracted;
            return region == "china" ? "10300" : "20700";
        }
        // 从 token 各段 JWT payload 提取 app_id；提取不到返回空串（调用方按区域回退）。
        public static string StepFunTokenAppId(string token) {
            if (!String.IsNullOrWhiteSpace(token)) {
                foreach (string part in token.Split(new[] { "..." }, StringSplitOptions.None)) {
                    object payload = J.Jwt(part);
                    double? appId = J.Num(payload, "app_id");
                    if (appId.HasValue && appId.Value > 0) return ((long)appId.Value).ToString(CultureInfo.InvariantCulture);
                }
            }
            return "";
        }
        // 接口区域 → 官方域名：默认国际站；只允许这两个 host（ProviderService.CloudRequest 把关）。
        public static string StepFunHost(string region) { return region == "china" ? "https://platform.stepfun.com" : "https://platform.stepfun.ai"; }
        // appid → 官方域名：10300 是国内站应用，其余（含 20700 与未知值）都落国际站。
        // token 自带 app_id 时站点由它决定，与用户配置的区域无关。
        public static string StepFunHostForAppId(string appId) { return appId == "10300" ? "https://platform.stepfun.com" : "https://platform.stepfun.ai"; }
        // StepFun 信封：status===1 成功（payload 可能在 data 下，也可能就在根上）；
        // status!==1 时 message/desc 命中认证类关键词 → 未授权，其余 → 服务不可用。
        // 关键词只收明确的鉴权失效信号：裸 "token" 会误命中限额等普通业务文案
        //（如 "token plan limit exceeded"），把服务错误错报成凭据过期。
        public static object StepFunEnvelope(object root) {
            if (J.Num(root, "status") == 1) return J.Get(root, "data") ?? root;
            string message = (J.Str(root, "message") + " " + J.Str(root, "desc")).ToLowerInvariant();
            bool auth = new[] { "unauthorized", "auth", "login", "expire", "登录" }.Any(message.Contains);
            throw new ProviderException(auth ? StepFunExpiredMessage : "StepFun 接口返回错误（status " + J.Str(root, "status") + "）", auth ? "expired" : "error");
        }
        // QueryStepPlanRateLimit 的 payload：Coding Plan（5 小时 / 每周）与 Token Plan（credit
        // 数据）可同时返回。left_rate 是 0–1 的剩余比例 → usedPercent = (1 − left) × 100；
        // reset_time 是 Unix 秒（字符串兼容），0 / 非法 → 该窗口没有重置时间，用量照常展示。
        public static ProviderState StepFun(object root) {
            object data = StepFunEnvelope(root);
            var s = new ProviderState { Id = "stepfun", Plan = "StepFun" };
            StepWindow(s, "5 小时", PickNum(data, "five_hour_usage_left_rate", "fiveHourUsageLeftRate"), Pick(data, "five_hour_usage_reset_time", "fiveHourUsageResetTime"), 18000);
            StepWindow(s, "每周", PickNum(data, "weekly_usage_left_rate", "weeklyUsageLeftRate"), Pick(data, "weekly_usage_reset_time", "weeklyUsageResetTime"), 604800);
            StepFunCredit(s, data);
            return s;
        }
        private static void StepWindow(ProviderState s, string label, double? left, object reset, double seconds) {
            if (!left.HasValue || left.Value < 0 || left.Value > 1) return;
            string resetIso = reset == null ? "" : J.Iso(reset);
            // 无重置时间且剩余为 0 = 该窗口未激活（如 Token Plan 用户没有 Coding Plan），不展示；
            // 真正用尽的窗口（left=0 但有 reset）正常显示。
            if (left.Value == 0 && resetIso.Length == 0) return;
            Add(s, label, (1 - left.Value) * 100, resetIso, seconds);
        }
        // Token Plan（plan_family===2 或带 credit 数据）：credit_buckets[] 的加权剩余 =
        // Σresidual / Σtotal，任一桶数值非法 → 只丢弃 credit 窗口（本方法不输出），Coding
        // Plan 的 5h/weekly 窗口不受影响——窗口彼此独立判定可用性，一份坏数据不拖垮整份读数。
        // 无桶时回退 subscription 剩余比例、再回退 topup 剩余比例——两个来源绝不相加。
        // 重置时间取 subscription_credit_reset_time：0 = 窗口未激活，月度窗口照常按 30 天
        // 展示、无重置时间。
        public static void StepFunCredit(ProviderState s, object data) {
            bool tokenFamily = (J.Num(data, "plan_family") ?? J.Num(data, "planFamily")) == 2;
            object buckets = Pick(J.Get(data, "plan_credit_rate_limit"), "credit_buckets") ?? Pick(J.Get(data, "planCreditRateLimit"), "credit_buckets");
            bool hasCredit = buckets != null || PickNum(data, "subscription_credit_left_rate", "subscriptionCreditLeftRate").HasValue || PickNum(data, "topup_credit_left_rate", "topupCreditLeftRate").HasValue;
            if (!tokenFamily && !hasCredit) return;
            double left;
            List<object> items = J.Arr(buckets).ToList();
            if (items.Count > 0) {
                double total = 0, residual = 0;
                foreach (object bucket in items) {
                    double? t = PickNum(bucket, "credit_total", "creditTotal"), r = PickNum(bucket, "credit_residual", "creditResidual");
                    if (!t.HasValue || !r.HasValue || t.Value <= 0 || r.Value < 0 || r.Value > t.Value) return;   // 桶数据不完整：宁缺毋假，只丢 credit 窗口
                    total += t.Value; residual += r.Value;
                }
                left = total > 0 ? residual / total : 0;
            } else {
                double? subscription = PickNum(data, "subscription_credit_left_rate", "subscriptionCreditLeftRate");
                double? topup = PickNum(data, "topup_credit_left_rate", "topupCreditLeftRate");
                if (!subscription.HasValue && !topup.HasValue) return;
                left = (subscription ?? topup).Value;
            }
            Add(s, "每月 Token", (1 - Math.Max(0, Math.Min(1, left))) * 100, J.Iso(Pick(data, "subscription_credit_reset_time", "subscriptionCreditResetTime")), 2592000);
        }
        // GetStepPlanStatus 的 payload：套餐名在 subscription.name。
        public static string StepFunPlan(object root) {
            if (root == null) return "";
            object data = StepFunEnvelope(root);
            return J.Str(J.Get(data, "subscription"), "name");
        }
    }
    public sealed class ProviderService : IDisposable {
        private readonly HttpClient client;
        private readonly AppConfig config;
        public ProviderService(AppConfig config) {
            this.config = config ?? new AppConfig();
            // Windows 专属的 ServicePointManager 设置在 .NET 8 上默认即 TLS 1.2+，已移除。
            var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, UseCookies = false };
            string proxy = ResolveProxy(config.Proxy); handler.UseProxy = proxy.Length > 0; if (handler.UseProxy) handler.Proxy = new WebProxy(proxy);
            client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(18) };
        }
        public static string ResolveProxy(string setting) {
            if (setting == "direct") return "";
            if (setting != "auto" && !String.IsNullOrWhiteSpace(setting)) { Uri u; if (!Uri.TryCreate(setting, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https")) throw new ArgumentException("代理地址应为 http://主机:端口"); return setting; }
            try { string server = SystemProxyServer(); if (server.Length > 0) return server.Contains("://") ? server : "http://" + server; } catch { }
            foreach (string key in new[] { "HTTPS_PROXY", "HTTP_PROXY" }) { string value = Environment.GetEnvironmentVariable(key); Uri u; if (Uri.TryCreate(value, UriKind.Absolute, out u) && (u.Scheme == "http" || u.Scheme == "https")) return value; }
            return "";
        }
        // macOS 系统代理：scutil --proxy（Windows 版读注册表 Internet Settings）。
        // HTTPS 代理优先于 HTTP 代理。
        private static string SystemProxyServer() {
            string output = "";
            try {
                var start = new ProcessStartInfo("scutil", "--proxy") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var process = Process.Start(start)) {
                    if (process == null) return "";
                    output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(2000)) { try { process.Kill(); } catch { } return ""; }
                }
            } catch { return ""; }
            return ParseScutilProxy(output);
        }
        // 解析 scutil --proxy 的输出。真实输出形如 "HTTPSEnable : 1"（冒号两侧都有空格），
        // 旧实现只认 "HTTPSEnable:1" 的无空格形式而漏掉标准格式。现在按行内第一个冒号
        // 分离键值并分别 trim；返回 host[:port]，找不到启用的 HTTP(S) 代理时为空串。
        // 仍保持 HTTPS 优先于 HTTP。public 供自测回归（合成输出，不读真实系统配置）。
        public static string ParseScutilProxy(string output) {
            Func<string, string> value = name => {
                foreach (string raw in (output ?? "").Split('\n')) {
                    string line = raw.Trim();
                    int colon = line.IndexOf(':');
                    if (colon <= 0 || !String.Equals(line.Substring(0, colon).Trim(), name, StringComparison.Ordinal)) continue;
                    return line.Substring(colon + 1).Trim();
                }
                return "";
            };
            foreach (var prefix in new[] { "HTTPS", "HTTP" }) {
                if (value(prefix + "Enable") != "1") continue;
                string host = value(prefix + "Proxy"), port = value(prefix + "Port");
                if (host.Length > 0) return port.Length > 0 && !host.Contains(":") ? host + ":" + port : host;
            }
            return "";
        }
        private async Task<object> Request(string url, string token, string cookie, string body, Dictionary<string, string> extra, int timeoutSeconds = 18) {
            using (var deadline = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            using (var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, url)) {
                request.Headers.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
                if (!String.IsNullOrWhiteSpace(token)) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                if (!String.IsNullOrWhiteSpace(cookie)) request.Headers.TryAddWithoutValidation("Cookie", "WorkosCursorSessionToken=" + cookie);
                if (extra != null) foreach (var h in extra) { request.Headers.Remove(h.Key); request.Headers.TryAddWithoutValidation(h.Key, h.Value); }
                if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await client.SendAsync(request, deadline.Token).ConfigureAwait(false)) {
                    int status = (int)response.StatusCode;
                    if (status == 401) throw new ProviderException("登录已过期，请在原应用中重新登录", "expired");
                    if (status == 403) throw new ProviderException("接口拒绝访问（403），请检查代理或登录状态", "error");
                    if (status == 429) throw new ProviderException("查询过于频繁，稍后自动重试", "error");
                    if (!response.IsSuccessStatusCode) throw new ProviderException("服务暂时不可用（HTTP " + status + "）", "error");
                    return J.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                }
            }
        }
        private static string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }
        private static string ConfigPath(string variable, string fallback, string file) { string path = Environment.GetEnvironmentVariable(variable); if (String.IsNullOrWhiteSpace(path)) path = Path.Combine(Home, fallback); path = Environment.ExpandEnvironmentVariables(path); if (path.StartsWith("~")) path = Home + path.Substring(1); return Path.Combine(path, file); }
        public static string CodexHome() { return Path.GetDirectoryName(ConfigPath("CODEX_HOME", ".codex", "auth.json")); }
        public async Task<ProviderState> Fetch(string id) {
            if (ProviderCatalog.LocalOnly(id)) {
                string now = DateTime.UtcNow.ToString("o");
                return new ProviderState { Id = id, Status = "ready", Message = "仅本机用量", Plan = "本机日志", LastSuccess = now, LastAttempt = now };
            }
            try {
                ProviderState s;
                switch (id) {
                    case "codex": {
                        object a = J.File(ConfigPath("CODEX_HOME", ".codex", "auth.json")); string token = J.Str(a, "tokens", "access_token"); Need(token, id);
                        var headers = new Dictionary<string, string> { { "User-Agent", "codex-cli" } }; string account = J.Str(a, "tokens", "account_id"); if (account.Length > 0) headers.Add("ChatGPT-Account-Id", account);
                        object usage;
                        try { usage = await Request("https://chatgpt.com/backend-api/wham/usage", token, null, null, headers).ConfigureAwait(false); }
                        catch (ProviderException e) { if (e.State == "expired") throw new ProviderException("Codex 登录令牌已过期。打开 Codex 应用或运行一次 codex 即可自动续期，然后刷新。", "expired"); throw; }
                        s = Parsers.Codex(usage); s.Account = J.Str(J.Jwt(J.Str(a, "tokens", "id_token")), "email");
                        try {
                            var resetHeaders = new Dictionary<string, string>(headers) { { "OpenAI-Beta", "codex-1" }, { "originator", "Codex Desktop" }, { "Accept", "application/json" } };
                            object credits = await Request("https://chatgpt.com/backend-api/wham/rate-limit-reset-credits", token, null, null, resetHeaders, 4).ConfigureAwait(false);
                            UsageDetails.ParseResetCredits(s, credits, DateTime.UtcNow);
                        } catch { s.ResetCreditsError = "重置额度暂未返回"; }
                        break;
                    }
                    case "claude": {
                        // 凭据链：钥匙串「Claude Code-credentials」（新版 Claude Code）优先，
                        // ~/.claude/.credentials.json 兜底（同一 claudeAiOauth JSON 结构）。
                        object a = ClaudeCredentials.Read(ConfigPath("CLAUDE_CONFIG_DIR", ".claude", ".credentials.json"), Keychain.GetFromService); string token = J.Str(a, "claudeAiOauth", "accessToken"); Need(token, id);
                        object usage;
                        try { usage = await Request("https://api.anthropic.com/api/oauth/usage", token, null, null, new Dictionary<string, string> { { "anthropic-beta", "oauth-2025-04-20" } }).ConfigureAwait(false); }
                        catch (ProviderException e) { if (e.State == "expired") throw new ProviderException("Claude Code 的登录令牌已过期。运行一次 Claude Code 即可自动续期，然后刷新。", "expired"); throw; }
                        s = Parsers.Claude(usage);
                        string plan = J.Str(a, "claudeAiOauth", "subscriptionType"); if (plan.Length > 0) s.Plan = "Claude " + CultureInfo.InvariantCulture.TextInfo.ToTitleCase(plan);
                        try { s.Account = J.Str(J.File(Path.Combine(Home, ".claude.json")), "oauthAccount", "emailAddress"); } catch { } break;
                    }
                    case "cursor": {
                        // Windows 的 %APPDATA%\Cursor\... 在 macOS 是 ~/Library/Application Support/Cursor/...。
                        string db = PlatformPaths.CursorStateDb;
                        string cookie = CursorCookie(); Need(cookie, id);
                        var headers = new Dictionary<string, string> { { "User-Agent", "Mozilla/5.0" } };
                        s = Parsers.Cursor(await Request("https://cursor.com/api/usage-summary", null, cookie, null, headers).ConfigureAwait(false));
                        // Grok Bot is a separate weekly allowance. A missing or rejected call
                        // must not turn the whole Cursor provider into an error.
                        try { Parsers.CursorGrokBot(s, await CursorDashboard("get-sand-usage-status", "{}").ConfigureAwait(false)); } catch { }
                        s.Account = NativeCredentials.SqliteText(db, "cursorAuth/cachedEmail"); break;
                    }
                    case "deepseek": {
                        // Phase 5：活动账户的密钥（env 优先语义不变）。
                        string key = Store.ProviderKey(id, config);
                        // No API key but DeepSeek Harness in use: its local usage is all there is to show.
                        // (The Harness signs in with a DeepSeek platform grant that the balance API does not accept.)
                        if (String.IsNullOrWhiteSpace(key) && HarnessLogs.Present) { string at = DateTime.UtcNow.ToString("o"); return new ProviderState { Id = id, Status = "ready", Message = "仅本机用量", Plan = "DeepSeek Harness", LastSuccess = at, LastAttempt = at }; }
                        Need(key, id); s = Parsers.DeepSeek(await Request("https://api.deepseek.com/user/balance", key, null, null, null).ConfigureAwait(false)); break;
                    }
                    case "grok": {
                        object auth = J.File(ConfigPath("GROK_HOME", ".grok", "auth.json")); object entry = J.Dict(auth).Where(p => p.Key == "https://accounts.x.ai/sign-in" || p.Key.StartsWith("https://auth.x.ai::", StringComparison.Ordinal)).Where(p => J.Str(p.Value, "key").Length > 0).OrderByDescending(p => J.Str(p.Value, "expires_at")).Select(p => p.Value).FirstOrDefault();
                        string token = J.Str(entry, "key"); Need(token, id); var headers = new Dictionary<string, string> { { "X-XAI-Token-Auth", "xai-grok-cli" }, { "x-grok-client-mode", "cli" }, { "x-grok-client-version", "1.0.0" } }; string userId = J.Str(entry, "user_id"); if (userId.Length > 0) headers.Add("x-userid", userId);
                        s = Parsers.Grok(await Request("https://cli-chat-proxy.grok.com/v1/billing?format=credits", token, null, null, headers).ConfigureAwait(false)); break;
                    }
                    case "antigravity": {
                        ProviderState local = await LocalAntigravity.TryRead().ConfigureAwait(false);
                        if (local != null) { s = local; break; }
                        string credential = NativeCredentials.GenericCredential("gemini:antigravity"); Need(credential, id); object auth = J.Parse(credential); string token = J.Str(auth, "token", "access_token"); Need(token, id);
                        const string endpoint = "https://cloudcode-pa.googleapis.com/v1internal:"; var headers = new Dictionary<string, string> { { "User-Agent", "antigravity" } };
                        object loaded = await Request(endpoint + "loadCodeAssist", token, null, "{\"metadata\":{\"ideType\":\"ANTIGRAVITY\"}}", headers).ConfigureAwait(false);
                        string project = J.Str(loaded, "cloudaicompanionProject"); string body = project.Length == 0 ? "{}" : J.Serializer().Serialize(new Dictionary<string, string> { { "project", project } });
                        s = new ProviderState { Id = id };
                        if (project.Length > 0) { try { s = Parsers.Antigravity(await Request(endpoint + "retrieveUserQuotaSummary", token, null, body, headers).ConfigureAwait(false)); } catch (ProviderException e) { if (e.State == "expired") throw; } }
                        if (s.Quotas.Count == 0) {
                            s = Parsers.Antigravity(await Request(endpoint + "fetchAvailableModels", token, null, body, headers).ConfigureAwait(false));
                            if (s.Quotas.Count > 0 && s.Quotas.All(q => q.Used == 0)) throw new ProviderException("仅检测到模型可用性，尚未取得真实额度。请打开 Antigravity 后刷新。", "setup");
                        }
                        break;
                    }
                    case "copilot": {
                        string token = CopilotToken(); Need(token, id);
                        var headers = new Dictionary<string, string> { { "Authorization", "token " + token }, { "Editor-Version", "vscode/1.96.2" }, { "Editor-Plugin-Version", "copilot-chat/0.26.7" }, { "User-Agent", "GitHubCopilotChat/0.26.7" }, { "X-Github-Api-Version", "2025-04-01" } };
                        s = Parsers.Copilot(await KeyedRequest(id, "https://api.github.com/copilot_internal/user", null, headers).ConfigureAwait(false));
                        try { s.Account = J.Str(await Request("https://api.github.com/user", null, null, null, new Dictionary<string, string> { { "Authorization", "token " + token }, { "Accept", "application/vnd.github+json" } }, 6).ConfigureAwait(false), "login"); } catch { }
                        break;
                    }
                    case "kimi": {
                        string key = Store.ProviderKey(id, config);
                        // 静默回退：未保存密钥、也没有环境变量时，从本机 Kimi CLI 导入——
                        // config.toml 内联 api_key（长期凭据，导入即持久化到活动账户），否则 OAuth
                        // access_token（短寿命凭据，不落钥匙串，每次 Fetch 重新解析并按需刷新）。
                        if (String.IsNullOrWhiteSpace(key)) key = LocalCliCredentials.Import(id, Accounts.ActiveId(config, id));
                        Need(key, id);
                        string host = config.KimiRegion == "international" ? "https://api.kimi.ai" : "https://api.kimi.com";
                        s = Parsers.Kimi(await KeyedRequest(id, host + "/coding/v1/usages", key, null).ConfigureAwait(false)); break;
                    }
                    case "opencode": {
                        string key = Store.ProviderKey(id, config); Need(key, id);
                        s = Parsers.OpenCode(await KeyedRequest(id, "https://opencode.ai/zen/go/v1/usage", key, null).ConfigureAwait(false), DateTime.UtcNow); break;
                    }
                    case "zcode": {
                        string key = Store.ProviderKey(id, config);
                        // 静默回退：未保存密钥、也没有环境变量时，从本机 ZCode CLI 的登录凭据导入（到活动账户）。
                        if (String.IsNullOrWhiteSpace(key)) key = LocalCliCredentials.Import(id, Accounts.ActiveId(config, id));
                        Need(key, id);
                        string host = config.ZaiRegion == "global" ? "https://api.z.ai" : "https://open.bigmodel.cn";
                        s = Parsers.Zai(await KeyedRequest(id, host + "/api/monitor/usage/quota/limit", key, null).ConfigureAwait(false)); break;
                    }
                    case "mimo": {
                        // 凭据链：桌面客户端换票（每 tick 现铸、不落盘）→ 钥匙串手动 Console Cookie
                        // → 都没有才报 setup。换票不可用（客户端未装/未登录/加密存储/被拒）一律
                        // 回退手动路径，行为与旧版一致。
                        string cookie = null;
                        string[] stores = MimoDesktop.StoreCandidates(Home);
                        if (stores.Length > 0) {
                            // 15 秒总时限覆盖整个逐候选循环（本地读库 + 每条候选各一次换票）。
                            using (var deadline = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15))) {
                                System.Threading.CancellationToken token = deadline.Token;
                                try { cookie = await MimoDesktopCookie(stores, (hop, hopDeadline) => MimoExchangeHop(hop, hopDeadline), token).ConfigureAwait(false); } catch { }
                            }
                        }
                        bool automatic = cookie != null;
                        if (!automatic) {
                            string raw = Store.ProviderKey(id, config); Need(raw, id);
                            cookie = Parsers.MimoCookie(raw);
                        }
                        // Console Cookie 通道：/balance（钱包+探活）→ 账户 / 套餐 / 月度用量 →
                        // 消费金额探测。账户、套餐、消费三处失败不拖垮读数，但未授权（expired）
                        // 任何一处出现都要浮出——Cookie 失效不该被「部分成功」掩盖。
                        s = new ProviderState { Id = id, Plan = "MiMo" };
                        Parsers.MimoWallet(s, await MiMoGet("/balance", cookie).ConfigureAwait(false));
                        try { s.Account = Parsers.MimoAccount(await MiMoGet("/userProfile", cookie).ConfigureAwait(false)); } catch (ProviderException e) { if (e.State == "expired") throw; }
                        string periodEnd = "";
                        try { periodEnd = Parsers.MimoPlan(s, await MiMoGet("/tokenPlan/detail", cookie).ConfigureAwait(false)); } catch (ProviderException e) { if (e.State == "expired") throw; }
                        Parsers.MimoUsage(s, await MiMoGet("/tokenPlan/usage", cookie).ConfigureAwait(false), periodEnd);
                        // 消费金额端点：金额暂不展示（钱包余额已进 Balances），失败可容忍；
                        // 仍探活以便 Cookie 失效在这里同样暴露。
                        try { await MiMoGet("/usage", cookie).ConfigureAwait(false); } catch (ProviderException e) { if (e.State == "expired") throw; }
                        // 状态区分凭据来源（与 CredentialPanel 的「已连接 · <套餐>」胶囊同口径）。
                        if (automatic) s.Plan += " · 自动续期（桌面客户端）";
                        break;
                    }
                    case "stepfun": {
                        // Oasis-Token 通道：先按需滚动刷新（access 剩余 <30 分钟时走 RefreshToken
                        // 端点换新对并写回钥匙串，见 StepFunAuth），再 QueryStepPlanRateLimit（额度，
                        // 必需）→ GetStepPlanStatus（套餐名，短超时可吞）。token 自带 app_id 时站点
                        // 由它锁定，不做跨站重试（StepFunChannel）；appid 提取失败才按区域回退、
                        // expired 换站重试。请求仍被拒（401/expired）时兜底强制刷新一次再试——
                        // 覆盖 access 被服务端提前作废的场景；刷新判死（匿名令牌/refresh 过期）
                        // 直接走现有 expired 文案，不无限重试。
                        string raw = Store.ProviderKey(id, config); Need(raw, id);
                        string token = Parsers.StepFunToken(raw);
                        if (token.Length == 0) throw new ProviderException("Oasis-Token 无法识别：请粘贴纯 Token 或整段 Oasis-Token=… Cookie。", "setup");
                        // env 来的 token 不落钥匙串：轮换只在内存生效，下次读取仍以 env 为准。
                        bool persistable = Store.KeyEnvironmentName(id, config).Length == 0;
                        string accountId = Accounts.ActiveId(config, id);
                        Func<string, string, string, string, int, Task<object>> post = StepFunPost;
                        // 刷新请求的 host 与 oasis-appid 必须同站（跨站带有效 token 会被平台判
                        // 盗用烧 token）：两侧都从同一生效 appid（token 自带优先、提取不到按区域
                        // 回退）派生，天然一致。
                        // 写回失败可观测：SetProviderKey 写失败回 false（不抛异常），只记一条
                        // 日志（context 固定文案，绝不带 token 本体）——否则"刷新只在内存生效、
                        // 下次还拿旧 token"完全静默，用户与排障都看不到。
                        Action<string> persist = null;
                        if (persistable) persist = fresh => {
                            try { if (!Store.SetProviderKey(id, accountId, fresh)) ErrorLog.Append("stepfun-keychain-writeback", new InvalidOperationException("Oasis-Token 刷新结果写回钥匙串失败，本次刷新仅在内存生效")); }
                            catch (Exception e) { ErrorLog.Append("stepfun-keychain-writeback", e); }
                        };
                        Func<string, bool, Task<string>> roll = (current, force) => {
                            // token 提取不到 app_id 时站点不确定：区域回退的 appid 可能打错站
                            //（平台判盗用烧 token），非 force 的预刷新直接跳过返回 current——
                            // 等 StepFunChannel 校准 region 后，靠 401/expired 兜底路径刷新。
                            // force 路径不变（兜底刷新必须能救回被判过期的会话）。
                            if (!force && Parsers.StepFunTokenAppId(current).Length == 0) return Task.FromResult(current);
                            return StepFunAuth.EnsureFreshAsync(current, Parsers.StepFunWebId(current), Parsers.StepFunAppId(current, config.StepfunRegion), Parsers.StepFunHostForAppId(Parsers.StepFunAppId(current, config.StepfunRegion)), force, post, persist);
                        };
                        try { token = await roll(token, false).ConfigureAwait(false); }
                        catch (ProviderException e) { if (e.State == "expired") throw; }
                        try {
                            s = await StepFunChannel(token, config.StepfunRegion, post, region => {
                                config.StepfunRegion = region;
                                // 与 Connect 的区域切换同款持久化：写失败不拖垮本次已到手的读数。
                                try { Store.Write("settings.json", config); } catch { }
                            }).ConfigureAwait(false);
                        } catch (ProviderException e) when (e.State == "expired") {
                            token = await roll(token, true).ConfigureAwait(false);   // 判死在 roll 内抛 expired
                            s = await StepFunChannel(token, config.StepfunRegion, post, region => {
                                config.StepfunRegion = region;
                                try { Store.Write("settings.json", config); } catch { }
                            }).ConfigureAwait(false);
                        }
                        break;
                    }
                    default: {
                        CustomProvider custom;
                        if (!ProviderCatalog.Custom.TryGetValue(id, out custom)) throw new ProviderException("不支持的平台", "setup");
                        // Phase 5：自定义平台同样按活动账户取密钥（env 不适用，KeyEnv 为空）。
                        s = await CustomProviders.Fetch(client, custom, Store.SavedKey(id, Accounts.ActiveId(config, id)), DateTime.UtcNow).ConfigureAwait(false); break;
                    }
                }
                if (s.Quotas.Count == 0 && s.Balances.Count == 0) throw new ProviderException("接口未返回可识别的额度；没有将缺失数据当作零", "error");
                s.Status = "ready"; s.Message = "已连接"; s.LastSuccess = DateTime.UtcNow.ToString("o"); s.LastAttempt = s.LastSuccess; return s;
            } catch (ProviderException e) { return new ProviderState { Id = id, Status = e.State, Message = e.Message, LastAttempt = DateTime.UtcNow.ToString("o") }; }
            catch (TaskCanceledException) { return Failure(id, "查询超时，请检查代理连接"); }
            catch (HttpRequestException) { return Failure(id, "网络连接失败，请检查代理设置"); }
            catch (UnauthorizedAccessException) { return Failure(id, "无法读取本机登录文件，请检查文件权限"); }
            catch (Exception) { return Failure(id, "无法读取此平台的数据格式，请在原应用登录后重试"); }
        }
        // A public JSON document (the GitHub releases API for the update check).
        public Task<object> GetJson(string url) { return Request(url, null, null, null, new Dictionary<string, string> { { "Accept", "application/vnd.github+json" } }); }
        // Cursor: the editor's own session (state.vscdb), sent the way cursor.com expects it.
        private static string CursorDb { get { return PlatformPaths.CursorStateDb; } }
        public static string CursorCookie() {
            string token = NativeCredentials.SqliteText(CursorDb, "cursorAuth/accessToken"); if (String.IsNullOrWhiteSpace(token)) return "";
            string sub = J.Str(J.Jwt(token), "sub"); if (sub.Contains("|")) sub = sub.Substring(sub.LastIndexOf('|') + 1);
            return sub.Length == 0 ? "" : sub + "%3A%3A" + token;
        }
        // POST cursor.com/api/dashboard/<endpoint> with the editor session.
        public Task<object> CursorDashboard(string endpoint, string body) {
            string cookie = CursorCookie(); if (cookie.Length == 0) throw new ProviderException(ProviderCatalog.Help("cursor"), "setup");
            var headers = new Dictionary<string, string> { { "User-Agent", "Mozilla/5.0" }, { "Origin", "https://cursor.com" }, { "Referer", "https://cursor.com/dashboard" }, { "Accept", "application/json" } };
            return Request("https://cursor.com/api/dashboard/" + endpoint, null, cookie, body, headers, 25);
        }
        public Task<ProviderState> FetchCustom(CustomProvider provider, string secret) { return CustomProviders.Fetch(client, provider, secret, DateTime.UtcNow); }
        // API-key providers: a rejected key is a settings problem, not an expired app login.
        private async Task<object> KeyedRequest(string id, string url, string key, Dictionary<string, string> headers) {
            try { return await Request(url, key, null, null, headers).ConfigureAwait(false); }
            catch (ProviderException e) {
                if (e.State != "expired") throw;
                throw new ProviderException(id == "copilot" ? "GitHub 授权已失效，请重新使用 GitHub 登录。" : ProviderCatalog.Name(id) + " 的 API Key 无效或已过期，请重新填写。", "expired");
            }
        }
        // ── 云端额度通道（MiMo Console Cookie / StepFun Oasis-Token）────────
        // 这些端点只为官方 Web 控制台服务，凭据随浏览器会话走：请求带桌面浏览器 UA、
        // 显式 Cookie/Origin/Referer 头，host 锁死在官方域名白名单内。
        internal const string DesktopUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
        // MiMo：全部 GET {base}/api/v1/<path>，Cookie 即归一化后的 Console Cookie。
        private Task<object> MiMoGet(string path, string cookie, int timeoutSeconds = 18) {
            var headers = new Dictionary<string, string> {
                { "Accept", "application/json, text/plain, */*" },
                { "Cookie", cookie },
                { "Origin", "https://platform.xiaomimimo.com" },
                { "Referer", "https://platform.xiaomimimo.com/#/console/balance" }
            };
            return CloudRequest("https://platform.xiaomimimo.com/api/v1" + path, new[] { "platform.xiaomimimo.com" }, null, headers, Parsers.MimoExpiredMessage, timeoutSeconds);
        }
        // MiMo 桌面客户端自动续期（候选逐组换票）：旧实现只读第一个存在的候选，而旧版
        // 分区（"Xiaomi MiMo AI"）恰在候选列表首位——升级用户该路径残留、passToken 已
        // 失效时，自动续期永远打不中现行分区。现在对每个存在的候选分别读库 + 换票：
        // 任一失败结局（拒登 / 限流 / 不可用）都继续下一个——候选至多两个、15 秒总时限
        // 封顶，瞬时失败不值得为它放弃另一条可能有效的候选；任一候选成功即返回它的
        // Console Cookie；全部失败返回 null（调用方回退手动 Cookie）。15 秒总时限由调用方的
        // deadline 覆盖整个循环。transport 缝注入真实 HTTP（MimoExchangeHop），自测传
        // 合成链；ReadAccount 的单库解析逻辑不变（含 0600 副本与用完即删）。
        internal static async Task<string> MimoDesktopCookie(string[] candidates, Func<MimoExchange.Hop, System.Threading.CancellationToken, Task<MimoExchange.Reply>> transport, System.Threading.CancellationToken deadline) {
            bool minted = false; MimoExchange.Outcome last = MimoExchange.Outcome.Unavailable;
            MimoDesktop.ReadReason lastRead = MimoDesktop.ReadReason.StoreAbsent;
            foreach (string store in candidates ?? Array.Empty<string>()) {
                MimoDesktop.ReadResult account = MimoDesktop.ReadAccount(new[] { store });
                // 不存在（StoreAbsent，客户端未装）/ 读不了 / 加密存储 / 未登录：换下一个。
                // StoreAbsent 以外的读取失败都记下来——客户端已装却因加密存储或未登录静默
                // 回退是最难排障的形态（循环结束后统一去重落一条，见下方）。
                if (account.Reason != MimoDesktop.ReadReason.Ok) { if (account.Reason != MimoDesktop.ReadReason.StoreAbsent) lastRead = account.Reason; continue; }
                minted = true;
                MimoExchange.Result fresh = await MimoExchange.Mint(MimoDesktop.SeedHeader(account.Cookie), MimoExchange.ConsoleEntry(), hop => transport(hop, deadline), null, deadline).ConfigureAwait(false);
                if (fresh.Outcome == MimoExchange.Outcome.Ok && fresh.CookieHeader.Length > 0) { NoteMimoMint(null); return fresh.CookieHeader; }
                // Ok 但 Cookie 头为空（会话 Cookie 全部过期/被白名单丢弃的罕见形态）按
                // Unavailable 归类，否则诊断文案会自相矛盾地出现"换票结局 Ok"。
                last = fresh.Outcome == MimoExchange.Outcome.Ok ? MimoExchange.Outcome.Unavailable : fresh.Outcome;
            }
            if (minted) NoteMimoMint("换票结局 " + last);   // 有候选读出来了但换票没成：记一条诊断（去重后）
            else if (lastRead != MimoDesktop.ReadReason.StoreAbsent) NoteMimoMint("凭据读取结局 " + lastRead);
            return null;
        }
        // 换票失败可观测：候选读出来但 Mint 没成功、或客户端已装但凭据读不出时，旧实现
        // 整段 catch{} 完全静默——用户只看到"回退手动 Cookie 后 expired"，排障没有任何
        // 线索。现在经 SourceHealth.RecordError 记一条（内存 LastError 进健康摘要；
        // Desktop 再把 ErrorSink 挂成 MonitorPanel.LogError 落 errors.log）。Fetch 每分钟
        // 一次，每次都追加会刷屏，因此按「结局变化才记」去重：静态记录上次已记的结局，
        // 相同结局不再重复落盘（LastError 保留首次记录、时间戳随之变旧，换取日志不刷屏）；
        // 成功即清零，下次失败算新的变化。文案只带枚举结局，绝不带 passToken /
        // serviceToken 等凭据值。detail = null 表示成功（只清零）。gate 防并发 Fetch 下
        // check-then-act 重复落一行。
        private static string mimoMintLogged;
        private static readonly object mimoMintGate = new object();
        private static void NoteMimoMint(string detail) {
            lock (mimoMintGate) {
                if (mimoMintLogged == detail) return;
                mimoMintLogged = detail;
                if (detail == null) return;   // 成功：只清零，不记
                SourceHealth.RecordError("mimo", new InvalidOperationException("MiMo 桌面客户端自动续期失败（" + detail + "），本次回退手动 Console Cookie"));
            }
        }
        // MiMo 桌面客户端换票的逐跳传输：复用本服务的 HttpClient（已禁自动重定向、走同一代理、
        // UseCookies=false 保 Set-Cookie 原样可见），逐跳做内网地址守卫；总时限由 deadline 控制。
        private async Task<MimoExchange.Reply> MimoExchangeHop(MimoExchange.Hop hop, System.Threading.CancellationToken deadline) {
            await GuardPrivateAddress(hop.Url).ConfigureAwait(false);
            using (var request = new HttpRequestMessage(HttpMethod.Get, hop.Url)) {
                if (hop.Headers != null) foreach (var h in hop.Headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                using (HttpResponseMessage response = await client.SendAsync(request, deadline).ConfigureAwait(false)) {
                    var reply = new MimoExchange.Reply { Status = (int)response.StatusCode, Location = response.Headers.Location == null ? "" : response.Headers.Location.ToString() };
                    foreach (string value in response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string> lines) && lines != null ? lines : Enumerable.Empty<string>()) reply.SetCookie.Add(value);
                    reply.Body = await ReadCappedAsync(response.Content, MimoExchangeBodyLimit).ConfigureAwait(false);
                    return reply;
                }
            }
        }
        // 换票链响应体上限：对齐 NativeCredentials 的 4MB 单元格口径。SSO 链上的页面
        // 可能返回任意大的 HTML，ReadAsStringAsync 不限量会把整段灌进内存；超出即截断，
        // 换票判定只需要 401 body 的 loginUrl 与 console 的 code 字段（都在 JSON 开头），
        // 截断不影响分类。internal 供自测直接喂合成内容（MimoExchangeHop 走真实 HTTP，
        // 不自测）。
        private const int MimoExchangeBodyLimit = 4 * 1024 * 1024;
        internal static async Task<string> ReadCappedAsync(HttpContent content, int maxBytes) {
            using (Stream stream = await content.ReadAsStreamAsync().ConfigureAwait(false)) {
                // 租 80KB 池化分块按需攒，小 body（换票链的常见形态）不再固定吃 4MB LOH 分配；
                // 只有真到大 body 时 MemoryStream 才按倍增长到上限。
                var gathered = new MemoryStream();
                byte[] chunk = System.Buffers.ArrayPool<byte>.Shared.Rent(80 * 1024);
                try {
                    int read;
                    while (gathered.Length < maxBytes && (read = await stream.ReadAsync(chunk, 0, (int)Math.Min(chunk.Length, maxBytes - gathered.Length)).ConfigureAwait(false)) > 0) gathered.Write(chunk, 0, read);
                    return Encoding.UTF8.GetString(gathered.GetBuffer(), 0, (int)gathered.Length);
                } finally {
                    System.Buffers.ArrayPool<byte>.Shared.Return(chunk);
                }
            }
        }
        // StepFun：固定 POST 空 JSON 体，Oasis-Token 同时放在专用 Cookie 与 oasis-* 头里。
        // appid 必须与 token 所属应用一致（由 Parsers.StepFunAppId 从 token 提取）；
        // connect-protocol-version / Origin / Referer 与官方 Web 端请求保持一致。
        private Task<object> StepFunPost(string url, string token, string webid, string appid, int timeoutSeconds = 18) {
            string origin = Regex.Match(url, "^https://[^/]+").Value;
            var headers = new Dictionary<string, string> {
                { "Accept", "*/*" },
                { "connect-protocol-version", "1" },
                { "oasis-appid", appid },
                { "oasis-platform", "web" },
                { "oasis-webid", webid },
                { "Origin", origin },
                { "Referer", origin + "/plan-usage" },
                { "Cookie", "Oasis-Token=" + token + "; Oasis-Webid=" + webid }
            };
            return CloudRequest(url, new[] { "platform.stepfun.ai", "platform.stepfun.com" }, "{}", headers, Parsers.StepFunExpiredMessage, timeoutSeconds);
        }
        // StepFun 通道主体（post 注入真实 HTTP 供自测替换；persistRegion 写回回退后的区域）。
        // 站点选择：token 各段 JWT 里能提取到 app_id 时站点由它决定（20700→.ai、10300→.com，
        // 见 StepFunHostForAppId），且不做跨站重试——平台对"appid 与站点不配"的有效 token 判
        // 盗用并烧 token，锁站是保护；只有 appid 提取失败（垃圾 token、靠区域回退）时才维持
        // 旧行为：按配置区域起手，expired 视为区域错配自动换另一站完整重试一次（appid 按
        // 新区域重算）。回退成功才写回区域，后续刷新直接用对的站点；两站都 expired 抛最后一
        // 次（真过期与双重错配对用户的处理动作相同）。非 expired 失败立即上抛，不做无意义的
        // 跨站重试。
        internal static async Task<ProviderState> StepFunChannel(string token, string region, Func<string, string, string, string, int, Task<object>> post, Action<string> persistRegion) {
            string webid = Parsers.StepFunWebId(token);
            string tokenAppId = Parsers.StepFunTokenAppId(token);
            // 每个候选站 = { host, appid }。锁站时只有一个候选；区域回退时按配置区域起手。
            string[][] sites = tokenAppId.Length > 0
                ? new[] { new[] { Parsers.StepFunHostForAppId(tokenAppId), tokenAppId } }
                : new[] {
                    new[] { Parsers.StepFunHost(region), Parsers.StepFunAppId(token, region) },
                    new[] { Parsers.StepFunHost(region == "china" ? "global" : "china"), Parsers.StepFunAppId(token, region == "china" ? "global" : "china") }
                };
            ProviderException expired = null;
            foreach (string[] site in sites) {
                string host = site[0], appid = site[1];
                try {
                    ProviderState s = Parsers.StepFun(await post(host + "/api/step.openapi.devcenter.Dashboard/QueryStepPlanRateLimit", token, webid, appid, 18).ConfigureAwait(false));
                    // 主请求成功即证明凭据有效：套餐名是次要数据，任何失败（含 expired——
                    // 两个端点对同一 token 的判定可能不一致）只吞掉，不把整份读数连坐成过期。
                    try {
                        string plan = Parsers.StepFunPlan(await post(host + "/api/step.openapi.devcenter.Dashboard/GetStepPlanStatus", token, webid, appid, 6).ConfigureAwait(false));
                        if (plan.Length > 0) s.Plan = plan;
                    } catch (Exception) { }
                    if (tokenAppId.Length == 0 && host != Parsers.StepFunHost(region) && persistRegion != null) persistRegion(region == "china" ? "global" : "china");
                    return s;
                } catch (ProviderException e) when (e.State == "expired") { expired = e; }
            }
            throw expired;
        }
        // 共用发送与状态映射：只有 401 → 未授权（token 失效在 HTTP 层唯一可靠的信号）；
        // 403 / 3xx（重定向已禁用）→ 服务错误——StepFun 平台真正的鉴权失败走 HTTP 200 +
        // 信封 status≠1，HTTP 层的 403/3xx 多为 WAF 风控或 CDN 重定向，判成过期会误导
        // 用户重填仍然有效的 token；429 → 限流；其余非 2xx / 坏 JSON → 服务不可用。
        private async Task<object> CloudRequest(string url, string[] allowedHosts, string body, Dictionary<string, string> extra, string loginMessage, int timeoutSeconds) {
            Uri uri = CheckedUri(url, allowedHosts);
            await GuardPrivateAddress(uri).ConfigureAwait(false);
            using (var deadline = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            using (var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, uri)) {
                request.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
                if (extra != null) foreach (var h in extra) { request.Headers.Remove(h.Key); request.Headers.TryAddWithoutValidation(h.Key, h.Value); }
                if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await client.SendAsync(request, deadline.Token).ConfigureAwait(false)) {
                    int status = (int)response.StatusCode;
                    ProviderException mapped = CloudStatusException(status, loginMessage);
                    if (mapped != null) throw mapped;
                    if (status == 429) throw new ProviderException("查询过于频繁，稍后自动重试", "error");
                    if (!response.IsSuccessStatusCode) throw new ProviderException("服务暂时不可用（HTTP " + status + "）", "error");
                    string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    try { return J.Parse(text); }
                    catch (Exception) { throw new ProviderException("服务暂时不可用（返回了无法解析的数据）", "error"); }
                }
            }
        }
        // 上面状态映射的纯函数形态（供自测直接调用，不发起请求）：401 之外的状态一律
        // 不代表凭据失效。返回 null 表示该状态不在此映射，交回调用方按后续分支处理。
        internal static ProviderException CloudStatusException(int status, string loginMessage) {
            if (status == 401) return new ProviderException(loginMessage, "expired");
            if (status == 403) return new ProviderException("访问被拒绝，稍后自动重试", "error");
            if (status >= 300 && status < 400) return new ProviderException("服务返回了重定向", "error");
            return null;
        }
        // 出口安全阀（可从自测直接调用）：只接受 https；host 必须与官方域名完全相等
        //（user-info "@"、子域后缀 ".evil.com"、大小写混淆都进不来）。返回解析后的 Uri。
        public static Uri CheckedUri(string url, params string[] allowedHosts) {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps) throw new ProviderException("云端额度通道只允许 https 请求", "error");
            string host = uri.Host.ToLowerInvariant();
            if (!allowedHosts.Contains(host)) throw new ProviderException("请求主机 " + host + " 不在官方域名白名单内，已拒绝连接", "error");
            return uri;
        }
        // hosts 劫持把官方域名解析到环回/内网地址时拒绝连接（尽力而为：解析失败不拦截，
        // 交给请求自身的网络错误路径——经代理的场景本地可能不解析）。internal 供 MiMo
        // 桌面客户端换票的逐跳传输复用。
        internal static async Task GuardPrivateAddress(Uri uri) {
            IPAddress[] addresses;
            try { addresses = await Dns.GetHostAddressesAsync(uri.Host).ConfigureAwait(false); } catch { return; }
            if (addresses.Any(IsPrivateAddress)) throw new ProviderException(uri.Host + " 解析到了内网地址，已拒绝连接", "error");
        }
        // 环回 / 私有 / 保留地址判定（含 IPv4-mapped IPv6 与 fc00::/7 唯一本地地址）。
        internal static bool IsPrivateAddress(IPAddress address) {
            if (address == null) return true;
            byte[] raw = address.GetAddressBytes();
            if (raw.Length == 16 && raw[10] == 0xFF && raw[11] == 0xFF) return IsPrivateAddress(new IPAddress(raw.Skip(12).ToArray()));   // ::ffff:a.b.c.d
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) {
                byte[] b = raw;
                return b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168;
            }
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) {
                if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || IPAddress.Equals(address, IPAddress.IPv6Loopback) || IPAddress.Equals(address, IPAddress.IPv6Any)) return true;
                return (raw[0] & 0xFE) == 0xFC;
            }
            return false;
        }
        // Copilot: the token from the in-app GitHub device login, else one already saved by
        // an official Copilot client (copilot.vim / JetBrains / Copilot CLI) — read-only.
        // Copilot 客户端保存 GitHub 授权的候选位置（apps.json / hosts.json × 两个根目录）。
        // SourceHealth 的凭据存在性判断与此同源（review P2-3），不要在两处各写一份。
        internal static readonly string[] CopilotTokenPaths = BuildCopilotTokenPaths();
        private static string[] BuildCopilotTokenPaths() {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new[] { Path.Combine(local, "github-copilot", "apps.json"), Path.Combine(local, "github-copilot", "hosts.json"), Path.Combine(home, ".config", "github-copilot", "apps.json"), Path.Combine(home, ".config", "github-copilot", "hosts.json") };
        }
        public static string CopilotToken() {
            string saved = Store.SavedKey("copilot"); if (saved.Length > 0) return saved;
            foreach (string path in CopilotTokenPaths) {
                try {
                    foreach (var entry in J.Dict(J.File(path))) if (entry.Key.StartsWith("github.com", StringComparison.OrdinalIgnoreCase)) { string token = J.Str(entry.Value, "oauth_token"); if (token.Length > 0) return token; }
                } catch { }
            }
            return "";
        }
        // GitHub OAuth device flow with the public VS Code Copilot client ID (read:user).
        public sealed class DeviceLogin { public string DeviceCode = "", UserCode = "", VerificationUri = ""; public int Interval = 5, ExpiresIn = 900; }
        private const string CopilotClientId = "Iv1.b507a08c87ecfe98";
        public async Task<DeviceLogin> StartCopilotLogin() {
            object reply = await PostForm("https://github.com/login/device/code", new Dictionary<string, string> { { "client_id", CopilotClientId }, { "scope", "read:user" } }).ConfigureAwait(false);
            var login = new DeviceLogin { DeviceCode = J.Str(reply, "device_code"), UserCode = J.Str(reply, "user_code"), VerificationUri = J.Str(reply, "verification_uri"), Interval = (int)(J.Num(reply, "interval") ?? 5), ExpiresIn = (int)(J.Num(reply, "expires_in") ?? 900) };
            if (login.DeviceCode.Length == 0 || login.UserCode.Length == 0) throw new ProviderException("GitHub 没有返回设备码", "error");
            if (!login.VerificationUri.StartsWith("https://github.com/", StringComparison.Ordinal)) login.VerificationUri = "https://github.com/login/device";
            return login;
        }
        // Returns the token, or null while authorization is still pending.
        public async Task<string> PollCopilotLogin(DeviceLogin login) {
            object reply = await PostForm("https://github.com/login/oauth/access_token", new Dictionary<string, string> { { "client_id", CopilotClientId }, { "device_code", login.DeviceCode }, { "grant_type", "urn:ietf:params:oauth:grant-type:device_code" } }).ConfigureAwait(false);
            string token = J.Str(reply, "access_token"), error = J.Str(reply, "error");
            if (token.Length > 0) return token;
            if (error == "authorization_pending") return null;
            if (error == "slow_down") { login.Interval += 5; return null; }
            if (error == "expired_token") throw new ProviderException("设备码已过期，请重新登录", "error");
            if (error == "access_denied") throw new ProviderException("已在 GitHub 取消授权", "error");
            throw new ProviderException("GitHub 登录失败：" + (error.Length > 0 ? error : "未知错误"), "error");
        }
        private async Task<object> PostForm(string url, Dictionary<string, string> form) {
            using (var request = new HttpRequestMessage(HttpMethod.Post, url)) {
                request.Headers.TryAddWithoutValidation("Accept", "application/json"); request.Headers.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
                request.Content = new FormUrlEncodedContent(form);
                using (HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false)) {
                    if (!response.IsSuccessStatusCode) throw new ProviderException("GitHub 暂时不可用（HTTP " + (int)response.StatusCode + "）", "error");
                    return J.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                }
            }
        }
        private static ProviderState Failure(string id, string message) { return new ProviderState { Id = id, Status = "error", Message = message, LastAttempt = DateTime.UtcNow.ToString("o") }; }
        private static void Need(string value, string id) { if (String.IsNullOrWhiteSpace(value)) throw new ProviderException(ProviderCatalog.Help(id), "setup"); }
        public void Dispose() { client.Dispose(); }
    }
    // Windows 用 advapi32 CredRead（凭据管理器）与 winsqlite3 P/Invoke；macOS 上
    // 凭据走钥匙串（Keychain，service "codeusagemonit"），SQLite 走 Sqlite 适配层
    // （Microsoft.Data.Sqlite，只读 + busy_timeout 1500ms，行数与单元格大小设有上限）。
    internal static class NativeCredentials {
        public static string GenericCredential(string target) {
            if (String.IsNullOrWhiteSpace(target)) return "";
            return Keychain.Get(target);
        }
        // Read-only query returning every column as text; empty on any failure.
        public static List<string[]> SqliteRows(string path, string query) {
            var rows = new List<string[]>(); if (!File.Exists(path)) return rows;
            using (Sqlite db = Sqlite.Open(path, 1500)) {
                if (db == null) return rows;
                foreach (object[] r in db.Query(query)) {
                    if (rows.Count >= 5000) break;
                    var row = new string[r.Length];
                    for (int c = 0; c < r.Length; c++) {
                        byte[] blob = r[c] as byte[];
                        if (blob != null) { if (blob.Length <= 0 || blob.Length > 4 * 1024 * 1024) { row[c] = ""; continue; } row[c] = Encoding.UTF8.GetString(blob); }
                        else if (r[c] == null) row[c] = "";
                        else row[c] = Convert.ToString(r[c], System.Globalization.CultureInfo.InvariantCulture);
                    }
                    rows.Add(row);
                }
            }
            return rows;
        }
        public static string SqliteText(string path, string key) {
            if (!File.Exists(path)) return "";
            using (Sqlite db = Sqlite.Open(path, 1500)) {
                if (db == null) return "";
                var rows = db.Query("SELECT value FROM ItemTable WHERE key = @key LIMIT 1", new Dictionary<string, object> { { "@key", key } });
                if (rows.Count == 0) return "";
                byte[] blob = rows[0][0] as byte[];
                if (blob != null) { if (blob.Length < 1 || blob.Length > 1024 * 1024) return ""; return Encoding.UTF8.GetString(blob); }
                string text = rows[0][0] == null ? "" : Convert.ToString(rows[0][0], System.Globalization.CultureInfo.InvariantCulture);
                return text.Length < 1 || text.Length > 1024 * 1024 ? "" : text;
            }
        }
    }
    public static partial class HistoryService {
        public static string DayKey(DateTime day) { return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        // The last `count` days of a day list — what every "近 30 天" figure has always
        // shown. The stored history now reaches back UsageArchive.HorizonDays days, so the
        // summaries that sum whole per-agent slices must cut here instead of summing the
        // file; otherwise extending the window would silently change those figures.
        public static List<DayUsage> Recent(IEnumerable<DayUsage> days, int count) {
            string oldest = DayKey(DateTime.Today.AddDays(-(count - 1)));
            return days.Where(d => d != null && d.Day != null && String.CompareOrdinal(d.Day, oldest) >= 0).ToList();
        }
        // Clients may delete or prune old session files, and a rebuilt index cannot count
        // them any more. Past days therefore keep the largest total ever observed. Today is
        // always replaced (it is still growing); a time-zone or engine change starts over.
        public static UsageHistory Merge(UsageHistory previous, UsageHistory next, string oldestDay, string today) {
            if (previous == null || next == null || previous.Days.Count == 0 || previous.Zone != next.Zone || previous.Engine != next.Engine) return next;
            foreach (DayUsage old in previous.Days) {
                if (old.Day == null || String.CompareOrdinal(old.Day, today) >= 0 || String.CompareOrdinal(old.Day, oldestDay) < 0) continue;
                int index = next.Days.FindIndex(d => d.Day == old.Day && d.Agent == old.Agent);
                if (index < 0) next.Days.Add(old); else if (next.Days[index].Tokens < old.Tokens) next.Days[index] = old;
            }
            return next;
        }
    }
}
