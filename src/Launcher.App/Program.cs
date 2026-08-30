using Avalonia;
using Avalonia.Win32;
using System;

namespace Launcher.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (Launcher.Core.Utils.AntiDebugGuard.ShouldExit()) return;
        Launcher.Core.Utils.AntiDebugGuard.ScheduleLateCheck(TimeSpan.FromSeconds(4));
        // 8-30 沙盒：启动时清理残留的 Starview 防火墙规则（崩溃未删的孤儿规则会持续挡 java 出站）
        Launcher.Core.Launch.Sandbox.SandboxManager.CleanupOrphanFirewallRules();
        // 8-29 内存诊断钩子：--mem-profile 开启定时/切页内存采样（默认关，dev 专用）
        if (args.Contains("--mem-profile")) Services.MemProfile.Enabled = true;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // 8-23 根治汉堡 Popup 卡深色：Popup 默认是独立顶层窗口，打开时 Windows 合成器降级
            // 主窗口亚克力渲染（内容区整体变暗且不自动恢复）。OverlayPopups = Popup 在窗口内渲染，
            // 不建独立窗口 → 主窗口合成不受影响。副作用：所有 Popup 不出窗口边界（本应用 Popup 均窗口内使用）。
            .With(new Win32PlatformOptions { OverlayPopups = true })
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}
