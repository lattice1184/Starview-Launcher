using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Launcher.Core.Model.Modrinth;
using Launcher.Core.Services;

namespace Launcher.App.Views;

/// <summary>
/// 窗口内模态确认覆盖层（DialogService 载体）。8-24 由 MessageDialogWindow 抽取改造：
/// 主窗口内渲染（经 MainWindow.DialogHost），不再开第二个顶层窗口 → 主窗口不失活，
/// 亚克力合成不降级，弹窗期间保持透明（根治「点确认弹窗背景回调实色」）。
/// </summary>
public partial class DialogOverlay : UserControl
{
    private TaskCompletionSource<bool>? _result;
    private TaskCompletionSource<string?>? _resultPath;
    private MainWindow? _host;
    private string? _instanceId;
    private ProjectType _pathType;

    public DialogOverlay()
    {
        InitializeComponent();
        KeyDown += OnOverlayKeyDown;
    }

    /// <summary>确认框（cancel 传空隐藏取消按钮）</summary>
    public static Task<bool> ConfirmAsync(MainWindow host, string message,
        string title, string confirm, string cancel)
    {
        var o = Create(host, title);
        o.MessageText.Text = message;
        o.ConfirmBtn.Content = confirm;
        o.CancelBtn.Content = cancel;
        o.CancelBtn.IsVisible = cancel.Length > 0;
        return ShowAsync(host, o, o._result = new());
    }

    /// <summary>警告框：红字加粗原因 + 普通色说明</summary>
    public static Task<bool> WarnAsync(MainWindow host, string reason, string detail,
        string title, string confirm, string cancel)
    {
        var o = Create(host, title);
        o.MessageText.Text = reason;
        o.MessageText.Foreground = new SolidColorBrush(Color.Parse("#E05A5A")); // Danger 红
        o.MessageText.FontWeight = FontWeight.SemiBold;
        o.DetailText.Text = detail;
        o.DetailText.IsVisible = detail.Length > 0;
        o.ConfirmBtn.Content = confirm;
        o.CancelBtn.Content = cancel;
        o.CancelBtn.IsVisible = cancel.Length > 0;
        return ShowAsync(host, o, o._result = new());
    }

    /// <summary>信息框（仅确定）</summary>
    public static Task<bool> InfoAsync(MainWindow host, string message, string title)
        => ConfirmAsync(host, message, title, "知道了", "");

    /// <summary>安装路径文本框确认（目录选择器不可用时的保底）：null = 取消</summary>
    public static Task<string?> ShowPathAsync(MainWindow host, string defaultPath, string instanceId, ProjectType type)
    {
        var o = Create(host, "确认安装位置");
        o.MessageText.Text = "安装目录（可以改成别的实例目录或自定义文件夹）：";
        o.PathPanel.IsVisible = true;
        o.PathInput.Text = defaultPath;
        o._instanceId = instanceId;
        o._pathType = type;
        o.UpdatePathPreview();
        o.ConfirmBtn.Content = "开始安装";
        o.CancelBtn.Content = "取消";
        return ShowAsync(host, o, o._resultPath = new());
    }

    private static DialogOverlay Create(MainWindow host, string title)
    {
        var o = new DialogOverlay { _host = host };
        o.TitleText.Text = title;
        return o;
    }

    private static async Task<T> ShowAsync<T>(MainWindow host, DialogOverlay overlay, TaskCompletionSource<T> tcs)
    {
        host.ShowDialogOverlay(overlay);
        // 焦点必须落在覆盖层内（scrim 只挡鼠标不挡键盘）：路径框聚焦输入，其余聚焦确定按钮
        if (overlay.PathPanel.IsVisible) overlay.PathInput.Focus();
        else overlay.ConfirmBtn.Focus();
        return await tcs.Task;
    }

    private void Finish(bool ok)
    {
        if (ok) _result?.TrySetResult(true);
        else _result?.TrySetResult(false);
        _resultPath?.TrySetResult(ok ? PathInput.Text?.Trim() ?? "" : null);
        _host?.HideDialogOverlay();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Finish(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Finish(false);

    private void OnOverlayKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
    }

    private void OnPathChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e) => UpdatePathPreview();

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (_host is not { PlatformImpl: not null, IsVisible: true }) return;
        var folders = await _host.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择安装目录",
            AllowMultiple = false,
        });
        if (folders.Count > 0) PathInput.Text = folders[0].Path.LocalPath;
    }

    private void UpdatePathPreview()
    {
        if (_pathType == default) return; // 非路径确认对话框不刷预览
        var dir = PathInput.Text?.Trim() ?? "";
        var target = EcosystemService.ResolveInstallPath(dir, _instanceId ?? "", _pathType);
        PathPreviewText.Text = $"将装到：{target}";
    }
}
