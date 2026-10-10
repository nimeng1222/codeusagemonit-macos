// 开机自启动。Windows 写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run（App.cs:449-450）；
// macOS 用 LaunchAgent：~/Library/LaunchAgents/com.codeusagemonit.plist（RunAtLoad），
// 程序参数 --background（只驻留托盘）。SetEnabled 返回是否写成功；失败不打断设置保存。
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace CodeUsageMonit {
    public static class AutoStart {
        public const string AppId = "codeusagemonit";
        // 当前平台是否实现了自启动。
        public static bool Supported { get { return OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(); } }
        private static string PlistPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", AppId + ".plist"); } }
        public static bool IsEnabled() {
            if (OperatingSystem.IsWindows()) return RegistryRunEntryExists();
            if (!OperatingSystem.IsMacOS()) return false;
            try { return File.Exists(PlistPath); } catch { return false; }
        }
        public static bool SetEnabled(bool enable) {
            if (OperatingSystem.IsWindows()) return SetWindowsRun(enable);
            if (!OperatingSystem.IsMacOS()) return false;
            try {
                if (!enable) { if (File.Exists(PlistPath)) { Run("launchctl", "unload", PlistPath); File.Delete(PlistPath); } return true; }
                string[] arguments = LaunchArguments();
                if (arguments == null) return false;
                Directory.CreateDirectory(Path.GetDirectoryName(PlistPath));
                File.WriteAllText(PlistPath, Plist(arguments), new UTF8Encoding(false));
                return File.Exists(PlistPath);
            } catch { return false; }
        }
        // 可再启动的命令行：apphost（bin/.../codeusagemonit）自己就是入口；dotnet <app>.dll
        // 形式下 MainModule 指向 dotnet host，LaunchAgent 必须写成 [dotnet, 入口DLL, --background]，
        // 否则只会再启动 dotnet 本体、应用起不来。两个形态见 LaunchArguments(mainModule, entry)。
        private static string[] LaunchArguments() {
            return LaunchArguments(Process.GetCurrentProcess().MainModule?.FileName, EntryAssemblyLocation());
        }
        // 入口程序集的托管 DLL 路径（apphost 与 dotnet host 两种形态下都指向同一 DLL；
        // 单文件发布为空串）。
        private static string EntryAssemblyLocation() {
            try { return System.Reflection.Assembly.GetEntryAssembly()?.Location ?? ""; } catch { return ""; }
        }
        // 由主模块路径与入口程序集路径推导参数（合成路径自测覆盖两种形态）：
        //   · 入口镜像本身是 DLL → ["dotnet", 主模块DLL, --background]（保留既有行为）；
        //   · 主模块是 dotnet host（dotnet / dotnet.exe）→ [dotnet路径, 入口DLL, --background]，
        //     入口 DLL 缺失时返回 null（宁可让 SetEnabled 失败，也不写无法启动的 plist）；
        //   · 其余（apphost / 自包含）→ [主程序, --background]。
        public static string[] LaunchArguments(string mainModule, string entryAssembly) {
            if (String.IsNullOrEmpty(mainModule) || !File.Exists(mainModule)) return null;
            if (String.Equals(Path.GetExtension(mainModule), ".dll", StringComparison.OrdinalIgnoreCase)) return new[] { "dotnet", mainModule, "--background" };
            if (String.Equals(Path.GetFileNameWithoutExtension(mainModule), "dotnet", StringComparison.OrdinalIgnoreCase)) {
                if (String.IsNullOrEmpty(entryAssembly) || !File.Exists(entryAssembly) || !String.Equals(Path.GetExtension(entryAssembly), ".dll", StringComparison.OrdinalIgnoreCase)) return null;
                return new[] { mainModule, entryAssembly, "--background" };
            }
            return new[] { mainModule, "--background" };
        }
        private static string Plist(string[] programArguments) {
            var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = true, Encoding = new UTF8Encoding(false) };
            using (var sw = new StringWriter())
            using (var xml = XmlWriter.Create(sw, settings)) {
                xml.WriteStartElement("plist"); xml.WriteAttributeString("version", "1.0");
                xml.WriteStartElement("dict");
                Pair(xml, "Label", AppId);
                xml.WriteStartElement("key"); xml.WriteString("ProgramArguments"); xml.WriteEndElement();
                xml.WriteStartElement("array");
                foreach (string part in programArguments) { xml.WriteStartElement("string"); xml.WriteString(part); xml.WriteEndElement(); }
                xml.WriteEndElement();
                xml.WriteStartElement("key"); xml.WriteString("RunAtLoad"); xml.WriteEndElement();
                xml.WriteStartElement("true"); xml.WriteEndElement();
                Pair(xml, "ProcessType", "Interactive");
                xml.WriteEndElement();
                xml.WriteEndElement(); xml.WriteEndDocument(); xml.Flush();
                return sw.ToString();
            }
        }
        private static void Pair(XmlWriter xml, string key, string value) {
            xml.WriteStartElement("key"); xml.WriteString(key); xml.WriteEndElement();
            xml.WriteStartElement("string"); xml.WriteString(value); xml.WriteEndElement();
        }
        private static void Run(string file, params string[] args) {
            // ArgumentList 逐参传递：路径含空格（如用户名带空格）不会被拆。
            try {
                var info = new ProcessStartInfo(file) { UseShellExecute = false };
                foreach (string arg in args) info.ArgumentList.Add(arg);
                using (var p = Process.Start(info)) { if (p != null) p.WaitForExit(4000); }
            } catch { }
        }
        private static bool RegistryRunEntryExists() { try { using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) return key != null && key.GetValue(AppId) != null; } catch { return false; } }
        private static bool SetWindowsRun(bool enable) {
            // --side-by-side 的副本从不碰自启动项（由 Desktop 的调用方跳过）。
            try {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) {
                    if (enable) key.SetValue(AppId, "\"" + Path.Combine(Store.Root, "codeusagemonit.exe") + "\" --background");
                    else key.DeleteValue(AppId, false);
                }
                return true;
            } catch { return false; }
        }
    }
}
