using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Launcher.App.Services;
using Launcher.Core.Download;
using Launcher.Core.Utils;

namespace Launcher.App.Views;

/// <summary>
/// 8-31 转窗口内覆盖层：导出整合包设置（内容勾选 / 输出位置 / 包名描述）。
/// 由 MainWindow.DialogHost 挂载（主窗不失活 → 亚克力不降级，根治「导出后主窗变回实色」），
/// 确认返回 ExportSettings；取消/关闭返回 null。
/// </summary>
public partial class ExportDialogWindow : UserControl
{
    private TaskCompletionSource<ExportSettings?>? _result;
    private MainWindow? _host;

    public ExportDialogWindow()
    {
        InitializeComponent();
        KeyDown += OnOverlayKeyDown;
    }

    /// <summary>展示导出设置框（host 挂载主窗；defaultDir 默认输出目录；defaultName 默认包名）</summary>
    public static Task<ExportSettings?> ShowAsync(MainWindow host, string defaultName, string defaultDir)
    {
        var view = new ExportDialogWindow { _host = host };
        view.NameBox.Text = defaultName;
        view.PathBox.Text = defaultDir;
        var tcs = new TaskCompletionSource<ExportSettings?>();
        view._result = tcs;
        host.ShowDialogOverlay(view);
        return tcs.Task;
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var picker = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (picker is null) return;
        var folders = await picker.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择导出位置",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].Path.IsAbsoluteUri)
            PathBox.Text = folders[0].Path.LocalPath;
    }

    private void OnExport(object? sender, RoutedEventArgs e)
    {
        var dir = PathBox.Text?.Trim() ?? "";
        if (dir.Length == 0)
        {
            PathBox.Text = GameDirectory.InstallDir();
            dir = PathBox.Text;
        }
        // 包名清洗（非法文件名字符 → 下划线）
        var name = ModpackImporter.SafeId(NameBox.Text?.Trim() ?? "");
        _result?.TrySetResult(new ExportSettings(
            IncludeMods.IsChecked == true,
            IncludeSaves.IsChecked == true,
            IncludeConfig.IsChecked == true,
            IncludeResourcepacks.IsChecked == true,
            IncludeShaders.IsChecked == true,
            IncludeOptions.IsChecked == true,
            dir,
            name,
            DescBox.Text?.Trim() ?? ""));
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _result?.TrySetResult(null);
        Close();
    }

    private void OnOverlayKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { OnCancel(sender, e); e.Handled = true; }
    }

    /// <summary>收起覆盖层（DialogHost 由主窗持有）</summary>
    private void Close() => _host?.HideDialogOverlay();
}
