namespace Launcher.Core.Utils;

/// <summary>
/// 入口反调试（8-18）：Release 构建在 Main 最前检测调试器 → 写日志 → 静默退出（无弹窗）。
/// 双层检测：托管 Debugger.IsAttached + 原生 IsDebuggerPresent（防工具直接 attach）。
/// 豁免：环境变量 LATTICE_SKIP_ANTIDEBUG=1（逃生门）；DEBUG 构建默认关闭（开发者调试不受影响）。
/// 检测委托可注入（单测模拟，不真挂调试器）。
/// </summary>
public static class AntiDebugGuard
{
    /// <summary>是否启用（DEBUG 构建默认关——开发调试不误伤；Release 默认开）</summary>
    internal static bool Enabled =
#if DEBUG
        false;
#else
        true;
#endif

    /// <summary>测试注入：托管层检测结果（null = 用真实 Debugger.IsAttached）</summary>
    internal static Func<bool>? ManagedDetector;

    /// <summary>测试注入：原生层检测结果（null = 用真实 IsDebuggerPresent）</summary>
    internal static Func<bool>? NativeDetector;

    /// <summary>测试注入：延迟复查的退出动作（null = 真实 Environment.Exit——测试注入防杀测试进程）</summary>
    internal static Action? ExitAction;

    /// <summary>是否检测到调试器（DEBUG 构建或豁免环境变量恒 false）</summary>
    public static bool IsDebuggerDetected()
    {
        if (!Enabled) return false;
        if (Environment.GetEnvironmentVariable("LATTICE_SKIP_ANTIDEBUG") == "1") return false;
        if (ManagedDetector?.Invoke() ?? System.Diagnostics.Debugger.IsAttached) return true;
        if (OperatingSystem.IsWindows() && (NativeDetector?.Invoke() ?? IsDebuggerPresent())) return true;
        return false;
    }

    /// <summary>入口调用：检测到调试器 → 写日志 → 返回 true（调用方静默退出）</summary>
    public static bool ShouldExit()
    {
        if (!IsDebuggerDetected()) return false;
        TryLog("[entry] debugger detected, exit per guard policy.");
        return true;
    }

    /// <summary>
    /// 启动后延迟复查：Main 首检只覆盖启动瞬间，快速 attach（秒级）会漏——安排一次
    /// 延迟二次检测（默认 4s，覆盖常见工具 attach 窗口）。Release 下 Enabled=false 为 no-op。
    /// </summary>
    public static void ScheduleLateCheck(TimeSpan delay)
    {
        if (!Enabled) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay); } catch { return; }
            if (IsDebuggerDetected())
            {
                TryLog("[late-check] debugger attached during startup, exit per guard policy.");
                if (ExitAction is not null) ExitAction();
                else TerminateSelf(); // 硬终止：不走 finalizer/AppDomain 清理（被调试态下会挂起）
            }
        });
    }

    private static void TryLog(string message)
    {
        try
        {
            var dir = Path.Combine(Launcher.Core.Utils.AppPaths.DataRoot, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "launcher.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { /* 日志失败不阻塞退出路径 */ }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool IsDebuggerPresent();

    // 8-30 macOS：kernel32 TerminateProcess 非 Windows 会 DllNotFoundException——改跨平台 Process.Kill()
    private static void TerminateSelf() =>
        System.Diagnostics.Process.GetCurrentProcess().Kill();
}
