using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Launcher.App.Animations;
using Launcher.App.ViewModels;

namespace Launcher.App.Views;

public partial class DownloadView : UserControl
{
    private DownloadViewModel? _vm;
    private bool _firstFade = true; // 首次进入页面不淡入，只响应切换

    public DownloadView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not DownloadViewModel vm) return;
            _vm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            vm.Tasks.CollectionChanged += OnListChanged;   // 全局任务增删/完成 → 队列面板淡入
            vm.History.CollectionChanged += OnListChanged; // 历史增删 → 队列面板淡入
        };
    }

    private void OnListChanged(object? s, NotifyCollectionChangedEventArgs e) => FadeIn(QueuePanelHost);

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // tab 切换（ActiveTab 变化 / 队列选中变化）→ 下一帧新内容已就位，淡入当前可见面板
        if (e.PropertyName is "ActiveTab" or "IsQueueTabSelected" or "IsNotQueueTabSelected")
            Dispatcher.UIThread.Post(FadeInCurrentTab);
    }

    private void FadeInCurrentTab()
    {
        var target = _vm is { IsQueueTabSelected: true } ? QueuePanelHost : ActiveTabHost;
        FadeIn(target);
    }

    /// <summary>内容淡入 + 4px 上移（200ms Standard）。host=target 互斥打断连点；首次渲染不淡入。</summary>
    private void FadeIn(Control target)
    {
        if (_firstFade)
        {
            _firstFade = false;
            return;
        }
        if (!target.IsEffectivelyVisible) return;
        target.Opacity = 0;
        var tx = new TranslateTransform(0, 4);
        target.RenderTransform = tx;
        UiAnim.Animate(200, UiAnim.Curves.Standard, e =>
        {
            target.Opacity = e;
            tx.Y = 4 * (1 - e);
        }, null, target);
    }

    /// <summary>8-26 打开日志中心（下载页「日志」按钮）——窗口内覆盖层，主窗不失活不降级</summary>
    private void OnOpenDownloadLog(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Views.LogCenterView.Open();
}
