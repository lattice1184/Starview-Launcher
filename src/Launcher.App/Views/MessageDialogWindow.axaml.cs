using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Launcher.Core.Model.Modrinth;
using Launcher.Core.Services;

namespace Launcher.App.Views;

/// <summary>
/// 模态确认对话框（DialogService.Confirm 载体）：消息 + 确认/取消按钮。
/// 8-22 扩展：安装路径确认（可编辑目录 + 实时落点预览 + 浏览），返回 string?。
/// </summary>
public partial class MessageDialogWindow : Window
{
    private TaskCompletionSource<bool>? _result;
    private TaskCompletionSource<string?>? _resultPath;
    private string? _instanceId;
    private ProjectType _pathType;

    public MessageDialogWindow()
    {
        InitializeComponent();
        global::Launcher.App.Animations.UiAnim.AttachDialog(this, Root);
    }

    /// <summary>展示确认框并等待用户决定（cancel 传 "" 隐藏取消按钮）</summary>
    public static async Task<bool> Confirm(Window? owner, string message,
        string title, string confirm, string cancel)
    {
        var win = new MessageDialogWindow { Title = title };
        win.MessageText.Text = message;
        win.ConfirmBtn.Content = confirm;
        win.CancelBtn.Content = cancel;
        win.CancelBtn.IsVisible = cancel.Length > 0;
        return await ShowAndWaitAsync(owner, win, win._result = new());
    }

    /// <summary>
    /// 警告对话框：红字加粗原因 + 普通色说明（前提不满足/操作失败弹窗化，替代无着重色的状态栏小字）。
    /// </summary>
    public static async Task<bool> Warn(Window? owner, string reason, string detail,
        string title, string confirm = "确定", string cancel = "取消")
    {
        var win = new MessageDialogWindow { Title = title };
        win.MessageText.Text = reason;
        win.MessageText.Foreground = new SolidColorBrush(Color.Parse("#E05A5A")); // Danger 红
        win.MessageText.FontWeight = FontWeight.SemiBold;                          // 加粗表示原因
        win.DetailText.Text = detail;
        win.DetailText.IsVisible = detail.Length > 0;
        win.ConfirmBtn.Content = confirm;
        win.CancelBtn.Content = cancel;
        win.CancelBtn.IsVisible = cancel.Length > 0;
        return await ShowAndWaitAsync(owner, win, win._result = new());
    }

    /// <summary>8-23 PCL2 式安装路径选择：直接弹系统目录选择器（默认指向对应实例 mods 落点），
    /// 一步到位不再「选实例下拉 + 文本框」两步；null = 取消（不安装），否则用户确认的目录。
    /// StorageProvider 不可用（owner 不可见等）时回退旧文本框对话框。</summary>
    public static async Task<string?> ConfirmInstallPathAsync(Window? owner, string gameDir, string instanceId, ProjectType type)
    {
        var defaultPath = EcosystemService.ResolveInstallPath(gameDir, instanceId, type);
        try
        {
            if (owner is { PlatformImpl: not null, IsVisible: true })
            {
                var start = await owner.StorageProvider.TryGetFolderFromPathAsync(defaultPath);
                var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "选择安装目录（默认已指向对应实例的 mods 文件夹）",
                    AllowMultiple = false,
                    SuggestedStartLocation = start,
                });
                if (folders.Count > 0) return folders[0].Path.LocalPath;
                return null; // 用户取消 → 不安装
            }
        }
        catch { /* 目录选择器异常 → 回退文本框对话框 */ }
        return await ShowPathDialogAsync(owner, defaultPath, instanceId, type);
    }

    /// <summary>回退：旧文本框 + 浏览 + 实时落点预览（目录选择器不可用时的保底）</summary>
    private static async Task<string?> ShowPathDialogAsync(Window? owner, string defaultPath, string instanceId, ProjectType type)
    {
        var win = new MessageDialogWindow { Title = "确认安装位置" };
        win.MessageText.Text = "安装目录（可以改成别的实例目录或自定义文件夹）：";
        win.PathPanel.IsVisible = true;
        win.PathInput.Text = defaultPath;
        win._instanceId = instanceId;
        win._pathType = type;
        win.UpdatePathPreview();
        win.ConfirmBtn.Content = "开始安装";
        win.CancelBtn.Content = "取消";
        return await ShowAndWaitAsync(owner, win, win._resultPath = new());
    }

    private static async Task<T> ShowAndWaitAsync<T>(Window? owner, MessageDialogWindow win, TaskCompletionSource<T> tcs)
    {
        try
        {
            // owner 不可见/未加载时 ShowDialog 抛异常（静默失败导致确认框不出现）——兜底独立窗口
            if (owner is { PlatformImpl: not null, IsVisible: true }) await win.ShowDialog(owner);
            else { win.WindowStartupLocation = WindowStartupLocation.CenterScreen; win.Show(); }
        }
        catch
        {
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            win.Show();
        }
        return await tcs.Task;
    }

    private void UpdatePathPreview()
    {
        if (_pathType == default) return; // 非路径确认对话框不刷预览
        var dir = PathInput.Text?.Trim() ?? "";
        var target = EcosystemService.ResolveInstallPath(dir, _instanceId ?? "", _pathType);
        PathPreviewText.Text = $"将装到：{target}";
    }

    private void OnPathChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e) => UpdatePathPreview();

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择安装目录",
            AllowMultiple = false,
        });
        if (folders.Count > 0) PathInput.Text = folders[0].Path.LocalPath;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        _result?.TrySetResult(true);
        _resultPath?.TrySetResult(PathInput.Text?.Trim() ?? "");
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _result?.TrySetResult(false);
        _resultPath?.TrySetResult(null);
        Close();
    }

    /// <summary>兜底：标题栏 X / Alt+F4 / ESC 关闭也完成 Task（防调用方永久挂起）</summary>
    protected override void OnClosed(EventArgs e)
    {
        _result?.TrySetResult(false);
        _resultPath?.TrySetResult(null);
        base.OnClosed(e);
    }

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            _result?.TrySetResult(false);
            _resultPath?.TrySetResult(null);
            Close();
            return;
        }
        base.OnKeyDown(e);
    }
}
