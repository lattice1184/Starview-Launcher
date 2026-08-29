using System.Diagnostics;
using Launcher.App.ViewModels;

namespace Launcher.App.Services;

/// <summary>
/// --mem-profile 诊断钩子（8-29）：定时 + 切页输出内存分解，定位 227MB 大头。
/// dev 专用：Enabled 默认 false（不传 --mem-profile 零开销），mem-profile.log 不入库。
/// 列含义（对照任务管理器）：WS=工作集(WorkingSet64)、Priv=私有提交(PrivateMemorySize64)、
/// Heap=托管堆(GC.GetTotalMemory)。WS 对「工作集」列、Priv 对「提交大小」列——对照哪列≈227MB 就知道用户看的是哪个。
/// </summary>
public static class MemProfile
{
    public static bool Enabled { get; set; }

    private static StreamWriter? _log;
    private static readonly object Lock = new();

    /// <summary>输出一行采样（where 是触发点：boot/tick/切页名）</summary>
    public static void Sample(string where)
    {
        if (!Enabled) return;
        try
        {
            var p = Process.GetCurrentProcess();
            var line = $"[mem] {DateTime.Now:HH:mm:ss} {where,-10} " +
                       $"WS={p.WorkingSet64 / 1024.0 / 1024.0:F0}MB " +
                       $"Priv={p.PrivateMemorySize64 / 1024.0 / 1024.0:F0}MB " +
                       $"Commit={(p.PagedMemorySize64 + p.PrivateMemorySize64) / 1024.0 / 1024.0:F0}MB " +
                       $"Heap={GC.GetTotalMemory(false) / 1024.0 / 1024.0:F1}MB " +
                       $"Img={ImageLoader.CacheCount} EcoTabs={EcosystemViewModel.BuiltCount}";
            lock (Lock)
            {
                _log ??= new StreamWriter(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Launcher", "logs", "mem-profile.log"), append: true) { AutoFlush = true };
                _log.WriteLine(line);
                Console.WriteLine(line);
            }
        }
        catch { /* 诊断钩子永不抛 */ }
    }
}
