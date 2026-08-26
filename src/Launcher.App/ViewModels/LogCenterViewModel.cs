using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Launcher.App.ViewModels;

/// <summary>日志条目（平铺列表项）。分类：下载 / 启动 / 修复。</summary>
public sealed record LogEntry(string Category, string Display, string? FilePath, string TimeText, bool IsPlaceholder);

/// <summary>
/// 8-26 日志中心从0重做：平铺列表 + 分类 tab + 异步读文件。
/// 弃旧 TreeView（Avalonia 12 单击选中≠展开时序打架、容器/叶子同构、纯 code-behind 无 VM——反复出问题源头）。
/// </summary>
public partial class LogCenterViewModel : ObservableObject
{
    public string[] Categories { get; } = ["下载", "启动", "修复"];

    public bool IsDownloadTab => SelectedCategory == "下载";
    public bool IsLaunchTab => SelectedCategory == "启动";
    public bool IsRepairTab => SelectedCategory == "修复";

    [ObservableProperty] private string _selectedCategory = "下载";
    [ObservableProperty] private LogEntry? _selectedEntry;
    [ObservableProperty] private string _content = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<LogEntry> Entries { get; } = [];

    private static string LogsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Launcher", "logs");

    public LogCenterViewModel() => Reload();

    [RelayCommand]
    private void SelectCategory(string category) => SelectedCategory = category;

    partial void OnSelectedCategoryChanged(string value)
    {
        OnPropertyChanged(nameof(IsDownloadTab));
        OnPropertyChanged(nameof(IsLaunchTab));
        OnPropertyChanged(nameof(IsRepairTab));
        Reload();
    }

    partial void OnSelectedEntryChanged(LogEntry? value) => _ = LoadContentAsync(value);

    /// <summary>重建当前分类条目列表。选中分类/刷新按钮/构造函数共用。</summary>
    [RelayCommand]
    private void Reload()
    {
        Entries.Clear();
        var root = LogsRoot;
        switch (SelectedCategory)
        {
            case "启动": AddLaunches(root); break;
            case "修复": AddRepairs(root); break;
            default: AddDownloads(root); break;
        }
        if (Entries.Count == 0)
        {
            SelectedEntry = null;
            Content = "（该分类还没有日志，下载/启动后这里会出现）";
            SummaryText = "";
        }
        else SelectedEntry = Entries[0];
    }

    /// <summary>下载：downloads/{任务}_{HHmmss}.log 任务摘要（同名按时间倒序取最近）+ download.log 完整过程固定尾条目。</summary>
    private void AddDownloads(string root)
    {
        var dlRoot = Path.Combine(root, "downloads");
        if (Directory.Exists(dlRoot))
        {
            foreach (var f in Directory.GetFiles(dlRoot, "*.log")
                         .OrderByDescending(Path.GetFileName)
                         .GroupBy(Path.GetFileNameWithoutExtension)
                         .Select(g => g.First())
                         .OrderByDescending(Path.GetFileName))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var display = Regex.Replace(name, @"_\d{6}$", "");
                Entries.Add(new LogEntry("下载", display, f, ExtractHms(Path.GetFileName(f)), false));
            }
        }
        else Entries.Add(new LogEntry("下载", "（还没有任务日志）", null, "", true));
        var dlFull = Path.Combine(root, "download.log");
        if (File.Exists(dlFull))
            Entries.Add(new LogEntry("下载", "完整日志（download.log）", dlFull, "", false));
    }

    /// <summary>启动：launch-{yyyyMMdd-HHmmss}.log，一条启动会话一个文件，按时间倒序。</summary>
    private void AddLaunches(string root)
    {
        if (!Directory.Exists(root)) { Entries.Add(new LogEntry("启动", "（还没有启动日志）", null, "", true)); return; }
        var files = Directory.GetFiles(root, "launch-*.log").OrderByDescending(Path.GetFileName);
        var any = false;
        foreach (var f in files)
        {
            var ts = Path.GetFileNameWithoutExtension(f).Replace("launch-", "");
            Entries.Add(new LogEntry("启动", FormatLaunchTime(ts), f, ts, false));
            any = true;
        }
        if (!any) Entries.Add(new LogEntry("启动", "（还没有启动日志）", null, "", true));
    }

    /// <summary>修复：downloads/自动修复*.log，最近 20 条（高频噪音）。</summary>
    private void AddRepairs(string root)
    {
        var dlRoot = Path.Combine(root, "downloads");
        if (Directory.Exists(dlRoot))
        {
            var repairs = Directory.GetFiles(dlRoot, "自动修复*.log")
                .OrderByDescending(Path.GetFileName)
                .Take(20);
            foreach (var f in repairs)
            {
                var name = Regex.Replace(Path.GetFileNameWithoutExtension(f), @"_\d{6}$", "");
                Entries.Add(new LogEntry("修复", name, f, ExtractHms(Path.GetFileName(f)), false));
            }
        }
        if (Entries.Count == 0) Entries.Add(new LogEntry("修复", "（还没有修复记录）", null, "", true));
    }

    /// <summary>异步读选中条目内容（大文件只留尾部显示，防卡 UI）。</summary>
    private async Task LoadContentAsync(LogEntry? entry)
    {
        if (entry is null) { Content = ""; SummaryText = ""; return; }
        SummaryText = entry.Display;
        if (entry.IsPlaceholder || entry.FilePath is null)
        {
            Content = entry.IsPlaceholder ? entry.Display : "（无内容）";
            return;
        }
        if (!File.Exists(entry.FilePath)) { Content = "（日志文件不存在，可能已清理）"; return; }
        try
        {
            var text = await File.ReadAllTextAsync(entry.FilePath);
            Content = text.Length <= MaxChars ? text : "…（文件过大，仅显示尾部）\n\n" + text[^MaxChars..];
        }
        catch (Exception ex) { Content = $"（无法读取日志：{ex.Message}）"; }
    }

    /// <summary>用系统记事本打开原文。</summary>
    [RelayCommand]
    private void OpenFile()
    {
        if (SelectedEntry?.FilePath is not { } path || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { Content = "（无法打开日志文件）"; }
    }

    private const int MaxChars = 512 * 1024;

    private static string ExtractHms(string fileName)
        => Regex.Match(fileName, @"_(\d{6})\.log$").Groups[1].Value is { Length: 6 } hms
            ? $"{hms[..2]}:{hms[2..4]}:{hms[4..]}"
            : "";

    private static string FormatLaunchTime(string ts)
    {
        // yyyyMMdd-HHmmss → yyyy-MM-dd HH:mm
        if (ts.Length >= 13 && ts[8] == '-')
            return $"{ts[..4]}-{ts[4..6]}-{ts[6..8]} {ts[9..11]}:{ts[11..13]}";
        return ts;
    }
}
