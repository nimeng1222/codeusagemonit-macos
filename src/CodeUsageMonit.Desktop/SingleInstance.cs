// 单实例（macOS 走 Unix domain socket）。
// 套接字路径按实例名（--demo / --side-by-side 各自独立）；已有实例时第二进程发送
// "show" 或 "quit" 后退出；监听线程把命令投递到 UI 线程。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace CodeUsageMonit {
    internal static class SingleInstance {
        private static Socket listener;
        private static Thread thread;
        private static string path;
        private static Action<string> onCommand;

        public static string PathFor(string suffix) {
            string name = "codeusagemonit" + suffix + "-" + Environment.UserName + ".sock";
            return System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);
        }
        // 尝试成为第一实例：只有自己 Bind 成功（取得实例所有权）才返回 true，之后由 Listen
        // 接管命令；任何“已有实例在跑”的信号——探测连通、Bind 撞上并发起势的对手——都转发
        // command 并返回 false（入口 Program.Main 把 true 当启动 GUI 的许可）。
        // 旧实现的两个洞（review #4）：① Bind 抛异常时 `return Send(command)`，补发成功却
        // 返回 true，双实例；② 探测失败就无条件删套接字文件再 Bind，并发起动时会把对方刚
        // Bind 好的活套接字删掉，两个 listener 同时存活。现在：探测失败先短暂重试（对端可能
        // 正在起势，或监听积压未消化导致 connect 被瞬时拒绝），仍失败才在跨进程文件锁保护下
        // 清残留并 Bind；Bind 再冲突即按“已有实例”处理。
        public static bool Begin(string suffix, string command) {
            path = PathFor(suffix);
            if (Send(command)) return false;
            using (FileStream gate = AcquireGate(path + ".gate", 2000)) {
                // 拿到锁后再探两次：并发的另一个起动者可能刚完成 Bind（这条重试同时覆盖
                // “对端活着但积压未消化”的瞬时拒绝，避免把活套接字当残留删掉）。
                for (int attempt = 0; ; attempt++) {
                    if (Send(command)) return false;
                    if (attempt >= 1) break;
                    Thread.Sleep(60);
                }
                // 两次都连不上：套接字文件是残留死文件（上次未 Done 就退出），删除重 Bind。
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                try {
                    listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    listener.Bind(new UnixDomainSocketEndPoint(path));
                    listener.Listen(2);
                    return true;
                } catch {
                    // 对方抢先 Bind 成功（刚刚起势）：按“已有实例”退出；补发只是尽力通知，
                    // 成败都不改变返回值。
                    Send(command);
                    return false;
                }
            }
        }
        // flock(2)（libSystem）操作码与 errno，按本机 SDK 头文件 sys/fcntl.h:485-488 与
        // sys/errno.h:129-130：LOCK_EX=0x02、LOCK_NB=0x04、EWOULDBLOCK==EAGAIN==35。
        private const int LockExclusive = 0x2, LockNonBlocking = 0x4, ErrnoWouldBlock = 35;
        [DllImport("libSystem.dylib", SetLastError = true)]
        [SupportedOSPlatform("macos")]
        private static extern int flock(int fileDescriptor, int operation);
        // 跨进程 gate（套接字同目录的 .gate 文件）：flock(2) 排它锁真正跨进程互斥。
        // 旧实现用 FileShare.None——那只是 .NET 的进程内簿记，Unix 上既不拦其它进程打开
        // 同一文件、也没有任何内核锁，gate 从未跨进程生效（.NET 的 FileShare 在非 Windows
        // 平台不强制）。现在：FileShare.ReadWrite 打开（避免与其它持锁/等待者互相干扰打开
        // 这一步）→ SafeFileHandle 的 fd 上 flock(LOCK_EX|LOCK_NB) → 返回 -1 且 errno=
        // EWOULDBLOCK（锁被别的起动进程持有）则关闭重试，超时返回 null（调用方失去的只是
        // “串行化清残留”这层额外保护，返回值正确性仍由 Bind 冲突分支兜底）。锁文件常驻不删
        // ——删除本身有经典竞态（删的瞬间另一个等待者已按旧 inode 拿到锁），零字节文件留在
        // 用户 temp 目录无害，交给系统清理。锁生命周期 = FileStream 生命周期：Dispose 关 fd
        // 即自动放锁（using 结构天然正确）。internal 是为让 --gate-harness 实验复用同一条
        // 拿锁路径，生产调用只有 Begin 一处。
        internal static FileStream AcquireGate(string gatePath, int timeoutMilliseconds) {
            if (!OperatingSystem.IsMacOS()) return null; // flock P/Invoke 仅声明于 macOS；其余平台按“没拿到锁”退化
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (true) {
                FileStream stream = null;
                try {
                    stream = new FileStream(gatePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                    var handle = stream.SafeFileHandle;
                    bool referenced = false;
                    try { handle.DangerousAddRef(ref referenced); } catch { stream.Dispose(); return null; }
                    int locked, error;
                    try {
                        locked = flock((int)handle.DangerousGetHandle(), LockExclusive | LockNonBlocking);
                        error = Marshal.GetLastWin32Error(); // 必须紧随 P/Invoke 读取，否则可能被覆盖
                    } finally { if (referenced) handle.DangerousRelease(); }
                    if (locked == 0) return stream; // 锁到手；随 FileStream 释放自动解除
                    stream.Dispose();
                    if (error != ErrnoWouldBlock) return null; // 非竞争性失败（权限/IO 等）：按拿不到锁退化
                } catch (IOException) { try { if (stream != null) stream.Dispose(); } catch { } }
                catch { try { if (stream != null) stream.Dispose(); } catch { } return null; }
                if (DateTime.UtcNow >= until) return null;
                Thread.Sleep(25);
            }
        }
        // 发送一次性命令；对端不在线返回 false。
        public static bool Send(string suffix, string command) {
            path = PathFor(suffix);
            return Send(command);
        }
        // 连接超时（探测/补发共用）：UDS connect 没有默认超时，对端 backlog 满且实现为
        // 阻塞语义时会卡住起动进程。超时按“对端不在线”处理返回 false。
        private const int ConnectTimeoutMilliseconds = 2000;
        private static bool Send(string command) {
            try {
                using (var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)) {
                    client.Blocking = true;
                    // BeginConnect + WaitOne：超时后直接返回 false，using 释放 socket 会中止
                    // 挂起的连接（不调 EndConnect，避免在未完成操作上阻塞等待）。
                    var pending = client.BeginConnect(new UnixDomainSocketEndPoint(path), null, null);
                    if (!pending.AsyncWaitHandle.WaitOne(ConnectTimeoutMilliseconds)) return false;
                    client.EndConnect(pending);
                    client.Send(Encoding.UTF8.GetBytes(command + "\n"), SocketFlags.None);
                    return true;
                }
            } catch { return false; }
        }
        // 监听已绑定的套接字；每条命令回调到 UI 线程。
        public static void Listen(Action<string> command) {
            onCommand = command;
            if (listener == null) return;
            thread = new Thread(AcceptLoop) { IsBackground = true, Name = "codeusagemonit-single-instance" };
            thread.Start();
        }
        private static void AcceptLoop() {
            var buffer = new byte[64];
            while (listener != null) {
                Socket client;
                try { client = listener.Accept(); } catch { return; }
                try {
                    int read = 0, total = 0;
                    while (total < buffer.Length && (read = client.Receive(buffer, total, buffer.Length - total, SocketFlags.None)) > 0) {
                        total += read;
                        if (Array.IndexOf(buffer, (byte)'\n', 0, total) >= 0) break;
                    }
                    string commandLine = Encoding.UTF8.GetString(buffer, 0, Math.Max(0, total)).Trim();
                    if (commandLine.Length > 0) {
                        var handler = onCommand;
                        if (handler != null) DispatcherPost(() => handler(commandLine));
                    }
                } catch { } finally { try { client.Dispose(); } catch { } }
            }
        }
        private static void DispatcherPost(Action action) {
            try { Avalonia.Threading.Dispatcher.UIThread.Post(action); } catch { }
        }
        // 退出前清掉套接字文件（防止残留死文件影响下次启动——Bind 前也会清理）。
        public static void Done() {
            // 注意：不要在这里 listener.Close()——.NET 的 SafeSocketHandle 会自旋等待挂起的
            // Accept 返回，而 macOS 上 shutdown(listen fd) 不会唤醒 accept(2)，两边互等即死锁
            // （UI 线程永久卡在 Thread.Sleep 等待循环）。Done 只在进程退出路径调用，
            // fd 由 OS 回收；监听线程是后台线程，随进程结束。
            listener = null;
            try { if (path != null && File.Exists(path)) File.Delete(path); } catch { }
        }
    }
    // harness 集（生产路径不触及）：--instance-harness / --instance-harness-child 并发起动
    // 竞赛（review #4 验收），--gate-harness / --gate-holder / --gate-contender 验证 gate
    // 的 flock 真跨进程互斥（flock 落地验收）。父进程拉起子进程，断言后打印证据并清理。
    internal static class SingleInstanceHarness {
        public static int Run(string[] args) {
            int children = Value(args, "--children", 6), rounds = Value(args, "--rounds", 6);
            string exe = Environment.ProcessPath;
            if (String.IsNullOrEmpty(exe) || !File.Exists(exe)) { Console.Error.WriteLine("harness: cannot locate own executable"); return 2; }
            int failedRounds = 0;
            for (int round = 1; round <= rounds; round++) {
                // macOS 域套接字路径上限 104 字符：唯一后缀必须短（8 位随机即可，轮末清理）。
                string suffix = ".H" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string socket = SingleInstance.PathFor(suffix);
                try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(socket)); } catch { }
                bool stale = round % 2 == 1;
                if (stale) { try { File.WriteAllText(socket, "stale-dead-file"); } catch { } } // 残留死文件（非套接字，connect 必拒）
                var results = new List<bool>();
                var processes = new List<Process>();
                try {
                    for (int i = 0; i < children; i++) {
                        var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true };
                        info.ArgumentList.Add("--instance-harness-child");
                        info.ArgumentList.Add(suffix);
                        processes.Add(Process.Start(info));
                    }
                    foreach (Process process in processes) {
                        string line = process.StandardOutput.ReadToEnd().Trim();
                        process.WaitForExit();
                        results.Add(line.EndsWith("True", StringComparison.Ordinal));
                        Console.WriteLine("  child: '" + line + "' exit=" + process.ExitCode);
                        if (process.ExitCode != 0 || line.Length == 0) results[results.Count - 1] = false;
                    }
                } catch (Exception e) { Console.WriteLine("  spawn error: " + e.Message); }
                finally { foreach (Process process in processes) { try { process.Dispose(); } catch { } } }
                int winners = results.Count(won => won);
                bool ok = results.Count == children && winners == 1;
                if (!ok) failedRounds++;
                Console.WriteLine("round " + round + (stale ? " (stale socket file)" : "") + ": " + winners + " winner / " + (results.Count - winners) + " loser of " + children + " -> " + (ok ? "OK" : "FAILED"));
                try { if (File.Exists(socket)) File.Delete(socket); } catch { }
                try { if (File.Exists(socket + ".gate")) File.Delete(socket + ".gate"); } catch { }
            }
            Console.WriteLine(failedRounds == 0
                ? "PASSED: " + rounds + " rounds x " + children + " concurrent starters, exactly one Begin=true each"
                : "FAILED: " + failedRounds + "/" + rounds + " rounds with wrong winner count");
            return failedRounds == 0 ? 0 : 1;
        }
        // 子进程：同抢一个套接字，报告 Begin 结果。赢家模拟真实首实例（accept 循环在线，
        // 存活窗口覆盖其余进程的探测/补发），输家必须立即返回且未持有任何 listener。
        public static int Child(string suffix) {
            if (String.IsNullOrEmpty(suffix)) { Console.Error.WriteLine("harness-child: missing suffix"); return 2; }
            bool won = SingleInstance.Begin(suffix, "show");
            Console.WriteLine("BEGIN=" + (won ? "True" : "False"));
            if (won) {
                SingleInstance.Listen(_ => { });
                Thread.Sleep(1500);
                SingleInstance.Done();
            }
            return 0;
        }
        // gate 互斥实验（--gate-harness，flock 落地验收；生产路径不触及）：证明 AcquireGate
        // 的锁真正跨进程。流程：父进程拉起持锁者，等它打印 LOCKED（此刻锁已被进程 A 持有）
        // 再拉起竞争者；竞争者起势后打印 READY，父进程立刻放行（GO）并记时刻。竞争者从 GO
        // 到拿到锁的等待必须 ≈ 持锁者剩余持有时间（≥ MinWaitMilliseconds）——若锁不跨进程
        // （旧 FileShare.None 在 Unix 的实况）则 ≈0ms 立即到手。对照段：无人持锁时同一
        // AcquireGate 应在 <500ms 内拿到，排除“锁永远拿不到”的假阳性。打印各段时间作为证据。
        private const int GateHolderMilliseconds = 4000, GateMinWaitMilliseconds = 1000, GateControlMaxMilliseconds = 500;
        public static int GateHarnessRun() {
            string suffix = ".H" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string gate = SingleInstance.PathFor(suffix) + ".gate";
            string exe = Environment.ProcessPath;
            if (String.IsNullOrEmpty(exe) || !File.Exists(exe)) { Console.Error.WriteLine("gate-harness: cannot locate own executable"); return 2; }
            try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(gate)); } catch { }
            try { if (File.Exists(gate)) File.Delete(gate); } catch { }
            Process holder = null, contender = null;
            try {
                var holderInfo = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true };
                holderInfo.ArgumentList.Add("--gate-holder"); holderInfo.ArgumentList.Add(suffix);
                holder = Process.Start(holderInfo);
                string locked = holder.StandardOutput.ReadLine(); // 阻塞到持锁者确认锁在手
                if (locked != "LOCKED") { Console.Error.WriteLine("gate-harness: holder did not report LOCKED (got '" + (locked ?? "null") + "')"); return 1; }
                long lockedAt = Environment.TickCount64;
                Console.WriteLine("holder: LOCKED confirmed, will hold flock for " + GateHolderMilliseconds + "ms");

                var contenderInfo = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardInput = true };
                contenderInfo.ArgumentList.Add("--gate-contender"); contenderInfo.ArgumentList.Add(suffix);
                contender = Process.Start(contenderInfo);
                string ready = contender.StandardOutput.ReadLine();
                if (ready != "READY") { Console.Error.WriteLine("gate-harness: contender did not report READY (got '" + (ready ?? "null") + "')"); return 1; }
                long releasedIn = Math.Max(0, lockedAt + GateHolderMilliseconds - Environment.TickCount64); // GO 时持锁者还剩多久放锁
                contender.StandardInput.WriteLine("GO");
                long goAt = Environment.TickCount64;
                string verdict = contender.StandardOutput.ReadLine();
                long afterGo = Environment.TickCount64 - goAt;
                Console.WriteLine("contender: '" + verdict + "' (holder still had ~" + releasedIn + "ms of hold left at GO; observed response " + afterGo + "ms)");

                // 对照：竞争者已放锁，无人持锁时立即拿到。
                long controlStart = Environment.TickCount64;
                FileStream control = SingleInstance.AcquireGate(gate, 2000);
                long controlWaited = Environment.TickCount64 - controlStart;
                if (control != null) { try { control.Dispose(); } catch { } }
                Console.WriteLine("control: uncontended acquire waited " + controlWaited + "ms");

                bool contenderOk = verdict != null && verdict.StartsWith("WAITED=", StringComparison.Ordinal)
                    && Int64.TryParse(verdict.Substring("WAITED=".Length), out long waited)
                    && waited >= GateMinWaitMilliseconds && waited <= GateHolderMilliseconds + 5000;
                bool controlOk = control != null && controlWaited < GateControlMaxMilliseconds;
                int holderExit = -1;
                try { if (holder.WaitForExit(10000)) holderExit = holder.ExitCode; } catch { }
                bool ok = contenderOk && controlOk && holderExit == 0;
                Console.WriteLine(ok
                    ? "PASSED: gate is cross-process mutex — contender blocked " + verdict.Substring("WAITED=".Length) + "ms while holder kept flock, uncontended acquire " + controlWaited + "ms"
                    : "FAILED: contenderOk=" + contenderOk + " controlOk=" + controlOk + " holderExit=" + holderExit);
                return ok ? 0 : 1;
            } catch (Exception e) { Console.WriteLine("  gate-harness error: " + e.Message); return 1; }
            finally {
                foreach (Process process in new[] { holder, contender }) {
                    if (process == null) continue;
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    try { process.Dispose(); } catch { }
                }
                try { if (File.Exists(gate)) File.Delete(gate); } catch { }
            }
        }
        // 持锁者子进程：拿到 gate 即上报 LOCKED（父进程以行读取同步），持有固定时长后放锁。
        public static int GateHolder(string suffix) {
            if (String.IsNullOrEmpty(suffix)) { Console.Error.WriteLine("gate-holder: missing suffix"); return 2; }
            FileStream gate = SingleInstance.AcquireGate(SingleInstance.PathFor(suffix) + ".gate", 2000);
            if (gate == null) { Console.Error.WriteLine("gate-holder: cannot acquire gate"); return 1; }
            Console.WriteLine("LOCKED"); Console.Out.Flush();
            Thread.Sleep(GateHolderMilliseconds);
            Console.WriteLine("RELEASED");
            try { gate.Dispose(); } catch { }
            return 0;
        }
        // 竞争者子进程：起势即上报 READY，等父进程 GO 后尝试拿锁，上报从 GO 到获锁的毫秒数。
        // 放锁后再打印（try 失败也先释放），父进程的对照段不必等它退出。
        public static int GateContender(string suffix) {
            if (String.IsNullOrEmpty(suffix)) { Console.Error.WriteLine("gate-contender: missing suffix"); return 2; }
            Console.WriteLine("READY"); Console.Out.Flush();
            if (Console.ReadLine() != "GO") { Console.Error.WriteLine("gate-contender: no GO on stdin"); return 2; }
            long start = Environment.TickCount64;
            FileStream gate = SingleInstance.AcquireGate(SingleInstance.PathFor(suffix) + ".gate", 10000);
            long waited = Environment.TickCount64 - start;
            if (gate != null) { try { gate.Dispose(); } catch { } }
            Console.WriteLine(gate == null ? "TIMEOUT" : "WAITED=" + waited);
            return gate == null ? 1 : 0;
        }
        private static int Value(string[] args, string name, int fallback) {
            int at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length && Int32.TryParse(args[at + 1], out int value) && value > 0 ? value : fallback;
        }
    }
}
