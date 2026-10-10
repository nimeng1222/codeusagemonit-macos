using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace CodeUsageMonit {
    // ── Usage from other devices ──────────────────────────────────────
    // Each install has an id (data\device.json). Its hourly usage can be exported as SQL, or
    // uploaded to a WebDAV folder, and imported on another device. Imported usage is kept
    // per device (data\devices\<id>.json) and added to the local indexes in memory only, so
    // a rescan never overwrites it and importing the same file twice changes nothing.
    public sealed class DeviceIdentity { public string Id = "", Name = ""; }
    public sealed class DeviceData {
        public string Id = "", Name = "", Exported = "", Imported = "", Version = "";
        // agent → hour bucket key (as in LogFile.Hours) → usage of that hour.
        public Dictionary<string, Dictionary<string, Bucket>> Agents = new Dictionary<string, Dictionary<string, Bucket>>();
        public double Tokens() { return Agents.Values.SelectMany(a => a.Values).Sum(b => b.Tokens()); }
        public double Cost() { return Agents.Values.SelectMany(a => a.Values).Sum(b => b.D); }
    }
    public sealed class ImportResult {
        // Added: hours not seen before. Updated: a newer export of the same hours. Same: already
        // imported, unchanged (or an older export). Duplicate: identical to this device's own
        // records for that hour and model, i.e. the same logs copied or synced here.
        public int Added, Updated, Same, Duplicate, Own, Skipped;
        public int Rows { get { return Added + Updated; } }
        public List<string> Devices = new List<string>();
        public string Summary() {
            if (Added + Updated + Same + Duplicate == 0) return Own > 0 ? I18n.T("文件里只有本机的数据，无需导入。") : I18n.T("没有可导入的用量记录。") + (Skipped > 0 ? I18n.T("；{0} 条格式不对或已超过保留期，未导入", Skipped) : "");
            string text = Rows == 0 ? I18n.T("{0}：没有新的用量，之前已经导入过或与本机记录相同", String.Join("、", Devices))
                : I18n.T("{0}：新增 {1} 条、更新 {2} 条小时记录", String.Join("、", Devices), Added, Updated);
            if (Same > 0 && Rows > 0) text += I18n.T("；{0} 条之前已导入，未重复计算", Same);
            if (Duplicate > 0) text += I18n.T("；{0} 条与本机自己的记录完全一致（多半是复制或同步过来的同一份日志），已自动去重", Duplicate);
            if (Own > 0) text += I18n.T("；跳过本机的 {0} 条", Own);
            if (Skipped > 0) text += I18n.T("；{0} 条格式不对或已超过保留期，未导入", Skipped);
            return text + I18n.T("。");
        }
    }

    public static class Devices {
        public const string Folder = "devices";
        private const string FilePrefix = "device:";
        // Cursor's usage is read from the account, so every device already sees all of it.
        public static readonly string[] AccountWide = { "cursor" };
        private static readonly object gate = new object();
        private static readonly Regex SafeId = new Regex("^[A-Za-z0-9_-]{1,64}$");

        public static DeviceIdentity Self() {
            lock (gate) {
                var d = Store.Read<DeviceIdentity>("device.json");
                if (String.IsNullOrEmpty(d.Id) || !SafeId.IsMatch(d.Id)) {
                    d = new DeviceIdentity { Id = Guid.NewGuid().ToString("N").Substring(0, 12), Name = Environment.MachineName };
                    try { Store.Write("device.json", d); } catch { }
                }
                if (String.IsNullOrWhiteSpace(d.Name)) d.Name = Environment.MachineName;
                return d;
            }
        }
        public static void Rename(string name) {
            lock (gate) { var d = Self(); d.Name = String.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim(); if (d.Name.Length > 40) d.Name = d.Name.Substring(0, 40); Store.Write("device.json", d); }
        }
        public static List<DeviceData> Imported() {
            var list = new List<DeviceData>();
            string dir = Path.Combine(Store.Data, Folder);
            if (!Directory.Exists(dir)) return list;
            foreach (string file in Directory.GetFiles(dir, "*.json")) {
                DeviceData d = Store.Read<DeviceData>(Path.Combine(Folder, Path.GetFileName(file)));
                if (SafeId.IsMatch(d.Id ?? "")) list.Add(d);
            }
            return list.OrderBy(d => d.Name, StringComparer.CurrentCulture).ToList();
        }
        public static void Remove(string id) {
            if (!SafeId.IsMatch(id ?? "")) return;
            string path = Path.Combine(Store.Data, Folder, id + ".json");
            if (File.Exists(path)) File.Delete(path);
        }
        private static void Save(DeviceData d) { Directory.CreateDirectory(Path.Combine(Store.Data, Folder)); Store.Write(Path.Combine(Folder, d.Id + ".json"), d); }

        // The local indexes plus every imported device's hours, as new index objects (the
        // local ones are what the scanners persist and must stay this device's own).
        public static Dictionary<string, LogIndex> Attach(Dictionary<string, LogIndex> local, IEnumerable<DeviceData> devices) {
            var result = new Dictionary<string, LogIndex>(local);
            List<DeviceData> list = devices.ToList();
            foreach (string agent in list.SelectMany(d => d.Agents.Keys).Distinct().ToList()) {
                if (AccountWide.Contains(agent)) continue;
                LogIndex mine; local.TryGetValue(agent, out mine);
                var merged = new LogIndex();
                if (mine != null) { merged.CoveredFrom = mine.CoveredFrom; merged.Updated = mine.Updated; merged.Pricing = mine.Pricing; merged.Seen = mine.Seen; merged.Files.AddRange(mine.Files); }
                Dictionary<string, Bucket> own = OwnGroupTotals(mine);
                string earliest = null;
                foreach (DeviceData d in list) {
                    Dictionary<string, Bucket> hours;
                    if (!d.Agents.TryGetValue(agent, out hours) || hours.Count == 0) continue;
                    // A bucket whose totals equal this device's own record is the same usage
                    // (logs copied or synced here): left out so it is not counted twice —
                    // also for copies stored by an older version before the import side
                    // started removing them (review #5). Compared GROUP BY GROUP
                    // (Phase 4.5b): the remote view may split a group across endpoints where
                    // this device keeps one, so per-bucket comparison kept every piece and
                    // counted the whole group twice. Both sides are aggregated per
                    // hour|provider|model first; when a group matches, every remote bucket
                    // of that group is left out.
                    if (own.Count > 0) {
                        var groups = new Dictionary<string, Bucket>(StringComparer.Ordinal);
                        foreach (var pair in hours) {
                            string group = GroupKeyOf(pair.Key);
                            Bucket sum; if (!groups.TryGetValue(group, out sum)) groups[group] = sum = new Bucket();
                            sum.Add(pair.Value, 1);
                        }
                        var dropped = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var pair in groups) {
                            Bucket total;
                            if (own.TryGetValue(pair.Key, out total) && SameUsage(pair.Value, total)) dropped.Add(pair.Key);
                        }
                        if (dropped.Count > 0) {
                            var fresh = new Dictionary<string, Bucket>(StringComparer.Ordinal);
                            foreach (var pair in hours) if (!dropped.Contains(GroupKeyOf(pair.Key))) fresh[pair.Key] = pair.Value;
                            if (fresh.Count == 0) continue;
                            hours = fresh;
                        }
                    }
                    merged.Files.Add(new LogFile { Name = FilePrefix + d.Id, Hours = hours });
                    string first = hours.Keys.OrderBy(k => k, StringComparer.Ordinal).First(); if (earliest == null || String.CompareOrdinal(first, earliest) < 0) earliest = first;
                }
                if (merged.Files.Count == (mine == null ? 0 : mine.Files.Count)) continue;
                if (mine == null && earliest != null) {
                    DateTime at; if (DateTime.TryParseExact(earliest.Substring(0, 10), "yyyyMMddHH", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at)) merged.CoveredFrom = at.ToString("o");
                }
                result[agent] = merged;
            }
            return result;
        }
        public static bool IsImported(LogFile file) { return file.Name.StartsWith(FilePrefix, StringComparison.Ordinal); }

        // ── SQL export ────────────────────────────────────────────────
        private static readonly string[] Columns = { "device_id", "agent", "hour_utc", "provider", "model", "endpoint", "input_tokens", "cache_read_tokens", "cache_write_tokens", "output_tokens", "other_tokens", "requests", "cost_usd", "unpriced_tokens", "timed_requests", "timed_output_tokens", "timed_seconds" };
        // This device's usage (from the saved local indexes) and, with withImported, the
        // devices imported here. SQLite-compatible: sqlite3 usage.db < export.sql.
        public static string ExportSql(DateTime nowUtc, bool withImported) {
            DeviceIdentity self = Self();
            var sql = new StringBuilder();
            sql.Append("-- codeusagemonit usage export\n");
            sql.Append("-- device: ").Append(OneLine(self.Name)).Append(" (").Append(self.Id).Append("), exported ").Append(nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append(", codeusagemonit ").Append(AppInfo.Version).Append('\n');
            sql.Append("-- One row per device, agent, UTC hour, provider, model and endpoint. Token counts, requests,\n");
            sql.Append("-- API-equivalent cost and timing only: no conversation text, keys or account details.\n");
            sql.Append("-- Import it in codeusagemonit (Settings > Data), or load it into SQLite: sqlite3 usage.db < this.sql\n");
            sql.Append("BEGIN TRANSACTION;\n");
            sql.Append("CREATE TABLE IF NOT EXISTS cum_devices (device_id TEXT PRIMARY KEY, name TEXT, exported_utc TEXT, app_version TEXT);\n");
            sql.Append("CREATE TABLE IF NOT EXISTS cum_usage_hours (device_id TEXT NOT NULL, agent TEXT NOT NULL, hour_utc TEXT NOT NULL, provider TEXT NOT NULL, model TEXT NOT NULL, endpoint TEXT, input_tokens REAL, cache_read_tokens REAL, cache_write_tokens REAL, output_tokens REAL, other_tokens REAL, requests REAL, cost_usd REAL, unpriced_tokens REAL, timed_requests REAL, timed_output_tokens REAL, timed_seconds REAL, PRIMARY KEY (device_id, agent, hour_utc, provider, model, endpoint));\n");
            var devices = new List<DeviceData> { Own(self, nowUtc) };
            if (withImported) devices.AddRange(Imported().Where(d => d.Id != self.Id));
            foreach (DeviceData d in devices)
                sql.Append("INSERT OR REPLACE INTO cum_devices (device_id, name, exported_utc, app_version) VALUES (").Append(Lit(d.Id)).Append(", ").Append(Lit(d.Name)).Append(", ").Append(Lit(d.Exported)).Append(", ").Append(Lit(d.Version)).Append(");\n");
            string head = "INSERT OR REPLACE INTO cum_usage_hours (" + String.Join(", ", Columns) + ") VALUES (";
            foreach (DeviceData d in devices)
                foreach (var agent in d.Agents.OrderBy(a => a.Key, StringComparer.Ordinal))
                    foreach (var hour in agent.Value.OrderBy(h => h.Key, StringComparer.Ordinal)) {
                        string[] parts = hour.Key.Split('|'); DateTime at;
                        if (!DateTime.TryParseExact(parts[0], "yyyyMMddHH", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at)) continue;
                        Bucket b = hour.Value;
                        sql.Append(head).Append(Lit(d.Id)).Append(", ").Append(Lit(agent.Key)).Append(", ").Append(Lit(at.ToString("yyyy-MM-ddTHH:00:00Z", CultureInfo.InvariantCulture))).Append(", ")
                           .Append(Lit(parts.Length > 1 ? parts[1] : "")).Append(", ").Append(Lit(parts.Length > 2 ? parts[2] : "")).Append(", ").Append(parts.Length > 3 ? Lit(parts[3]) : "NULL");
                        foreach (double v in new[] { b.I, b.C, b.W, b.O, b.X, b.R, b.D, b.U, b.TN, b.TO, b.TS }) sql.Append(", ").Append(Num(v));
                        sql.Append(");\n");
                    }
            sql.Append("COMMIT;\n");
            return sql.ToString();
        }

        // ── CSV / JSON export（Phase 4.1）─────────────────────────────
        // Same numbers as the SQL export — one row per device, agent, UTC hour, provider,
        // model and endpoint — but this device's hours come from the ARCHIVE-MERGED index,
        // so the export reaches the full HorizonDays window instead of the hot 33 days. A
        // second section carries the daily history (per agent and local day) derived from
        // the same merged view, matching what the app charts. Numbers only: no conversation
        // text, keys or account details.
        public sealed class ExportBundle {
            public DeviceIdentity Self = new DeviceIdentity();
            public DateTime ExportedUtc;
            public List<DeviceData> Devices = new List<DeviceData>();   // self first, then the imported ones
            public List<DayUsage> Days = new List<DayUsage>();          // this machine's view (own + imported), local days
        }
        public static ExportBundle ExportData(DateTime nowUtc) {
            var bundle = new ExportBundle { Self = Self(), ExportedUtc = nowUtc };
            List<string> agents = UsageScanner.Agents.Select(a => a.Id).Where(id => !AccountWide.Contains(id)).ToList();
            var local = UsageScanner.Load(agents);
            bundle.Devices.Add(OwnFrom(bundle.Self, nowUtc, UsageArchive.MergeAll(local, nowUtc)));
            List<DeviceData> imported = Imported().Where(x => x.Id != bundle.Self.Id).ToList();
            bundle.Devices.AddRange(imported);
            var withImports = UsageArchive.MergeAll(Devices.Attach(local, imported), nowUtc);
            bundle.Days = HistoryService.FromIndexes(withImports, TimeZoneInfo.Local, DateTime.Today, UsageArchive.HorizonDays).Days;
            return bundle;
        }
        // RFC 4180: a cell containing a comma, quote, CR or LF is quoted; quotes double.
        // Line endings are CRLF; the endpoint cell is empty where the SQL export writes NULL.
        public static string ExportCsv(ExportBundle bundle) {
            var csv = new StringBuilder();
            csv.Append("granularity,device_id,agent,period_utc,provider,model,endpoint,input_tokens,cache_read_tokens,cache_write_tokens,output_tokens,other_tokens,requests,cost_usd,unpriced_tokens,timed_requests,timed_output_tokens,timed_seconds\r\n");
            CultureInfo c = CultureInfo.InvariantCulture;
            Action<string, DeviceData, string, string, string, string, string, Bucket> row = (granularity, d, agent, period, provider, model, endpoint, b) => {
                csv.Append(granularity).Append(',').Append(Cell(d.Id)).Append(',').Append(Cell(agent)).Append(',').Append(Cell(period)).Append(',')
                   .Append(Cell(provider)).Append(',').Append(Cell(model)).Append(',').Append(Cell(endpoint));
                foreach (double v in new[] { b.I, b.C, b.W, b.O, b.X, b.R, b.D, b.U, b.TN, b.TO, b.TS }) csv.Append(',').Append(v.ToString("R", c));
                csv.Append("\r\n");
            };
            foreach (DeviceData d in bundle.Devices)
                foreach (var agent in d.Agents.OrderBy(a => a.Key, StringComparer.Ordinal))
                    foreach (var hour in agent.Value.OrderBy(h => h.Key, StringComparer.Ordinal)) {
                        string[] parts = hour.Key.Split('|'); DateTime at;
                        if (!DateTime.TryParseExact(parts[0], "yyyyMMddHH", c, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at)) continue;
                        row("hour", d, agent.Key, at.ToString("yyyy-MM-ddTHH:00:00Z", c), parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? parts[2] : "", parts.Length > 3 ? parts[3] : null, hour.Value);
                    }
            foreach (DayUsage day in bundle.Days.OrderBy(x => x.Day, StringComparer.Ordinal).ThenBy(x => x.Agent, StringComparer.Ordinal)) {
                double other = Math.Max(0, day.Tokens - day.InputTokens - day.CachedTokens - day.CacheCreationTokens - day.OutputTokens);
                row("day", new DeviceData { Id = bundle.Self.Id }, day.Agent, day.Day + "T00:00:00Z", "", "", null,
                    new Bucket { I = day.InputTokens, C = day.CachedTokens, W = day.CacheCreationTokens, O = day.OutputTokens, X = other, R = day.Requests, D = day.CostKnown ? day.Cost : 0, U = day.UnpricedTokens, TN = day.TimedRequests, TO = day.TimedOutput, TS = day.TimedSeconds });
            }
            return csv.ToString();
        }
        private static string Cell(string s) {
            s = s ?? "";
            // CSV 公式注入中和（Phase 4 初审 P2-1）：以 = + - @ 开头的单元格在 Excel /
            // Numbers 里会被当公式求值，前置 ' 使其按文本打开（通行做法）。数字列不走这里。
            if (s.Length > 0 && (s[0] == '=' || s[0] == '+' || s[0] == '-' || s[0] == '@')) s = "'" + s;
            return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        public static string ExportJson(ExportBundle bundle) {
            CultureInfo c = CultureInfo.InvariantCulture;
            var devices = new List<object>();
            foreach (DeviceData d in bundle.Devices) {
                var agents = new Dictionary<string, object>();
                foreach (var agent in d.Agents.OrderBy(a => a.Key, StringComparer.Ordinal)) {
                    var rows = new List<object>();
                    foreach (var hour in agent.Value.OrderBy(h => h.Key, StringComparer.Ordinal)) {
                        string[] parts = hour.Key.Split('|'); DateTime at;
                        if (!DateTime.TryParseExact(parts[0], "yyyyMMddHH", c, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at)) continue;
                        Bucket b = hour.Value;
                        rows.Add(new Dictionary<string, object> {
                            { "hourUtc", at.ToString("yyyy-MM-ddTHH:00:00Z", c) },
                            { "provider", parts.Length > 1 ? parts[1] : "" }, { "model", parts.Length > 2 ? parts[2] : "" },
                            { "endpoint", parts.Length > 3 ? (object)parts[3] : null },
                            { "inputTokens", b.I }, { "cacheReadTokens", b.C }, { "cacheWriteTokens", b.W }, { "outputTokens", b.O }, { "otherTokens", b.X },
                            { "requests", b.R }, { "costUsd", b.D }, { "unpricedTokens", b.U },
                            { "timedRequests", b.TN }, { "timedOutputTokens", b.TO }, { "timedSeconds", b.TS }
                        });
                    }
                    agents[agent.Key] = rows;
                }
                devices.Add(new Dictionary<string, object> { { "id", d.Id }, { "name", d.Name }, { "exportedUtc", d.Exported }, { "appVersion", d.Version }, { "agents", agents } });
            }
            var days = new List<object>();
            foreach (DayUsage day in bundle.Days.OrderBy(x => x.Day, StringComparer.Ordinal).ThenBy(x => x.Agent, StringComparer.Ordinal)) {
                double other = Math.Max(0, day.Tokens - day.InputTokens - day.CachedTokens - day.CacheCreationTokens - day.OutputTokens);
                days.Add(new Dictionary<string, object> {
                    { "day", day.Day }, { "agent", day.Agent },
                    { "inputTokens", day.InputTokens }, { "cacheReadTokens", day.CachedTokens }, { "cacheWriteTokens", day.CacheCreationTokens },
                    { "outputTokens", day.OutputTokens }, { "otherTokens", other }, { "requests", day.Requests },
                    { "costUsd", day.CostKnown ? (object)day.Cost : null }, { "unpricedTokens", day.UnpricedTokens },
                    { "timedRequests", day.TimedRequests }, { "timedOutputTokens", day.TimedOutput }, { "timedSeconds", day.TimedSeconds }
                });
            }
            var root = new Dictionary<string, object> {
                { "app", "codeusagemonit" }, { "version", AppInfo.Version },
                { "exportedUtc", bundle.ExportedUtc.ToString("o", c) }, { "device", bundle.Self.Id },
                { "devices", devices }, { "days", days },
                { "note", "Token, request, cost and timing counts only: no conversation text, keys or account details. devices[].agents: one row per device, agent, UTC hour, provider, model and endpoint (this device archive-merged, up to 370 days); days: this machine's view per agent and local day; costUsd null where no price was known." }
            };
            return J.Serializer().Serialize(root);
        }
        // This device's hours from the saved indexes (several log files may share an hour).
        // Claude / Codex hours read before endpoint tracking carry no endpoint: the one in
        // effect then is written out, so another device attributes them the same way.
        private static DeviceData Own(DeviceIdentity self, DateTime nowUtc) {
            var own = new Dictionary<string, LogIndex>();
            foreach (UsageScanner.Agent agent in UsageScanner.Agents) {
                if (AccountWide.Contains(agent.Id)) continue;
                own[agent.Id] = Store.Read<LogIndex>(agent.File);
            }
            return OwnFrom(self, nowUtc, own);
        }
        // Same body over indexes the caller prepared: the SQL export passes the plain saved
        // indexes (uploads keep their hot-window scope), the CSV / JSON exports pass the
        // archive-merged view so they reach the full HorizonDays window (Phase 4.1).
        private static DeviceData OwnFrom(DeviceIdentity self, DateTime nowUtc, Dictionary<string, LogIndex> own) {
            var d = new DeviceData { Id = self.Id, Name = self.Name, Exported = nowUtc.ToString("o"), Version = AppInfo.Version };
            EndpointLog log = Store.Read<EndpointLog>("endpoints.json");
            foreach (UsageScanner.Agent agent in UsageScanner.Agents) {
                if (AccountWide.Contains(agent.Id)) continue;
                LogIndex index;
                if (!own.TryGetValue(agent.Id, out index) || index == null || index.Version != LogIndex.CurrentVersion) continue;
                var hours = new Dictionary<string, Bucket>(StringComparer.Ordinal);
                foreach (HourUsage h in index.Entries()) {
                    string endpoint = h.Endpoint;
                    if (endpoint == null && (agent.Id == "claude" || (agent.Id == "codex" && !CodexLogs.IsOfficial(h.Provider)))) {
                        EndpointMark mark = Endpoints.Resolve(log, agent.Id == "claude" ? Endpoints.ClaudeApp : Endpoints.CodexApp, h.Hour);
                        endpoint = mark == null ? "?" : mark.Official ? "" : EndpointAttribution.Id(mark);
                    }
                    string key = LogIndex.BucketKey(h.Hour, h.Provider, h.Model, endpoint);
                    Bucket b; if (!hours.TryGetValue(key, out b)) hours[key] = b = new Bucket();
                    b.I += h.Input; b.C += h.Cached; b.W += h.CacheWrite; b.O += h.Output; b.X += h.Other; b.R += h.Requests; b.D += h.Cost; b.U += h.Unpriced; b.TN += h.TimedRequests; b.TO += h.TimedOutput; b.TS += h.TimedSeconds;
                }
                if (hours.Count > 0) d.Agents[agent.Id] = hours;
            }
            return d;
        }
        private static string Lit(string s) { return s == null ? "NULL" : "'" + s.Replace("'", "''") + "'"; }
        private static string Num(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
        private static string OneLine(string s) { return (s ?? "").Replace('\r', ' ').Replace('\n', ' '); }

        // ── SQL import ────────────────────────────────────────────────
        // Reads the INSERT statements of an export (other statements are ignored) and merges them
        // into what this device already has, deciding per hour without asking:
        //   · rows of this device are skipped (its own logs are the source);
        //   · a device's hour seen before is updated only by a newer export (totals only grow);
        //   · an hour / provider / model whose totals equal this device's own record exactly is the
        //     same usage (logs copied or synced here) and is left out, so it is not counted twice.
        public static ImportResult ImportSql(string text, DateTime nowUtc) {
            DeviceIdentity self = Self();
            var result = new ImportResult();
            var names = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            var usage = new List<Dictionary<string, object>>();
            foreach (var insert in Inserts(text)) {
                if (insert.Key == "cum_devices") { object id; if (insert.Value.TryGetValue("device_id", out id) && id is string) names[(string)id] = insert.Value; }
                else if (insert.Key == "cum_usage_hours") usage.Add(insert.Value);
            }
            var known = new HashSet<string>(UsageScanner.Agents.Select(a => a.Id).Where(id => !AccountWide.Contains(id)));
            DateTime oldest = nowUtc.AddDays(-LogReader.RetainDays);
            // Valid rows, and their totals per device / agent / hour / provider / model.
            var rows = new List<ImportRow>();
            var groups = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            // Numeric cells must be finite and in a sane range: SQL like 1e999 parses to
            // Infinity, which the JSON serializer refuses when a device is saved and used to
            // abort a whole multi-device import (review #12). Such a row is refused here,
            // before anything is stored.
            const double MaxAmount = 1e12;
            // The imported keys of each device, grouped by agent and hour (Phase 4.5c). An
            // export is the device's full view of every hour it holds, so a stored key that
            // the import no longer contains is a stale bucket of a re-partitioned remote
            // view (an old endpoint key used to linger beside the new one forever, doubling
            // that hour — the monotonic rule below is per key and never removes anything).
            // Rows refused for a bad NUMBER still register their key (P2-3): what was
            // rejected is the value, not the key's existence — without this, recycling
            // would delete the very bucket a later good export is expected to refresh.
            var incoming = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);
            foreach (var row in usage) {
                string device = Text(row, "device_id"), agent = Text(row, "agent");
                if (device == self.Id) { result.Own++; continue; }
                DateTime hour;
                if (!SafeId.IsMatch(device ?? "") || agent == null || !known.Contains(agent) || !DateTime.TryParse(Text(row, "hour_utc") ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out hour) || hour < oldest) { result.Skipped++; continue; }
                hour = new DateTime(hour.Year, hour.Month, hour.Day, hour.Hour, 0, 0, DateTimeKind.Utc);
                bool bad = false;
                Func<string, double> num = column => { double v = Number(row, column); if (Double.IsNaN(v) || v < 0 || v > MaxAmount) { bad = true; return 0; } return v; };
                var r = new ImportRow { Device = device, Agent = agent, Group = agent + "|" + LogIndex.BucketKey(hour, Text(row, "provider") ?? "", Text(row, "model") ?? ""), Key = LogIndex.BucketKey(hour, Text(row, "provider") ?? "", Text(row, "model") ?? "", Text(row, "endpoint")),
                    Usage = new Bucket { I = num("input_tokens"), C = num("cache_read_tokens"), W = num("cache_write_tokens"), O = num("output_tokens"), X = num("other_tokens"), R = num("requests"),
                        D = num("cost_usd"), U = num("unpriced_tokens"), TN = num("timed_requests"), TO = num("timed_output_tokens"), TS = num("timed_seconds") } };
                Dictionary<string, HashSet<string>> perAgent;
                if (!incoming.TryGetValue(device, out perAgent)) incoming[device] = perAgent = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                string agentHour = agent + "|" + r.Key.Substring(0, 10);
                HashSet<string> keys; if (!perAgent.TryGetValue(agentHour, out keys)) perAgent[agentHour] = keys = new HashSet<string>(StringComparer.Ordinal);
                keys.Add(r.Key);
                if (bad) { result.Skipped++; continue; }
                rows.Add(r);
                Bucket sum; if (!groups.TryGetValue(device + "|" + r.Group, out sum)) groups[device + "|" + r.Group] = sum = new Bucket(); sum.Add(r.Usage, 1);
            }
            Dictionary<string, Bucket> local = LocalTotals(rows.Select(r => r.Agent).Distinct());
            var touched = new Dictionary<string, DeviceData>(StringComparer.Ordinal);
            var found = new Dictionary<string, DeviceData>(StringComparer.Ordinal);
            Func<string, DeviceData> stored = id => { DeviceData d; if (!found.TryGetValue(id, out d)) found[id] = d = Imported().FirstOrDefault(x => x.Id == id); return d; };
            lock (gate) {
                foreach (ImportRow r in rows) {
                    Bucket mine;
                    if (local.TryGetValue(r.Group, out mine) && SameUsage(groups[r.Device + "|" + r.Group], mine)) {
                        result.Duplicate++;
                        // The same usage now lives in this device's own logs; a remote copy
                        // imported earlier would be counted twice through Attach (review #5).
                        // Drop its buckets and keep the device touched so the removal is saved.
                        DeviceData stale = stored(r.Device);
                        if (stale != null) {
                            Dictionary<string, Bucket> staleHours;
                            if (stale.Agents.TryGetValue(r.Agent, out staleHours)) {
                                string groupKey = r.Group.Substring(r.Agent.Length + 1);
                                foreach (string old in staleHours.Keys.Where(k => InGroup(k, groupKey)).ToList()) staleHours.Remove(old);
                            }
                            touched[r.Device] = stale;
                        }
                        continue;
                    }
                    DeviceData d;
                    if (!touched.TryGetValue(r.Device, out d)) { d = stored(r.Device) ?? new DeviceData { Id = r.Device, Name = r.Device }; touched[r.Device] = d; }
                    Dictionary<string, Bucket> hours; if (!d.Agents.TryGetValue(r.Agent, out hours)) d.Agents[r.Agent] = hours = new Dictionary<string, Bucket>(StringComparer.Ordinal);
                    Bucket before;
                    if (!hours.TryGetValue(r.Key, out before)) { hours[r.Key] = r.Usage; result.Added++; }
                    // An older export (fewer tokens, or as many tokens with fewer requests)
                    // never lowers the totals. The same tokens and requests re-imported with
                    // corrected cost / timing metadata does update it (review #6); replaying
                    // the identical export stays a no-op.
                    else if (r.Usage.Tokens() < before.Tokens() || (r.Usage.Tokens() == before.Tokens() && r.Usage.R < before.R)) result.Same++;
                    else if (r.Usage.Tokens() == before.Tokens() && r.Usage.R == before.R && SameMetadata(r.Usage, before)) result.Same++;
                    else { hours[r.Key] = r.Usage; result.Updated++; }
                }
                string cutoff = LogIndex.HourKey(oldest);
                foreach (DeviceData d in touched.Values) {
                    Dictionary<string, object> meta;
                    string storedExported = d.Exported, incomingExported = null;   // 导入前 / 导入文件的导出时间：回收守则的两端
                    if (names.TryGetValue(d.Id, out meta)) { string n = Text(meta, "name"); if (!String.IsNullOrWhiteSpace(n)) d.Name = n.Length > 40 ? n.Substring(0, 40) : n; incomingExported = Text(meta, "exported_utc") ?? ""; d.Exported = incomingExported; d.Version = Text(meta, "app_version") ?? ""; }
                    // Phase 4.5c：只有更新的导出（或首次导入）才按（设备，小时）整体替换——
                    // 旧导出缺行只说明它更旧，绝不能借回收删掉新导出带来的桶。
                    if (NewerExport(storedExported, incomingExported)) {
                        Dictionary<string, HashSet<string>> perAgent;
                        if (incoming.TryGetValue(d.Id, out perAgent))
                            foreach (var pair in perAgent) {
                                int sep = pair.Key.IndexOf('|');
                                string agent = pair.Key.Substring(0, sep), hourPart = pair.Key.Substring(sep + 1);
                                Dictionary<string, Bucket> hours;
                                if (!d.Agents.TryGetValue(agent, out hours)) continue;
                                foreach (string old in hours.Keys.Where(k => k.Length > 10 && k.Substring(0, 10) == hourPart && !pair.Value.Contains(k)).ToList()) hours.Remove(old);
                            }
                    }
                    d.Imported = nowUtc.ToString("o");
                    foreach (var hours in d.Agents.Values) foreach (string old in hours.Keys.Where(k => String.CompareOrdinal(k, cutoff) < 0).ToList()) hours.Remove(old);
                    // One device failing to save must not abort the import of the rest
                    // (review #12: the saves are sequential, not one transaction).
                    try { Save(d); result.Devices.Add(d.Name); } catch { }
                }
                foreach (string device in rows.Where(r => !touched.ContainsKey(r.Device)).Select(r => r.Device).Distinct()) {
                    Dictionary<string, object> meta; string n = names.TryGetValue(device, out meta) ? Text(meta, "name") : null;
                    result.Devices.Add(String.IsNullOrWhiteSpace(n) ? device : n);
                }
            }
            return result;
        }
        private sealed class ImportRow { public string Device, Agent, Group, Key; public Bucket Usage; }
        // This device's own totals per agent / hour / provider / model (all endpoints together).
        private static Dictionary<string, Bucket> LocalTotals(IEnumerable<string> agents) {
            var totals = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            foreach (UsageScanner.Agent agent in UsageScanner.Agents.Where(a => agents.Contains(a.Id))) {
                LogIndex index = Store.Read<LogIndex>(agent.File);
                if (index.Version != LogIndex.CurrentVersion) continue;
                foreach (HourUsage h in index.Entries()) {
                    string key = agent.Id + "|" + LogIndex.BucketKey(h.Hour, h.Provider, h.Model);
                    Bucket b; if (!totals.TryGetValue(key, out b)) totals[key] = b = new Bucket();
                    b.I += h.Input; b.C += h.Cached; b.W += h.CacheWrite; b.O += h.Output; b.X += h.Other; b.R += h.Requests;
                }
            }
            return totals;
        }
        // Token counts and requests equal: the same usage, not a coincidence.
        private static bool SameUsage(Bucket a, Bucket b) {
            return a.Tokens() > 0 && Math.Abs(a.I - b.I) < .5 && Math.Abs(a.C - b.C) < .5 && Math.Abs(a.W - b.W) < .5 && Math.Abs(a.O - b.O) < .5 && Math.Abs(a.X - b.X) < .5 && Math.Abs(a.R - b.R) < .5;
        }
        // The pricing / timing fields a later export of the same usage completes (review #6).
        private static bool SameMetadata(Bucket a, Bucket b) {
            return Close(a.D, b.D) && Close(a.U, b.U) && Close(a.TN, b.TN) && Close(a.TO, b.TO) && Close(a.TS, b.TS);
        }
        // Phase 4.5c guard: per-hour wholesale replacement (recycling) only by a NEWER export
        // of the same device. An unknown incoming time (bare rows without cum_devices meta)
        // never recycles: without an order there is no snapshot semantics.
        private static bool NewerExport(string storedExported, string incomingExported) {
            if (String.IsNullOrEmpty(incomingExported)) return false;
            if (String.IsNullOrEmpty(storedExported)) return true;
            DateTime a, b;
            if (!DateTime.TryParse(incomingExported, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out a)) return false;
            if (!DateTime.TryParse(storedExported, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out b)) return true;
            return a >= b;
        }
        private static bool Close(double a, double b) { double scale = Math.Max(Math.Max(Math.Abs(a), Math.Abs(b)), 1e-9); return Math.Abs(a - b) <= 1e-9 * scale; }
        // Does a stored hour key belong to this hour / provider / model group (any endpoint)?
        private static bool InGroup(string hourKey, string groupKey) { return hourKey == groupKey || (hourKey.Length > groupKey.Length && hourKey[groupKey.Length] == '|' && hourKey.StartsWith(groupKey, StringComparison.Ordinal)); }
        // The hour | provider | model part of a stored bucket key (the endpoint varies).
        private static string GroupKeyOf(string key) { string[] parts = key.Split('|'); return parts[0] + "|" + (parts.Length > 1 ? parts[1] : "") + "|" + (parts.Length > 2 ? parts[2] : ""); }
        // This index's own totals per hour / provider / model, endpoints combined.
        private static Dictionary<string, Bucket> OwnGroupTotals(LogIndex index) {
            var totals = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            if (index == null || index.Version != LogIndex.CurrentVersion) return totals;
            foreach (HourUsage h in index.Entries()) {
                string key = LogIndex.BucketKey(h.Hour, h.Provider, h.Model);
                Bucket b; if (!totals.TryGetValue(key, out b)) totals[key] = b = new Bucket();
                b.I += h.Input; b.C += h.Cached; b.W += h.CacheWrite; b.O += h.Output; b.X += h.Other; b.R += h.Requests;
            }
            return totals;
        }
        private static string Text(Dictionary<string, object> row, string column) { object v; return row.TryGetValue(column, out v) ? v as string : null; }
        // A missing or unparseable cell is 0; a non-finite value (Infinity from "1e999") is
        // NaN, which makes the whole row refuse instead of poisoning the device file
        // with a number the JSON serializer cannot write (review #12).
        private static double Number(Dictionary<string, object> row, string column) {
            object v; if (!row.TryGetValue(column, out v) || v == null) return 0;
            double n;
            if (v is double) n = (double)v;
            else if (!Double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return 0;
            return !Double.IsNaN(n) && !Double.IsInfinity(n) ? n : Double.NaN;
        }
        // INSERT [OR …] INTO table [(columns)] VALUES (…), (…); → (table, column → value) per
        // row. Values: 'text' (with '' escapes), numbers and NULL. Comments are skipped.
        internal static IEnumerable<KeyValuePair<string, Dictionary<string, object>>> Inserts(string sql) {
            foreach (string statement in Statements(sql ?? "")) {
                Match m = Regex.Match(statement, @"^\s*INSERT\s+(?:OR\s+\w+\s+)?INTO\s+[""`\[]?(\w+)[""`\]]?\s*(?:\(([^)]*)\))?\s*VALUES\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (!m.Success) continue;
                string table = m.Groups[1].Value.ToLowerInvariant();
                string[] columns = m.Groups[2].Success && m.Groups[2].Value.Trim().Length > 0
                    ? m.Groups[2].Value.Split(',').Select(c => c.Trim().Trim('"', '`', '[', ']').ToLowerInvariant()).ToArray()
                    : table == "cum_usage_hours" ? Columns : table == "cum_devices" ? new[] { "device_id", "name", "exported_utc", "app_version" } : new string[0];
                foreach (List<object> tuple in Tuples(m.Groups[3].Value)) {
                    var row = new Dictionary<string, object>(StringComparer.Ordinal);
                    for (int i = 0; i < columns.Length && i < tuple.Count; i++) row[columns[i]] = tuple[i];
                    yield return new KeyValuePair<string, Dictionary<string, object>>(table, row);
                }
            }
        }
        private static IEnumerable<string> Statements(string sql) {
            var current = new StringBuilder();
            for (int i = 0; i < sql.Length; i++) {
                char c = sql[i];
                if (c == '\'') {
                    current.Append(c);
                    for (i++; i < sql.Length; i++) { current.Append(sql[i]); if (sql[i] == '\'') { if (i + 1 < sql.Length && sql[i + 1] == '\'') { current.Append('\''); i++; } else break; } }
                } else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { while (i < sql.Length && sql[i] != '\n') i++; current.Append('\n'); }
                else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? sql.Length : end + 1; current.Append(' '); }
                else if (c == ';') { if (current.ToString().Trim().Length > 0) yield return current.ToString(); current.Clear(); }
                else current.Append(c);
            }
            if (current.ToString().Trim().Length > 0) yield return current.ToString();
        }
        private static IEnumerable<List<object>> Tuples(string values) {
            int i = 0;
            while (i < values.Length) {
                while (i < values.Length && values[i] != '(') i++;
                if (i >= values.Length) yield break;
                i++;
                var tuple = new List<object>();
                while (i < values.Length) {
                    while (i < values.Length && Char.IsWhiteSpace(values[i])) i++;
                    if (i >= values.Length) break;
                    if (values[i] == ')') { i++; break; }
                    if (values[i] == ',') { i++; continue; }
                    if (values[i] == '\'') {
                        var text = new StringBuilder();
                        for (i++; i < values.Length; i++) { if (values[i] == '\'') { if (i + 1 < values.Length && values[i + 1] == '\'') { text.Append('\''); i++; } else { i++; break; } } else text.Append(values[i]); }
                        tuple.Add(text.ToString());
                    } else {
                        int start = i; while (i < values.Length && values[i] != ',' && values[i] != ')') i++;
                        string token = values.Substring(start, i - start).Trim();
                        double n;
                        tuple.Add(token.Equals("NULL", StringComparison.OrdinalIgnoreCase) ? null : Double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out n) ? (object)n : token);
                    }
                }
                yield return tuple;
            }
        }
    }

    // ── Backups ───────────────────────────────────────────────────────
    public sealed class BackupInfo { public string Path = "", Name = ""; public DateTime Created; public long Size; public bool Auto; }
    public static class Backups {
        private const string Pattern = "codeusagemonit-backup-*.zip", Manifest = "backup.json";
        public static string Folder(AppConfig config) {
            return config != null && !String.IsNullOrWhiteSpace(config.BackupFolder) ? config.BackupFolder.Trim() : Path.Combine(Store.Data, "backups");
        }
        // What a backup holds: settings, custom providers, the endpoint timeline, the usage
        // indexes, the hourly archive (Phase 1), imported devices and the background
        // picture. Not the DPAPI keys (bound to this Windows user, they stay where they
        // are). device.json rides along since Phase 4.5a: a restored install keeps its
        // device id, so the same machine does not re-upload its whole history under a new
        // id — which the other devices would import next to the old one and count twice.
        public static bool Included(string relative) {
            string name = (relative ?? "").Replace('\\', '/');
            if (name.Contains("..") || name.StartsWith("/")) return false;
            if (name.StartsWith(Devices.Folder + "/", StringComparison.OrdinalIgnoreCase)) return name.Count(c => c == '/') == 1 && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            if (name.StartsWith("archive/", StringComparison.OrdinalIgnoreCase)) return name.Count(c => c == '/') == 1 && name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
            if (name.Contains("/")) return false;
            return name == "settings.json" || name == "device.json" || name == "endpoints.json" || name == "custom-providers.json" || name == "history.json"
                || name.EndsWith("-logs.json", StringComparison.OrdinalIgnoreCase) || name.StartsWith("background.", StringComparison.OrdinalIgnoreCase);
        }
        public static string Create(AppConfig config, bool auto, DateTime nowUtc) {
            string dir = Folder(config); Directory.CreateDirectory(dir);
            string stamp = nowUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(dir, "codeusagemonit-backup-" + stamp + (auto ? "-auto" : "") + ".zip"), temp = path + ".tmp";
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(dir, "codeusagemonit-backup-" + stamp + "-" + n + (auto ? "-auto" : "") + ".zip");
            try {
                using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create)) {
                    string root = Store.Data.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                    foreach (string file in Directory.GetFiles(Store.Data, "*", SearchOption.AllDirectories)) {
                        string relative = file.Substring(root.Length).Replace('\\', '/');
                        if (!Included(relative)) continue;
                        // A scan may replace an index at this moment: read it into memory first.
                        byte[] bytes = null;
                        for (int attempt = 0; attempt < 3 && bytes == null; attempt++) { try { bytes = File.ReadAllBytes(file); } catch (IOException) { Thread.Sleep(150); } }
                        if (bytes == null) throw new IOException(I18n.T("无法读取 {0}", relative));
                        using (Stream s = zip.CreateEntry(relative, CompressionLevel.Optimal).Open()) s.Write(bytes, 0, bytes.Length);
                    }
                    DeviceIdentity self = Devices.Self();
                    string manifest = J.Serializer().Serialize(new Dictionary<string, object> { { "app", "codeusagemonit" }, { "version", AppInfo.Version }, { "created", nowUtc.ToString("o") }, { "device", self.Name }, { "auto", auto } });
                    using (var writer = new StreamWriter(zip.CreateEntry(Manifest).Open(), new UTF8Encoding(false))) writer.Write(manifest);
                }
                File.Move(temp, path);
            } finally { if (File.Exists(temp)) { try { File.Delete(temp); } catch { } } }
            return path;
        }
        public static List<BackupInfo> List(AppConfig config) {
            string dir = Folder(config);
            if (!Directory.Exists(dir)) return new List<BackupInfo>();
            return Directory.GetFiles(dir, Pattern).Select(p => { var info = new FileInfo(p); return new BackupInfo { Path = p, Name = info.Name, Created = info.LastWriteTimeUtc, Size = info.Length, Auto = info.Name.EndsWith("-auto.zip", StringComparison.OrdinalIgnoreCase) }; })
                .OrderByDescending(b => b.Created).ToList();
        }
        // Keeps the newest `keep` automatic backups; backups made by hand are never removed.
        public static int Prune(AppConfig config) {
            int removed = 0;
            foreach (BackupInfo old in List(config).Where(b => b.Auto).Skip(Math.Max(1, config.BackupKeep))) { try { File.Delete(old.Path); removed++; } catch { } }
            return removed;
        }
        public static bool Due(AppConfig config, DateTime nowUtc) {
            if (config.BackupHours <= 0) return false;
            BackupInfo newest = List(config).FirstOrDefault(b => b.Auto);
            return newest == null || nowUtc - newest.Created >= TimeSpan.FromHours(config.BackupHours);
        }
        // Writes the backup's files over the current ones, after a backup of the current
        // state (made by hand, so pruning never removes it). Returns that safety backup.
        public static string Restore(AppConfig config, string zipPath, DateTime nowUtc) {
            using (var zip = ZipFile.OpenRead(zipPath)) {
                if (zip.GetEntry(Manifest) == null) throw new InvalidDataException(I18n.T("这不是 codeusagemonit 的备份文件。"));
                string safety = Create(config, false, nowUtc);
                foreach (ZipArchiveEntry entry in zip.Entries) {
                    if (!Included(entry.FullName) || entry.Length > 512L * 1024 * 1024) continue;
                    string target = Path.Combine(Store.Data, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (Stream source = entry.Open()) using (FileStream output = File.Create(temp)) source.CopyTo(output);
                    if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target);
                }
                return safety;
            }
        }
    }

    // ── WebDAV sync ───────────────────────────────────────────────────
    // <folder>/devices/<device id>.sql per device: each device uploads only its own usage and
    // imports everyone else's, so devices never overwrite each other.
    // Baseline（Phase 4.4）：上次成功同步后本机文件在服务器上的 ETag/Last-Modified 组合戳。
    // BaselineUrl 绑定文件夹地址——换了服务器基线即作废，退化为直接上传（与旧行为一致）。
    public sealed class SyncState { public string Last = "", Message = ""; public bool Ok; public string BaselineUrl = "", Baseline = ""; }
    public sealed class SyncOutcome { public string Message = "", Folder = ""; public string Baseline; public bool Conflicted; }
    public static class WebDavSync {
        public const string KeyId = "webdav";
        public static string Validate(string url) {
            if (String.IsNullOrWhiteSpace(url)) return I18n.T("请填写 WebDAV 文件夹地址。");
            Uri uri;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return I18n.T("地址应为 https://… 开头的完整网址。");
            if (uri.UserInfo.Length > 0) return I18n.T("用户名和密码请填在下面的输入框里，不要写进地址。");
            if (uri.Scheme == Uri.UriSchemeHttp && !CustomProviders.IsPrivateHost(uri)) return I18n.T("公网地址必须使用 https；http 只允许本机或局域网地址。");
            return null;
        }
        private static string Folder(string url) { url = url.Trim(); return url.EndsWith("/") ? url : url + "/"; }
        private static HttpClient Client(AppConfig config, string user, string password) {
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            string proxy = ProviderService.ResolveProxy(config.Proxy); handler.UseProxy = proxy.Length > 0; if (handler.UseProxy) handler.Proxy = new WebProxy(proxy);
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
            if (!String.IsNullOrEmpty(user) || !String.IsNullOrEmpty(password)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes((user ?? "") + ":" + (password ?? ""))));
            return client;
        }
        private static async Task<HttpResponseMessage> Send(HttpClient client, string method, string url, string body, string depth, string ifMatch = null) {
            var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (depth != null) request.Headers.TryAddWithoutValidation("Depth", depth);
            // CAS：带 If-Match 的 PUT 只有在远端 ETag 仍是探测到的那个时才会生效——服务器
            // 不支持时多半忽略此头，此时仍有前置的戳比对兜底（Phase 4.4）。
            if (ifMatch != null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            if (body != null) request.Content = new StringContent(body, Encoding.UTF8, method == "PROPFIND" ? "application/xml" : "application/sql");
            // Headers-only completion: bodies are streamed (and byte-capped) by the caller
            // instead of being buffered whole by SendAsync before any size check (review #11).
            HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status == 401) throw new ProviderException(I18n.T("WebDAV 用户名或密码不对（401）。坚果云等服务需要使用“应用密码”。"), "error");
            if (status == 403) throw new ProviderException(I18n.T("WebDAV 拒绝访问（403），请检查文件夹权限。"), "error");
            if (status >= 300 && status < 400) throw new ProviderException(I18n.T("WebDAV 地址被重定向（HTTP {0}），请填写最终的文件夹地址。", status), "error");
            return response;
        }
        // A device's export sits far below these caps; they exist so a runaway server
        // response is cut off while streaming instead of buffered whole (review #11).
        internal const long MaxSqlBytes = 64L * 1024 * 1024, MaxListingBytes = 4L * 1024 * 1024;
        internal const int MaxDevices = 500, DownloadIdleSeconds = 120;
        // Reads a response body as UTF-8 text while counting bytes: returns null once the
        // limit is exceeded (the transfer stops there, nothing over the cap is buffered).
        // The idle token restarts on every chunk, so a stalled server cannot hang the sync.
        internal static async Task<string> ReadCapped(Stream stream, long limit, CancellationTokenSource idle, int chunk = 1 << 16) {
            var decoder = Encoding.UTF8.GetDecoder();
            var text = new StringBuilder(); var bytes = new byte[Math.Max(1, chunk)]; var chars = new char[bytes.Length];
            long total = 0; int read;
            while ((read = await stream.ReadAsync(bytes, 0, bytes.Length, idle == null ? CancellationToken.None : idle.Token).ConfigureAwait(false)) > 0) {
                total += read;
                if (total > limit) return null;
                if (idle != null) idle.CancelAfter(DownloadIdleSeconds * 1000);
                int count = decoder.GetChars(bytes, 0, read, chars, 0, false);
                text.Append(chars, 0, count);
            }
            return text.ToString();
        }
        // 建目录（PROPFIND 探测 → MKCOL），走注入的 transport：生产走 HTTP，自测的内存
        // WebDAV 也要能建目录（Phase 4 初审 P2-4：原直连版无调用方，已删）。
        private static async Task Ensure(HttpClient client, SyncTransport transport, string folder) {
            using (HttpResponseMessage probe = await transport.SendAsync(client, "PROPFIND", folder, null, "0", null).ConfigureAwait(false)) if ((int)probe.StatusCode == 207 || probe.IsSuccessStatusCode) return;
            using (HttpResponseMessage made = await transport.SendAsync(client, "MKCOL", folder, null, null, null).ConfigureAwait(false)) {
                int status = (int)made.StatusCode;
                if (!made.IsSuccessStatusCode && status != 405) throw new ProviderException(I18n.T("无法在 WebDAV 上创建文件夹（HTTP {0}）。", status), "error");
            }
        }
        // Names of the .sql files directly inside folder.
        internal static List<string> Files(string multistatus, string folder) {
            var names = new List<string>();
            var xml = new XmlDocument { XmlResolver = null };
            xml.LoadXml(multistatus);
            var ns = new XmlNamespaceManager(xml.NameTable); ns.AddNamespace("d", "DAV:");
            string folderPath = new Uri(folder).AbsolutePath.TrimEnd('/');
            foreach (XmlNode href in xml.SelectNodes("//d:response/d:href", ns)) {
                string path = href.InnerText.Trim(); Uri absolute;
                if (Uri.TryCreate(path, UriKind.Absolute, out absolute)) path = absolute.AbsolutePath;
                path = Uri.UnescapeDataString(path).TrimEnd('/');
                string name = path.Substring(path.LastIndexOf('/') + 1);
                if (String.Equals(Uri.UnescapeDataString(folderPath), path, StringComparison.Ordinal)) continue;
                if (Regex.IsMatch(name, "^[A-Za-z0-9_-]{1,64}\\.sql$")) { names.Add(name); if (names.Count >= MaxDevices) break; }
            }
            return names;
        }
        public static async Task<string> Test(AppConfig config, string url, string user, string password) {
            string error = Validate(url); if (error != null) throw new ProviderException(error, "setup");
            using (HttpClient client = Client(config, user, password))
            using (HttpResponseMessage response = await Send(client, "PROPFIND", Folder(url), null, "0").ConfigureAwait(false)) {
                int status = (int)response.StatusCode;
                if (status == 404) return I18n.T("连接成功；文件夹还不存在，第一次同步时会创建。");
                if (status != 207 && !response.IsSuccessStatusCode) throw new ProviderException(I18n.T("WebDAV 返回 HTTP {0}。", status), "error");
                return I18n.T("连接成功。");
            }
        }
        // One remote file's (ETag, Last-Modified) from a PROPFIND multistatus: the two ends
        // of the CAS check. Either alone is enough to compare; a server offering neither
        // (Key stays "") makes every comparison vacuous and the sync falls back to the old
        // behaviour — it simply uploads (Phase 4.4).
        internal sealed class RemoteStamp { public string ETag = "", Modified = "";
            public string Key { get { return (ETag ?? "") + "|" + (Modified ?? ""); } }
            public bool Known { get { return (ETag ?? "").Length > 0 || (Modified ?? "").Length > 0; } }
        }
        // RFC 7232：If-Match 只做强比较，弱 ETag（W/"…"）永远配不上——按原文发送会让
        // 弱 ETag 服务器永久 412（初审 P1-1）。剥掉弱前缀后再发；服务器仍判不匹配时由
        // PUT 循环的重试/裸 PUT 兜底。
        private static string StrongTag(string etag) {
            string s = (etag ?? "").Trim();
            return s.Length >= 2 && (s[0] == 'W' || s[0] == 'w') && s[1] == '/' ? s.Substring(2).Trim() : s;
        }
        // Finds one file's stamp by name in a multistatus document; null when absent.
        internal static RemoteStamp StampOf(string multistatus, string fileName) {
            try {
                var xml = new XmlDocument { XmlResolver = null };
                xml.LoadXml(multistatus);
                var ns = new XmlNamespaceManager(xml.NameTable); ns.AddNamespace("d", "DAV:");
                foreach (XmlNode response in xml.SelectNodes("//d:response", ns)) {
                    XmlNode href = response.SelectSingleNode("d:href", ns);
                    if (href == null) continue;
                    string path = href.InnerText.Trim(); Uri absolute;
                    if (Uri.TryCreate(path, UriKind.Absolute, out absolute)) path = absolute.AbsolutePath;
                    path = Uri.UnescapeDataString(path).TrimEnd('/');
                    string name = path.Substring(path.LastIndexOf('/') + 1);
                    if (!String.Equals(name, fileName, StringComparison.Ordinal)) continue;
                    XmlNode etag = response.SelectSingleNode("d:propstat/d:prop/d:getetag", ns);
                    XmlNode modified = response.SelectSingleNode("d:propstat/d:prop/d:getlastmodified", ns);
                    return new RemoteStamp { ETag = etag == null ? "" : etag.InnerText.Trim(), Modified = modified == null ? "" : modified.InnerText.Trim() };
                }
            } catch { }
            return null;
        }
        // HTTP 原语封装（Phase 4.4）：生产实现直接走 Send / ReadCapped；自测注入内存实现
        // 复现「探测 → 冲突 → 下载合并 → 重试上传」，不发起网络请求。
        public class SyncTransport {
            public virtual HttpClient Client(AppConfig config, string user, string password) { return WebDavSync.Client(config, user, password); }
            public virtual Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string url, string body, string depth, string ifMatch) { return Send(client, method, url, body, depth, ifMatch); }
            public virtual Task<string> ReadAsync(Stream stream, long limit, CancellationTokenSource idle) { return ReadCapped(stream, limit, idle); }
        }
        // PROPFIND depth 0 on one file: its stamp, or null when it does not exist (or the
        // server refused — a failed probe degrades to a plain upload, never to a skip).
        private static async Task<RemoteStamp> Probe(HttpClient client, SyncTransport transport, string fileUrl) {
            try {
                using (HttpResponseMessage response = await transport.SendAsync(client, "PROPFIND", fileUrl,
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:getetag/><d:getlastmodified/></d:prop></d:propfind>", "0", null).ConfigureAwait(false)) {
                    int status = (int)response.StatusCode;
                    if (status == 404) return null;
                    if (status != 207 && !response.IsSuccessStatusCode) return null;
                    using (CancellationTokenSource idle = new CancellationTokenSource(DownloadIdleSeconds * 1000))
                    using (Stream body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        return StampOf(await transport.ReadAsync(body, MaxListingBytes, idle).ConfigureAwait(false), fileUrl.Substring(fileUrl.LastIndexOf('/') + 1));
                }
            } catch { return null; }
        }
        // The conflicting remote copy, downloaded and merged before it is overwritten. Own
        // rows are skipped by ImportSql (this device's logs are the source); the point is an
        // OLDER upload that still carried other devices' rows — overwriting it would lose
        // them, merging keeps them (Phase 4.4).
        private static async Task MergeRemote(HttpClient client, SyncTransport transport, string fileUrl, DateTime nowUtc) {
            try {
                using (HttpResponseMessage get = await transport.SendAsync(client, "GET", fileUrl, null, null, null).ConfigureAwait(false)) {
                    if (!get.IsSuccessStatusCode || get.Content.Headers.ContentLength > MaxSqlBytes) return;
                    string sql;
                    using (CancellationTokenSource idle = new CancellationTokenSource(DownloadIdleSeconds * 1000))
                    using (Stream body = await get.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        sql = await transport.ReadAsync(body, MaxSqlBytes, idle).ConfigureAwait(false);
                    if (sql != null) Devices.ImportSql(sql, nowUtc);
                }
            } catch { }
        }
        public static async Task<string> Run(AppConfig config, string url, string user, string password, DateTime nowUtc) {
            return (await RunDetailed(config, url, user, password, nowUtc, null).ConfigureAwait(false)).Message;
        }
        public static async Task<SyncOutcome> RunDetailed(AppConfig config, string url, string user, string password, DateTime nowUtc, SyncTransport transport) {
            transport = transport ?? new SyncTransport();
            string error = Validate(url); if (error != null) throw new ProviderException(error, "setup");
            DeviceIdentity self = Devices.Self();
            string root = Folder(url), devices = root + "devices/", mine = self.Id + ".sql";
            SyncState saved = State();
            string baseline = saved.BaselineUrl == url.Trim() ? saved.Baseline ?? "" : "";   // 换了文件夹：基线作废
            var outcome = new SyncOutcome { Folder = url.Trim(), Baseline = null, Conflicted = false };
            using (HttpClient client = transport.Client(config, user, password)) {
                await Ensure(client, transport, root).ConfigureAwait(false);
                await Ensure(client, transport, devices).ConfigureAwait(false);
                // ── CAS：上传前把远端本机文件的戳与基线比对（Phase 4.4）。不一致说明上一轮
                // 同步之后有人动过这个文件（换机重装、手动覆盖、旧版多合一导出、服务器迁移）：
                // 先下载合并再上传。If-Match 兜住探测与上传之间另一次写入。弱 ETag（W/"…"）
                // 按 RFC 7232 强比较永远配不上，旧代码会永久 412 硬失败（初审 P1-1）：
                // 发之前剥掉 W/ 前缀；重试仍 412 就降级为无 If-Match 的普通 PUT——冲突此时
                // 已经合并过，裸 PUT 不丢数据，保可用性。
                bool conflicted = false; string match = null;
                RemoteStamp current = await Probe(client, transport, devices + mine).ConfigureAwait(false);
                if (current != null && current.Known && baseline.Length > 0 && baseline != current.Key) {
                    await MergeRemote(client, transport, devices + mine, nowUtc).ConfigureAwait(false);
                    conflicted = true;
                    current = await Probe(client, transport, devices + mine).ConfigureAwait(false) ?? current;
                }
                if (current != null && current.ETag.Length > 0) match = StrongTag(current.ETag);
                string probedKey = current != null ? current.Key : "";   // 上传前看到的远端状态
                string putEtag = "";
                for (int attempt = 0; attempt < 3; attempt++) {
                    string ifMatch = attempt < 2 ? match : null;   // 第三轮：裸 PUT 兜底
                    using (HttpResponseMessage put = await transport.SendAsync(client, "PUT", devices + mine, Devices.ExportSql(nowUtc, false), null, ifMatch).ConfigureAwait(false)) {
                        int status = (int)put.StatusCode;
                        if (status == 412 || status == 409) {
                            if (attempt == 0) {   // 探测与上传之间被写入：下载合并后带新戳重试一次
                                await MergeRemote(client, transport, devices + mine, nowUtc).ConfigureAwait(false);
                                RemoteStamp again = await Probe(client, transport, devices + mine).ConfigureAwait(false);
                                // 只有远端状态真的变了才算冲突：弱 ETag 服务器对任何 If-Match 都
                                // 回 412（初审 P1-1），那是语义性拒绝而不是数据冲突，不虚报。
                                if (again == null || again.Key != probedKey) conflicted = true;
                                match = again != null && again.ETag.Length > 0 ? StrongTag(again.ETag) : null;
                                continue;
                            }
                            if (attempt == 1 && ifMatch != null) { match = null; continue; }   // 仍冲突：降级为裸 PUT
                        }
                        if (!put.IsSuccessStatusCode) throw new ProviderException(I18n.T("上传本机数据失败（HTTP {0}）。", status), "error");
                        putEtag = put.Headers.Contains("ETag") ? String.Join(",", put.Headers.GetValues("ETag")).Trim() : "";
                        break;
                    }
                }
                string listing;
                using (HttpResponseMessage list = await transport.SendAsync(client, "PROPFIND", devices, "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:getetag/><d:getlastmodified/></d:prop></d:propfind>", "1", null).ConfigureAwait(false)) {
                    if ((int)list.StatusCode != 207) throw new ProviderException(I18n.T("读取 WebDAV 文件列表失败（HTTP {0}）。", (int)list.StatusCode), "error");
                    using (CancellationTokenSource idle = new CancellationTokenSource(DownloadIdleSeconds * 1000))
                    using (Stream body = await list.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        listing = await transport.ReadAsync(body, MaxListingBytes, idle).ConfigureAwait(false);
                    if (listing == null) throw new ProviderException(I18n.T("WebDAV 文件列表超过 {0} MB 上限，请清理同步文件夹。", (int)(MaxListingBytes / 1024 / 1024)), "error");
                }
                // 新基线：列表里本机文件上传后的完整戳（ETag|Last-Modified，与探测同形态）；
                // 服务器连戳都不给时才退回 PUT 响应里的 ETag（无 Modified 可比）。
                RemoteStamp after = StampOf(listing, mine);
                outcome.Baseline = after != null && after.Known ? after.Key : putEtag.Length > 0 ? putEtag + "|" : "";
                var merged = new List<string>(); int rows = 0, others = 0;
                foreach (string name in Files(listing, devices)) {
                    if (name == self.Id + ".sql") continue;
                    others++;
                    using (HttpResponseMessage get = await transport.SendAsync(client, "GET", devices + Uri.EscapeDataString(name), null, null, null).ConfigureAwait(false)) {
                        if (!get.IsSuccessStatusCode) continue;
                        if (get.Content.Headers.ContentLength > MaxSqlBytes) continue;
                        // Streamed with a byte cap: an oversized or stalling transfer stops at
                        // the limit instead of buffering whole first (review #11).
                        string sql;
                        using (CancellationTokenSource idle = new CancellationTokenSource(DownloadIdleSeconds * 1000))
                        using (Stream body = await get.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            sql = await transport.ReadAsync(body, MaxSqlBytes, idle).ConfigureAwait(false);
                        if (sql == null) continue;
                        ImportResult result = Devices.ImportSql(sql, nowUtc);
                        rows += result.Rows; merged.AddRange(result.Devices);
                    }
                }
                string done = others == 0 ? I18n.T("已上传本机数据；文件夹里还没有其他设备。")
                    : merged.Count == 0 ? I18n.T("已上传本机数据；另外 {0} 台设备没有可合并的用量。", others)
                    : I18n.T("已上传本机数据，并合并 {0} 台设备（{1}）。", merged.Count, String.Join("、", merged.Distinct()));
                outcome.Conflicted = conflicted;
                // 句子拼接：剥掉前句句号再接分号从句，避免「。；」连出。
                outcome.Message = conflicted ? done.TrimEnd('。', '.') + I18n.T("；远端本机文件已被更新过，已先合并再上传。") : done;
                return outcome;
            }
        }
        public static SyncState State() { return Store.Read<SyncState>("sync-state.json"); }
        public static void Record(bool ok, string message, DateTime nowUtc) { Record(ok, message, nowUtc, null); }
        // outcome carries the new CAS baseline from a finished run; a failed sync passes
        // null and keeps the old baseline untouched.
        public static void Record(bool ok, string message, DateTime nowUtc, SyncOutcome outcome) {
            try {
                SyncState state = State();
                state.Last = nowUtc.ToString("o"); state.Ok = ok; state.Message = message;
                if (outcome != null && outcome.Baseline != null) { state.Baseline = outcome.Baseline; state.BaselineUrl = outcome.Folder; }
                Store.Write("sync-state.json", state);
            } catch { }
        }
        public static bool Due(AppConfig config, DateTime nowUtc) {
            if (config.SyncMinutes <= 0 || String.IsNullOrWhiteSpace(config.WebDavUrl)) return false;
            DateTime last; return !LogIndex.Parse(State().Last, out last) || nowUtc - last >= TimeSpan.FromMinutes(config.SyncMinutes);
        }
    }
}
