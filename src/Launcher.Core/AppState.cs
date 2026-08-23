namespace Launcher.Core;

/// <summary>
/// 全局统一状态（8-22 工程化步骤 1）：所有模块从同一处读取「当前实例根目录」和「当前选中版本」，
/// 不再各自从 VM/设置/实例对象取（数据不一致的根源）。
/// - InstanceRoot：启动器安装根（.minecraft），初始化写入 GameDirectory.InstallDir()
/// - CurrentVersionId：当前选中版本（主页版本下拉是全局权威，HomeViewModel 同步写入）
/// Core 层模块（修复/日志/校验）直接读这里，不依赖 App 层 VM。
/// </summary>
public static class AppState
{
    private static readonly object Gate = new();

    /// <summary>当前实例根目录（本启动器安装根）。初始化后不变；切换实例目录走设置变更</summary>
    public static string InstanceRoot { get; private set; } = "";

    /// <summary>当前选中版本 ID（如 fabric-loader-0.19.3-26.1.2；未选 = 空）</summary>
    public static string CurrentVersionId { get; private set; } = "";

    /// <summary>最近安装的版本 ID（8-23：主页下拉自动选中最新安装——下载模组「跟随实例」落到正确目标）</summary>
    public static string LastInstalledVersionId { get; private set; } = "";

    /// <summary>版本安装完成时记录（主页刷新据此自动选中）</summary>
    public static void SetLastInstalledVersion(string? versionId)
    {
        lock (Gate) { if (!string.IsNullOrEmpty(versionId)) LastInstalledVersionId = versionId; }
    }

    /// <summary>启动器初始化时写入实例根（App 启动处调用一次）</summary>
    public static void InitInstanceRoot(string root)
    {
        lock (Gate) { if (string.IsNullOrEmpty(InstanceRoot)) InstanceRoot = root; }
    }

    /// <summary>8-22：目录变更时覆盖实例根（目录窗口确认 / 设置页改目录）。
    /// Init 保底默认目录，此处更新为实际选择；空值忽略（保留原值）</summary>
    public static void UpdateInstanceRoot(string root)
    {
        lock (Gate) { if (!string.IsNullOrEmpty(root)) InstanceRoot = root; }
    }

    /// <summary>主页版本切换时同步（全局权威 = 主页版本下拉）</summary>
    public static void SetCurrentVersion(string? versionId)
    {
        lock (Gate) { CurrentVersionId = versionId ?? ""; }
    }
}
