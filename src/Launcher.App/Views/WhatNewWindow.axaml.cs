using Avalonia.Controls;
using Avalonia.Interactivity;
using Launcher.Core.Utils;

namespace Launcher.App.Views;

/// <summary>
/// 8-31 更新后弹窗（What's New）：升级后首次启动列出本次更新内容（版本分组）。
/// 由 App 启动时按 ChangelogState 判断触发；「开始使用」关闭。
/// </summary>
public partial class WhatNewWindow : Window
{
    public WhatNewWindow(string title, IReadOnlyList<ChangelogCatalog.ChangelogGroup> groups)
    {
        InitializeComponent();
        global::Launcher.App.Animations.UiAnim.AttachDialog(this, Root);
        Title = $"更新 {title}";
        TitleText.Text = $"{title} 更新内容";
        GroupsList.ItemsSource = groups;
    }

    private void OnDone(object? sender, RoutedEventArgs e) => Close();
}
