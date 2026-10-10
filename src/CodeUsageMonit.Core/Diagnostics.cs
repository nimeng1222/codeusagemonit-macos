using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeUsageMonit {
    // ROADMAP Phase 3.3（诊断报告导出）：把版本/运行环境、数据目录探测、每平台健康摘要
    //（SourceHealth.Rows，Phase 3.2）、errors.log 尾部与非敏感配置指纹组装成一份 Markdown
    // 报告。桌面设置页「导出诊断报告…」与 CLI「codeusage doctor」共用同一构建器。
    //
    // 脱敏红线（验收：报告全文检索无任何 key/路径明文）：
    //   · 密钥绝不读取、绝不输出——报告只使用存在性布尔（SourceHealth.HasCredential）；
    //   · 本地路径中的用户主目录打码为 ~（MaskPath；Scrub 再兜底一遍）；
    //   · endpoint / 代理 / WebDAV URL 只保留 host（HostOf）；
    //   · 组装完成后全文 Scrub：key 形态（sk-…、ghp_…、JWT、Bearer 头、api_key= 查询参数、
    //     长随机 token）与意外混入的主目录路径一律替换为 [已脱敏]。
    public static class Diagnostics {
        public const int ErrorLogTailLines = 60;   // errors.log 尾部行数
        // 报告文案不走 I18nTable：报告里大量 2 字短语（平台/状态/开/关…）一旦进表，
        // 会经 I18n 的片段替换机制污染英文模式下其它内置字符串。这里自带一张英文表，
        // 只在报告内生效；设置页按钮等真正的 UI 词条仍在 I18nTable。
        private static readonly Dictionary<string, string> En = new Dictionary<string, string> {
            { "codeusagemonit 诊断报告", "codeusagemonit Diagnostic Report" },
            { "生成时间", "Generated at" },
            { "版本", "Version" },
            { "系统", "OS" },
            { "数据目录", "Data folder" },
            { "路径", "Path" },
            { "可写探测", "Write probe" },
            { "通过", "passed" },
            { "平台健康", "Platform health" },
            { "平台", "Platform" },
            { "状态", "State" },
            { "上次成功", "Last success" },
            { "上次尝试", "Last attempt" },
            { "连续失败", "Consecutive failures" },
            { "最近错误", "Last error" },
            { "最近错误日志（errors.log 尾部 {0} 行）", "Recent errors (last {0} lines of errors.log)" },
            { "本机用量扫描", "Local usage scan" },
            { "本进程没有记录到扫描错误。", "No scan errors recorded in this process." },
            { "统计/历史", "Statistics/history" },
            { "（没有 errors.log）", "(no errors.log)" },
            { "（读取失败：{0}）", "(failed to read: {0})" },
            { "正常", "OK" },
            { "瞬时失败", "Transient failure" },
            { "退避中", "Backing off" },
            { "凭据失效", "Credentials invalid" },
            { "未配置", "Not configured" },
            { "配置指纹", "Config fingerprint" },
            { "启用平台", "Enabled platforms" },
            { "刷新", "Refresh" },
            { "全局 {0} 分钟", "global {0} min" },
            { "使用中 {0} 分钟", "in-use {0} min" },
            { "单独设置", "per-platform" },
            { "第三方统计", "Third-party stats" },
            { "价目同步", "Price sync" },
            { "更新检查", "Update check" },
            { "开", "on" },
            { "关", "off" },
            { "已配置（{0}）", "configured ({0})" },
            { "自动同步", "auto sync" },
            { "手动", "manual" },
            { "代理", "Proxy" },
            { "直连", "direct" },
            { "自动", "auto" },
            { "自定义（{0}）", "custom ({0})" },
            { "备份", "Backups" },
            { "每 {0} 小时", "every {0} h" },
            { "关闭", "off" },
            { "保留 {0} 份", "{0} kept" },
            { "界面语言", "Language" },
            { "跟随系统", "follow system" },
            { "国际", "international" },
            { "国内", "china" },
            { "自定义平台", "Custom platforms" },
            { "无", "none" },
            { "本报告只含版本、健康状态、错误摘要与非敏感配置；不含任何 API 密钥、令牌、密码、账户或对话内容。路径中的用户主目录已替换为 ~。", "This report contains only version, health, error summaries and non-sensitive configuration; no API keys, tokens, passwords, accounts or conversations. Home directories in paths are replaced with ~." },
        };
        private static string T(string zh) {
            if (!I18n.English) return zh;
            string en; return En.TryGetValue(zh, out en) ? en : zh;
        }
        private static string T(string zh, object arg) {
            try { return String.Format(CultureInfo.InvariantCulture, T(zh), arg); } catch (FormatException) { return String.Format(CultureInfo.InvariantCulture, zh, arg); }
        }

        // channelOf：桌面传 limits.ChannelOf（已有通道才看得到），CLI 传 null（只读缓存）。
        public static string BuildReport(AppConfig config, IEnumerable<ProviderState> states, Func<string, LimitsRuntime.Channel> channelOf, DateTime nowUtc) {
            var b = new StringBuilder();
            b.AppendLine("# " + T("codeusagemonit 诊断报告"));
            b.AppendLine();
            b.AppendLine("- " + T("生成时间") + ": " + Stamp(nowUtc) + (I18n.English ? "" : "（" + nowUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " 本地）"));
            b.AppendLine("- " + T("版本") + ": codeusagemonit " + AppInfo.Version);
            b.AppendLine("- .NET: " + Environment.Version + " · " + RuntimeInformation.ProcessArchitecture);
            b.AppendLine("- " + T("系统") + ": " + RuntimeInformation.OSDescription.Trim());
            b.AppendLine();
            b.AppendLine("## " + T("数据目录"));
            b.AppendLine();
            b.AppendLine("- " + T("路径") + ": " + MaskPath(Store.Data));
            string probe = Store.ProbeWritable();
            b.AppendLine("- " + T("可写探测") + ": " + (probe == null ? T("通过") : MaskPath(probe)));
            b.AppendLine();
            AppendHealth(b, config, states, channelOf, nowUtc);
            AppendErrorLog(b);
            AppendFingerprint(b, config);
            b.AppendLine();
            b.AppendLine("---");
            b.AppendLine();
            b.AppendLine(T("本报告只含版本、健康状态、错误摘要与非敏感配置；不含任何 API 密钥、令牌、密码、账户或对话内容。路径中的用户主目录已替换为 ~。"));
            return Scrub(b.ToString());
        }

        private static void AppendHealth(StringBuilder b, AppConfig config, IEnumerable<ProviderState> states, Func<string, LimitsRuntime.Channel> channelOf, DateTime nowUtc) {
            b.AppendLine("## " + T("平台健康"));
            b.AppendLine();
            b.AppendLine("| " + T("平台") + " | " + T("状态") + " | " + T("上次成功") + " (UTC) | " + T("上次尝试") + " (UTC) | " + T("连续失败") + " | " + T("最近错误") + " |");
            b.AppendLine("|---|---|---|---|---|---|");
            foreach (HealthRow row in SourceHealth.Rows(config, states, channelOf)) {
                b.AppendLine("| " + Cell(row.Name) + " | " + Cell(StateText(row.State)) + " | " + Cell(When(row.LastSuccessUtc)) + " | " + Cell(When(row.LastAttemptUtc)) + " | " +
                    (row.ConsecutiveFailures > 0 ? row.ConsecutiveFailures.ToString(CultureInfo.InvariantCulture) : "—") + " | " + Cell(row.LastError) + " |");
            }
            b.AppendLine();
            b.AppendLine("### " + T("本机用量扫描"));
            b.AppendLine();
            var scanErrors = SourceHealth.AllErrors().Where(p => p.Key != SourceHealth.HistoryKey).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
            if (scanErrors.Count == 0) b.AppendLine("- " + T("本进程没有记录到扫描错误。"));
            else foreach (var pair in scanErrors) b.AppendLine("- " + pair.Key + " — " + OneLine(pair.Value.Message) + "（" + Stamp(pair.Value.AtUtc) + "）");
            SourceHealth.ErrorRecord history = SourceHealth.LastErrorOf(SourceHealth.HistoryKey);
            if (history != null) b.AppendLine("- " + T("统计/历史") + " — " + OneLine(history.Message) + "（" + Stamp(history.AtUtc) + "）");
            b.AppendLine();
        }

        private static void AppendErrorLog(StringBuilder b) {
            b.AppendLine("## " + T("最近错误日志（errors.log 尾部 {0} 行）", ErrorLogTailLines));
            b.AppendLine();
            b.AppendLine("```");
            try {
                string path = Path.Combine(Store.Data, "errors.log");
                // P2-2：尾读最近 N 行，不整读（errors.log 已有 512KB 上限，仍避免整读）。
                string tail = File.Exists(path) ? ErrorLog.ReadTail(path, ErrorLogTailLines) : "";
                if (tail.Length == 0) b.AppendLine(T("（没有 errors.log）"));
                else { b.Append(tail); if (!tail.EndsWith("\n", StringComparison.Ordinal)) b.AppendLine(); }
            } catch (Exception e) { b.AppendLine(T("（读取失败：{0}）", e.Message)); }
            b.AppendLine("```");
            b.AppendLine();
        }

        private static void AppendFingerprint(StringBuilder b, AppConfig config) {
            b.AppendLine("## " + T("配置指纹"));
            b.AppendLine();
            b.AppendLine("- " + T("启用平台") + ": " + String.Join(", ", (config.Enabled ?? new string[0]).Select(ProviderCatalog.Name)));
            string mode = config.RefreshMode == "fixed" ? T("固定") : T("自适应");
            b.AppendLine("- " + T("刷新") + ": " + mode + " · " + T("全局 {0} 分钟", config.RefreshMinutes) +
                (config.FastInUse ? " · " + T("使用中 {0} 分钟", Math.Max(1, config.InUseMinutes)) : "") +
                (config.ProviderMinutes != null && config.ProviderMinutes.Count > 0 ? " · " + T("单独设置") + ": " + String.Join(", ", config.ProviderMinutes.Where(p => p.Value > 0).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)) : ""));
            b.AppendLine("- " + T("第三方统计") + ": " + OnOff(config.ShowThirdParty) + " · " + T("价目同步") + ": " + OnOff(config.PriceSync) + " · " + T("更新检查") + ": " + OnOff(config.UpdateCheck));
            b.AppendLine("- " + T("WebDAV") + ": " + (String.IsNullOrWhiteSpace(config.WebDavUrl) ? T("未配置") : T("已配置（{0}）", HostOf(config.WebDavUrl))) + " · " + T("自动同步") + ": " + (config.SyncMinutes > 0 ? T("{0} 分钟", config.SyncMinutes) : T("手动")));
            b.AppendLine("- " + T("代理") + ": " + (config.Proxy == "direct" ? T("直连") : config.Proxy == "auto" || String.IsNullOrWhiteSpace(config.Proxy) ? T("自动") : T("自定义（{0}）", HostOf(config.Proxy))));
            b.AppendLine("- " + T("备份") + ": " + (config.BackupHours > 0 ? T("每 {0} 小时", config.BackupHours) : T("关闭")) + " · " + T("保留 {0} 份", Math.Max(1, config.BackupKeep)) + " · " + MaskPath(Backups.Folder(config)));
            b.AppendLine("- " + T("界面语言") + ": " + (config.Language == "zh" ? "中文" : config.Language == "en" ? "English" : T("跟随系统")) + " · Kimi: " + (config.KimiRegion == "global" || config.KimiRegion == "international" ? T("国际") : T("国内")) + " · ZCode: " + (config.ZaiRegion == "global" ? T("国际") : T("国内")));
            var customs = ProviderCatalog.Custom.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
            b.AppendLine("- " + T("自定义平台") + ": " + (customs.Count == 0 ? T("无") : String.Join(", ", customs.Select(p => p.Name + "（" + HostOf(p.Url) + "）"))));
        }

        // ── 脱敏 ─────────────────────────────────────────────────────────
        // 用户主目录（含结尾分隔符形态）→ ~。只替换完整的家目录前缀，不动同名前缀目录。
        public static string MaskPath(string path) {
            if (String.IsNullOrEmpty(path)) return path;
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (String.IsNullOrEmpty(home) || home.Length < 2) return path;
            home = home.TrimEnd('/', '\\');
            string flat = path.Replace(home + "/", "~/").Replace(home + "\\", "~\\");
            return flat == home ? "~" : flat;
        }
        // URL 只保留 host（userinfo、scheme、路径、查询、端口一并丢弃）。
        public static string HostOf(string url) {
            if (String.IsNullOrWhiteSpace(url)) return "";
            string trimmed = url.Trim();
            if (!trimmed.Contains("://")) trimmed = "https://" + trimmed;
            Uri uri;
            return Uri.TryCreate(trimmed, UriKind.Absolute, out uri) && !String.IsNullOrEmpty(uri.Host) ? uri.Host : "";
        }
        // 常见密钥形态。宁可多脱敏（诊断文本里出现长随机串本就可疑），不可漏。
        // 例外：纯十六进制的 40+ 位串（SHA-1/SHA-256、commit hash）是不可重放的单向
        // 摘要，保留可读性（review P2-4：不误伤报告里正常的十六进制字段）。
        private static readonly Regex[] SecretPatterns = {
            new Regex(@"(?i)\bsk-[A-Za-z0-9_-]{8,}"),
            new Regex(@"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{16,}\b"),
            new Regex(@"\bgithub_pat_[A-Za-z0-9_]{20,}\b"),
            new Regex(@"\bAIza[A-Za-z0-9_-]{30,}"),                                             // Google API key
            new Regex(@"(?i)\b(bearer)\s+\S+"),                                                 // 保留 Bearer 字样，只脱值
            new Regex(@"(?i)\b[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b"),     // JWT 三段式
            new Regex(@"(?i)(?<=\b(?:api[_-]?key|apikey|access[_-]?token|refresh[_-]?token|token|authorization|password|secret)\b\s*[:=]\s*)\S+"),
            new Regex(@"(?<![A-Za-z0-9])[0-9a-f]{32}(?![A-Za-z0-9])", RegexOptions.IgnoreCase), // 32 位十六进制（MD5 等）
        };
        // 长随机 token/base64（40+ 位）；纯十六进制豁免（见上）。
        private static readonly Regex LongTokenPattern = new Regex(@"(?<![A-Za-z0-9])[A-Za-z0-9+/_-]{40,}(?![A-Za-z0-9])");
        // URL 查询参数里的密钥：保留参数名，只脱值。
        private static readonly Regex QuerySecret = new Regex(@"(?i)([?&](?:key|token|api[_-]?key|apikey|access[_-]?token|signature)=)([^&\s]{4,})");
        private const string Redacted = "[已脱敏]";
        private static bool IsPureHex(string text) {
            foreach (char c in text) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f') && !(c >= 'A' && c <= 'F')) return false;
            return true;
        }
        public static string Scrub(string text) {
            if (String.IsNullOrEmpty(text)) return text;
            for (int i = 0; i < SecretPatterns.Length; i++)
                text = SecretPatterns[i].Replace(text, i == 4 ? "$1 " + Redacted : Redacted);
            text = LongTokenPattern.Replace(text, m => IsPureHex(m.Value) ? m.Value : Redacted);
            text = QuerySecret.Replace(text, "$1" + Redacted);
            return MaskPath(text);
        }

        // ── 小工具 ───────────────────────────────────────────────────────
        private static string StateText(string state) {
            switch (state) {
                case SourceHealth.Ok: return T("正常");
                case SourceHealth.Transient: return T("瞬时失败");
                case SourceHealth.Backoff: return T("退避中");
                case SourceHealth.Auth: return T("凭据失效");
                default: return T("未配置");
            }
        }
        private static string OnOff(bool value) { return value ? T("开") : T("关"); }
        private static string Stamp(DateTime utc) { return utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC"; }
        private static string When(string iso) {
            DateTimeOffset at;
            return String.IsNullOrEmpty(iso) || !DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out at) || at.Year < 2000 ? "—" : Stamp(at.UtcDateTime);
        }
        private static string Cell(string text) {
            if (String.IsNullOrEmpty(text)) return "—";
            return text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }
        private static string OneLine(string text) { return String.IsNullOrEmpty(text) ? "—" : text.Replace("\r", " ").Replace("\n", " "); }
    }
}
