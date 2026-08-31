using Avalonia.Controls;
using Avalonia.Input;
using Launcher.App.ViewModels;
using Launcher.App.Services;
namespace Launcher.App.Views;

public partial class VersionDownloadView : UserControl
{
    public VersionDownloadView() => InitializeComponent();

    /// <summary>8-31 版本行点击 → 直接弹加载器选择并下载（不先进详情页；行内[下载]按钮点击不冒泡到 Tapped——按钮内部消费指针）</summary>
    private void OnVersionRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: VersionListItemVM item } && DataContext is VersionDownloadViewModel vm)
            vm.DownloadVersionCommand.Execute(item);
    }
}
