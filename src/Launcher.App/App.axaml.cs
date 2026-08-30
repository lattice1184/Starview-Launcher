using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Launcher.Animation;
using Launcher.App.Services;
using Launcher.App.ViewModels;
using Launcher.App.Views;
using Launcher.Core.Services;
using Launcher.Core.Utils;
using Microsoft.Extensions.Logging;
using PCL.Core.App.IoC;
using PCL.Core.Logging;
using PCL.Core.UI.Animation.Core;

namespace Launcher.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        // 8-20 下载日志落盘（%AppData%\Launcher\logs\download.log，Info+，简洁逐行）——失败可查
        Launcher.Core.Download.DownloadLogFile.Attach();
        // 8-22 步骤8：下载完成 → Toast（2 秒自动关 + 文件名 + 「查看日志」进日志中心）。
        // 只对「完成」弹（失败任务在队列行显示错误）；日志类任务（启动/修复事件）不重复弹。
        Launcher.Core.Events.AppEvents.Subscribe<Launcher.Core.Events.DownloadCompletedEvent>(e =>
        {
            if (e.FileName.StartsWith("自动修复")) return; // 修复日志已有独立事件流
            var fileName = System.IO.Path.GetFileName(e.TargetPath);
            if (string.IsNullOrEmpty(fileName)) fileName = e.FileName;
            Services.NotificationService.Success($"下载完成：{fileName}",
                durationMs: 2000,
                actionText: "查看日志",
                onAction: () => OpenLogCenter());
        });
        // 8-22 步骤1：Core 层统一状态初始化（实例根 + 当前版本）
        Launcher.Core.AppState.InitInstanceRoot(Launcher.Core.Utils.GameDirectory.InstallDir());
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 主窗口关闭前不退出
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // 8-22 首次启动先弹目录窗口（settings.json 未指定时）：提前到主窗口构造前、
            // Show() 非阻塞——不再等主链构造完才出现，也不阻塞版本扫描；跳过即用默认目录
            if (LauncherSettings.Current.GameDirectory is null)
            {
                try { new Views.GameDirSetupWindow().Show(); }
                catch (Exception ex) { System.Console.Error.WriteLine($"[FATAL] GameDirSetupWindow: {ex}"); }
            }

            // 8-13 批次 34 终局：无启动动画（各版动画在真机上帧驱动跳变/抢 CPU——用户拍板全删），
            // 窗口构造完直接全量显示，启动最短路径
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
            // 8-31 插件接口：加载 plugins/ 目录的插件（总开关默认关；坏插件跳过不拖垮启动器）
            Launcher.Core.Plugin.PluginManager.Instance.Load();
            // 问号 ToolTip 屏幕边缘翻转（8-13）：全局挂主窗口，窗口右/下边缘的提示自动翻向可视区域
            ToolTipEdgeFlip.Attach(desktop.MainWindow);
            // 外观实时应用：保存（AppearanceChanged）与预览（PreviewChanged）都刷新强调色 + 自定义背景。
            // AL7：预览必须传 VM 值（Settings 未写盘时读不到新值——旧版预览永远不生效）
            if (desktop.MainWindow.DataContext is MainViewModel mainVm)
            {
                var window = desktop.MainWindow as MainWindow;
                mainVm.Settings.AppearanceChanged += () =>
                {
                    ApplyAccentColor(LauncherSettings.Current.AccentColor);
                    ApplyBackgroundColor(LauncherSettings.Current.BackgroundColor);
                    window?.ApplyBackgroundImage(LauncherSettings.Current.BackgroundImagePath);
                };
                mainVm.Settings.PreviewChanged += () =>
                {
                    ApplyAccentColor(mainVm.Settings.AccentColor);
                    ApplyBackgroundColor(mainVm.Settings.BackgroundColor);
                    window?.ApplyBackgroundImage(mainVm.Settings.BackgroundImagePathText);
                };
            }
            // 8-19 内存瘦身：图片磁盘缓存后台清理（30 天前的图标文件），不阻塞启动
            _ = Task.Run(() => ImageLoader.CleanupDiskCache());
            // 8-30 后台静默更新：延迟 10s 检查最新 release，下载就绪后提示「重启安装」；失败静默只记日志
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                try
                {
                    if (!LauncherSettings.Current.AutoCheckUpdate) return;
                    var result = await Launcher.Core.Services.UpdateCheckService.CheckAsync(PCL.Core.App.Basics.VersionName);
                    if (result.HasUpdate)
                    {
                        var tag = result.LatestTag ?? "";
                        var path = result.ReadyPath!;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            Services.NotificationService.Success($"发现新版本 {tag}，已准备好，重启后生效",
                                durationMs: 10000,
                                actionText: "重启安装",
                                onAction: () => ApplyUpdate(path, tag)));
                    }
                    else if (result.Error is not null)
                    {
                        Launcher.Core.Utils.AppLog.Instance?.LogWarning("[update] 后台检查失败: {Error}", result.Error);
                    }
                }
                catch (Exception ex)
                {
                    Launcher.Core.Utils.AppLog.Instance?.LogWarning(ex, "[update] 后台检查异常");
                }
            });
            // 8-29 内存诊断钩子：--mem-profile 开启后启动基线 + 每 3s 采样（默认关，dev 专用）
            if (Services.MemProfile.Enabled)
            {
                Services.MemProfile.Sample("boot");
                var memTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                memTimer.Tick += (_, _) => Services.MemProfile.Sample("tick");
                memTimer.Start();
            }
            // 启动序列在 Opened 里触发（小窗 logo → 窗口放大）；这里同步做初始化，任一失败只记日志不阻止窗口出现
            // 启动时确保自建游戏目录结构（D 盘优先；无 D 盘回退 Downloads\YanKa Launcher\.minecraft）
            Guard("GameDirectory.EnsureDefault", GameDirectory.EnsureDefault);

            // CF Key 一次性迁移（AL50）：旧版 KeyProxy 密文 key.bin → 设置（DPAPI 加密落盘），
            // 迁移成功即删密文文件与空目录（key 不再经代理，直接在主进程使用）。
            // 后台执行不阻塞窗口；迁移失败（文件缺失/损坏）静默，用户在设置页重新填写。
            _ = Task.Run(() =>
            {
                try
                {
                    var s = LauncherSettings.Current;
                    if (!string.IsNullOrWhiteSpace(s.CurseForgeApiKey)) return; // 已配置，无需迁移
                    var legacy = LegacyKeyStore.ReadLegacyKey();
                    if (string.IsNullOrWhiteSpace(legacy)) return;
                    s.CurseForgeApiKey = legacy;
                    s.Save(); // Secrets.Protect 自动加密落盘
                    if (File.Exists(LegacyKeyStore.DefaultFilePath)) File.Delete(LegacyKeyStore.DefaultFilePath);
                    var dir = Path.GetDirectoryName(LegacyKeyStore.DefaultFilePath)!;
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { /* 迁移失败不阻塞启动 */ }
            });

            // 应用个性化强调色、背景色与自定义背景（设置页可改，运行时可换）
            ApplyAccentColor(LauncherSettings.Current.AccentColor);
            ApplyBackgroundColor(LauncherSettings.Current.BackgroundColor);
            (desktop.MainWindow as MainWindow)?.ApplyBackgroundImage(LauncherSettings.Current.BackgroundImagePath);

            // [生命周期引导] 注入 Avalonia 适配层
            AnimationService.UIAccessProviderFactory = () => new AvaloniaUIAccessProvider();
            // 8-26 内存/资源瘦身：启动器动画走自研 UiAnim，从不用 PCL.Core 动画引擎——
            // 关掉其 60Hz 定时器 + N 个空转计算线程（见 AnimationService.DisableIdleEngine）
            AnimationService.DisableIdleEngine = true;
            LogService.FatalErrorReporter = message =>
            {
                Launcher.Core.Utils.AppLog.Instance?.LogError(null, "[fatal] {Message}", message);
                ShowFatalError(message);
            };
            // 8-30 全局日志统一落盘 AppPaths.DataRoot/logs——Linux 上可执行目录可能只读（写不进），
            // 且日志中心/体验者排查都看这。必须在 Lifecycle.OnLoading（LogService.StartAsync）前设置。
            PCL.Core.Logging.LogService.LogDirectoryOverride = Launcher.Core.Utils.AppPaths.LogsDir;

            // 启动 PCL.Core 生命周期（Avalonia 驱动消息循环，不运行 WPF 容器）。
            // 任一环节失败只记日志，不得阻止窗口出现；窗口构造失败则仍为 fatal。
            Guard("Lifecycle.OnInitialize", () => Lifecycle.OnInitialize());

            // Show（8-13 批次 34 终局：直接激活显示，无 splash 无等待）
            desktop.MainWindow.Show();

            Guard("Lifecycle.OnLoading", () => Lifecycle.OnLoading());
            Guard("Lifecycle.OnWindowCreated", () => Lifecycle.OnWindowCreated());
            // 启动完成埋点（Logger 在 Loading 阶段就绪，此时可用）
            Launcher.Core.Utils.AppLog.Instance?.LogInformation("[app] startup complete");
            // 8-18 头像丢失修复：启动早期（账号/网络未就绪）的首次刷新可能失败 → 完成后补刷一次
            try { if (desktop.MainWindow.DataContext is MainViewModel homeVm) homeVm.Home.RefreshPlayer(); } catch { /* 补刷失败不阻塞 */ }
            // 8-18 头像排查：3s 后确认属性值（区分「赋值被清」vs「绑定不显示」）
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                try
                {
                    if (desktop.MainWindow.DataContext is MainViewModel v)
                        Launcher.Core.Utils.AppLog.Instance?.LogInformation("[avatar] check3s: {Value}",
                            v.Home.PlayerAvatar is null ? "null" : v.Home.PlayerAvatar.GetType().Name);
                }
                catch { }
            });
            desktop.Exit += (_, _) =>
            {
                Guard("Lifecycle.Shutdown", () => Lifecycle.Shutdown());
            };
            // UI 线程未捕获异常兜底（弹崩溃窗口 + 防崩溃）
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                var msg = e.Exception?.Message ?? "";
                // 布局/渲染阶段异常不置 Handled：半坏状态继续会连环出错，交给进程崩溃兜底并保留堆栈
                if (msg.Contains("Layout") || msg.Contains("Arrange") || msg.Contains("Measure") || msg.Contains("Render"))
                {
                    ShowFatalError($"界面异常：{e.Exception}");
                    return;
                }
                e.Handled = true;
                ShowFatalError($"未捕获异常：{e.Exception}");
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static int _fatalShown;

    /// <summary>致命错误：写日志 + 弹崩溃报告窗口（PCL 式；防递归只弹一次）</summary>
    private static void ShowFatalError(string message)
    {
        System.Console.Error.WriteLine($"[FATAL] {message}");
        try
        {
            var logDir = Path.Combine(
                Launcher.Core.Utils.AppPaths.DataRoot, "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log"),
                $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { /* 日志写入失败不阻塞 */ }

        try
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (Interlocked.Exchange(ref _fatalShown, 1) == 1) return; // 只弹一次（展示时才置位）
                try
                {
                    if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                        Views.CrashReportWindow.Show(message);
                    else
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => Views.CrashReportWindow.Show(message));
                }
                catch { /* 弹窗失败不递归 */ }
            });
        }
        catch { }
    }

    /// <summary>
    /// 应用强调色（主题系统）：替换 Accent/AccentHover 及派生色 AccentDark（深卡）/AccentLight（亮字）/
    /// AccentSoft（半透明深卡）/OnAccent（前景对比色）——按钮/进度条/tab/卡片徽章全跟随。
    /// </summary>
    private void ApplyAccentColor(string hex)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex) || !hex.StartsWith('#')) hex = "#6C8CFF";
            var accent = Avalonia.Media.Color.Parse(hex);
            Resources["Accent"] = accent;
            // AccentHover = 每通道提亮 8%
            var h = accent;
            h = new Avalonia.Media.Color(h.A,
                (byte)Math.Min(255, h.R + 255 * 0.08),
                (byte)Math.Min(255, h.G + 255 * 0.08),
                (byte)Math.Min(255, h.B + 255 * 0.08));
            Resources["AccentHover"] = h;
            // 派生色（纯字节数学，Core 可测）
            var rgb = AccentColorMath.TryNormalizeHex(hex) ?? new Rgb24(0x6C, 0x8C, 0xFF);
            var dark = AccentColorMath.DeriveDark(rgb);
            Resources["AccentDark"] = Avalonia.Media.Color.FromRgb(dark.R, dark.G, dark.B);
            Resources["AccentSoft"] = Avalonia.Media.Color.FromArgb(AccentColorMath.SoftAlpha, dark.R, dark.G, dark.B);
            var light = AccentColorMath.DeriveLight(rgb);
            Resources["AccentLight"] = Avalonia.Media.Color.FromRgb(light.R, light.G, light.B);
            var on = AccentColorMath.DeriveOnAccent(rgb);
            Resources["OnAccent"] = Avalonia.Media.Color.FromRgb(on.R, on.G, on.B);
        }
        catch { /* 强调色非法则保持默认 */ }
    }

    /// <summary>
    /// 应用背景色（亮暗二态翻转）：解析背景色（含 alpha）→ 派生整套表面色 → 写 BackgroundColor/文字色/卡片色
    /// 资源键。浅色背景 → 深字白卡；暗色/低 alpha → 现状暗主题。非法值回退默认（不覆盖现有资源）。
    /// </summary>
    private void ApplyBackgroundColor(string? hex)
    {
        try
        {
            var bg = BackgroundPaletteMath.TryParse(hex) ?? BackgroundPaletteMath.TryParse(BackgroundPaletteMath.DefaultBackground);
            if (bg is null) return;
            var p = BackgroundPaletteMath.Derive(bg);
            Resources["BackgroundColor"] = Avalonia.Media.Color.FromArgb(bg.A, bg.R, bg.G, bg.B);
            Resources["TextPrimary"] = Avalonia.Media.Color.FromRgb(p.TextPrimary.R, p.TextPrimary.G, p.TextPrimary.B);
            Resources["TextSecondary"] = Avalonia.Media.Color.FromRgb(p.TextSecondary.R, p.TextSecondary.G, p.TextSecondary.B);
            Resources["TextDim"] = Avalonia.Media.Color.FromRgb(p.TextDim.R, p.TextDim.G, p.TextDim.B);
            Resources["BgBase"] = Avalonia.Media.Color.FromRgb(p.BgBase.R, p.BgBase.G, p.BgBase.B);
            Resources["BgSurface"] = Avalonia.Media.Color.FromRgb(p.BgSurface.R, p.BgSurface.G, p.BgSurface.B);
            Resources["BgRaised"] = Avalonia.Media.Color.FromRgb(p.BgRaised.R, p.BgRaised.G, p.BgRaised.B);
            Resources["BgHover"] = Avalonia.Media.Color.FromRgb(p.BgHover.R, p.BgHover.G, p.BgHover.B);
            Resources["BgActive"] = Avalonia.Media.Color.FromRgb(p.BgActive.R, p.BgActive.G, p.BgActive.B);
            Resources["BorderColor"] = Avalonia.Media.Color.FromRgb(p.BorderColor.R, p.BorderColor.G, p.BorderColor.B);
        }
        catch { /* 背景色非法则保持现有资源 */ }
    }

    /// <summary>8-26 打开日志中心（Toast「查看日志」按钮）——窗口内覆盖层，主窗不失活不降级</summary>
    private static void OpenLogCenter() => Views.LogCenterView.Open();

    /// <summary>应用更新：启动替换流程后退出本进程（Windows 子进程 / Unix 延迟脚本接管替换与重启）。
    /// 就绪状态靠下载包文件存在性自愈——替换成功新进程删 source，失败则下次启动仍提示重试</summary>
    private static async void ApplyUpdate(string readyPath, string tag)
    {
        var err = await Launcher.Core.Update.UpdateInstaller.StartAsync(readyPath);
        if (err is not null)
        {
            Services.NotificationService.Error($"更新失败：{err}");
            return;
        }
        Launcher.Core.Utils.AppLog.Instance?.LogInformation("[update] 安装流程已启动，进程退出由子进程/脚本接管");
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.Shutdown();
        }
        catch { }
        Environment.Exit(0);
    }

    /// <summary>生命周期调用兜底：异常只记录，不阻止窗口创建</summary>
    private static void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { System.Console.Error.WriteLine($"[FATAL] {what}: {ex}"); }
    }
}
