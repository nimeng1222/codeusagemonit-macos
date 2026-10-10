// LaunchCli 隔离 harness（仅 --launchcli-harness 进入，生产路径不触及；review #8 验收）：
// 在 PATH 前部放一个合成 osascript（把收到的 argv 记录到文件后退出 0，绝不启动真实
// Terminal），反射调用私有的 MonitorPanel.LaunchCli，断言：
//   ① 脚本提交成功（LaunchCli 返回 true）；
//   ② 取出 do script 的命令串，经 AppleScript 还原（Terminal 交给 shell 的正是还原后的
//      串）后写入临时脚本、用真实 /bin/sh 解析执行——带空格、单引号、$ 的路径与参数
//      必须逐字还原为两个完整的词（旧实现在此被空格截断成多段）。
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace CodeUsageMonit {
    internal static class LaunchCliHarness {
        private static void Require(bool value) { if (!value) throw new Exception("Assertion failed"); }
        public static int Run() {
            string dir = Path.Combine(Path.GetTempPath(), "cum-launchcli-" + Guid.NewGuid().ToString("N"));
            string bin = Path.Combine(dir, "bin"), argvLog = Path.Combine(dir, "argv.tsv"), run = Path.Combine(dir, "do.sh");
            Directory.CreateDirectory(bin);
            // 合成 osascript：逐参数记录（换行分隔），退出 0。
            File.WriteAllText(Path.Combine(bin, "osascript"), "#!/bin/sh\nfor a in \"$@\"; do printf '%s\\n' \"$a\" >> \"$CUM_ARGV\"; done\nexit 0\n");
            try { File.SetUnixFileMode(Path.Combine(bin, "osascript"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
            // 空格 + 单引号 + $：旧实现会被空格截断、单引号破坏 shell 引用。
            string path = "/opt/my tools/o'brien$v1";
            string previousPath = Environment.GetEnvironmentVariable("PATH"), previousArgv = Environment.GetEnvironmentVariable("CUM_ARGV");
            try {
                Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + previousPath);
                Environment.SetEnvironmentVariable("CUM_ARGV", argvLog);
                var method = typeof(MonitorPanel).GetMethod("LaunchCli", BindingFlags.NonPublic | BindingFlags.Static);
                Require(method != null);
                bool submitted = (bool)method.Invoke(null, new object[] { path, new[] { "login" } }); // 新签名：参数数组逐元素 Quote
                string[] argv = File.Exists(argvLog) ? File.ReadAllLines(argvLog) : new string[0];
                Require(submitted && argv.Length == 2 && argv[0] == "-e");
                const string prefix = "tell application \"Terminal\" to do script \"";
                Require(argv[1].StartsWith(prefix, StringComparison.Ordinal) && argv[1].EndsWith("\"", StringComparison.Ordinal));
                // AppleScript 还原（\" → "、\\ → \），得到 Terminal 交给用户 shell 的命令串。
                string command = argv[1].Substring(prefix.Length, argv[1].Length - prefix.Length - 1).Replace("\\\"", "\"").Replace("\\\\", "\\");
                // 同 Terminal 的 do script：把命令串交给真实 shell 解析执行，断言逐字还原。
                File.WriteAllText(run, "printf '%s\\n' " + command + "\n");
                var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true };
                info.ArgumentList.Add(run);
                using (var process = Process.Start(info)) {
                    string[] words = process.StandardOutput.ReadToEnd().Split('\n');
                    process.WaitForExit();
                    Require(process.ExitCode == 0 && words.Length >= 2 && words[0] == path && words[1] == "login");
                }
                Console.WriteLine("do script: " + command);
                Console.WriteLine("PASSED: LaunchCli delivered '" + path + "' + login as two literal shell words (spaces/quote/$ preserved)");
                return 0;
            } catch (Exception e) {
                Console.Error.WriteLine("FAILED: " + e.Message);
                return 1;
            } finally {
                Environment.SetEnvironmentVariable("PATH", previousPath);
                Environment.SetEnvironmentVariable("CUM_ARGV", previousArgv);
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
