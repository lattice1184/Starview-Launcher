using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Services;
using Launcher.Core.Utils;

namespace Launcher.App.Views;

/// <summary>存储空间行：路径 + 大小 + 是否可删除</summary>
public sealed partial class StorageItemVM : ObservableObject
{
    public string Path { get; }
    public bool CanDelete { get; }
    public bool IsFile { get; }
    public bool IsHeader { get; }

    [ObservableProperty]
    public partial string SizeText { get; set; } = "…";

    public IRelayCommand DeleteCommand { get; }
    public Action<StorageItemVM>? DeleteRequested { get; set; }

    public StorageItemVM(string path, bool canDelete = false, bool isFile = false, bool isHeader = false)
    {
        Path = path;
        CanDelete = canDelete;
        IsFile = isFile;
        IsHeader = isHeader;
        DeleteCommand = new RelayCommand(() => DeleteRequested?.Invoke(this));
    }
}

/// <summary>存储空间窗口：列出启动器全部文件位置与占用，可清理日志/缓存/崩溃报告</summary>
public partial class StorageWindow : Window
{
    public ObservableCollection<StorageItemVM> Items { get; } = [];

    public StorageWindow()
    {
        InitializeComponent();
        global::Launcher.App.Animations.UiAnim.AttachDialog(this, Root);
        DataContext = this;
        Opened += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Items.Clear();
        var gameDir = LauncherSettings.Current.GameDirectory ?? GameDirectory.Detect();
        var appData = Path.Combine(AppPaths.DataRoot);

        // 分组扫描（Core 层；设置页「模块与存储」分区共用同一逻辑）
        var groups = await Task.Run(() => StorageScanner.Scan(gameDir, appData));
        foreach (var g in groups)
        {
            Add($"—— {g.DisplayName} ——", isHeader: true);
            if (g.Items.Count == 0) { Add("（无）", isHeader: true); continue; }
            foreach (var item in g.Items)
                Add(item.Path, item.CanDelete ? "（可删）" : null, canDelete: item.CanDelete, isFile: item.IsFile);
        }

        // 后台算大小（大目录 GB 级，逐项异步；防 UI 卡）
        var snap = Items.Where(i => !i.IsHeader).ToList();
        await Task.Run(() =>
        {
            foreach (var item in snap)
                item.SizeText = StorageScanner.FormatSize(StorageScanner.ItemSize(item.Path, item.IsFile));
        });
    }

    private void Add(string path, string? hint = null, bool canDelete = false, bool isFile = false, bool isHeader = false)
        => Items.Add(new StorageItemVM(isHeader ? $"— {path} —" : hint is null ? path : $"{path} {hint}",
            canDelete, isFile, isHeader)
        {
            DeleteRequested = OnDeleteRequested,
        });

    /// <summary>删除确认（对话框）→ 删除文件/目录 → 移除列表项。
    /// 8-22 全栈排查：旧实现 Task.Run 内 ShowDialog（必须 UI 线程）→ 跨线程异常被吞 → 确认框永不出现、删除永远不执行</summary>
    private async void OnDeleteRequested(StorageItemVM item)
    {
        var owner = DialogService.MainWindow();
        if (owner is null || !await DialogService.Confirm(owner,
                $"删除：{item.Path}\n\n此操作不可恢复，确认删除？", "删除", "删除", "取消"))
        {
            return;
        }
        try
        {
            await Task.Run(() =>
            {
                if (item.IsFile) { if (File.Exists(item.Path)) File.Delete(item.Path); }
                else if (Directory.Exists(item.Path)) Directory.Delete(item.Path, true);
            });
            Dispatcher.UIThread.Post(() =>
            {
                Items.Remove(item);
                NotificationService.Success("已删除");
            });
        }
        catch (Exception ex)
        {
            NotificationService.Error($"删除失败: {ex.Message}");
        }
    }
}
