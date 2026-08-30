using System.Text.Json;

namespace Launcher.Core.Utils;

/// <summary>
/// 更新后弹窗状态（8-31）：记录上次看到 changelog 的版本，升级后首次启动弹「本次更新内容」。
/// 状态落盘 {DataRoot}/changelog-state.json（独立于用户设置，首装不弹、升级弹一次）。
/// </summary>
public static class ChangelogState
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>测试注入：重定向状态文件路径（避免污染真实数据目录）</summary>
    internal static string? StateFileOverrideForTest;

    private sealed record State(string? LastSeenVersion);

    private static string StateFilePath()
        => StateFileOverrideForTest ?? Path.Combine(AppPaths.DataRoot, "changelog-state.json");

    /// <summary>上次展示过 changelog 的版本（首装未记录 → null）</summary>
    public static string? GetLastSeen()
    {
        try
        {
            var path = StateFilePath();
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<State>(File.ReadAllText(path), JsonOpts)?.LastSeenVersion;
        }
        catch { return null; }
    }

    /// <summary>记录已展示过的版本（弹窗展示后调用）</summary>
    public static void SetSeen(string version)
    {
        try
        {
            var path = StateFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new State(version), JsonOpts));
        }
        catch { /* 状态记录失败不影响启动 */ }
    }

    /// <summary>该弹吗：有过记录（非首装）且当前版本比上次看到的更新 → 升级了要弹</summary>
    public static bool ShouldShow(string currentVersion)
    {
        var seen = GetLastSeen();
        if (string.IsNullOrEmpty(seen)) return false; // 首装不弹
        return VersionUtil.Compare(currentVersion, seen) > 0;
    }
}
