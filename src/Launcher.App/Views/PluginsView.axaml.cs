using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Launcher.App.ViewModels;

namespace Launcher.App.Views;

public partial class PluginsView : UserControl
{
    public PluginsView() => InitializeComponent();

    /// <summary>导入插件：文件选择器（.dll）→ VM.ImportAsync（复制到 plugins/ + 登记哈希 + 立即加载）。</summary>
    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PluginsViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择插件（.dll）",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("插件 (*.dll)") { Patterns = ["*.dll"] },
            ],
        });
        if (files.Count == 0 || !files[0].Path.IsAbsoluteUri) return;
        await vm.ImportCommand.ExecuteAsync(files[0].Path.LocalPath);
    }
}
