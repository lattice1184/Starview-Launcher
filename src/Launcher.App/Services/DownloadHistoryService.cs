using System.Text.Json;
using Launcher.Core.Download;

namespace Launcher.App.Services;

/// <summary>下载历史条目（任务名/状态/时间/错误；SourceUrl/TargetPath 供「重新下载/打开位置」，旧 json 缺失为 null）</summary>
public sealed record DownloadHistoryEntry(string Name, string State, DateTime Time, string? Error,
    string? SourceUrl = null, string? TargetPath = null)
{
    public string TimeText => Time.ToString("MM-dd HH:mm");
}

/// <summary>
/// 下载历史持久化（AppData\Launcher\history.json，最多保留 200 条）。
/// 任务进入终态（完成/失败/取消）时记录；暂停不算终态（恢复后继续）。
/// </summary>
public static class DownloadHistoryService
{
    private const int MaxEntries = 200;

    private static readonly string PathFile = Path.Combine(
        Launcher.Core.Utils.AppPaths.DataRoot, "history.json");

    private static readonly List<DownloadHistoryEntry> Entries = Load();

    public static IReadOnlyList<DownloadHistoryEntry> All => Entries;

    /// <summary>历史变化通知（UI 刷新）</summary>
    public static event Action? Changed;

    /// <summary>任务终态 → 记录（去重：同一任务只记一次）</summary>
    public static void Record(DownloadTask task)
    {
        var state = task.State;
        if (state is not (DownloadTaskState.Completed or DownloadTaskState.Failed or DownloadTaskState.Canceled)) return;
        Entries.Insert(0, new DownloadHistoryEntry(task.Name, task.StateText, DateTime.Now, task.Error,
            task.SourceUrl, task.TargetPath));
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

    private static List<DownloadHistoryEntry> Load()
    {
        try
        {
            if (File.Exists(PathFile))
            {
                var list = JsonSerializer.Deserialize<List<DownloadHistoryEntry>>(File.ReadAllText(PathFile));
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
