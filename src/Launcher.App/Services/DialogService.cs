using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Launcher.App.Views;
using Launcher.Core.Model.Modrinth;
using Launcher.Core.Services;

namespace Launcher.App.Services;

/// <summary>
/// 全局确认/提示对话框服务。8-24 主路径改窗口内覆盖层（DialogOverlay，经 MainWindow.DialogHost）：
/// 不再开第二个顶层模态窗口 → 主窗口不失活、亚克力不降级，透明档弹窗期间保持透明。
/// 主窗口不可见等边角情况回退旧模态窗口（MessageDialogWindow）。API 签名不变。
/// </summary>
public static class DialogService
{
    /// <summary>
    /// 确认对话框 → true=确认 / false=取消。cancel 传空字符串隐藏取消按钮（仅确定）。
    /// </summary>
    public static Task<bool> Confirm(Window? owner, string message,
        string title = "确认", string confirm = "确定", string cancel = "取消")
    {
        var main = MainWindow();
        if (main is { IsVisible: true })
            return DialogOverlay.ConfirmAsync(main, message, title, confirm, cancel);
        return MessageDialogWindow.Confirm(owner, message, title, confirm, cancel); // 兜底
    }

    /// <summary>信息对话框（仅确定）</summary>
    public static Task<bool> Info(Window? owner, string message, string title = "提示")
    {
        var main = MainWindow();
        if (main is { IsVisible: true })
            return DialogOverlay.InfoAsync(main, message, title);
        return MessageDialogWindow.Confirm(owner, message, title, "知道了", ""); // 兜底
    }

    /// <summary>
    /// 警告对话框：红字加粗原因 + 普通色说明。
    /// 返回 true=确认（如"立即下载并启动"） / false=取消。
    /// </summary>
    public static Task<bool> Warn(Window? owner, string reason, string detail,
        string title = "无法继续", string confirm = "确定", string cancel = "取消")
    {
        var main = MainWindow();
        if (main is { IsVisible: true })
            return DialogOverlay.WarnAsync(main, reason, detail, title, confirm, cancel);
        return MessageDialogWindow.Warn(owner, reason, detail, title, confirm, cancel); // 兜底
    }

    /// <summary>8-22 安装前路径确认：优先系统目录选择器（一步到位）；不可用时回退文本框覆盖层。
    /// null = 取消中止安装，否则用户确认的目录。</summary>
    public static Task<string?> ConfirmInstallPath(Window? owner, string gameDir, string instanceId, ProjectType type)
    {
        var main = MainWindow();
        if (main is { IsVisible: true, PlatformImpl: not null })
        {
            var defaultPath = EcosystemService.ResolveInstallPath(gameDir, instanceId, type);
            try
            {
                return PickFolderAsync(main, defaultPath)
                    ?? DialogOverlay.ShowPathAsync(main, defaultPath, instanceId, type);
            }
            catch { /* 目录选择器异常 → 回退文本框覆盖层 */ }
            return DialogOverlay.ShowPathAsync(main, defaultPath, instanceId, type);
        }
        return MessageDialogWindow.ConfirmInstallPathAsync(owner, gameDir, instanceId, type); // 兜底
    }

    /// <summary>系统目录选择器：null = 用户取消（不安装）</summary>
    private static async Task<string?> PickFolderAsync(MainWindow main, string defaultPath)
    {
        var start = await main.StorageProvider.TryGetFolderFromPathAsync(defaultPath);
        var folders = await main.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择安装目录（默认已指向对应实例的 mods 文件夹）",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    /// <summary>取当前主窗口</summary>
    public static MainWindow? MainWindow()
        => ApplicationLifetimeHolder.Desktop?.MainWindow as MainWindow;
}

/// <summary>应用生命周期持有（避免 Services 直接依赖 App 类）</summary>
internal static class ApplicationLifetimeHolder
{
    public static Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime? Desktop =>
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime);
}
