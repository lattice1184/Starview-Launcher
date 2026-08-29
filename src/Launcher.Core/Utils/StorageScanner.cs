namespace Launcher.Core.Utils;

/// <summary>存储位置：路径 + 是文件还是目录 + 是否可删</summary>
public sealed record StorageLocation(string Path, bool IsFile, bool CanDelete);

/// <summary>存储分组：一组可统计/可清理的位置（TotalBytes = 各位置大小之和）</summary>
public sealed record StorageGroup(string Key, string DisplayName, IReadOnlyList<StorageLocation> Items, long TotalBytes);

/// <summary>
/// 存储占用扫描与清理（按模块分组）：游戏文件 / 下载缓存 / 日志 / 备份导出。
/// 纯 IO、无 UI 依赖——设置页「模块与存储」分区与 StorageWindow 共用；App 侧 Task.Run 包裹防卡 UI。
/// </summary>
public static class StorageScanner
{
    /// <summary>
    /// 扫描全部存储位置并分组统计。appData 可注入（测试隔离）；null 用真实 %AppData%\Launcher。
    /// </summary>
    public static List<StorageGroup> Scan(string? gameDir = null, string? appData = null)
    {
        gameDir ??= LauncherSettings.Current.GameDirectory ?? GameDirectory.Detect();
        appData ??= Path.Combine(Launcher.Core.Utils.AppPaths.DataRoot);

        var groups = new List<StorageGroup>();

        // 游戏文件（不可删——删除即卸载游戏文件，仅统计占用；排除 *.parts 分片目录，归下载缓存组）
        var gameLocations = new[] {
            new StorageLocation(Path.Combine(gameDir, "versions"), false, false),
            new StorageLocation(Path.Combine(gameDir, "libraries"), false, false),
            new StorageLocation(Path.Combine(gameDir, "assets"), false, false),
            new StorageLocation(Path.Combine(gameDir, "saves"), false, false),
            new StorageLocation(Path.Combine(gameDir, "mods"), false, false),
            new StorageLocation(Path.Combine(gameDir, "resourcepacks"), false, false),
            new StorageLocation(Path.Combine(gameDir, "shaderpacks"), false, false),
        };
        groups.Add(new("game", "游戏文件", gameLocations,
            gameLocations.Sum(l => ItemSize(l.Path, l.IsFile, IsPartsDir))));

        // 下载缓存（*.parts 分片目录 + AppData 缓存 + 整合包导出 + 直接下载的 mod + 失败下载残留，可删）
        var dlItems = new List<StorageLocation>();
        if (Directory.Exists(gameDir))
        {
            foreach (var d in Directory.EnumerateDirectories(gameDir, "*.parts", SearchOption.AllDirectories))
                dlItems.Add(new StorageLocation(d, false, true));
            // 8-19 第二批：失败/中断下载的残留（.tmp/.race*）——TargetPath 为空的组内任务终态不清理，
            // 这里统一兜底可统计可清理
            foreach (var f in Directory.EnumerateFiles(gameDir, "*.tmp", SearchOption.AllDirectories))
                dlItems.Add(new StorageLocation(f, true, true));
            foreach (var f in Directory.EnumerateFiles(gameDir, "*.race*", SearchOption.AllDirectories))
                dlItems.Add(new StorageLocation(f, true, true));
        }
        dlItems.Add(new StorageLocation(Path.Combine(appData, "cache"), false, true));
        dlItems.Add(new StorageLocation(Path.Combine(gameDir, "downloads", "modpacks"), false, true));
        dlItems.Add(new StorageLocation(Path.Combine(gameDir, "downloads", "mods"), false, true)); // 8-19 第二批：详情页「直接下载」落点，可清理
        groups.Add(new("downloads", "下载缓存", dlItems, Sum(dlItems)));

        // 日志（游戏日志 + 崩溃报告 + 启动器日志，可删）
        groups.Add(Group("logs", "日志", gameDir, [
            new StorageLocation(Path.Combine(gameDir, "logs"), false, true),
            new StorageLocation(Path.Combine(gameDir, "crash-reports"), false, true),
            new StorageLocation(Path.Combine(appData, "logs"), false, true),
        ]));

        // 备份导出（backups/*.zip，可删）
        var backupItems = new List<StorageLocation>();
        if (Directory.Exists(Path.Combine(gameDir, "backups")))
            foreach (var f in Directory.EnumerateFiles(Path.Combine(gameDir, "backups"), "*.zip"))
                backupItems.Add(new StorageLocation(f, true, true));
        groups.Add(new("backups", "备份导出", backupItems, Sum(backupItems)));

        return groups;
    }

    /// <summary>删除一组的可删位置（占用中失败 continue），返回实际释放字节数</summary>
    public static long DeleteGroup(StorageGroup group)
    {
        long freed = 0;
        foreach (var item in group.Items.Where(i => i.CanDelete))
        {
            try
            {
                var size = ItemSize(item.Path, item.IsFile); // 先量后删（删了就没法量）
                if (item.IsFile) { if (File.Exists(item.Path)) File.Delete(item.Path); }
                else if (Directory.Exists(item.Path)) Directory.Delete(item.Path, true);
                freed += size; // 删除成功才计
            }
            catch { /* 占用中/权限——跳过继续 */ }
        }
        return freed;
    }

    private static StorageGroup Group(string key, string name, string baseDir, StorageLocation[] locations)
        => new(key, name, locations, Sum(locations));

    private static long Sum(IEnumerable<StorageLocation> items)
        => items.Sum(i => ItemSize(i.Path, i.IsFile));

    /// <summary>路径大小（文件单文件长；目录递归求和；异常/不存在 = 0）。excludeDir：命中谓词的目录跳过不计</summary>
    public static long ItemSize(string path, bool isFile, Func<string, bool>? excludeDir = null)
    {
        try
        {
            if (isFile) return File.Exists(path) ? new FileInfo(path).Length : 0;
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            {
                if (excludeDir?.Invoke(dir) == true) continue;
                foreach (var f in Directory.EnumerateFiles(dir))
                    total += FileLen(f);
            }
            foreach (var f in Directory.EnumerateFiles(path))
                total += FileLen(f);
            return total;
        }
        catch { return 0; }
    }

    /// <summary>*.parts 目录判定（下载分片临时目录，归下载缓存组）</summary>
    public static bool IsPartsDir(string dir)
        => Path.GetFileName(dir).EndsWith(".parts", StringComparison.OrdinalIgnoreCase);

    private static long FileLen(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0L; }
    }

    /// <summary>字节 → 可读文本（B/KB/MB/GB）</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.0} GB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };
}
