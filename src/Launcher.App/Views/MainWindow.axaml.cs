using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Launcher.App.Animations;
using Launcher.App.Services;
using Launcher.App.ViewModels;
using Launcher.Core.Utils;

namespace Launcher.App.Views;

public partial class MainWindow : Window
{
    /// <summary>导航按钮注册表（页面 id → 按钮）。视觉全部用本地值驱动——本地值优先级凌驾样式 Setter，
    /// 模板 TemplateBinding 实时跟随，根治样式伪类在 Avalonia 12 下 hover 失效 / pressed 白影的问题。</summary>
    private readonly Dictionary<string, Button> _navButtons = new();

    /// <summary>常规尺寸下限（8-16 批次 50：构造器 Show 前设定——启动即最终尺寸，不再先出小窗）</summary>
    private const double NormalMinWidth = 760, NormalMinHeight = 500;

    /// <summary>激活指示条是否已首次定位（首次直接落位，之后切换才滑动）</summary>
    private bool _navIndicatorFirst = true;

    /// <summary>闲置内存让渡（8-18 批次 80）：无操作 3 分钟 → GC + 工作集修剪</summary>
    private readonly IdleMemoryTuner _idleTuner = new();

    /// <summary>点击光晕残留防抖（8-18）：动画中断（快速连点/同 slot 抢占）时旧光晕不移除会卡在导航上</summary>
    private readonly Dictionary<string, Ellipse> _navRipples = new();

    public MainWindow()
    {
        InitializeComponent();
        // 闲置检测：窗口级输入事件（冒泡覆盖全部子元素）只更新原子时间戳，零开销。
        // 8-18 修正：不含 PointerMoved——鼠标微动/漂移会不断重置计时导致永不修剪；只认明确输入
        PointerPressed += (_, _) => _idleTuner.OnUserActivity();
        PointerWheelChanged += (_, _) => _idleTuner.OnUserActivity();
        KeyDown += (_, _) => _idleTuner.OnUserActivity();
        // 8-18 失焦/最小化 → 立即修剪（用户离开马上让资源，不等 3 分钟；回来活动自动重置）
        Deactivated += (_, _) =>
        {
            _idleTuner.TrimNow();
            UiAnim.FinishAll(); // 8-19：新窗口打开（详情/弹窗）失焦时强制动画落终值，防 hover 色残留
        };
        ((System.ComponentModel.INotifyPropertyChanged)this).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WindowState) && WindowState == WindowState.Minimized)
                _idleTuner.TrimNow();
        };
        // 8-18 透明度防御：合成降级（ActualTransparencyLevel → None）后恢复时重新应用用户透明度——
        // 某些页面/交互触发亚克力合成失败会丢透明度，恢复后自动回到用户设置值。
        // 8-19 硬性要求：改取 VM 实时值 + 观感一致跳过——任何非用户改动时机不得把透明度写回旧值
        // （此前重放持久值：预览未保存时把观感打回默认，交互几次即"被重置"）
        ((System.ComponentModel.INotifyPropertyChanged)this).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActualTransparencyLevel) && ActualTransparencyLevel != WindowTransparencyLevel.None)
            {
                ApplyAppearanceDefensive();
                ApplyOpacityFallback(); // 8-19 第二批：合成恢复时导航层一并还原（原恢复分支是死代码）
            }
        };
        // 8-18 输入框失焦：点击窗口任意非输入控件处 → 清除焦点（Avalonia 默认点击面板/空白不移除 TextBox 焦点）
        PointerPressed += (_, e) =>
        {
            if (e.Source is not TextBox && e.Source is not Avalonia.Controls.Button
                && e.Source is not ComboBox && e.Source is not ToggleButton)
                FocusManager?.Focus(null, NavigationMethod.Unspecified, KeyModifiers.None); // 8-18 点别处即失焦
        };
        // 8-16 批次 50：窗口以最终尺寸创建（XAML 已无 150×150 splash 尺寸；CenterScreen 在 Show 时按此尺寸居中），
        // 消灭「先显示左上角 150×150 小窗、Opened 里再放大居中」的两段跳变
        var (tw, th) = ResolveTargetSize();
        MinWidth = NormalMinWidth;
        MinHeight = NormalMinHeight;
        Width = tw;
        Height = th;
        // 8-13 批次 34 终局：无启动动画——亚克力层/描边默认即显示（axaml 默认 AcrylicBlur hint）
        _navButtons["home"] = NavHome;
        _navButtons["version"] = NavVersions;
        _navButtons["download"] = NavDownloads;
        _navButtons["server"] = NavServer;
        _navButtons["multiplayer"] = NavMultiplayer;
        _navButtons["ecosystem"] = NavEcosystem;
        _navButtons["settings"] = NavSettings;
        RegisterNavIcons();
        // AL47 整合包拖入：全窗口接收 zip/mrpack 拖拽（DragOver 过滤，Drop 取第一个导入）
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DropEvent, OnWindowDrop);
        // VM 到达后订阅激活态变化（覆盖点击/跳转/GoRepair 等所有导航路径）
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel main)
                main.PropertyChanged += OnVmPropertyChanged;
            ApplyNavVisuals();
        };
        // 窗口显示后 ActualTransparencyLevel 才为最终值；亚克力合成失败时切不透明，保证窗口永远可见
        Opened += (_, _) =>
        {
            UiAnim.Host = this; // 渲染帧驱动的全局宿主（splash 等无 host 参数的动画取帧时钟）
            ApplyOpacityFallback();
            ApplyAppearanceFromVm(); // 8-19 取 VM 实时值（构造时已加载设置；DataContext 缺失时内部回退持久值）
            ApplyNavVisuals(); // 兜底：DataContext 若早于挂载，这里补一次
            // 页面切换：平滑滑入淡出
            PageHost.PageTransition = new UiAnim.FadeSlideTransition { Duration = TimeSpan.FromMilliseconds(180) };
            // Toast 出现时右侧滑入（NVIDIA 浮窗风；Opacity 淡入淡出由绑定驱动），关闭时右滑出
            ToastsHost.ContainerPrepared += (_, e) =>
            {
                UiAnim.SlideInX(e.Container);
                if (e.Container.DataContext is ToastItem t)
                    t.OnRemoving = () => UiAnim.SlideOutToRight(e.Container);
            };
            // 外观实时跟随设置页改动（保存应用 + 预览）。
            // AL7：预览必须传 VM 值——Settings 未写盘时读不到新值，旧版预览/即时生效永远不变化
            if (DataContext is MainViewModel main)
            {
                main.Settings.AppearanceChanged += ApplyAppearanceFromVm; // 保存后持久值==VM 值，同一入口
                main.Settings.PreviewChanged += ApplyAppearanceFromVm;
            }
            // 8-16 批次 50：尺寸与居中已前移构造器（Show 前）——此处不再 resize/重摆，窗口一次落位
            // 兜底：启动期间导航未布局被跳过，可见后 150ms 一次性补定位（不用链式 Post，防饿死 UI 线程）
            var indicatorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            indicatorTimer.Tick += (_, _) => { indicatorTimer.Stop(); ApplyNavVisuals(); };
            indicatorTimer.Start();
            // 彩蛋：启动完成后随机一条小提示（可关）
            if (LauncherSettings.Current.StartupTipEnabled)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1800); // 等放大/淡出播完
                    Dispatcher.UIThread.Post(() => NotificationService.Info(StartupTips.Random()));
                });
            }
        };
        // 无边框最大化：外框圆角归 0，避免透明角露出壁纸（监听 WindowState 属性变化）
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
                WindowRoot.CornerRadius = WindowState == WindowState.Maximized ? new CornerRadius(0) : new CornerRadius(12);
        };
        // 8-19 关闭兜底：尺寸存档（8-23 观感档已即时落盘，无防抖可清）
        Closing += (_, _) => SaveWindowSize();
    }

    /// <summary>8-16 点击 Toast 复制内容到剪贴板（错误信息全模块可带走）</summary>
    private void OnToastCopy(object? sender, PointerPressedEventArgs e)
    {
        // 8-22 步骤8：点在「查看日志」按钮上不复制，交给按钮点击
        if (sender is Avalonia.Controls.Border { DataContext: ToastItem t } b
            && e.Source is not Button
            && TopLevel.GetTopLevel(b)?.Clipboard is { } cb && !string.IsNullOrEmpty(t.Message))
            _ = cb.SetTextAsync(t.Message);
    }

    /// <summary>8-22 步骤8：Toast 动作按钮（如「查看日志」）→ 执行绑定的动作</summary>
    private void OnToastAction(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ToastItem { OnAction: { } action } }) action();
    }

    /// <summary>8-13 批次 34 终局：无启动动画——RootSurface 默认可见（axaml 默认状态），描边终值即 axaml 的 #4D2F3745。</summary>

    /// <summary>颜色线性插值（亚克力层淡入时描边同步渐显；不依赖 Color.Lerp 的版本差异）</summary>
    private static IBrush LerpBrush(Color from, Color to, double e)
    {
        var c = Color.FromArgb(
            (byte)(from.A + (to.A - from.A) * e),
            (byte)(from.R + (to.R - from.R) * e),
            (byte)(from.G + (to.G - from.G) * e),
            (byte)(from.B + (to.B - from.B) * e));
        return new SolidColorBrush(c);
    }

    /// <summary>启动尺寸 = 存档窗口尺寸（夹到主屏工作区内）；无存档用默认 900×600。</summary>
    private (double w, double h) ResolveTargetSize()
    {
        var s = LauncherSettings.Current;
        if (Screens.Primary?.WorkingArea is not { } wa) return (900, 600);
        var w = s.WindowWidth >= NormalMinWidth ? Math.Min(s.WindowWidth, wa.Width) : 900.0;
        var h = s.WindowHeight >= NormalMinHeight ? Math.Min(s.WindowHeight, wa.Height) : 600.0;
        return (w, h);
    }

    /// <summary>关闭时记住窗口尺寸（下次启动构造器按此尺寸直接打开）。
    /// 防再污染：最大化/最小化时不覆盖存档；尺寸夹到主屏工作区 95% 内（防止真机测试/改分辨率
    /// 把超大尺寸存进设置，导致下次启动「窗口突然变大」）。</summary>
    private void SaveWindowSize()
    {
        var s = LauncherSettings.Current;
        if (WindowState != WindowState.Normal) return;
        var wa = Screens.Primary?.WorkingArea;
        s.WindowWidth = wa is { } a ? Math.Min(Width, a.Width * 0.95) : Width;
        s.WindowHeight = wa is { } b ? Math.Min(Height, b.Height * 0.95) : Height;
        s.Save();
    }

    // ---------- 无边框窗口标题栏：拖拽 / 双击最大化 / 最小化 / 最大化 / 关闭 ----------

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount >= 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        BeginMoveDrag(e);
    }

    // ---------- 整合包拖入（AL47：zip/mrpack 全窗口可拖；Avalonia 12 走 DataTransfer.Items + DataFormat.File） ----------

    private static bool IsPackFile(string name) =>
        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase);

    /// <summary>8-13 皮肤图片拖入换肤（png/jpg/jpeg——littleskin 皮肤库下载的 PNG 直接拖进来用）</summary>
    private static bool IsSkinFile(string name) =>
        name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    /// <summary>从拖拽项取文件路径（TryGetRaw 返回 IStorageItem 或原始路径字符串，兼容两种平台实现）</summary>
    private static string? TryGetFilePath(IDataTransferItem item)
    {
        if (!item.Formats.Contains(DataFormat.File)) return null;
        var raw = item.TryGetRaw(DataFormat.File);
        return raw switch
        {
            Avalonia.Platform.Storage.IStorageItem si => si.Path.LocalPath,
            string s => s,
            _ => null,
        };
    }

    private static void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        var hasPack = (e.DataTransfer.Items ?? []).Any(i => TryGetFilePath(i) is { } p && IsPackFile(p));
        var hasSkin = (e.DataTransfer.Items ?? []).Any(i => TryGetFilePath(i) is { } p && IsSkinFile(p));
        e.DragEffects = hasPack || hasSkin ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object? sender, DragEventArgs e)
    {
        var files = (e.DataTransfer.Items ?? [])
            .Select(TryGetFilePath)
            .Where(p => p is not null)
            .Cast<string>()
            .ToList();

        // 8-13 皮肤图片拖入 → 直接换肤（littleskin 皮肤库下载的 PNG 一条龙：拖进来就生效）
        var skin = files.FirstOrDefault(IsSkinFile);
        if (skin is not null)
        {
            if (DataContext is MainViewModel main && main.Home is { } home)
                home.ApplyLocalSkin(skin);
            return;
        }

        var packs = files.Where(IsPackFile).ToList();
        if (packs.Count == 0)
        {
            NotificationService.Info("支持拖入 .png/.jpg 皮肤图片（自动换肤）或 .zip/.mrpack 整合包");
            return;
        }
        if (packs.Count > 1)
            NotificationService.Info("已开始导入第一个整合包，其余已忽略");
        ModpackImportFlow.StartAsync(packs[0]);
    }

    // ---------- 窗口按钮（纯 Border，手动 hover 变色；无模板/行为，杜绝缩放位移错位） ----------

    private void TitleBtn_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Border b) return;
        if (ReferenceEquals(b, BtnClose))
        {
            UiAnim.TweenBrush(b, TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.Parse("#C42B1C")), UiAnim.Durations.Fast, "nav");
            UiAnim.TweenBrush(BtnCloseGlyph, TextBlock.ForegroundProperty, Brushes.White, UiAnim.Durations.Fast, "nav");
        }
        else
        {
            UiAnim.TweenBrush(b, TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.Parse("#2C3544")), UiAnim.Durations.Fast, "nav");
            UiAnim.TweenBrush(BtnMinGlyph, TextBlock.ForegroundProperty, Brushes.White, UiAnim.Durations.Fast, "nav");
        }
    }

    private void TitleBtn_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Border b) return;
        UiAnim.TweenBrush(b, TemplatedControl.BackgroundProperty, Brushes.Transparent, UiAnim.Durations.Fast, "nav");
        if (ReferenceEquals(b, BtnClose))
            UiAnim.TweenBrush(BtnCloseGlyph, TextBlock.ForegroundProperty, new SolidColorBrush(Color.Parse("#8A93A6")), UiAnim.Durations.Fast, "nav");
        else
            UiAnim.TweenBrush(BtnMinGlyph, TextBlock.ForegroundProperty, new SolidColorBrush(Color.Parse("#8A93A6")), UiAnim.Durations.Fast, "nav");
    }

    private void TitleBtn_PointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true; // 挡住标题条拖拽

    private void TitleBtn_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Border b || !b.IsPointerOver) return;
        if (ReferenceEquals(b, BtnClose)) Close();
        else WindowState = WindowState.Minimized;
    }

    // ---------- 导航视觉（本地值驱动；hover/按下/激活三态互斥恢复） ----------

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "IsHomeActive" or "IsVersionsActive" or "IsDownloadsActive" or "IsServerActive" or "IsMultiplayerActive" or "IsEcosystemActive" or "IsSettingsActive")
            ApplyNavVisuals();
    }

    /// <summary>
    /// 自定义背景：内容区铺满图片 + 65% 黑压暗（保文字可读）。
    /// 空路径清本地值 → 回落用户背景色（{DynamicResource BackgroundColor}）；图片失效（被删/损坏）同样回落，不崩。
    /// 内存：DecodeToWidth 限 2560 宽；换图前释放旧 Bitmap。
    /// </summary>
    public void ApplyBackgroundImage(string? path)
    {
        // 释放旧图（若有）
        if (ContentSurface.Background is ImageBrush { Source: Bitmap old })
        {
            old.Dispose();
            ContentSurface.Background = null;
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            // 无图 → 清本地值，让 {DynamicResource BackgroundColor}（用户背景色）生效
            ContentSurface.Background = null;
            BgDim.IsVisible = false;
            return;
        }
        try
        {
            // 8-18：限宽解码（2560）——4K 壁纸全尺寸 BGRA 33MB，缩到窗口可用宽度省显存/内存（注释与实现终于对齐）
            using var stream = File.OpenRead(path);
            var bitmap = Bitmap.DecodeToWidth(stream, 2560);
            ContentSurface.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
            BgDim.IsVisible = true;
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[BACKGROUND] 背景图片加载失败 {path}: {ex.Message}");
            ContentSurface.Background = null;
            BgDim.IsVisible = false;
        }
    }

    /// <summary>按 VM 激活态刷新全部导航视觉（active：深青底 + 白字；左色条由独立元素滑动定位）。
    /// 激活切换保持瞬跳——过渡由 NavIndicator 滑动承担；hover/释放的平滑走 TweenNavBack。</summary>
    private void ApplyNavVisuals()
    {
        if (DataContext is not MainViewModel main) return;
        string? activePage = null;
        foreach (var (page, btn) in _navButtons)
        {
            var (bg, fg) = NavTargetVisuals(btn);
            btn.Background = bg;
            btn.Foreground = fg;
            if (IsPageActive(main, page))
            {
                activePage = page;
                MoveNavIndicator(btn);
            }
        }
        // 批次 72（8-18）：图标双态交叉淡入 + 激活切换瞬间的语义动作
        foreach (var (page, _) in _navButtons) AnimateIconState(page, page == activePage);
        if (activePage is not null && activePage != _lastActivePage)
        {
            PlayNavIconAction(activePage);
            _lastActivePage = activePage;
        }
    }

    // ===== 导航图标双态 + 语义动作（批次 72，8-18；FluentSystemIcons Regular/Filled 矢量双字形） =====
    /// <summary>图标双字形注册表（页面 → Regular 层/Filled 层）。</summary>
    private readonly Dictionary<string, (Avalonia.Controls.Shapes.Path R, Avalonia.Controls.Shapes.Path F)> _navIcons = new();
    private string? _lastActivePage;

    private void RegisterNavIcons()
    {
        _navIcons["home"] = (NavHomeIconR, NavHomeIconF);
        _navIcons["version"] = (NavVersionIconR, NavVersionIconF);
        _navIcons["download"] = (NavDownloadIconR, NavDownloadIconF);
        _navIcons["server"] = (NavServerIconR, NavServerIconF);
        _navIcons["multiplayer"] = (NavMultiIconR, NavMultiIconF);
        _navIcons["ecosystem"] = (NavEcoIconR, NavEcoIconF);
        _navIcons["settings"] = (NavSettingsIconR, NavSettingsIconF);
        _navIconHosts["home"] = NavHomeHost;
        _navIconHosts["version"] = NavVersionHost;
        _navIconHosts["download"] = NavDownloadHost;
        _navIconHosts["server"] = NavServerHost;
        _navIconHosts["multiplayer"] = NavMultiHost;
        _navIconHosts["ecosystem"] = NavEcoHost;
        _navIconHosts["settings"] = NavSettingsHost;
    }

    /// <summary>图标容器 Grid（28×28；8-18 删 halo 圆块后保留——点击光晕 ripple 宿主）</summary>
    private readonly Dictionary<string, Grid> _navIconHosts = new();

    private string? FindPage(Button btn)
    {
        foreach (var (page, b) in _navButtons)
            if (ReferenceEquals(b, btn)) return page;
        return null;
    }

    /// <summary>双态交叉淡入：激活 → Filled 显 / Regular 隐（150ms）；取消激活反向。复用 UiAnim 帧驱动。</summary>
    private void AnimateIconState(string page, bool active)
    {
        if (!_navIcons.TryGetValue(page, out var p)) return;
        var rFrom = p.R.Opacity;
        var fFrom = p.F.Opacity;
        var rTo = active ? 0.0 : 1.0;
        var fTo = active ? 1.0 : 0.0;
        if (Math.Abs(rFrom - rTo) < 0.001) return;
        UiAnim.Animate(150, UiAnim.Curves.Decelerate, e =>
        {
            p.R.Opacity = rFrom + (rTo - rFrom) * e;
            p.F.Opacity = fFrom + (fTo - fFrom) * e;
        }, host: p.F, slot: "navicon");
    }

    /// <summary>图标语义动作：激活切换瞬间对当前可见层播放一次表演（纯 transform，sin 往返一次；完成后清变换）。</summary>
    private void PlayNavIconAction(string page)
    {
        if (!_navIcons.TryGetValue(page, out var p)) return;
        var target = p.F.Opacity > 0.5 ? p.F : p.R; // 当前可见层（双态动画未完成时按现状取）
        switch (page)
        {
            case "download": // 谷歌式箭头往下插：下沉 5px + 轻微 squash 回弹（插入感）
                var tt = new TranslateTransform(0, 0);
                var tts = new ScaleTransform(1, 1);
                target.RenderTransform = new TransformGroup { Children = [tt, tts] };
                target.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
                UiAnim.Animate(260, UiAnim.Curves.Standard, e =>
                {
                    tt.Y = Math.Sin(e * Math.PI) * 5;
                    tts.ScaleY = 1 - Math.Sin(e * Math.PI) * 0.08; // 落地 squash
                }, () => target.RenderTransform = null, target, slot: "navact");
                break;
            case "settings": // 齿轮微转
                var rt = new RotateTransform(0);
                target.RenderTransform = rt;
                target.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
                UiAnim.Animate(220, UiAnim.Curves.Standard, e => rt.Angle = Math.Sin(e * Math.PI) * 15,
                    () => target.RenderTransform = null, target, slot: "navact");
                break;
            case "server": // 服务器顶灯亮起：scale 脉冲加强
                var st = new ScaleTransform(1, 1);
                target.RenderTransform = st;
                target.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
                UiAnim.Animate(240, UiAnim.Curves.Standard, e =>
                {
                    var s = 1 + Math.Sin(e * Math.PI) * 0.2;
                    st.ScaleX = s;
                    st.ScaleY = s;
                }, () => target.RenderTransform = null, target, slot: "navact");
                break;
            case "ecosystem": // 生态转动：转一整圈（360°）
                var rt2 = new RotateTransform(0);
                target.RenderTransform = rt2;
                target.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
                UiAnim.Animate(320, UiAnim.Curves.Standard, e =>
                {
                    rt2.Angle = 360 * e; // 匀速转一整圈
                }, () => target.RenderTransform = null, target, slot: "navact");
                break;
            case "home": // 房子落位（单向收敛，非弹跳）
                var st2 = new ScaleTransform(0.92, 0.92);
                target.RenderTransform = st2;
                target.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
                UiAnim.Animate(150, UiAnim.Curves.Decelerate, e =>
                {
                    var s = 0.92 + 0.08 * e;
                    st2.ScaleX = s;
                    st2.ScaleY = s;
                }, () => target.RenderTransform = null, target, slot: "navact");
                break;
            case "version": // 书页从下落入位
                var tt2 = new TranslateTransform(0, 2);
                target.RenderTransform = tt2;
                UiAnim.Animate(150, UiAnim.Curves.Decelerate, e => tt2.Y = 2 * (1 - e),
                    () => target.RenderTransform = null, target, slot: "navact");
                break;
            case "multiplayer": // 人群从右聚拢 + 两人间虚线流动
                var tt3 = new TranslateTransform(3, 0);
                target.RenderTransform = tt3;
                UiAnim.Animate(150, UiAnim.Curves.Decelerate, e => tt3.X = 3 * (1 - e),
                    () => target.RenderTransform = null, target, slot: "navact");
                if (NavMultiDash is not null)
                {
                    // 虚线平移一个周期（dash 总长 1.6+2=3.6）——连接线"流动"
                    UiAnim.Animate(600, UiAnim.Curves.Linear, e => NavMultiDash.StrokeDashOffset = -3.6 * e,
                        () => NavMultiDash.StrokeDashOffset = 0, NavMultiDash, slot: "navact");
                }
                break;
        }
    }

    /// <summary>导航按钮目标视觉（激活/非激活）——ApplyNavVisuals 瞬跳与 NavExit/NavRelease 过渡共用，
    /// 避免两处颜色计算漂移。激活底跟随 AccentDark 派生色（主题系统）。</summary>
    private (IBrush Bg, IBrush Fg) NavTargetVisuals(Button btn)
    {
        foreach (var (page, b) in _navButtons)
        {
            if (ReferenceEquals(b, btn) && DataContext is MainViewModel main && IsPageActive(main, page))
            {
                var bg = App.Current.Resources.TryGetResource("AccentDark", null, out var dark) && dark is Color dc
                    ? (IBrush)new SolidColorBrush(dc)
                    : (IBrush)new SolidColorBrush(Color.Parse("#12332F"));
                return (bg, Brushes.White);
            }
        }
        return (Brushes.Transparent, new SolidColorBrush(Color.Parse("#8A93A6"))); // TextSecondary
    }

    /// <summary>激活指示条滑到目标按钮：首次直接定位，之后 180ms 平滑滑动（host=Indicator 互斥打断连点）。
    /// AL7：强调色跟随设置；按钮位置/高度用 TranslatePoint 实测，不假设布局常量。
    /// 注意：布局未就绪（splash 期间 RootSurface 不可见，子树不布局）时直接跳过——绝不 Post 重试
    /// （会每帧往 UI 队列塞回调把渲染饿死 → 未响应），定位兜底在 splash 完成回调里做。</summary>
    private void MoveNavIndicator(Button btn)
    {
        var accentHex = LauncherSettings.Current.AccentColor;
        NavIndicator.Background = new SolidColorBrush(Color.Parse(
            string.IsNullOrWhiteSpace(accentHex) || !accentHex.StartsWith('#') ? "#6C8CFF" : accentHex));
        if (NavIndicator.Parent is not Visual parent) return;
        if (btn.Bounds.Height <= 0) return; // 布局未就绪，跳过（splash 完成后由兜底调用补齐）
        var top = btn.TranslatePoint(new Point(0, 0), parent)?.Y ?? NavIndicator.Margin.Top;
        var h = btn.Bounds.Height;
        if (_navIndicatorFirst)
        {
            _navIndicatorFirst = false;
            NavIndicator.Height = h;
            NavIndicator.Margin = new Thickness(10, top, 0, 0);
            return;
        }
        // 布局写一次落定（Margin/Height），动画期用 Transform 反向补偿——每帧 0 布局失效。
        // 打断重入：先捕获当前视觉态（Margin + 现存 Transform 的位移/缩放），再落定；
        // e=0 时补偿量=0 → 视觉位置=原处，e=1 时恒等 → done 清变换
        var fromTopEff = NavIndicator.Margin.Top;
        var fromHEff = NavIndicator.Height;
        if (NavIndicator.RenderTransform is TransformGroup g
            && g.Children[0] is TranslateTransform tt0 && g.Children[1] is ScaleTransform st0)
        {
            fromTopEff += tt0.Y;
            fromHEff *= st0.ScaleY;
        }
        NavIndicator.Margin = new Thickness(10, top, 0, 0);
        NavIndicator.Height = h;
        var tt = new TranslateTransform(0, 0);
        var st = new ScaleTransform(1, 1);
        NavIndicator.RenderTransform = new TransformGroup { Children = { tt, st } };
        NavIndicator.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative); // 顶部锚定，ScaleY 不位移
        UiAnim.Animate(180, UiAnim.Curves.Standard, e =>
        {
            tt.Y = (fromTopEff - top) * (1 - e);
            st.ScaleY = (fromHEff + (h - fromHEff) * e) / h;
        }, () => NavIndicator.RenderTransform = null, NavIndicator);
    }

    private static bool IsPageActive(MainViewModel main, string page) => page switch
    {
        "home" => main.IsHomeActive,
        "version" => main.IsVersionsActive,
        "download" => main.IsDownloadsActive,
        "server" => main.IsServerActive,
        "multiplayer" => main.IsMultiplayerActive,
        "ecosystem" => main.IsEcosystemActive,
        "settings" => main.IsSettingsActive,
        _ => false,
    };

    private void NavEnter(object? sender, PointerEventArgs e)
    {
        if (sender is not Button btn || btn is null || DataContext is not MainViewModel main) return;
        foreach (var (page, b) in _navButtons)
        {
            if (ReferenceEquals(b, btn) && IsPageActive(main, page)) return; // 激活项 hover 不改色
        }
        UiAnim.TweenBrush(btn, TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.Parse("#2C3544")), UiAnim.Durations.Fast, "nav"); // BgHover
        UiAnim.TweenBrush(btn, TemplatedControl.ForegroundProperty, new SolidColorBrush(Color.Parse("#E8EAF0")), UiAnim.Durations.Fast, "nav"); // TextPrimary
        // 8-18：hover 圆块已删（用户拍板——有圆块反而看着不协调），hover 只改背景/前景色
    }

    private void NavExit(object? sender, PointerEventArgs e) => TweenNavBack(sender);

    private void NavPress(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Button btn)
        {
            UiAnim.TweenBrush(btn, TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.Parse("#1A2029")), UiAnim.Durations.Fast, "nav"); // 按下变深
            // 批次 75：自研点击光晕（弃 RippleBehavior——模板 RippleHost 查找不可靠实测无波纹）。
            // 光晕叠加在图标容器 Grid 同格（Grid 单格子元素重叠不占布局），从点击点扩散淡出。
            if (FindPage(btn) is { } page && _navIconHosts.TryGetValue(page, out var host))
            {
                // 8-18 防残留：动画中断（快速连点/同 slot 抢占）时旧光晕不移除会卡在导航上——先清旧
                if (_navRipples.TryGetValue(page, out var oldRipple) && host.Children.Contains(oldRipple))
                    host.Children.Remove(oldRipple);
                    var size = 28.0;
                    var ripple = new Ellipse
                    {
                        Width = size, Height = size,
                        Fill = new SolidColorBrush(Color.Parse("#4DFFFFFF")),
                        IsHitTestVisible = false,
                        RenderTransform = new ScaleTransform(0, 0),
                        RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                        Opacity = 0.4,
                    };
                    _navRipples[page] = ripple; // 登记防残留（onDone 移除后字典留空条目无害）
                    // 圆心 = halo 容器中心（28×28，图标 14 居中）；扩散至 1.6 倍容器
                    host.Children.Add(ripple);
                    UiAnim.Animate(280, UiAnim.Curves.Standard, ev =>
                    {
                        var e2 = 1 - Math.Pow(1 - ev, 3); // cubic-out
                        if (ripple.RenderTransform is ScaleTransform st) { st.ScaleX = 1.6 * e2; st.ScaleY = 1.6 * e2; }
                        ripple.Opacity = 0.4 * (1 - e2);
                    }, () => host.Children.Remove(ripple), ripple, slot: "navrip");
                }
            }
    }

    private void NavRelease(object? sender, PointerReleasedEventArgs e) => TweenNavBack(sender);

    /// <summary>悬停退出/松手释放：动画回激活态目标色（不再瞬跳）</summary>
    private void TweenNavBack(object? s)
    {
        if (s is not Button btn) return;
        var (bg, fg) = NavTargetVisuals(btn);
        UiAnim.TweenBrush(btn, TemplatedControl.BackgroundProperty, bg, UiAnim.Durations.Fast, "nav");
        UiAnim.TweenBrush(btn, TemplatedControl.ForegroundProperty, fg, UiAnim.Durations.Fast, "nav");
        // 8-18：halo 圆块已删（用户拍板），回退只改背景/前景色
    }

    /// <summary>8-19 合成降级双向：失败时整窗不透明 + 隐藏导航层；合成恢复时还原导航层
    /// （旧实现 IsVisible=false 永不复原——会话中降级后整窗一直"变深"）。8-23 实色档例外：
    /// 窗口本就不透明，导航始终可见，不随合成降级隐藏。</summary>
    private void ApplyOpacityFallback()
    {
        if (RootSurface is null || NavSurface is null) return;
        if (CurrentOpacityMode() == OpacityMode.Solid)
        {
            NavSurface.IsVisible = true;
            return;
        }
        if (ActualTransparencyLevel != WindowTransparencyLevel.None)
        {
            NavSurface.IsVisible = true; // 合成已恢复（幂等 no-op）
            return;
        }
        // 合成失败：亚克力材质回退纯色（Material.FallbackColor 已设；这里确保不透明）
        if (RootSurface.Material is ExperimentalAcrylicMaterial m)
            m.FallbackColor = Avalonia.Media.Color.Parse("#FF12161F");
        NavSurface.IsVisible = false;
    }

    /// <summary>8-23 当前观感档（构造期 DataContext 缺失回退持久值——与 ApplyAppearanceFromVm 一致）</summary>
    private OpacityMode CurrentOpacityMode()
        => DataContext is MainViewModel main ? main.Settings.Opacity : LauncherSettings.Current.Opacity;

    /// <summary>8-19 透明度硬性要求：统一入口——取 VM 实时值应用外观（VM 缺失时回退持久值）。
    /// 透明度改动已即时落盘，VM 值 == 持久值，任何时机重放都不会把观感打回旧值。</summary>
    /// <summary>8-19 第二批：设置页汉堡 Popup 等路径显式重放透明度（Popup 合成重建后 TintOpacity
    /// 可能回落 XAML 初值，而 ActualTransparencyLevel 事件不报）——internal 供 SettingsView 调用</summary>
    internal void ApplyAppearanceFromVm()
    {
        if (DataContext is MainViewModel main)
            ApplyAppearance(main.Settings.Opacity, (DensityMode)main.Settings.DensityIndex);
        else
            ApplyAppearance(LauncherSettings.Current.Opacity, (DensityMode)LauncherSettings.Current.Density);
    }

    /// <summary>8-19 防御重放：观感未变则跳过——只有用户再次改动才变化（硬性要求）。
    /// 8-23 改两档单选后按「档位 + BackgroundSource」一致性判断——Popup 合成重建可能把
    /// BackgroundSource 打回 XAML 初值（Digger），单比 TintOpacity 会漏判。
    /// RootSurface 与 NavSurface 各自独立判断（合成降级时 NavSurface 可能不可见，Material 失效）。</summary>
    private void ApplyAppearanceDefensive()
    {
        if (DataContext is not MainViewModel main || RootSurface?.Material is not ExperimentalAcrylicMaterial m) return;
        var mode = main.Settings.Opacity;
        var solid = mode == OpacityMode.Solid;
        var navChanged = NavSurface?.Material is ExperimentalAcrylicMaterial nm
            && (nm.BackgroundSource != (solid ? AcrylicBackgroundSource.None : AcrylicBackgroundSource.Digger)
                || Math.Abs(nm.TintOpacity - (solid ? 1.0 : 0.88)) >= 0.001);
        if (m.BackgroundSource == (solid ? AcrylicBackgroundSource.None : AcrylicBackgroundSource.Digger)
            && Math.Abs(m.TintOpacity - (solid ? 1.0 : 0.30)) < 0.001 && !navChanged) return;
        ApplyAppearance(mode, (DensityMode)main.Settings.DensityIndex);
    }

    /// <summary>应用外观设置：窗口观感档 + 界面密度（强调色由 App 应用）。
    /// 8-23 滑块改两档单选：透明档 Digger（材质只有 None/Digger，Digger 即毛玻璃），实色档 None+TintOpacity=1 纯不透明。</summary>
    private void ApplyAppearance(OpacityMode mode, DensityMode density)
    {
        // 观感：透明档固定 Digger 观感值（主区 0.30 / 导航 0.88 较实保可读）；实色档纯色不透明
        if (RootSurface?.Material is ExperimentalAcrylicMaterial m)
        {
            m.TintColor = Avalonia.Media.Color.Parse("#12161F");
            if (mode == OpacityMode.Solid)
            {
                m.BackgroundSource = AcrylicBackgroundSource.None;
                m.TintOpacity = 1;
            }
            else
            {
                m.BackgroundSource = AcrylicBackgroundSource.Digger;
                m.TintOpacity = 0.30;
            }
        }
        if (NavSurface?.Material is ExperimentalAcrylicMaterial nm)
        {
            nm.TintColor = Avalonia.Media.Color.Parse("#12161F");
            if (mode == OpacityMode.Solid)
            {
                nm.BackgroundSource = AcrylicBackgroundSource.None;
                nm.TintOpacity = 1;
            }
            else
            {
                nm.BackgroundSource = AcrylicBackgroundSource.Digger;
                nm.TintOpacity = 0.88; // 8-18 调定：0.55 白壁纸下导航「花了」，接近实色保可读
            }
        }

        // 密度：整 UI 缩放（AL7 上调：紧凑 0.95 / 标准 1.0 / 舒适 1.15——旧 0.9 默认把整 UI 缩 10%，字太小主因）
        if (ContentSurface?.RenderTransform is Avalonia.Media.ScaleTransform scaleTransform)
        {
            var scale = density switch
            {
                DensityMode.Compact => 0.95,
                DensityMode.Comfortable => 1.15,
                _ => 1.0,
            };
            scaleTransform.ScaleX = scale;
            scaleTransform.ScaleY = scale;
        }
    }
}
