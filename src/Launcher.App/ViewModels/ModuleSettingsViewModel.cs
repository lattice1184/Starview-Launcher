using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Services;
using Launcher.Core.Utils;

namespace Launcher.App.ViewModels;

/// <summary>存储分组行（占用 + 上限 + 清理）</summary>
public sealed partial class StorageGroupVM : ObservableObject
{
    public string Key { get; }
    public string Name { get; }

    [ObservableProperty]
    public partial string SizeText { get; set; } = "…";

    [ObservableProperty]
    public partial string CapText { get; set; } = "0";

    [ObservableProperty]
    public partial bool IsOverLimit { get; set; }

    [ObservableProperty]
    public partial bool CanDelete { get; set; }

    /// <summary>最近一次扫描结果（清理/刷新用）</summary>
    public StorageGroup? LatestGroup { get; set; }

    public IRelayCommand CleanCommand { get; }
    public Action<StorageGroupVM>? CleanRequested { get; set; }
    private readonly Action<StorageGroupVM>? _capChanged;

    public StorageGroupVM(string key, string name, Action<StorageGroupVM>? capChanged)
    {
        Key = key;
        Name = name;
        _capChanged = capChanged;
        CleanCommand = new RelayCommand(() => CleanRequested?.Invoke(this));
    }

    partial void OnCapTextChanged(string value) => _capChanged?.Invoke(this);
}

/// <summary>设置页「存储」分区：存储占用统计 + 上限 + 清理</summary>
public sealed partial class StorageSettingsViewModel : ObservableObject
{
    public ObservableCollection<StorageGroupVM> StorageGroups { get; } = [];

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    public AsyncRelayCommand ReloadStatsCommand { get; }

    /// <summary>组名静态表（与 StorageScanner 的 DisplayName 一致，构造时预置行）</summary>
    private static readonly string[] GroupNames = ["游戏文件", "下载缓存", "日志", "备份导出"];

    public StorageSettingsViewModel()
    {
        // 预置 4 个固定组行（顺序与 StorageScanner.Scan 一致：game/downloads/logs/backups）
        string[] keys = ["game", "downloads", "logs", "backups"];
        for (var i = 0; i < keys.Length; i++)
        {
            var vm = new StorageGroupVM(keys[i], GroupNames[i], OnCapChanged);
            vm.CleanRequested = CleanGroup;
            StorageGroups.Add(vm);
        }

        ReloadStatsCommand = new AsyncRelayCommand(ReloadStatsAsync);
    }

    /// <summary>上限改动（MB；0 = 不限）：解析合法即写盘 + 刷新超限状态</summary>
    private void OnCapChanged(StorageGroupVM vm)
    {
        var s = LauncherSettings.Current;
        if (!int.TryParse(vm.CapText.Trim(), out var mb) || mb < 0) return; // 非法输入不改（保留旧值）
        s.StorageCapsMb[vm.Key] = mb;
        s.Save();
        UpdateOverLimit(vm);
    }

    /// <summary>扫描全部存储位置（Task.Run 防卡 UI），回填各组占用/上限/超限</summary>
    private async Task ReloadStatsAsync()
    {
        IsScanning = true;
        List<StorageGroup> groups;
        try { groups = await Task.Run(() => StorageScanner.Scan()); }
        catch { groups = []; }
        var caps = LauncherSettings.Current.StorageCapsMb;
        foreach (var g in groups)
        {
            var row = StorageGroups.FirstOrDefault(x => x.Key == g.Key);
            if (row is null) continue;
            row.LatestGroup = g;
            row.SizeText = StorageScanner.FormatSize(g.TotalBytes);
            row.CanDelete = g.Items.Any(i => i.CanDelete);
            row.CapText = caps.TryGetValue(g.Key, out var mb) ? mb.ToString() : "0";
            UpdateOverLimit(row);
        }
        IsScanning = false;
    }

    private static void UpdateOverLimit(StorageGroupVM vm)
    {
        var caps = LauncherSettings.Current.StorageCapsMb;
        var capMb = caps.TryGetValue(vm.Key, out var mb) ? mb : 0;
        vm.IsOverLimit = capMb > 0 && vm.LatestGroup?.TotalBytes > capMb * 1024L * 1024;
    }

    /// <summary>清理一组（确认后删除可删位置，释放空间），完成后刷新</summary>
    private async void CleanGroup(StorageGroupVM vm)
    {
        if (vm.LatestGroup is null) { NotificationService.Info("还没有扫描结果，先点「重新扫描」"); return; }
        var owner = DialogService.MainWindow();
        if (owner is null || !await DialogService.Confirm(owner,
                $"清理「{vm.Name}」（当前占用 {vm.SizeText}）？\n\n删除后不可恢复，确认清理？", "清理", "清理", "取消"))
        {
            return;
        }
        try
        {
            var group = vm.LatestGroup;
            var freed = await Task.Run(() => StorageScanner.DeleteGroup(group));
            NotificationService.Success($"已清理，释放 {StorageScanner.FormatSize(freed)}");
        }
        catch (Exception ex)
        {
            NotificationService.Error($"清理失败: {ex.Message}");
        }
        await ReloadStatsAsync();
    }
}
