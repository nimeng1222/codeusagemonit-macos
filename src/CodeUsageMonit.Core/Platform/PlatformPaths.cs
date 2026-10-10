// macOS path candidates for data that other platforms keep under user profile folders.
// Cursor's editor session database (state.vscdb) lives under
// ~/Library/Application Support on macOS — %APPDATA% (SpecialFolder.ApplicationData)
// would wrongly map to ~/.config under .NET on Unix.
using System;
using System.IO;

namespace CodeUsageMonit {
    internal static class PlatformPaths {
        public static string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }
        // Cursor User\globalStorage\state.vscdb (roaming %APPDATA% on Windows).
        public static string CursorStateDb {
            get { return Path.Combine(Home, "Library", "Application Support", "Cursor", "User", "globalStorage", "state.vscdb"); }
        }
        public static string CursorStorageDir { get { return Path.GetDirectoryName(CursorStateDb); } }
    }
}
