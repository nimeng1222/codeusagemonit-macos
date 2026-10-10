using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodeUsageMonit {
    // Interface language. The Chinese text in the code is the key and English comes from
    // I18nTable (gettext-style). Text the code builds from pieces ("近 " + n + " 天") is
    // translated piece by piece: every table key of two or more characters found in it is
    // replaced, longest first. Pages are built in Chinese and Localize translates a finished
    // element tree in place, remembering each original so switching back restores it.
    // Stored data (quota labels in the cache, logs) stays Chinese; only what is shown changes.
    public static class I18n {
        private static string language = "zh";
        // "zh" or "en"; settings store "auto" | "zh" | "en".
        public static string Language { get { return language; } }
        public static bool English { get { return language == "en"; } }
        public static void Use(string setting) { language = Resolve(setting); if (English) everEnglish = true; }
        // Chinese trees need no pass until English has been shown once (then they are restored).
        // Localize（UI 层，Desktop 程序集的 I18nUi）读取它。
        private static bool everEnglish;
        public static bool EverEnglish { get { return everEnglish; } }
        public static string Resolve(string setting) {
            if (setting == "zh" || setting == "en") return setting;
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh" : "en";
        }
        // Culture for dates and month / weekday names.
        public static CultureInfo Culture { get { return CultureInfo.GetCultureInfo(English ? "en-US" : "zh-CN"); } }

        public static string T(string zh) {
            if (!English || String.IsNullOrEmpty(zh) || !HasChinese(zh)) return zh;
            string en; if (I18nTable.En.TryGetValue(zh, out en)) return en;
            lock (cache) {
                if (cache.TryGetValue(zh, out en)) return en;
                en = zh;
                foreach (string key in Fragments) if (en.IndexOf(key, StringComparison.Ordinal) >= 0) en = en.Replace(key, I18nTable.En[key]);
                if (cache.Count > 4000) cache.Clear();
                cache[zh] = en;
                return en;
            }
        }
        public static string T(string zh, params object[] args) {
            try { return String.Format(CultureInfo.InvariantCulture, T(zh), args); } catch (FormatException) { return String.Format(CultureInfo.InvariantCulture, zh, args); }
        }
        public static bool HasChinese(string s) { foreach (char c in s) if (c >= '㐀' && c <= '鿿') return true; return false; }
        private static readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.Ordinal);
        private static string[] fragments;
        private static string[] Fragments {
            get { return fragments ?? (fragments = I18nTable.En.Keys.Where(k => k.Length >= 2 && HasChinese(k)).OrderByDescending(k => k.Length).ThenBy(k => k, StringComparer.Ordinal).ToArray()); }
        }

        // ── Translating a built element tree ──────────────────────────────
        // 就地翻译（Localize/Visit/Swap）在同命名空间的
        // Desktop 程序集（I18nUi.Localize）实现：遍历逻辑树，翻译 TextBlock.Text / Run.Text、
        // 字符串 Content / Header、ToolTip、ContextMenu 与 AutomationProperties.Name，
        // 并用本类 EverEnglish 决定是否需要扫描（见 I18nUi）。
    }
}

