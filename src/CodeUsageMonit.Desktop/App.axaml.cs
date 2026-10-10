using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace CodeUsageMonit.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // ShutdownMode=OnExplicitShutdown（关窗只是收起到托盘）。
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // 面板窗口遇到错误：写 errors.log，不弹原生对话框、不退出。
            // 经 Core 的 ErrorLog 追加（review P2-2：超 512KB 自动截断保留尾部，日志有界）。
            Dispatcher.UIThread.UnhandledException += (sender, e) =>
            {
                CodeUsageMonit.ErrorLog.Append("ui-unhandled", e.Exception);
                e.Handled = true;
            };
            var ui = new CodeUsageMonit.MonitorPanel(this, Program.DemoMode);
            Program.Ui = ui;
            // --reveal-harness：面板就绪后在 UI 线程跑“隐藏 → Reveal 前台激活”断言。
            if (CodeUsageMonit.RevealHarness.Pending) CodeUsageMonit.RevealHarness.Run(ui);
            // --rsm-harness：手动调整边（ResizeChrome）与按住拖动滚动（BodyScroll）的真机断言。
            if (CodeUsageMonit.RsmHarness.Pending) CodeUsageMonit.RsmHarness.Run(ui);
            // --plan-harness：套餐归属（provider_id → 概览套餐卡）的真机断言与截图。
            if (CodeUsageMonit.Desktop.Program.PlanHarnessPending) CodeUsageMonit.PlanHarness.Run(ui);
            // Start 经 Dispatcher 以 ApplicationIdle 优先级投递启动。
            Dispatcher.UIThread.Post(() => ui.Start(Program.Background), DispatcherPriority.ApplicationIdle);
            // 单实例命令：show → 唤起面板；quit → 退出。
            CodeUsageMonit.SingleInstance.Listen(command =>
            {
                if (command == "quit") ui.Quit(); else ui.Reveal();
            });
            desktop.ShutdownRequested += (sender, e) => { ui.Dispose(); CodeUsageMonit.SingleInstance.Done(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
