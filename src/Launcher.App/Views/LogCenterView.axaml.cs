using Avalonia.Controls;
using Launcher.App.ViewModels;

namespace Launcher.App.Views;

/// <summary>8-26 日志中心（窗口内覆盖层版）：由 MainWindow.LogCenterHost 挂载，主窗内渲染不失活。
/// 薄 code-behind：只设 DataContext + 关闭事件；全部逻辑在 LogCenterViewModel。</summary>
public partial class LogCenterView : UserControl
{
    public event Action? CloseRequested;

    public LogCenterView()
    {
        InitializeComponent();
        DataContext = new LogCenterViewModel();
        CloseBtn.Click += (_, _) => CloseRequested?.Invoke();
    }

    /// <summary>统一打开入口（Toast「查看日志」/ 下载页「日志」按钮共用）：
    /// 主窗可见 → 窗口内覆盖层（不失活不降级）；主窗不可见（罕见）→ 兜底独立窗。</summary>
    public static void Open()
    {
        if (Launcher.App.Services.DialogService.MainWindow() is { } main && main.IsVisible)
        {
            main.ShowLogCenter();
            return;
        }
        var win = new Window
        {
            Width = 760, Height = 560, MinWidth = 560, MinHeight = 380,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Avalonia.Media.Brushes.Transparent,
            Content = new LogCenterView(),
        };
        win.Show();
    }
}
