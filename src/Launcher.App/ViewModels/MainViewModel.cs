using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Launcher.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    /// <summary>当前实例（其他页面跳转下载记录用）</summary>
    public static MainViewModel? Current { get; private set; }

    [ObservableProperty]
    public partial ViewModelBase? CurrentPage { get; set; }

    // 导航高亮（主页/版本/下载/账号/设置）
    [ObservableProperty]
    public partial bool IsHomeActive { get; set; } = true;

    [ObservableProperty]
    public partial bool IsVersionsActive { get; set; }

    [ObservableProperty]
    public partial bool IsDownloadsActive { get; set; }

    [ObservableProperty]
    public partial bool IsSettingsActive { get; set; }

    [ObservableProperty]
    public partial bool IsMultiplayerActive { get; set; }
    [ObservableProperty]
    public partial bool IsEcosystemActive { get; set; }

    public HomeViewModel Home { get; } = new();
    public SettingsViewModel Settings { get; } = new();

    // 8-19 惰性实例化：版本/下载/联机/生态 VM 首次进入对应页才创建——
    // 启动只付主页+设置的成本（版本页的 FileSystemWatcher 递归监听、下载页的历史订阅
    // 等常驻开销全部推迟到真正用到时，平时不占内存不耗事件）
    private VersionBrowseViewModel? _versions;
    public VersionBrowseViewModel Versions => _versions ??= new();

    private DownloadViewModel? _downloads;
    public DownloadViewModel Downloads => _downloads ??= new();

    private MultiplayerViewModel? _multiplayer;
    public MultiplayerViewModel Multiplayer => _multiplayer ??= new();

    private EcosystemNavViewModel? _ecosystemNav;
    public EcosystemNavViewModel EcosystemNav => _ecosystemNav ??= new();

    /// <summary>全局当前版本（主页权威，单向驱动下载/联机页——AF1：主页选什么，后面就全都是那个版本）</summary>
    [ObservableProperty]
    public partial VersionInstanceVM? CurrentVersion { get; set; }

    /// <summary>全局运行状态（客户端；版本页徽章用——AG2 状态同步）</summary>
    [ObservableProperty]
    public partial RunningVersionInfo? RunningVersion { get; set; }

    public MainViewModel()
    {
        Current = this;
        CurrentPage = Home;
        _ = Home.InitializeAsync();
    }

    /// <summary>
    /// 跳到下载板块的"下载记录"tab（下载中"查看下载进度"链接用）。
    /// returnTo：任务完成/失败后跳回的目标页（"version" / "download:mod" 等；null = 不跳回）。
    /// </summary>
    public void NavigateToDownloadQueue(string? returnTo = null)
    {
        Navigate("download");
        Downloads.NavigateToQueue();
        Downloads.SetReturnNavigation(returnTo);
    }

    /// <summary>跳到下载板块的"下载游戏"tab（版本页引导按钮用）</summary>
    public void NavigateToDownloadGame()
    {
        Navigate("download");
        Downloads.NavigateToGame();
    }

    /// <summary>公共导航入口（支持 "download:tab" 前缀；下载完成跳回来源页用）</summary>
    public void NavigateTo(string page) => Navigate(page);

    /// <summary>从版本页启动某版本：切主页并自动启动（版本页行 [启动] 按钮）</summary>
    public void LaunchVersion(string versionId, string gameDir)
    {
        Navigate("home");
        _ = Home.RequestLaunchAsync(versionId, gameDir);
    }

    /// <summary>跳版本页并选中指定版本（主页启动失败"去版本页补全"用；先等版本列表加载完成再选中——否则首次导航 _all 为空选不中）</summary>
    public async Task NavigateToVersionAsync(string? id = null)
    {
        Navigate("version");
        if (string.IsNullOrEmpty(id)) return;
        await Versions.LoadAsync(); // 重扫完成后选中（下载补全后红字同步）
        Versions.SelectById(id);
    }

    /// <summary>停止游戏（版本页 [停止]）</summary>
    public void StopGame() => Home.StopGameCommand.Execute(null);

    [RelayCommand]
    private void Navigate(string page)
    {
        // "download:mod" / "download:thirdparty"：下载页内切指定 tab（安装完成跳回原 tab 用）
        if (page.StartsWith("download:", StringComparison.Ordinal))
        {
            Navigate("download");
            Downloads.SelectTab(page["download:".Length..]);
            return;
        }
        IsHomeActive = page == "home";
        IsVersionsActive = page == "version";
        IsDownloadsActive = page == "download";
        IsSettingsActive = page == "settings";
        IsMultiplayerActive = page == "multiplayer";
        IsEcosystemActive = page == "ecosystem";
        ReleaseOtherPages(); // 8-18 内存让渡：非激活页的大列表延迟释放（切回时各自懒重建）
        Launcher.App.Services.MemProfile.Sample(page); // 8-29 内存诊断钩子：切页采样（--mem-profile 才打）
        if (page == "download") Downloads.ActivateDefault();
        if (page == "home") { Home.RefreshConfigText(); _ = Home.RefreshVersionsAsync(); } // 切回主页刷新配置摘要+已装版本
        if (page == "version") _ = Versions.LoadAsync(); // 每次进入强制重扫（下载补全后 JarMissing 红字同步消失——AG2）
        CurrentPage = page switch
        {
            "version" => Versions,
            "download" => Downloads,
            "settings" => Settings,
            "multiplayer" => Multiplayer,
            "ecosystem" => EcosystemNav,
            _ => Home,
        };
    }

    /// <summary>
    /// 8-18 内存让渡：切页动画（~220ms）完成后释放非激活页的大列表——最大资源给当前模块。
    /// 延迟 + 释放前校验当前页：快速连续切页不会误清新激活页的数据。
    /// </summary>
    private void ReleaseOtherPages()
    {
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(500); } catch { return; }
            if (!ReferenceEquals(CurrentPage, Home)) Home.ReleaseData();
            // 8-19 惰性化后：未创建过的 VM 无需释放（属性访问会触发构造，破坏惰性）
            if (!ReferenceEquals(CurrentPage, Versions) && _versions is not null) _versions.ReleaseData();
        });
    }

}

/// <summary>全局运行状态（AG2）：Kind = 客户端</summary>
public sealed record RunningVersionInfo(string VersionId, string Kind);
