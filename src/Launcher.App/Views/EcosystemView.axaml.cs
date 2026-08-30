using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Media;
using Launcher.App.Animations;
using Launcher.App.ViewModels;

namespace Launcher.App.Views;

public partial class EcosystemView : UserControl
{
    private bool _firstFade = true; // 首次进入页面不淡入，只响应刷新

    public EcosystemView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not EcosystemViewModel vm) return;
            vm.Cards.CollectionChanged += OnCardsChanged;
        };
    }

    /// <summary>8-25 下拉列表式安装：点「安装 ▾」在指针处弹出动作菜单（安装到当前实例 / 收藏）——
    /// 左键触发（ContextMenu 默认右键，这里 Click 显式打开）。
    /// 8-30 嵌套按钮冒泡根治：内层 Button 点击会冒泡到外层整卡 Button → 点安装也跳详情；
    /// Handled=true 阻断向上路由，安装按钮独立不触发 OpenDetailCommand。</summary>
    private void OnInstallMenuClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true; // 8-30 阻断冒泡：点「安装」不触发整卡详情跳转
        if (sender is Button { ContextMenu: { } menu } b)
            menu.Open(b);
    }

    /// <summary>8-25 修菜单命令失效：ContextMenu 是独立 Popup 视觉树，`$parent[UserControl]` 走不到父 VM →
    /// 菜单项 DataContext 是卡片 VM（placement target 继承），父 VM 从这里拿，直调 InstallCardCommand。</summary>
    private void OnInstallMenuItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: ProjectCardVM card }
            && DataContext is EcosystemViewModel vm)
            vm.InstallCardCommand.Execute(card);
    }

    /// <summary>搜索/分页刷新（Clear 起手重填）→ 结果列表淡入 + 4px 上移（180ms Standard）</summary>
    private void OnCardsChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Reset && e.NewStartingIndex != 0) return;
        FadeIn(CardsScroll);
    }

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
        UiAnim.Animate(180, UiAnim.Curves.Standard, e =>
        {
            target.Opacity = e;
            tx.Y = 4 * (1 - e);
        }, null, target);
    }
}
