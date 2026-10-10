using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;

namespace CodeUsageMonit.Desktop;

// 入口：--demo / --self-test / --probe / --quit /
// --background / --restart <pid> / --side-by-side。单实例与跨进程唤起走 Unix domain socket。
internal static class Program {
    public static bool SideBySide;
    public static bool PlanHarnessPending;
    public static bool DemoMode;
    public static bool Background;
    public static MonitorPanel Ui;

    [STAThread]
    public static int Main(string[] args) {
        if (args.Contains("--demo") && args.Contains("--probe")) return 2;
        // 单实例并发 harness（review #4 验收）：多进程同抢一个一次性套接字，恰好一个 Begin=true。
        if (args.Contains("--instance-harness")) return SingleInstanceHarness.Run(args);
        if (args.Contains("--instance-harness-child")) { int at = Array.IndexOf(args, "--instance-harness-child"); return SingleInstanceHarness.Child(at + 1 < args.Length ? args[at + 1] : ""); }
        // gate 互斥实验（flock 落地验收）：进程 A 持有 .gate，进程 B 必须等它放锁才能拿到。
        if (args.Contains("--gate-harness")) return SingleInstanceHarness.GateHarnessRun();
        if (args.Contains("--gate-holder")) { int at = Array.IndexOf(args, "--gate-holder"); return SingleInstanceHarness.GateHolder(at + 1 < args.Length ? args[at + 1] : ""); }
        if (args.Contains("--gate-contender")) { int at = Array.IndexOf(args, "--gate-contender"); return SingleInstanceHarness.GateContender(at + 1 < args.Length ? args[at + 1] : ""); }
        // LaunchCli quoting harness（review #8 验收）：合成 osascript + 真实 shell 往返。
        if (args.Contains("--launchcli-harness")) return LaunchCliHarness.Run();
        // Reveal harness（前台激活验收）：demo 模式拉起真实面板窗口，验证“隐藏 → Reveal”
        // 链路（窗口恢复可见 + NSApp 前台激活被执行）与托盘 toggle 语义；由 App 在
        // MonitorPanel 建好后触发（RevealHarness.Run），断言结果决定退出码。
        if (args.Contains("--reveal-harness")) RevealHarness.Pending = true;
        // RSM harness（手动调整边 / 拖动滚动验收）：真实窗口内以合成 Avalonia 指针事件驱动
        // ResizeChrome 与 BodyScroll 的完整管线，断言窗口几何与滚动偏移（RsmHarness.Run）。
        if (args.Contains("--rsm-harness")) RsmHarness.Pending = true;
        // Plan harness（套餐归属验收）：真实数据副本上断言概览套餐卡按 provider_id 归属
        // 渲染（Kimi 卡今日非零 + 满额推算行、ZCode 卡只含 GLM 模型），PlanHarness.Run。
        if (args.Contains("--plan-harness")) PlanHarnessPending = true;
        SideBySide = args.Contains("--side-by-side");
        string instance = (args.Contains("--demo") || RevealHarness.Pending ? ".Preview" : SideBySide || PlanHarnessPending ? ".SideBySide" : "");
        if (args.Contains("--quit")) { SingleInstance.Send(instance, "quit"); return 0; }
        if (args.Contains("--self-test")) { SelfTests.DesktopTestSource = UiSelfTests.Checks; return SelfTests.Run(); }
        if (args.Contains("--probe")) return Probe().GetAwaiter().GetResult();
        if (args.Contains("--demo") || RevealHarness.Pending) { Store.EnableDemoMode(); DemoMode = true; }
        // --restart <pid>: started by a copy that is about to exit (after a restore); wait for it.
        int restart = Array.IndexOf(args, "--restart");
        if (restart >= 0 && restart + 1 < args.Length && Int32.TryParse(args[restart + 1], out int previous)) {
            try { using (Process old = Process.GetProcessById(previous)) old.WaitForExit(15000); } catch { }
        }
        Background = args.Contains("--background");
        // 单实例：拿不到套接字就唤起已有实例并退出。
        if (!SingleInstance.Begin(instance, "show")) return 0;
        try {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        } finally { SingleInstance.Done(); }
        // harness 退出码（Rsm/Reveal/Plan 的 Environment.ExitCode）经这里生效：Main 返回值
        // 会覆盖 ExitCode 属性，无条件 return 0 会把失败吞成 0（滚轮盲区任务实测复现）。
        return Environment.ExitCode;
    }
    private static async Task<int> Probe() {
        var states = new System.Collections.Generic.List<ProviderState>();
        using (var service = new ProviderService(Store.Read<AppConfig>("settings.json"))) { foreach (string id in ProviderCatalog.Ids) states.Add(await service.Fetch(id)); }
        // Probe reports contain no tokens, raw API responses, or account identifiers.
        foreach (var s in states) s.Account = "";
        Store.Write("probe-results.json", states);
        return states.Any(s => s.Status == "ready") ? 0 : 1;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
