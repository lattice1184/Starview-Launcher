using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Launcher.App.Services;

/// <summary>
/// 工作集修剪（8-29 用户拍板「上修剪」）：闲置/失焦/最小化时把物理页让渡给系统，
/// 任务管理器「内存/工作集」列大幅下降——PCL 数字低的真实原理（非 PCL 代码省）。
/// 诚实语义：修剪 = 物理页让渡，提交内存（Commit）不变；可能写页面文件（SSD 寿命考虑，
/// 故只在闲置 60s / 失焦 / 最小化触发，用户一操作立即页故障重载恢复）。
/// 跳过活跃期（下载中/启动游戏）：正在用的页不让渡。冷却 2 分钟防抖。
/// 8-25 曾移除旧 IdleMemoryTuner（一键释放按钮假效果）；本次为自动修剪，失焦即生效，非假释放。
/// </summary>
public static class IdleMemoryTrimmer
{
    private const int IdleSeconds = 60;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    private static DateTime _lastActivity = DateTime.UtcNow;
    private static DateTime _lastTrim = DateTime.MinValue;
    private static bool _hooked;

    /// <summary>挂到主窗口（构造后调一次）：输入活动重置 + 失焦/最小化/闲置轮询触发修剪</summary>
    public static void Hook(Window window)
    {
        if (_hooked) return;
        _hooked = true;
        // 用户操作 → 重置闲置计时（一旦操作就不算闲置）
        window.AddHandler(InputElement.PointerPressedEvent, (_, _) => _lastActivity = DateTime.UtcNow);
        window.AddHandler(InputElement.PointerWheelChangedEvent, (_, _) => _lastActivity = DateTime.UtcNow);
        window.AddHandler(InputElement.KeyDownEvent, (_, _) => _lastActivity = DateTime.UtcNow);
        // 失焦 = 用户切走了 → 立即修剪
        window.Deactivated += (_, _) => TrimNow("失焦");
        // 最小化 → 立即修剪
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property?.Name == nameof(Window.WindowState) && window.WindowState == WindowState.Minimized)
                TrimNow("最小化");
        };
        // 闲置轮询：每 15s 检查一次无操作时长
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timer.Tick += (_, _) => { if (DateTime.UtcNow - _lastActivity > TimeSpan.FromSeconds(IdleSeconds)) TrimNow("闲置"); };
        timer.Start();
    }

    /// <summary>下载/启动活跃中 → 不让渡（正在用的页）</summary>
    private static bool HasActiveWork()
    {
        try
        {
            if (Launcher.Core.Download.DownloadManager.Instance.Tasks.Any(t =>
                    t.State is Launcher.Core.Download.DownloadTaskState.Queued
                        or Launcher.Core.Download.DownloadTaskState.Downloading
                        or Launcher.Core.Download.DownloadTaskState.Verifying))
                return true;
        }
        catch { }
        return false;
    }

    private static void TrimNow(string reason)
    {
        if (HasActiveWork()) return;
        if (DateTime.UtcNow - _lastTrim < Cooldown) return; // 冷却防抖
        _lastTrim = DateTime.UtcNow;
        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false); // 托管压缩（Heap 9MB 顺手）
            using var proc = Process.GetCurrentProcess();
            var before = proc.WorkingSet64;
            if (OperatingSystem.IsWindows())
                SetProcessWorkingSetSize(proc.Handle, new IntPtr(-1), new IntPtr(-1)); // 物理页让渡（Windows）
            var after = proc.WorkingSet64;
            MemProfile.Sample($"trim:{reason}({before / 1024 / 1024}->{after / 1024 / 1024}MB)");
        }
        catch { /* 修剪失败不影响运行 */ }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);
}
