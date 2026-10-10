// Time zone ids. Saved history and tests may use the Windows id "China Standard Time";
// on macOS TimeZoneInfo only resolves IANA ids. Find() accepts either form (via
// TryConvertWindowsIdToIanaId, with a small fallback table) so a saved history.json
// written on Windows keeps resolving here, and new saves store whatever id the
// platform natively understands ("Asia/Shanghai"), which Merge() then treats as a
// fresh history — the same behaviour as a zone change.
using System;

namespace CodeUsageMonit {
    public static class TimeZones {
        public static TimeZoneInfo Find(string id) {
            if (String.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
            try {
                string iana;
                if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out iana)) {
                    try { return TimeZoneInfo.FindSystemTimeZoneById(iana); } catch { }
                }
            } catch { }
            // ICU data is missing or the id is unknown: the one zone this app cares about.
            if (id == "China Standard Time") { try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); } catch { } }
            if (String.Equals(id, "Asia/Shanghai", StringComparison.OrdinalIgnoreCase)) { try { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); } catch { } }
            return TimeZoneInfo.Local;
        }
        // Tests and the default reporting zone ("China Standard Time").
        public static TimeZoneInfo China { get { return Find("China Standard Time"); } }
    }
}
