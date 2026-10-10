// macOS 前台激活（AppMonitor.Reveal 用）：window.Activate() 只请求键窗，macOS 对
// 非前台 app 会把激活让给当前活跃应用，窗口停留在浏览器等后面。把整个 app 提到前台
// 需要走 NSApplication：macOS 14+ 优先 -[NSApplication activate]，旧系统回退
// activateIgnoringOtherApps:YES；14+ 上旧 selector 已弃用但仍存在，两个都发以提高
// 到前台的成功率（弃用 ≠ 移除）。互操作先例：SingleInstance.AcquireGate 的 libSystem
// flock。所有调用都吞异常——激活失败只影响体验，绝不能打断 UI 流程。
// ActivateForeground 可注入、ActivationCount 可观测（--reveal-harness 验收用）。
// PinActiveMaterial（任务4）：Avalonia 的 AcrylicBlur 由 NSVisualEffectView 承载，默认
// FollowsWindowActiveState——失焦时系统切到更不透明的 inactive 外观，界面透明度跳变。
// 遍历窗口 contentView 的子视图找到效果视图，把 state 固定为 Active（=1），失焦后材质
// 保持不变。只在 UI 线程调用；重复执行安全（反复写同一个属性）；找不到效果视图（其它
// 平台/透明级别未生效）时静默返回，调用方不必关心。
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Platform;

namespace CodeUsageMonit {
    internal static class MacApp {
        // 生产调用只有 Reveal 一处；harness 以计数观测真实路径是否执行。
        internal static Action ActivateForeground = ActivateNow;
        internal static int ActivationCount;

        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern IntPtr objc_getClass(string name);
        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern IntPtr sel_registerName(string name);
        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
        // BOOL 参数按 1 字节传（arm64 的 objc BOOL 即 bool；byte 无符号扩展歧义最小）。
        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, byte value);
        // 同一函数按 BOOL（1 字节）返回值读：x0 高位对 1 字节返回值不作保证，按 IntPtr 读不可靠。
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern byte objc_msgSend_retbyte(IntPtr receiver, IntPtr selector);
        // 带一个指针参数的变体：isKindOfClass:(Class) 参数、objectAtIndex:(NSUInteger) 入参、
        // setState:(NSInteger) 入参。返回值按用途分三个名（x0 语义不同，不能共用签名）。
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_id(IntPtr receiver, IntPtr selector, IntPtr argument);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_void(IntPtr receiver, IntPtr selector, IntPtr argument);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern byte objc_msgSend_retbyte1(IntPtr receiver, IntPtr selector, IntPtr argument);

        // 本 app 是否为当前活跃应用（Dock 点击 / Cmd-Tab 唤回检测用，AppMonitor 的轮询）。
        [SupportedOSPlatform("macos")]
        internal static bool IsActive() {
            try {
                IntPtr app = objc_msgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                return app != IntPtr.Zero && objc_msgSend_retbyte(app, sel_registerName("isActive")) != 0;
            } catch { return false; }
        }

        // 窗口失焦后保持 Active 材质：把 contentView 子树里的每个 NSVisualEffectView 的
        // state 固定为 NSVisualEffectStateActive（1；0 = FollowsWindowActiveState）。
        // 挂点：WindowFrame.ApplyBackdrop（面板 + 设置窗口 + 外观预览共用）与主窗口
        // Activated（Avalonia 重建效果视图后的兜底）。幂等，失败静默。
        // CUM_MATERIAL_TRACE=1 时向 stdout 报告查找/固定结果（真机验收用，平时零输出零开销）。
        [SupportedOSPlatform("macos")]
        internal static void PinActiveMaterial(TopLevel topLevel) {
            try {
                IPlatformHandle handle = topLevel?.TryGetPlatformHandle();
                IntPtr nsWindow = handle == null ? IntPtr.Zero : handle.Handle;
                if (nsWindow == IntPtr.Zero) { Trace("no platform handle"); return; }
                IntPtr effectClass = objc_getClass("NSVisualEffectView");
                if (effectClass == IntPtr.Zero) { Trace("NSVisualEffectView class not found"); return; }
                pinned = 0;
                PinView(objc_msgSend(nsWindow, sel_registerName("contentView")), effectClass);
                Trace("pinned effect views: " + pinned);
            } catch (Exception e) { Trace("error " + e.GetType().Name); }
        }
        private static int pinned;
        private static void Trace(string message) {
            try { if (Environment.GetEnvironmentVariable("CUM_MATERIAL_TRACE") == "1") Console.WriteLine("[material] " + DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message); } catch { }
        }

        [SupportedOSPlatform("macos")]
        private static void PinView(IntPtr view, IntPtr effectClass) {
            if (view == IntPtr.Zero) return;
            if (objc_msgSend_retbyte1(view, sel_registerName("isKindOfClass:"), effectClass) != 0) {
                objc_msgSend_void(view, sel_registerName("setState:"), new IntPtr(1));   // NSVisualEffectStateActive
                pinned++;
            }
            IntPtr subviews = objc_msgSend(view, sel_registerName("subviews"));
            if (subviews == IntPtr.Zero) return;
            long count = objc_msgSend(subviews, sel_registerName("count")).ToInt64();
            for (long i = 0; i < count; i++)
                PinView(objc_msgSend_id(subviews, sel_registerName("objectAtIndex:"), new IntPtr(i)), effectClass);
        }

        [SupportedOSPlatform("macos")]
        private static void ActivateNow() {
            ActivationCount++;
            try {
                IntPtr app = objc_msgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                if (app == IntPtr.Zero) return;
                if (OperatingSystem.IsMacOSVersionAtLeast(14)) objc_msgSend(app, sel_registerName("activate"));
                objc_msgSend(app, sel_registerName("activateIgnoringOtherApps:"), (byte)1);
            } catch { }
        }
    }
}
