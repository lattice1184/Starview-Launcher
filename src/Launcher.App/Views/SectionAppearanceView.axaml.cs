using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Launcher.App.ViewModels;
using Launcher.Core.Utils;

using Launcher.App.Services;
namespace Launcher.App.Views;

public partial class SectionAppearanceView : UserControl
{
    public SectionAppearanceView() => InitializeComponent();

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    private IStorageProvider? Picker => TopLevel.GetTopLevel(this)?.StorageProvider;

    // ---------- 自定义颜色（#RRGGBB 提交） ----------

    private void OnCustomColorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitCustomColor();
    }

    private void OnCustomColorLostFocus(object? sender, RoutedEventArgs e) => CommitCustomColor();

    private void CommitCustomColor()
    {
        if (Vm is null) return;
        var box = this.FindControl<TextBox>("CustomColorBox");
        var err = this.FindControl<TextBlock>("CustomColorError");
        var hex = box?.Text?.Trim() ?? "";
        var rgb = AccentColorMath.TryNormalizeHex(hex);
        if (rgb is null)
        {
            if (err is not null) err.IsVisible = hex.Length > 0; // 空输入不报错（用户还没输完）
            return;
        }
        if (err is not null) err.IsVisible = false;
        Vm.ApplyCustomAccent($"#{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}");
    }

    // ---------- 背景图片选择 ----------

    private async void OnPickBackgroundClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || Picker is null) return;
        var files = await Picker.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择背景图片",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"] }],
        });
        if (files.Count > 0 && files[0].Path.IsAbsoluteUri)
            Vm.ApplyBackgroundImage(files[0].Path.LocalPath);
    
}

}
