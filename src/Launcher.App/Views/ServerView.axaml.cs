using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using Launcher.App.Services;
using Launcher.App.ViewModels;

namespace Launcher.App.Views;

public partial class ServerView : UserControl
{
    public ServerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ServerViewModel vm)
                vm.Logs.CollectionChanged += OnLogsChanged;
        };
    }

    /// <summary>建议档位按钮（0=测试低配 / 1=推荐 / 2=高配），填充建议编辑框</summary>
    private void OnPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string idx } && int.TryParse(idx, out var i)
            && DataContext is ServerViewModel vm)
            vm.ApplyPreset(i);
    }

    /// <summary>服务端日志到达时控制台自动滚动到底部</summary>
    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => LogScroll?.ScrollToEnd());
    }

    private void OnCommandKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Send();
    }

    private void OnSendClick(object? sender, RoutedEventArgs e) => Send();

    /// <summary>复制服务端控制台全部日志到剪贴板</summary>
    private async void OnCopyLogs(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ServerViewModel vm || vm.Logs.Count == 0)
        {
            Launcher.App.Services.NotificationService.Error("控制台暂无日志");
            return;
        }
        var top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard is not { } cb) return;
        await cb.SetTextAsync(string.Join(Environment.NewLine, vm.Logs));
        Launcher.App.Services.NotificationService.Success($"已复制 {vm.Logs.Count} 行日志");
    }

    /// <summary>导出日志（游戏/崩溃日志 + 系统信息 zip）</summary>
    private async void OnExportLogs(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择日志保存位置",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || !folders[0].Path.IsAbsoluteUri) return;
        try
        {
            var path = await Task.Run(() => Launcher.App.Services.LogExportHelper.ExportLogs(folders[0].Path.LocalPath));
            Launcher.App.Services.NotificationService.Success($"日志已导出：{Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Launcher.App.Services.NotificationService.Error($"导出失败: {ex.Message}");
        }
    }

    private void Send()
    {
        if (DataContext is not ServerViewModel vm) return;
        var box = this.FindControl<TextBox>("CommandBox");
        if (box is null) return;
        var cmd = box.Text;
        vm.SendCommandCommand.Execute(cmd);
        box.Text = "";
    }
}
