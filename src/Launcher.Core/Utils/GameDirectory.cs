namespace Launcher.Core.Utils;

/// <summary>游戏目录来源（启动列表标识用）</summary>
public enum GameDirectorySource { OwnDefault, Standard, Pcl, Custom }

/// <summary>
/// 游戏目录（.minecraft）解析，PCL2 式：
/// 安装目标（下载/安装落点）永远是启动器自建目录 Downloads\YanKa Launcher\.minecraft（或用户自配）；
/// PCL / 官方等已有环境的目录只作为"扫描源"（版本可见可启动，但不接收新安装）。
/// </summary>
public static class GameDirectory
{
    /// <summary>来源中文标签（"本启动器"/"PCL2"/"官方"/"自配"）</summary>
    public static string SourceLabel(GameDirectorySource source) => source switch
    {
        GameDirectorySource.OwnDefault => "本启动器",
        GameDirectorySource.Pcl => "PCL2",
        GameDirectorySource.Standard => "官方",
        GameDirectorySource.Custom => "自配",
        _ => "",
    };

    /// <summary>自建目录候选（C 盘 Downloads 历史位 + D 盘位）——扫描源用，换盘后旧版本仍可见</summary>
    private static IEnumerable<string> OwnCandidates()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "YanKa Launcher", ".minecraft");
        if (Directory.Exists("D:\\")) yield return Path.Combine("D:\\", "YanKa Launcher", ".minecraft");
    }

    /// <summary>启动器自建根（优先 D 盘 D:\YanKa Launcher\.minecraft；无 D 盘回退 C 盘 Downloads 历史位）</summary>
    public static string OwnDefault()
    {
        if (Directory.Exists("D:\\")) return Path.Combine("D:\\", "YanKa Launcher", ".minecraft");
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "YanKa Launcher", ".minecraft");
    }

    /// <summary>安装目标目录（下载/安装落点）：用户自配 ?? 启动器自建。永不探测已有环境。</summary>
    public static string InstallDir()
    {
        if (LauncherSettings.Current.GameDirectory is { } custom) return custom;
        return OwnDefault();
    }

    /// <summary>安装目标来源（标签："本启动器"/"自配"）</summary>
    public static GameDirectorySource DetectSource()
        => LauncherSettings.Current.GameDirectory is null ? GameDirectorySource.OwnDefault : GameDirectorySource.Custom;

    /// <summary>兼容入口：当前安装目标（历史调用点：下载/安装/默认启动目录）</summary>
    public static string Detect() => InstallDir();

    /// <summary>
    /// 版本发现扫描源：安装目标 + 自建目录历史位（跨盘），按序去重。
    /// 8-23 起不再扫描 PCL / 官方（AppData）已有环境——列表只剩自建目录的版本，
    /// 避免 PCL 版本混入导致默认选中/下载跟随选错实例。已安装版本的显示与启动来自这些目录；
    /// 新下载安装只进 InstallDir。
    /// </summary>
    /// <summary>8-22 进程内扫描缓存：启动后多次调用（版本扫描/清理/校验）复用同一结果，
    /// 消除 O(N²) junction 重复解析。改目录时 InvalidateScanCache 失效。</summary>
    private static readonly object ScanCacheGate = new();
    private static List<(string Dir, GameDirectorySource Source)>? _scanCache;

    /// <summary>8-22：目录变更后清扫描缓存（设置页改目录/重置时调用）</summary>
    public static void InvalidateScanCache()
    {
        lock (ScanCacheGate) { _scanCache = null; }
    }

    public static List<(string Dir, GameDirectorySource Source)> ScanSourceDirs()
    {
        lock (ScanCacheGate) { if (_scanCache is not null) return _scanCache; }
        var list = new List<(string Dir, GameDirectorySource Source)>();
        void Add(string dir, GameDirectorySource source)
        {
            if (string.IsNullOrEmpty(dir)) return;
            // AL57 双版本根治：按物理路径去重（junction/符号链接解析到真实目录）——
            // 快照版 PCL 的 .minecraft junction 指向正式版时，两路径字符串不同但物理相同，
            // 字符串比较会放行 → 同一份版本被扫两次（每版本 ×2 的根因）
            var phys = ResolvePhysical(dir);
            if (list.Any(x => string.Equals(ResolvePhysical(x.Dir), phys, StringComparison.OrdinalIgnoreCase))) return;
            if (Directory.Exists(Path.Combine(dir, "versions"))) list.Add((dir, source));
        }

        Add(InstallDir(), DetectSource());

        // 自建目录历史位置（跨盘扫描：C 盘旧位 / D 盘新位，换盘后旧版本仍可见）
        foreach (var candidate in OwnCandidates())
            Add(candidate, GameDirectorySource.OwnDefault);

        lock (ScanCacheGate) { _scanCache = list; } // double-check：锁内赋值，防并发读到半初始化引用
        return list;
    }

    /// <summary>由目录反查来源（标签用）</summary>
    public static GameDirectorySource SourceOf(string dir)
        => ScanSourceDirs().FirstOrDefault(x => string.Equals(x.Dir, dir, StringComparison.OrdinalIgnoreCase)).Source;

    /// <summary>
    /// 目录是否归本启动器管（自建/自配来源 OwnDefault/Custom；PCL/官方扫描源不算）——
    /// 8-14 误标根因：修复/自动修复以版本实际目录打安装标记，把 .yanla-installed 写进 PCL 目录。
    /// 找不到的目录按「不归本启动器」处理（防未知路径误判放行）。
    /// </summary>
    public static bool IsOwnInstallDir(string dir)
    {
        var phys = ResolvePhysical(dir);
        return ScanSourceDirs().Any(x => string.Equals(ResolvePhysical(x.Dir), phys, StringComparison.OrdinalIgnoreCase)
            && x.Source is GameDirectorySource.OwnDefault or GameDirectorySource.Custom);
    }

    /// <summary>8-19 生态修缮：MOD 安装落点基准——本启动器目录的实例装原目录；
    /// PCL/官方等外来实例「只读不写」：启动器下载的东西一律归类到启动器自己的目录
    /// （读 PCL 目录和往 PCL 目录里放是两回事）</summary>
    public static string ModInstallBaseDir(string instanceDir)
        => IsOwnInstallDir(instanceDir) ? instanceDir : InstallDir();

    /// <summary>解析 junction/符号链接到最终物理路径（尾部斜杠归一化；解析失败回退原路径）</summary>
    private static string ResolvePhysical(string dir)
    {
        try
        {
            var fi = new DirectoryInfo(dir);
            var target = fi.ResolveLinkTarget(returnFinalTarget: true);
            return (target?.FullName ?? fi.FullName).TrimEnd('\\', '/');
        }
        catch { return Path.GetFullPath(dir).TrimEnd('\\', '/'); }
    }

    /// <summary>确保自建目录结构存在（启动时调用一次；空目录也算已创建）</summary>
    public static void EnsureDefault()
    {
        var dir = OwnDefault();
        foreach (var sub in new[] { "versions", "libraries", "assets", "assets/indexes", "assets/objects" })
        {
            try { Directory.CreateDirectory(Path.Combine(dir, sub)); } catch { }
        }
    }
}
