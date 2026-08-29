namespace Launcher.Core.Utils;

/// <summary>
/// 跨平台路径服务（8-29 Linux 移植）：Windows 保留 %AppData%\Launcher 现有语义，Linux/macOS 走 XDG。
/// 各业务调用点统一经此类取路径，避免散落 Environment.GetFolderPath + 硬编码子目录。
/// </summary>
public static class AppPaths
{
    private const string AppDirName = "starview";

    /// <summary>配置与数据根目录（settings/账号/收藏/历史/日志/工具/多人在线）。</summary>
    /// <remarks>Windows: %AppData%\Launcher；Linux: $XDG_DATA_HOME/starview（默认 ~/.local/share/starview）。</remarks>
    public static string DataRoot { get; } = ResolveDataRoot();

    /// <summary>缓存根目录（imgcache 等）。</summary>
    /// <remarks>Windows: %LocalAppData%\Launcher；Linux: $XDG_CACHE_HOME/starview（默认 ~/.cache/starview）。</remarks>
    public static string CacheRoot { get; } = ResolveCacheRoot();

    /// <summary>默认下载目录：~/Downloads（跨平台同语义）。</summary>
    public static string Downloads => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    // ---------- 常用派生路径 ----------

    public static string LogsDir => Path.Combine(DataRoot, "logs");
    public static string CacheDir => Path.Combine(DataRoot, "cache");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string ImageCacheDir => Path.Combine(CacheRoot, "imgcache");
    public static string ToolsDir => Path.Combine(DataRoot, "tools");
    public static string MultiplayerDir => Path.Combine(DataRoot, "multiplayer");

    private static string ResolveDataRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Launcher");
        }

        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var baseDir = string.IsNullOrWhiteSpace(xdgData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : xdgData;
        return Path.Combine(baseDir, AppDirName);
    }

    private static string ResolveCacheRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Launcher");
        }

        var xdgCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var baseDir = string.IsNullOrWhiteSpace(xdgCache)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
            : xdgCache;
        return Path.Combine(baseDir, AppDirName);
    }
}
