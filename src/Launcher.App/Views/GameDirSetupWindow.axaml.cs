using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Launcher.Core.Utils;

namespace Launcher.App.Views;

/// <summary>
/// 首次启动的游戏目录询问窗：选择游戏版本/模组/存档的存放文件夹。
/// 确认后写入 settings.json（LauncherSettings.GameDirectory），此后不再询问。
/// </summary>
public partial class GameDirSetupWindow : Window
{
    public GameDirSetupWindow()
    {
        InitializeComponent();
        PathBox.Text = GameDirectory.InstallDir();
        global::Launcher.App.Animations.UiAnim.AttachDialog(this, Root);
    }

    /// <summary>浏览…：系统文件夹选择器</summary>
    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择游戏目录",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].Path.IsAbsoluteUri)
            PathBox.Text = folders[0].Path.LocalPath;
    }

    /// <summary>使用默认目录：重置为自建目录（D 盘优先）</summary>
    private void OnReset(object? sender, RoutedEventArgs e) => PathBox.Text = GameDirectory.OwnDefault();

    /// <summary>跳过：用默认目录（D 盘优先）并落盘——跳过也记住，下次不再弹（8-31 修「重装后时弹时不弹」）</summary>
    private void OnSkip(object? sender, RoutedEventArgs e)
    {
        var s = LauncherSettings.Current;
        s.GameDirectory = GameDirectory.OwnDefault();
        s.Save();
        global::Launcher.Core.AppState.UpdateInstanceRoot(s.GameDirectory);
        global::Launcher.Core.Utils.GameDirectory.InvalidateScanCache();
        Close();
    }

    /// <summary>开始使用：保存设置并关闭</summary>
    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var dir = PathBox.Text?.Trim() ?? "";
        if (dir.Length > 0)
        {
            var s = LauncherSettings.Current;
            s.GameDirectory = dir;
            s.Save();
            // 8-22：实例根跟随实际选择（Core 层修复/日志定位用正确目录）
            global::Launcher.Core.AppState.UpdateInstanceRoot(dir);
            global::Launcher.Core.Utils.GameDirectory.InvalidateScanCache();
            // 8-23 修复：确认后立即重建主页版本列表——否则首跑选目录后仍显示默认目录空快照
            try { _ = global::Launcher.App.ViewModels.MainViewModel.Current?.Home.RefreshVersionsAsync(); }
            catch { /* 刷新失败不阻塞关窗 */ }
        }
        Close();
    }
}
