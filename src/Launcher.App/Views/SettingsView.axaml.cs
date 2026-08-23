using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Launcher.App.Animations;
using Launcher.Core.Utils;

namespace Launcher.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // 汉堡菜单锚定到 ☰ 按钮（代码赋值比 XAML 元素绑定稳）；默认显示"游戏目录"分区
        SettingsMenu.PlacementTarget = SettingsMenuButton;
        ShowSection(0);
    }

    // ---------- 分类菜单（汉堡按钮弹出；本地值驱动视觉，防 Avalonia 12 伪类不可靠 / hover 错位） ----------

    private int _activeSection;

    private void OnToggleMenu(object? sender, RoutedEventArgs e)
    {
        if (SettingsMenu.IsOpen) CloseMenuAnimated(); // 收起先播动画再关
        else SettingsMenu.IsOpen = true;              // 弹出由 Opened 事件弹入
    }

    /// <summary>☰ 菜单弹入：缩放 0.9→1 + 淡入（180ms Standard，无弹性过冲——host=child 互斥打断连点重播）</summary>
    private void OnSettingsMenuOpened(object? sender, EventArgs e)
    {
        if (SettingsMenu.Child is not Control child) return;
        child.Opacity = 0;
        var tx = new ScaleTransform(0.9, 0.9);
        child.RenderTransform = tx;
        UiAnim.Animate(180, UiAnim.Curves.Standard, e2 =>
        {
            child.Opacity = e2;
            tx.ScaleX = 0.9 + 0.1 * e2;
            tx.ScaleY = 0.9 + 0.1 * e2;
        }, null, child);
        ReplayAppearance(); // 8-19 第二批：Popup 打开可能触发合成层重建致 TintOpacity 回落，打开后重放
    }

    /// <summary>8-19 第二批：汉堡 Popup 开合路径显式重放用户透明度（VM 值已即时落盘，幂等无旧值风险）。
    /// Post 延迟到渲染帧之后：Popup 关闭/合成重建发生在渲染时，立即重放会被重建覆盖。
    /// 8-23 修：原用 VisualRoot 判窗口——Avalonia 12 对 UserControl 返回 null，重放从未真正执行
    /// （Popup 打开内容区变暗后关不掉）。改用 TopLevel.GetTopLevel（项目惯例）。加轮询
    /// ActualTransparencyLevel：合成恢复（!= None）即重放，最多 5s；超时兜底重放一次。
    /// 根因已由 Program.cs OverlayPopups 根治（Popup 窗口内渲染不再触发合成降级）；此处保留
    /// GetTopLevel 修复 + 轮询作为其他合成降级场景的恢复保险。</summary>
    private DispatcherTimer? _replayTimer;

    private void ReplayAppearance()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(this) is MainWindow w) w.ApplyAppearanceFromVm();
        });
        if (_replayTimer is not null) return;
        var deadline = Environment.TickCount + 5000;
        _replayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _replayTimer.Tick += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not MainWindow w)
            {
                _replayTimer.Stop(); _replayTimer = null; return;
            }
            if (w.ActualTransparencyLevel != WindowTransparencyLevel.None || Environment.TickCount > deadline)
            {
                w.ApplyAppearanceFromVm();
                _replayTimer.Stop();
                _replayTimer = null;
            }
        };
        _replayTimer.Start();
    }

    /// <summary>8-19 补充：Popup 任意关闭路径（含外部点击 dismiss——IsLightDismissEnabled 不走
    /// CloseMenuAnimated）都重放，覆盖上次漏掉的关闭分支</summary>
    private void OnSettingsMenuClosed(object? sender, EventArgs e) => ReplayAppearance();

    /// <summary>☰ 菜单收起：先反向缩放+淡出（120ms），done 后才关。起点取当前值（弹入被中断时无跳变）。
    /// 点击外部 dismiss（IsLightDismissEnabled）是系统行为无法拦截，保持瞬间关闭。</summary>
    private void CloseMenuAnimated()
    {
        if (!SettingsMenu.IsOpen || SettingsMenu.Child is not Control child) return;
        var fromO = child.Opacity;
        var tx = child.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        var fromS = tx.ScaleX;
        child.RenderTransform = tx;
        UiAnim.Animate(120, UiAnim.Curves.Standard, e =>
        {
            child.Opacity = fromO * (1 - e);
            tx.ScaleX = fromS + (0.9 - fromS) * e;
            tx.ScaleY = tx.ScaleX;
        }, () =>
        {
            SettingsMenu.IsOpen = false;
            ReplayAppearance(); // 8-19 第二批：收起后同样重放（合成层复原）
        }, child);
    }

    private void OnSettingsNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string idx } && int.TryParse(idx, out var i))
            ShowSection(i);
    }

    /// <summary>分类切换：ContentControl 覆盖式布局——直接替换内容 + 新分区淡入上移（200ms），
    /// 旧分区瞬间消失即可（无流布局占位问题）。首帧（尚未有内容）直接显示不动画。</summary>
    private void ShowSection(int index)
    {
        _activeSection = index;
        ApplySettingsNavVisuals();
        CloseMenuAnimated(); // 选完自动收起（先播动画再关）
        var content = BuildSection(index);
        if (ContentHost.Content is null) { ContentHost.Content = content; return; }
        content.Opacity = 0;
        var ty = new TranslateTransform(0, 8);
        content.RenderTransform = ty;
        ContentHost.Content = content;
        UiAnim.Animate(200, UiAnim.Curves.Standard, e =>
        {
            content.Opacity = e;
            ty.Y = 8 * (1 - e);
        }, () => content.RenderTransform = null, content); // done 清残留变换
    }

    private static Control BuildSection(int index) => index switch
    {
        0 => new SectionGameDirView(),
        1 => new SectionLaunchView(),
        2 => new SectionAppearanceView(),
        3 => new SectionDownloadView(),
        5 => new SectionModulesView(),
        _ => new SectionAboutView(),
    };

    private void ApplySettingsNavVisuals()
    {
        var accent = AccentBrush();
        SetNavVisual(SettingsNavGameDir, _activeSection == 0, accent);
        SetNavVisual(SettingsNavLaunch, _activeSection == 1, accent);
        SetNavVisual(SettingsNavAppearance, _activeSection == 2, accent);
        SetNavVisual(SettingsNavDownload, _activeSection == 3, accent);
        SetNavVisual(SettingsNavModules, _activeSection == 5, accent);
        SetNavVisual(SettingsNavAbout, _activeSection == 4, accent);
    }

    private static void SetNavVisual(Button btn, bool active, IBrush accent)
    {
        btn.Background = active ? new SolidColorBrush(Color.Parse("#12332F")) : Brushes.Transparent;
        btn.Foreground = active ? Brushes.White : new SolidColorBrush(Color.Parse("#8A93A6"));
        btn.BorderBrush = active ? accent : Brushes.Transparent;
        // 恒为 3px 左占位：激活显强调条，非激活透明（若 0↔3 切换 Button 模板内容会内缩 3px 造成错位）
        btn.BorderThickness = new Thickness(3, 0, 0, 0);
    }

    private static IBrush AccentBrush()
    {
        var hex = LauncherSettings.Current.AccentColor;
        return new SolidColorBrush(Color.Parse(string.IsNullOrWhiteSpace(hex) || !hex.StartsWith('#') ? "#6C8CFF" : hex));
    }

    private bool IsActiveNav(Button btn) =>
        ReferenceEquals(btn, SettingsNavGameDir) && _activeSection == 0
        || ReferenceEquals(btn, SettingsNavLaunch) && _activeSection == 1
        || ReferenceEquals(btn, SettingsNavAppearance) && _activeSection == 2
        || ReferenceEquals(btn, SettingsNavDownload) && _activeSection == 3
        || ReferenceEquals(btn, SettingsNavModules) && _activeSection == 5
        || ReferenceEquals(btn, SettingsNavAbout) && _activeSection == 4;

    /// <summary>设置导航按钮目标视觉（激活深青/白字 vs 透明/灰字）——瞬跳与过渡共用</summary>
    private (IBrush Bg, IBrush Fg) SettingsNavTarget(Button btn)
    {
        var active = IsActiveNav(btn);
        return (active ? new SolidColorBrush(Color.Parse("#12332F")) : Brushes.Transparent,
                active ? Brushes.White : new SolidColorBrush(Color.Parse("#8A93A6")));
    }

    private void SettingsNavEnter(object? sender, PointerEventArgs e)
    {
        if (sender is not Button btn) return;
        if (ReferenceEquals(btn, SettingsMenuButton))
        {
            UiAnim.TweenBrush(btn, TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.Parse("#2C3544")), UiAnim.Durations.Fast, "nav"); // ☰ 无激活态，hover 直接变灰
            return;
        }
        if (IsActiveNav(btn)) return; // 激活项 hover 不改色
        UiAnim.TweenBrush(btn, TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.Parse("#2C3544")), UiAnim.Durations.Fast, "nav");
        UiAnim.TweenBrush(btn, TemplatedControl.ForegroundProperty, new SolidColorBrush(Color.Parse("#E8EAF0")), UiAnim.Durations.Fast, "nav");
    }

    private void SettingsNavExit(object? sender, PointerEventArgs e)
    {
        if (ReferenceEquals(sender, SettingsMenuButton))
            UiAnim.TweenBrush(SettingsMenuButton, TemplatedControl.BackgroundProperty, Brushes.Transparent, UiAnim.Durations.Fast, "nav");
        else
            TweenSettingsNavBack(sender);
    }

    private void SettingsNavPress(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Button btn)
            UiAnim.TweenBrush(btn, TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.Parse("#1A2029")), UiAnim.Durations.Fast, "nav");
    }

    private void SettingsNavRelease(object? sender, PointerReleasedEventArgs e)
    {
        if (ReferenceEquals(sender, SettingsMenuButton))
            UiAnim.TweenBrush(SettingsMenuButton, TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.Parse("#2C3544")), UiAnim.Durations.Fast, "nav"); // 松手仍悬停 → hover 色
        else
            TweenSettingsNavBack(sender);
    }

    /// <summary>悬停退出/松手释放：动画回激活态目标色（不再瞬跳）</summary>
    private void TweenSettingsNavBack(object? s)
    {
        if (s is not Button btn) return;
        var (bg, fg) = SettingsNavTarget(btn);
        UiAnim.TweenBrush(btn, TemplatedControl.BackgroundProperty, bg, UiAnim.Durations.Fast, "nav");
        UiAnim.TweenBrush(btn, TemplatedControl.ForegroundProperty, fg, UiAnim.Durations.Fast, "nav");
    }
}
