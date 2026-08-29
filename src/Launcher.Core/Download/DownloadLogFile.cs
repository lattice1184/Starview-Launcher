using PCL.Core.Logging;

namespace Launcher.Core.Download;

/// <summary>
/// 8-20 下载日志落盘：订阅 LogWrapper 事件 → 写 %AppData%\Launcher\logs\download.log。
/// 简洁逐行（[HH:mm:ss] 消息），只记 Info+（候选源/完成/失败/判死/换源——进度类 Debug 不写，
/// 高频噪音不进日志）。启动时 >5MB 轮转（清空重写）。同步写（日志低频，无需异步队列）。
/// </summary>
public static class DownloadLogFile
{
    private static readonly object Gate = new();
    private static string LogPath => Path.Combine(
        Launcher.Core.Utils.AppPaths.DataRoot, "logs", "download.log");

    /// <summary>8-22 步骤5：按任务独立落盘目录（内部树形日志查看器数据源）——
    /// logs/downloads/{任务名}_{时间戳}.log，与单文件 download.log 并存（单文件保留给「打开文件」习惯）。
    /// 任务名去非法字符（路径安全）。</summary>
    private static string LogsRoot => Path.Combine(
        Launcher.Core.Utils.AppPaths.DataRoot, "logs");

    private static bool _attached;

    /// <summary>启动时调用一次：订阅事件并开始落盘（幂等）</summary>
    public static void Attach()
    {
        lock (Gate)
        {
            if (_attached) return;
            _attached = true;
            try
            {
                var dir = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(dir);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024) File.WriteAllText(LogPath, ""); // 轮转
            }
            catch { /* 日志失败不影响启动 */ }
            LogWrapper.OnLog += (level, msg, module, ex) =>
            {
                if (level < LogLevel.Info) return; // 简洁：只落 Info+
                try
                {
                    lock (Gate)
                    {
                        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {msg}";
                        if (ex is not null) line += $" | {ex.GetType().Name}: {ex.Message}";
                        File.AppendAllText(LogPath, line + "\n");
                    }
                }
                catch { /* 日志写入失败无妨 */ }
            };
            // 8-22 步骤3：任务完成/失败事件 → 落盘（分段标记，日志可索引）
            Launcher.Core.Events.AppEvents.Subscribe<Launcher.Core.Events.DownloadCompletedEvent>(e =>
            {
                try
                {
                    lock (Gate)
                        File.AppendAllText(LogPath,
                            $"===== 完成: {e.FileName} → {e.TargetPath} [{e.CompletedAt:HH:mm:ss}] =====\n");
                    WriteTaskFile(e.FileName, e.CompletedAt,
                        $"状态：完成\n文件：{e.FileName}\n位置：{e.TargetPath}\n时间：{e.CompletedAt:yyyy-MM-dd HH:mm:ss}");
                }
                catch { }
            });
            Launcher.Core.Events.AppEvents.Subscribe<Launcher.Core.Events.DownloadFailedEvent>(e =>
            {
                try
                {
                    lock (Gate)
                        File.AppendAllText(LogPath,
                            $"===== 失败: {e.FileName} | {e.Error} [{e.CompletedAt:HH:mm:ss}] =====\n");
                    WriteTaskFile(e.FileName, e.CompletedAt,
                        $"状态：失败\n文件：{e.FileName}\n错误：{e.Error}\n时间：{e.CompletedAt:yyyy-MM-dd HH:mm:ss}");
                }
                catch { }
            });
        }
    }

    /// <summary>8-22 步骤5：按任务独立落盘（内部树形日志查看器叶子数据）——
    /// logs/downloads/{任务名}_{HHmmss}.log。任务名去非法字符；同名任务时间戳区分不覆盖。</summary>
    private static void WriteTaskFile(string taskName, DateTime time, string content)
    {
        try
        {
            var dir = Path.Combine(LogsRoot, "downloads");
            Directory.CreateDirectory(dir);
            var safe = string.Concat(taskName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)))
                .Trim();
            if (string.IsNullOrEmpty(safe)) safe = "任务";
            if (safe.Length > 60) safe = safe[..60];
            var path = Path.Combine(dir, $"{safe}_{time:HHmmss}.log");
            File.WriteAllText(path, content + Environment.NewLine);
        }
        catch { /* 单任务日志失败无妨 */ }
    }
}
