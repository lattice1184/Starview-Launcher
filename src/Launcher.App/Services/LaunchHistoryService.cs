using System.Text.Json;

namespace Launcher.App.Services;

/// <summary>启动记录结果</summary>
public enum LaunchOutcome { Success, Failed, Stopped, Crashed }

/// <summary>单次启动记录（时间/版本/结果/错误/耗时/日志文件——8-18 加 LogPath 供查看那次日志）</summary>
public sealed record LaunchHistoryEntry(
    DateTime Time, string VersionId, LaunchOutcome Outcome, string? Error, double DurationSeconds,
    string? LogPath = null)
{
    public string TimeText => Time.ToString("MM-dd HH:mm");
    public string OutcomeText => Outcome switch
    {
        LaunchOutcome.Success => "成功",
        LaunchOutcome.Failed => "失败",
        LaunchOutcome.Stopped => "已停止",
        _ => "异常退出",
    };
}

/// <summary>
/// 启动记录持久化（AppData\Launcher\launch-history.json，最多 200 条）。
/// 游戏启动错误不再只在内存控制台——每次启动终态落盘，可回看诊断。
/// </summary>
public static class LaunchHistoryService
{
    private const int MaxEntries = 200;

    private static readonly string PathFile = Path.Combine(
        Launcher.Core.Utils.AppPaths.DataRoot, "launch-history.json");

    private static readonly List<LaunchHistoryEntry> Entries = Load();

    public static IReadOnlyList<LaunchHistoryEntry> All => Entries;

    public static event Action? Changed;

    public static void Record(string versionId, LaunchOutcome outcome, string? error, double durationSeconds, string? logPath = null)
    {
        Entries.Insert(0, new LaunchHistoryEntry(DateTime.Now, versionId, outcome, error, durationSeconds, logPath));
        if (Entries.Count > MaxEntries) Entries.RemoveRange(MaxEntries, Entries.Count - MaxEntries);
        Save();
        Changed?.Invoke();
    }

    public static void Clear()
    {
        Entries.Clear();
        Save();
        Changed?.Invoke();
    }

    private static List<LaunchHistoryEntry> Load()
    {
        try
        {
            if (File.Exists(PathFile))
            {
                var list = JsonSerializer.Deserialize<List<LaunchHistoryEntry>>(File.ReadAllText(PathFile));
                if (list is not null) return list;
            }
        }
        catch { }
        return [];
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathFile)!);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(Entries));
        }
        catch { }
    }
}
