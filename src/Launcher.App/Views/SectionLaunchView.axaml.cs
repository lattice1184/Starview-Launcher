using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Launcher.App.ViewModels;

using Launcher.App.Services;
namespace Launcher.App.Views;

public partial class SectionLaunchView : UserControl
{
    public SectionLaunchView() => InitializeComponent();

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    private IStorageProvider? Picker => TopLevel.GetTopLevel(this)?.StorageProvider;

    private async void OnBrowseJava(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || Picker is null) return;
        var files = await Picker.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 Java 可执行文件",
            AllowMultiple = false,
            FileTypeFilter =
            [
                OperatingSystem.IsWindows()
                    ? new FilePickerFileType("Java 可执行文件") { Patterns = ["java.exe"] }
                    : new FilePickerFileType("Java 可执行文件") { Patterns = ["java"] },
            ],
        });
        if (files.Count > 0 && files[0].Path.IsAbsoluteUri)
            Vm.ApplyJavaPath(files[0].Path.LocalPath);
    }

    private void OnResetJava(object? sender, RoutedEventArgs e) => Vm?.ResetJavaPath();

    private void OnMemoryCustomKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitCustomMemory();
    }

    private void OnMemoryCustomLostFocus(object? sender, RoutedEventArgs e) => CommitCustomMemory();

    private void CommitCustomMemory()
    {
        if (Vm is null) return;
        var box = this.FindControl<TextBox>("MemoryCustomText");
        Vm.ApplyCustomMemory(box?.Text ?? "");
    
}

}
