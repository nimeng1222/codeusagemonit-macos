// Process discovery for the local Antigravity language server, replacing the Windows
// combination of WMI Win32_Process (name, command line, GetOwnerSid) and iphlpapi
// GetExtendedTcpTable (listening ports per PID). The macOS implementation parses
// `ps -eo pid=,user=,args=` for the process list and `lsof -nP -iTCP -sTCP:LISTEN -a -p
// <pid>` for the listening ports. Owner filtering uses the user name so only
// processes of the current user are counted.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodeUsageMonit {
    internal interface IProcessDiscovery {
        // (pid, --csrf_token value) of Antigravity language server processes owned by the
        // current user, best first.
        List<KeyValuePair<uint, string>> FindAntigravityServers();
        // TCP ports the process is listening on.
        List<int> ListenerPorts(uint pid);
    }

    internal sealed class MacProcessDiscovery : IProcessDiscovery {
        private static readonly Regex Csrf = new Regex("--(?:csrf_token|csrf-token)(?:=|\\s+)\"?([A-Za-z0-9._-]{8,256})", RegexOptions.Compiled);
        private static readonly Regex Port = new Regex(":(\\d{1,5})\\s+\\(LISTEN\\)", RegexOptions.Compiled);

        public List<KeyValuePair<uint, string>> FindAntigravityServers() {
            var servers = new List<KeyValuePair<uint, string>>();
            string user = Environment.UserName;
            // One line per process: "  123 ningmeng /…/language_server --csrf_token=…".
            foreach (string line in Run("ps", "-eo", "pid=,user=,args=", 5000).Split('\n')) {
                string text = line.Trim();
                if (text.Length == 0) continue;
                int first = text.IndexOf(' ');
                if (first <= 0) continue;
                uint pid;
                if (!UInt32.TryParse(text.Substring(0, first), out pid) || pid == 0) continue;
                string rest = text.Substring(first + 1).TrimStart();
                int second = rest.IndexOf(' ');
                string owner = second < 0 ? rest : rest.Substring(0, second);
                string command = second < 0 ? "" : rest.Substring(second + 1);
                // 同用户过滤：只统计当前用户启动的进程。
                if (!String.Equals(owner, user, StringComparison.Ordinal)) continue;
                string executable = command.Split(' ').FirstOrDefault() ?? "";
                bool antigravity = command.IndexOf("antigravity", StringComparison.OrdinalIgnoreCase) >= 0 || executable.EndsWith("/agy", StringComparison.OrdinalIgnoreCase) || executable.EndsWith("/agy.exe", StringComparison.OrdinalIgnoreCase);
                if (!antigravity) continue;
                Match match = Csrf.Match(command);
                if (!match.Success) continue;
                servers.Add(new KeyValuePair<uint, string>(pid, match.Groups[1].Value));
            }
            return servers;
        }

        public List<int> ListenerPorts(uint pid) {
            var ports = new List<int>();
            foreach (string line in Run("lsof", "-nP", "-iTCP", "-sTCP:LISTEN", "-a", "-p", pid.ToString(), 5000).Split('\n')) {
                Match match = Port.Match(line);
                int port;
                if (match.Success && Int32.TryParse(match.Groups[1].Value, out port) && port > 0 && port < 65536) ports.Add(port);
            }
            return ports.Distinct().ToList();
        }

        private static string Run(string file, params object[] argumentsWithTimeout) {
            try {
                var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                for (int i = 0; i < argumentsWithTimeout.Length - 1; i++) start.ArgumentList.Add(Convert.ToString(argumentsWithTimeout[i]));
                int timeout = Convert.ToInt32(argumentsWithTimeout[argumentsWithTimeout.Length - 1]);
                using (var process = Process.Start(start)) {
                    if (process == null) return "";
                    string output = process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(timeout)) { try { process.Kill(); } catch { } return ""; }
                    return output;
                }
            } catch { return ""; }
        }
    }

    internal static class ProcessDiscovery {
        // Injectable for tests; the platform default is the ps/lsof implementation.
        public static IProcessDiscovery Current = new MacProcessDiscovery();
    }
}
