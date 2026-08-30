using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Launcher.Core.Launch;

/// <summary>
/// 自动内存分配：按当前可用物理内存留出 1.5GB 余量给系统/其他应用，封顶总内存 60%。
/// 保证新开应用（游戏）能拿到足够内存，又不会占满挤掉别的应用。
/// AL16：取代固定预设/总内存 60%——内存紧张时按可用内存降配，留余量。
/// </summary>
public static class MemoryAllocator
{
    /// <summary>可用内存余量（MB）：留给系统和其他应用，避免挤占</summary>
    private const int ReserveMb = 1536;

    /// <summary>纯逻辑（可单测）：min(max(avail - reserve, 1024), total*0.6)</summary>
    public static int Compute(long availMb, long totalMb)
    {
        var safe = Math.Max(1024, availMb - ReserveMb);
        var cap = (int)Math.Max(1024, totalMb * 0.6);
        return Math.Min((int)safe, cap);
    }

    /// <summary>自动内存（MB）：GlobalMemoryStatusEx 取可用物理内存；拿不到退化总内存 60%</summary>
    public static int AutoMb()
    {
        var totalMb = (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);
        if (TryGetAvailPhysMb(out var availMb))
            return Compute(availMb, totalMb);
        return (int)Math.Max(1024, totalMb * 0.6);
    }

    private static bool TryGetAvailPhysMb(out long availMb)
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status)) { availMb = 0; return false; }
            availMb = (long)(status.ullAvailPhys / 1024 / 1024);
            return true;
        }
        if (OperatingSystem.IsMacOS())
            return TryReadMemAvailableMacOS(out availMb);
        return TryReadMemAvailableLinux(out availMb);
    }

    /// <summary>macOS：sysctl hw.pagesize + vm.page_free_count 算可用物理内存；拿不到返回 false 退化总内存 60%</summary>
    private static bool TryReadMemAvailableMacOS(out long availMb)
    {
        try
        {
            var pageSize = SysctlLong("hw.pagesize");
            var freePages = SysctlLong("vm.page_free_count");
            if (pageSize > 0 && freePages >= 0)
            {
                availMb = freePages * pageSize / 1024 / 1024;
                return true;
            }
        }
        catch { /* 读不到走退化 */ }
        availMb = 0;
        return false;
    }

    /// <summary>macOS：sysctl -n 读整数值（输出纯数字）</summary>
    private static long SysctlLong(string name)
    {
        var psi = new ProcessStartInfo("sysctl", "-n " + name)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi);
        if (p is null) return -1;
        var text = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(2000);
        return long.TryParse(text, out var v) ? v : -1;
    }

    /// <summary>Linux：/proc/meminfo MemAvailable（kB）；拿不到返回 false 退化总内存 60%</summary>
    private static bool TryReadMemAvailableLinux(out long availMb)
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[1], out var kB))
                {
                    availMb = kB / 1024;
                    return true;
                }
                break;
            }
        }
        catch { /* 读不到走退化 */ }
        availMb = 0;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
