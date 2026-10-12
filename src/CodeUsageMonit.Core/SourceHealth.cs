using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodeUsageMonit {
    // ROADMAP Phase 3.2（数据源健康状态）：把三处已有的信号——额度刷新通道（LimitsRuntime，
    // Phase 2）、平台读数（ProviderState.Status/Stale/Message）、本机用量扫描（UsageScanner，
    // 此前空 catch 静默吞错）——聚合为「每平台一行」的纯数据健康摘要，供卡片提示与诊断
    // 报告共用。只读聚合：不改变 LimitsRuntime 的状态机，也不改变扫描器的行为语义。
    //
    // 五态（ROADMAP Phase 3.2）：
    //   ok            正常——最近一次读取成功（或尚未尝试但凭据/数据齐备）；
    //   transient     瞬时失败——429/5xx/网络/解析等失败一次，旧读数保留并标 stale，
    //                 一分钟内即自动重试；
    //   backoff       退避中——连续瞬时失败 ≥ 2，按 1→2→4→8 分钟指数退避；
    //   auth          凭据失效——expired/setup，不再退避，仅 30 分钟低频探测；
    //   unconfigured  未配置——平台未启用，或启用但没有任何可用凭据。
    //
    // 错误登记（内存态，进程内可见）：UsageScanner.ScanAll 原来的空 catch、AppMonitor 的
    // 几处 fetch 异常 catch、ScanHistory 的整体 catch，现在都 RecordError 记下最近一次
    // 错误与时间；同时经 ErrorSink 交给宿主落 errors.log（Desktop 启动时把
    // MonitorPanel.LogError 挂进来；Core 不反向依赖 Desktop，CLI 进程无人订阅即只记录）。
    public sealed class HealthRow {
        public string Id, Name;
        public string State = SourceHealth.Unconfigured;  // ok | transient | backoff | auth | unconfigured
        public string LastSuccessUtc = "";                // ISO；空 = 从未成功
        public string LastAttemptUtc = "";                // ISO；空 = 从未尝试
        public int ConsecutiveFailures;
        public string LastError = "";                     // 最近一次错误（导出前由 Diagnostics 统一脱敏）
    }

    public static class SourceHealth {
        public const string Ok = "ok", Transient = "transient", Backoff = "backoff", Auth = "auth", Unconfigured = "unconfigured";

        // ── 错误登记（内存态）──
        public sealed class ErrorRecord { public DateTime AtUtc; public string Message; }
        private static readonly object gate = new object();
        private static readonly Dictionary<string, ErrorRecord> errors = new Dictionary<string, ErrorRecord>();
        // ScanHistory 的整体异常（历史派生等，不属于单个平台）记在这个键下。
        public const string HistoryKey = "*history*";
        // 宿主挂进来的落盘通道（Desktop: MonitorPanel.LogError）。自身失败不得成为新错误。
        public static Action<string, Exception> ErrorSink;

        public static void RecordError(string key, Exception e) {
            if (String.IsNullOrEmpty(key) || e == null) return;
            lock (gate) errors[key] = new ErrorRecord { AtUtc = DateTime.UtcNow, Message = e.GetType().Name + " " + e.Message };
            Action<string, Exception> sink = ErrorSink;
            if (sink != null) { try { sink(key, e); } catch { } }
        }
        public static ErrorRecord LastErrorOf(string key) { lock (gate) { ErrorRecord e; return errors.TryGetValue(key ?? "", out e) ? e : null; } }
        public static List<KeyValuePair<string, ErrorRecord>> AllErrors() { lock (gate) return errors.ToList(); }
        public static void ClearErrors() { lock (gate) errors.Clear(); }

        // ── 凭据存在性：只判断「有没有」，绝不读取密钥值 ──
        // 与 CLI 的 providers 说明（CliProgram.Credential）同一批本机位置；这里只做布尔
        // 判断，供五态里的 unconfigured 使用。
        public static bool HasCredential(string id, AppConfig config) {
            return HasCredential(id, config, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        // home 可注入的重载：供自测用假 home 构造候选文件（默认行为不变）。
        internal static bool HasCredential(string id, AppConfig config, string home) {
            if (ProviderCatalog.LocalOnly(id)) return true;   // pi：只统计本机日志，无需凭据
            // Phase 5：Key 型平台按活动账户判断（缺省 = 默认账户，行为不变）。
            string active = Accounts.ActiveId(config, id);
            switch (id) {
                case "codex": return File.Exists(Path.Combine(ProviderService.CodexHome(), "auth.json"));
                case "claude": return ClaudeLogs.ConfigDirs().Any(d => File.Exists(Path.Combine(d, ".credentials.json")))
                    || Keychain.Exists(ClaudeCredentials.KeychainService, Environment.UserName);   // 新版 Claude Code 把凭据存进钥匙串
                case "cursor": return File.Exists(PlatformPaths.CursorStateDb);
                case "antigravity": return true;   // 桌面应用本机凭据；存在性无法廉价探测，按可用处理
                case "grok": return File.Exists(Path.Combine(home, ".grok", "auth.json"));
                case "copilot": return Store.HasSavedKey("copilot") || ProviderService.CopilotTokenPaths.Any(File.Exists);
                default:
                    if (id != null && id.StartsWith("custom-", StringComparison.Ordinal)) {
                        CustomProvider custom;
                        return ProviderCatalog.Custom.TryGetValue(id, out custom) && custom.Auth == "none" ? true : Store.HasSavedKey(id, active);
                    }
                    // Key 型平台：环境变量非空，或活动账户已保存（只看引用文件是否存在，
                    // 不碰钥匙串值）。
                    if (ProviderCatalog.KeyEnv(id, config).Any(n => !String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(n))) || Store.HasSavedKey(id, active)) return true;
                    // CLI 静默导入凭据（review P2-3）：kimi/zcode 本地 CLI 已登录时 Fetch 会
                    // 自动导入，同样算已配置。只判断文件存在，绝不读取内容。路径常量与
                    // LocalCliCredentials 同源。
                    if (id == "kimi") return File.Exists(LocalCliCredentials.KimiConfigPath) || File.Exists(LocalCliCredentials.KimiCredentialsPath);
                    if (id == "zcode") return File.Exists(LocalCliCredentials.ZCodeCredentialsPath);
                    // MiMo 桌面客户端已登录即算已配置（自动续期链路读的就是这份 Cookies
                    // 库）。同 kimi/zcode 口径：只看候选文件存在、绝不读取内容；其他平台
                    // 没有客户端，StoreCandidates 返回空即 false。
                    if (id == "mimo") return MimoDesktop.StoreCandidates(home).Any(path => File.Exists(path));
                    return false;
            }
        }

        // ── 五态判定。channel 可为 null（CLI 只读缓存、桌面首查前）；state 亦可为 null。──
        public static string StateOf(string id, AppConfig config, ProviderState state, LimitsRuntime.Channel channel) {
            bool enabled = config != null && config.Enabled != null && config.Enabled.Contains(id);
            if (!enabled) return Unconfigured;
            string status = state == null || String.IsNullOrEmpty(state.Status) ? "loading" : state.Status;
            if (channel != null && channel.LastAttemptUtc != DateTime.MinValue) {
                if (channel.Kind == "auth" || status == "expired") return Auth;
                if (channel.Kind == "backoff") return channel.TransientFailures <= 1 ? Transient : Backoff;
                if (status == "setup") return Unconfigured;
                if (status == "error") return Transient;
            }
            // 无通道记录（从未尝试）或通道正常：按缓存读数与凭据收尾。
            if (status == "expired") return Auth;
            if (status == "setup") return Unconfigured;
            if (status == "error") return HasCredential(id, config) ? Transient : Unconfigured;
            if (status == "ready" || status == "cached") return Ok;
            return HasCredential(id, config) ? Ok : Unconfigured;   // loading：尚未尝试
        }

        // ── 每平台一行健康摘要。channelOf 由宿主提供（桌面：limits.ChannelOf；CLI：null），──
        // ── 保持本函数纯读取：不替调用方创建通道。────────────────────────────────────
        public static List<HealthRow> Rows(AppConfig config, IEnumerable<ProviderState> states, Func<string, LimitsRuntime.Channel> channelOf) {
            var byId = new Dictionary<string, ProviderState>();
            foreach (ProviderState s in states ?? Enumerable.Empty<ProviderState>())
                if (s != null && s.Id != null) byId[s.Id] = s;   // 同 id 后到者优先（与桌面 states 字典一致）
            var rows = new List<HealthRow>();
            foreach (string id in ProviderCatalog.All) {
                ProviderState state;
                byId.TryGetValue(id, out state);
                LimitsRuntime.Channel channel = channelOf != null ? channelOf(id) : null;
                var row = new HealthRow {
                    Id = id,
                    Name = ProviderCatalog.Name(id),
                    State = StateOf(id, config, state, channel)
                };
                if (channel != null && channel.LastAttemptUtc != DateTime.MinValue) {
                    row.LastAttemptUtc = channel.LastAttemptUtc.ToString("o");
                    row.LastSuccessUtc = channel.LastGoodUtc != DateTime.MinValue ? channel.LastGoodUtc.ToString("o") : (state != null ? state.LastSuccess ?? "" : "");
                    row.ConsecutiveFailures = channel.ConsecutiveFailures;
                } else if (state != null) {
                    row.LastSuccessUtc = state.LastSuccess ?? "";
                    row.LastAttemptUtc = state.LastAttempt ?? "";
                }
                ErrorRecord recorded = LastErrorOf(id);
                if (recorded != null) {
                    row.LastError = Trim(recorded.Message);
                } else if (state != null && (state.Status == "error" || state.Status == "expired" || state.Status == "setup")) {
                    row.LastError = Trim(state.Message);
                }
                rows.Add(row);
            }
            return rows;
        }
        private static string Trim(string text) {
            if (String.IsNullOrEmpty(text)) return "";
            string flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length > 160 ? flat.Substring(0, 160) + "…" : flat;
        }
    }
}
