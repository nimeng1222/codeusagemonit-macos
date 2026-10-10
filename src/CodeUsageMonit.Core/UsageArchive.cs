using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CodeUsageMonit {
    // One archived hour bucket. Key is the original bucket key of the hot index
    // ("yyyyMMddHH|provider|model[|endpoint]"), so the null / "" / host endpoint distinction
    // and every escaping survive the round trip unchanged; Agent is the hourly index the
    // bucket was read into ("codex", "claude", …). Numbers are the same aggregates a Bucket
    // holds (LocalLogs.cs).
    public sealed class ArchiveRow {
        public string Agent = "", Key = "";
        public DateTime Hour;                        // UTC hour, parsed from the key prefix
        public double I, C, W, O, X, R, D, U, TN, TO, TS;
        public Bucket Bucket() { return new Bucket { I = I, C = C, W = W, O = O, X = X, R = R, D = D, U = U, TN = TN, TO = TO, TS = TS }; }
        // Compact single-line JSON, one object per archive line. Numbers in invariant
        // round-trip format; the two strings are escaped by hand (J.Serializer would build
        // one instance per row).
        public string Serialize() {
            var c = CultureInfo.InvariantCulture;
            return "{\"a\":" + Text(Agent) + ",\"k\":" + Text(Key)
                + ",\"i\":" + I.ToString("R", c) + ",\"c\":" + C.ToString("R", c) + ",\"w\":" + W.ToString("R", c)
                + ",\"o\":" + O.ToString("R", c) + ",\"x\":" + X.ToString("R", c) + ",\"r\":" + R.ToString("R", c)
                + ",\"d\":" + D.ToString("R", c) + ",\"u\":" + U.ToString("R", c)
                + ",\"tn\":" + TN.ToString("R", c) + ",\"to\":" + TO.ToString("R", c) + ",\"ts\":" + TS.ToString("R", c) + "}";
        }
        // Damaged lines (a torn final line after a crash, a hand edit) return false and are
        // skipped: one bad row never takes the file down.
        public static bool TryParse(string line, out ArchiveRow row) {
            row = null;
            if (String.IsNullOrEmpty(line) || line[0] != '{') return false;
            try {
                object root = J.Parse(line); if (root == null) return false;
                string agent = J.Str(root, "a"), key = J.Str(root, "k");
                DateTime hour;
                if (agent.Length == 0 || key.Length < 11 || key[10] != '|' || !DateTime.TryParseExact(key.Substring(0, 10), "yyyyMMddHH", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out hour)) return false;
                Func<string, double> n = name => { double? v = J.Num(root, name); return v.HasValue && !Double.IsNaN(v.Value) && !Double.IsInfinity(v.Value) && v.Value >= 0 ? v.Value : 0; };
                row = new ArchiveRow { Agent = agent, Key = key, Hour = hour, I = n("i"), C = n("c"), W = n("w"), O = n("o"), X = n("x"), R = n("r"), D = n("d"), U = n("u"), TN = n("tn"), TO = n("to"), TS = n("ts") };
                return true;
            } catch { return false; }
        }
        private static string Text(string value) {
            var sb = new StringBuilder((value ?? "").Length + 2).Append('"');
            foreach (char ch in value ?? "") {
                if (ch == '"') sb.Append("\\\"");
                else if (ch == '\\') sb.Append("\\\\");
                else if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }
    }

    // Append-only archive of hourly usage under data/archive/usage-YYYY-MM.jsonl, one
    // ArchiveRow per line, split by the UTC month of the bucket. The hot indexes only keep
    // LogReader.RetainDays (33) days and history derivations were bounded by them; the
    // archive keeps a read-only projection beyond that, so a client pruning its transcripts
    // no longer means lost history (ROADMAP Phase 1).
    //
    // Write path: UsageScanner.ScanAll calls Append after every scan. Append writes every
    // bucket the index holds — including the ones LogReader.Finish just moved out of the
    // retention window into LogIndex.Expiring — but skips keys already on disk, so repeated
    // scans append nothing (idempotent). Writes are append-only batches flushed to disk
    // before the caller persists the trimmed index; a crash can at worst tear the last
    // line, which parsing skips — existing content is never rewritten.
    //
    // Read path: MergeInto / MergeAll wrap a hot index in a NEW in-memory LogIndex whose
    // extra LogFile holds the archived hours, so charts, daily derivations and window
    // estimates answer for deleted-source hours through the ordinary Entries() API. Rows
    // are cached per file and only re-read when length or mtime changed, so the 30 s scan
    // and render loop does not re-parse the archive every tick.
    public static class UsageArchive {
        // How far back archived detail reaches (days). The daily-history window and the
        // custom-range picker use the same bound; history.json trims at this horizon too.
        public const int HorizonDays = 370;
        // Off during self-tests: synthetic indexes must not leak into the real data
        // directory. Archive-specific tests re-enable it against a swapped temp directory.
        public static bool Enabled = true;
        private static readonly object Gate = new object();
        private sealed class Cached { public DateTime Modified; public long Length; public List<ArchiveRow> Rows; }
        private static readonly Dictionary<string, Cached> Cache = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);

        public static string Folder() { return Path.Combine(Store.Data, "archive"); }
        private static string FileFor(DateTime hourUtc) { return Path.Combine(Folder(), "usage-" + hourUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl"); }
        public static void InvalidateCache() { lock (Gate) Cache.Clear(); }

        // Appends every bucket of the index (hot hours plus LogIndex.Expiring) that is not
        // archived yet and not older than HorizonDays. Returns the number of rows written;
        // never throws. The index is not modified.
        public static int Append(LogIndex index, string agent, DateTime nowUtc) {
            if (index == null || String.IsNullOrEmpty(agent) || !Enabled) return 0;
            try {
                DateTime oldest = nowUtc.AddDays(-HorizonDays);
                var pending = new Dictionary<string, Bucket>(StringComparer.Ordinal);
                foreach (LogFile file in index.Files) foreach (var pair in file.Hours) Collect(pending, pair.Key, pair.Value, oldest);
                foreach (var pair in index.Expiring) Collect(pending, pair.Key, pair.Value, oldest);
                if (pending.Count == 0) return 0;
                lock (Gate) {
                    var known = new HashSet<string>(StringComparer.Ordinal);
                    foreach (Cached cached in LoadAll().Values) foreach (ArchiveRow row in cached.Rows) known.Add(row.Agent + "|" + row.Key);
                    List<ArchiveRow> fresh = pending.Where(p => !known.Contains(agent + "|" + p.Key))
                        .Select(p => { Bucket b = p.Value; return new ArchiveRow { Agent = agent, Key = p.Key, Hour = ParseHour(p.Key), I = b.I, C = b.C, W = b.W, O = b.O, X = b.X, R = b.R, D = b.D, U = b.U, TN = b.TN, TO = b.TO, TS = b.TS }; })
                        .ToList();
                    if (fresh.Count == 0) return 0;
                    Directory.CreateDirectory(Folder());
                    foreach (var group in fresh.GroupBy(row => FileFor(row.Hour))) {
                        string path = group.Key;
                        var text = new StringBuilder();
                        foreach (ArchiveRow row in group) text.AppendLine(row.Serialize());
                        // One append per file per scan: the batch hits the disk as a single
                        // write at the current end, flushed before the caller trims anything.
                        // leaveOpen: the fsync below must reach the still-open stream.
                        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) {
                            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true)) writer.Write(text.ToString());
                            stream.Flush(true);
                        }
                        Cache.Remove(path);   // re-read (cheap) on the next query
                    }
                    return fresh.Count;
                }
            } catch (Exception e) {
                // A failed archive write must never fail the scan itself. The reason goes to
                // stderr (invisible for the GUI, visible for CLI runs and diagnosis).
                try { Console.Error.WriteLine("codeusagemonit archive append failed: " + e.Message); } catch { }
                return -1;
            }
        }

        // Archived rows for one agent with Hour strictly before `beforeUtc` (the hot
        // window's edge), deduplicated by bucket key. Duplicate keys (two writers racing)
        // keep the copy with the larger usage.
        public static List<ArchiveRow> RowsFor(string agent, DateTime beforeUtc) {
            var rows = new Dictionary<string, ArchiveRow>(StringComparer.Ordinal);
            if (!Enabled || String.IsNullOrEmpty(agent)) return rows.Values.ToList();
            lock (Gate) {
                foreach (Cached cached in LoadAll().Values) {
                    foreach (ArchiveRow row in cached.Rows) {
                        if (row.Agent != agent || row.Hour >= beforeUtc) continue;
                        ArchiveRow previous;
                        if (!rows.TryGetValue(row.Key, out previous) || Score(row) > Score(previous)) rows[row.Key] = row;
                    }
                }
            }
            return rows.Values.ToList();
        }
        private static double Score(ArchiveRow row) { return row.I + row.C + row.W + row.O + row.X + row.R; }

        // The hot index plus the archived projection: a NEW in-memory LogIndex (never
        // persisted) whose extra LogFile answers for the hours the retention window no
        // longer holds. The hot index is not mutated. Archive rows are only used for hours
        // before the hot window's edge AND whose bucket key the hot index does not hold, so
        // hot values always win and no hour is ever counted twice — also at the 33-day
        // boundary, where Finish may briefly have company for the same hour.
        public static LogIndex MergeInto(LogIndex hot, string agent, DateTime nowUtc) {
            if (hot == null || !Enabled || String.IsNullOrEmpty(agent)) return hot;
            List<ArchiveRow> rows = RowsFor(agent, LogReader.HotFrom(nowUtc));
            if (rows.Count == 0) return hot;
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (LogFile file in hot.Files) {
                // 只统计本机文件（P1）：device:* 的导入数据与本机归档是不同来源，同键并存
                // 各自成立（导入与本机的同量去重是 Attach 的职责）；导入行不得屏蔽本机
                // 归档行，否则设备数据在位的那些小时会被静默漏计。
                if (Devices.IsImported(file)) continue;
                foreach (string key in file.Hours.Keys) present.Add(key);
            }
            var archived = new LogFile { Name = "archive:" + agent };
            foreach (ArchiveRow row in rows) if (!present.Contains(row.Key)) LogIndex.Add(archived, row.Key, row.Bucket(), 1);
            if (archived.Hours.Count == 0) return hot;
            var merged = new LogIndex { CoveredFrom = hot.CoveredFrom, Updated = hot.Updated, Pricing = hot.Pricing, Seen = hot.Seen };
            merged.Files.AddRange(hot.Files);
            merged.Files.Add(archived);
            return merged;
        }

        // MergeInto for a whole per-agent index dictionary (what AppMonitor and the CLI
        // build from WithDevices / Devices.Attach before deriving history or rendering).
        public static Dictionary<string, LogIndex> MergeAll(Dictionary<string, LogIndex> indexes, DateTime nowUtc) {
            if (indexes == null || indexes.Count == 0 || !Enabled) return indexes;
            var result = new Dictionary<string, LogIndex>(indexes);
            foreach (string agent in indexes.Keys.ToList()) result[agent] = MergeInto(indexes[agent], agent, nowUtc);
            return result;
        }

        private static void Collect(Dictionary<string, Bucket> pending, string key, Bucket bucket, DateTime oldest) {
            if (bucket == null || key == null || key.Length < 11) return;
            DateTime hour = ParseHour(key);
            if (hour == DateTime.MinValue || hour < oldest) return;
            // 克隆入列（P0）：归档只读索引。同一个桶键可以由多个会话文件各持一份（恢复出
            // 的同族会话），累加必须落在本方法的私有克隆上——若把第一个文件的活桶放进
            // pending 并就地累加，ScanAll 会把被改写的索引持久化，每小时总量逐轮翻倍。
            if (!pending.TryGetValue(key, out Bucket sum)) pending[key] = sum = new Bucket();
            sum.Add(bucket, 1);   // Bucket.Add 只读 other、改写 this；sum 是私有克隆，活桶只被读取
        }
        private static DateTime ParseHour(string key) {
            DateTime at;
            return key != null && key.Length >= 10 && DateTime.TryParseExact(key.Substring(0, 10), "yyyyMMddHH", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at) ? at : DateTime.MinValue;
        }

        // Every archive file, keyed by path. Files are re-read only when length or mtime
        // changed (steady state: nothing); entries for deleted files are dropped.
        private static Dictionary<string, Cached> LoadAll() {
            var all = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);
            string[] files;
            try { files = Directory.Exists(Folder()) ? Directory.GetFiles(Folder(), "usage-*.jsonl") : new string[0]; } catch { files = new string[0]; }
            foreach (string path in files) {
                Cached cached;
                DateTime modified; long length;
                try { FileInfo info = new FileInfo(path); if (!info.Exists) continue; modified = info.LastWriteTimeUtc; length = info.Length; }
                catch { continue; }
                if (!Cache.TryGetValue(path, out cached) || cached.Length != length || cached.Modified != modified) {
                    cached = new Cached { Modified = modified, Length = length, Rows = new List<ArchiveRow>() };
                    try {
                        // One bad line costs one row, never the file (damage tolerance).
                        foreach (string line in File.ReadLines(path)) { ArchiveRow row; if (ArchiveRow.TryParse(line, out row)) cached.Rows.Add(row); }
                    } catch { continue; }   // unreadable right now: keep the old cache, retry later
                    Cache[path] = cached;
                }
                all[path] = cached;
            }
            foreach (string stale in Cache.Keys.Where(k => !all.ContainsKey(k)).ToList()) Cache.Remove(stale);
            return all;
        }
    }
}
