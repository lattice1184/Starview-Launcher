using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Services;
using Launcher.Core.Plugin;
using Launcher.Core.Utils;

namespace Launcher.App.ViewModels;

/// <summary>
/// 插件管理页（8-31）：导入 / 沙箱试运行 / 停用 / 删除 + 总开关。
/// 试运行走独立子进程行为监测（PluginTrialRunner），结果面板展示实测报告——不信任插件自声明，先跑给你看。
/// </summary>
public partial class PluginsViewModel : ViewModelBase
{
    public ObservableCollection<PluginItemVM> Items { get; } = [];

    /// <summary>插件总开关（LauncherSettings.EnablePlugins；改后重启生效）</summary>
    [ObservableProperty]
    public partial bool IsPluginsEnabled { get; set; }

    /// <summary>试运行进行中（列表按钮禁用）</summary>
    [ObservableProperty]
    public partial bool IsTrialRunning { get; set; }

    /// <summary>试运行进行中的提示文案</summary>
    [ObservableProperty]
    public partial string TrialBusyText { get; set; } = "";

    /// <summary>最近一次试运行结果（null = 还没试过）</summary>
    [ObservableProperty]
    public partial PluginTrialResult? LastTrial { get; set; }

    public string PluginsDir => AppPaths.DataRoot + System.IO.Path.DirectorySeparatorChar + "plugins";

    public bool HasPlugins => Items.Count > 0;

    /// <summary>试运行结果标题（按状态给结论）</summary>
    public string TrialStatusText => LastTrial?.Status switch
    {
        PluginTrialStatus.Clean => "✅ 干净：试运行期间没写任何文件",
        PluginTrialStatus.WroteScratchOnly => "✅ 干净：只写了沙盒临时目录（Temp 被重定向，已隔离）",
        PluginTrialStatus.WroteOutside => "⚠ 越界：写到了敏感目录（见下方清单）",
        PluginTrialStatus.Crashed => "💀 崩溃：插件 OnLoad 抛异常",
        PluginTrialStatus.TimedOut => "⏱ 超时：试运行超时挂起，已强制终止",
        PluginTrialStatus.NotAPlugin => "🚫 不是插件：dll 里没有 IStarviewPlugin 实现",
        _ => "",
    };

    public bool HasTrial => LastTrial is not null;
    public bool TrialWentOutside => LastTrial?.Status == PluginTrialStatus.WroteOutside;
    public bool HasOutsideWrites => LastTrial is not null && LastTrial.OutsideWrites.Count > 0;
    public bool HasScratchWrites => LastTrial is not null && LastTrial.ScratchWrites.Count > 0;
    public bool HasTrialLogs => LastTrial is not null && LastTrial.Logs.Count > 0;
    public bool HasTrialNote => LastTrial is not null && !string.IsNullOrEmpty(LastTrial.Note);
    public bool CanRunTrial => !IsTrialRunning;

    public PluginsViewModel()
    {
        IsPluginsEnabled = LauncherSettings.Current.EnablePlugins;
        Refresh();
    }

    partial void OnLastTrialChanged(PluginTrialResult? value)
    {
        OnPropertyChanged(nameof(TrialStatusText));
        OnPropertyChanged(nameof(HasTrial));
        OnPropertyChanged(nameof(TrialWentOutside));
        OnPropertyChanged(nameof(HasOutsideWrites));
        OnPropertyChanged(nameof(HasScratchWrites));
        OnPropertyChanged(nameof(HasTrialLogs));
        OnPropertyChanged(nameof(HasTrialNote));
    }

    partial void OnIsTrialRunningChanged(bool value)
    {
        TrialCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPluginsEnabledChanged(bool value)
    {
        var s = LauncherSettings.Current;
        s.EnablePlugins = value;
        s.Save();
        NotificationService.Info(value ? "插件已开启，重启启动器后生效" : "插件已关闭，下次启动不再加载");
    }

    /// <summary>重扫 plugins/ 目录刷新列表（导入/删除/启停后调用）。</summary>
    public void Refresh()
    {
        Items.Clear();
        foreach (var d in PluginManager.Instance.ListPlugins())
            Items.Add(new PluginItemVM { Source = d });
        OnPropertyChanged(nameof(HasPlugins));
    }

    /// <summary>导入插件（文件选择后 code-behind 调；复制到 plugins/ + 登记哈希 + 立即加载）。</summary>
    [RelayCommand]
    private async Task ImportAsync(string path)
    {
        var owner = DialogService.MainWindow();
        var result = await Task.Run(() => PluginManager.Instance.Import(path));
        Refresh();
        if (!result.Ok)
        {
            if (owner is not null) await DialogService.Warn(owner, "导入插件失败", result.Message ?? "未知原因");
            else NotificationService.Info(result.Message ?? "导入失败");
            return;
        }
        NotificationService.Info($"已导入 {System.IO.Path.GetFileName(path)}");
    }

    /// <summary>沙箱试运行：独立子进程跑 OnLoad，行为监测（TEMP 重定向 + 敏感目录监听 + 超时强杀）。</summary>
    [RelayCommand(CanExecute = nameof(CanRunTrial))]
    private async Task TrialAsync(PluginItemVM item)
    {
        if (IsTrialRunning) return;
        IsTrialRunning = true;
        TrialBusyText = $"正在试运行 {item.DisplayName}…（最多 10 秒）";
        LastTrial = null;
        try
        {
            LastTrial = await PluginTrialRunner.RunAsync(item.FilePath);
        }
        catch (Exception ex)
        {
            LastTrial = new PluginTrialResult(PluginTrialStatus.Crashed, [], [], [], "试运行出错：" + ex.Message);
        }
        finally
        {
            IsTrialRunning = false;
            TrialBusyText = "";
        }
    }

    /// <summary>停用 / 启用（停用尝试运行时卸载；失败提示重启生效）。</summary>
    [RelayCommand]
    private async Task ToggleEnabledAsync(PluginItemVM item)
    {
        var owner = DialogService.MainWindow();
        var name = item.DisplayName;
        if (item.Source.Enabled)
        {
            if (owner is not null &&
                !await DialogService.Confirm(owner, $"停用 {name}？停用后不再加载，可随时重新启用。", "停用插件", "停用", "取消"))
                return;
            var r = await Task.Run(() => PluginManager.Instance.Disable(item.FilePath));
            if (!r.Ok) { NotificationService.Info(r.Message ?? "停用失败"); return; }
            NotificationService.Info(r.Deferred ? $"已停用 {name}（完全卸载需重启启动器）" : $"已停用 {name}");
        }
        else
        {
            var r = await Task.Run(() => PluginManager.Instance.Enable(item.FilePath));
            if (!r.Ok) { NotificationService.Info(r.Message ?? "启用失败"); return; }
            NotificationService.Info(r.Message is null ? $"已启用 {name}" : $"{name}：{r.Message}");
        }
        Refresh();
    }

    /// <summary>删除插件（确认后删文件 + 配置目录 + 登记）。</summary>
    [RelayCommand]
    private async Task DeleteAsync(PluginItemVM item)
    {
        var owner = DialogService.MainWindow();
        if (owner is null) return;
        var name = item.DisplayName;
        if (!await DialogService.Confirm(owner,
                $"删除 {name}？会同时删除它的插件文件和配置目录，此操作不可撤销。", "删除插件", "删除", "取消"))
            return;
        var r = await Task.Run(() => PluginManager.Instance.Delete(item.FilePath));
        Refresh();
        if (!r.Ok) NotificationService.Info(r.Message ?? "删除失败");
        else NotificationService.Info($"已删除 {name}");
    }
}
