using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Services;
using Launcher.Core.Download;
using Launcher.Core.Model.Loader;
using Launcher.Core.Services;
using Launcher.Core.Utils;

namespace Launcher.App.ViewModels;

/// <summary>
/// 下载游戏（下载板块 tab）：分类/搜索/分页浏览所有可用版本 + 下载安装。
/// 不包含加载器安装与版本管理（那些在【版本】页）。
/// </summary>
public partial class VersionDownloadViewModel : ViewModelBase
{
    private readonly VersionManifestService _svc;
    private readonly VersionInstaller _installer;

    public VersionSidebarViewModel Sidebar { get; }
    public DownloadDetailVM Detail { get; }

    [ObservableProperty]
    public partial string Status { get; set; } = "加载中…";

    public VersionDownloadViewModel()
    {
        _svc = new VersionManifestService();
        _installer = new VersionInstaller();
        Sidebar = new VersionSidebarViewModel();
        Detail = new DownloadDetailVM(_installer, OnInstalled);
    }

    /// <summary>8-31 行点击 / 行内 [下载]：填详情并直接走下载（弹加载器选择 → 入队 → 跳下载记录）。不先进详情页。</summary>
    [RelayCommand]
    private async Task DownloadVersion(VersionListItemVM item)
    {
        Detail.Select(item);
        // 8-31 预热：点行即后台拉四种加载器版本列表写缓存——弹窗预取与它经 LoaderService 在途去重共享，
        // 用户点加载器 chip 时不再干等慢源（实测 fabric 4s / quilt 11s / neoforged 连不上）
        WarmLoaderCaches(item.Id);
        await Detail.Download();
    }

    /// <summary>后台预热该版本四种加载器的可用版本列表（失败静默；写磁盘缓存后二次即秒开）</summary>
    private static void WarmLoaderCaches(string mcVersion)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var svc = new LoaderService();
                await Task.WhenAll(
                    svc.GetLoaderVersionsAsync(LoaderKind.Fabric, mcVersion, CancellationToken.None),
                    svc.GetLoaderVersionsAsync(LoaderKind.Quilt, mcVersion, CancellationToken.None),
                    svc.GetLoaderVersionsAsync(LoaderKind.Forge, mcVersion, CancellationToken.None),
                    svc.GetLoaderVersionsAsync(LoaderKind.NeoForge, mcVersion, CancellationToken.None));
            }
            catch { /* 预热失败不影响主流程 */ }
        });
    }

    private int _loaded;

    /// <summary>幂等加载（首次进入才拉清单；失败可重试）</summary>
    public async Task EnsureLoadedAsync()
    {
        if (Volatile.Read(ref _loaded) == 1) return;
        try
        {
            await LoadAsync();
            Volatile.Write(ref _loaded, 1);
        }
        catch { /* 失败保持 0，下次进入重试 */ }
    }

    public async Task LoadAsync()
    {
        try
        {
            await _svc.RefreshAsync();
            var all = _svc.Entries.ToList();
            var releases = all.Where(e => e.Type == "release" && !VersionClassifier.IsAprilFools(e)).ToList();
            var snapshots = all.Where(e => e.Type == "snapshot" && !VersionClassifier.IsAprilFools(e)).ToList();
            var ancient = all.Where(e => e.Type is "old_alpha" or "old_beta").ToList();
            var april = all.Where(VersionClassifier.IsAprilFools).ToList();

            Sidebar.Categories.Clear();
            Sidebar.Categories.Add(new VersionCategoryItemVM("最新正式版", VersionCategory.LatestRelease,
                Math.Min(VersionClassifier.LatestReleaseCount, releases.Count), "最近 5 个稳定版本"));
            Sidebar.Categories.Add(new VersionCategoryItemVM("全部正式版", VersionCategory.AllReleases, releases.Count, "所有稳定版本"));
            Sidebar.Categories.Add(new VersionCategoryItemVM("快照", VersionCategory.Snapshots, snapshots.Count, "开发预览版"));
            Sidebar.Categories.Add(new VersionCategoryItemVM("远古", VersionCategory.Ancient, ancient.Count, "Alpha / Beta 时代"));
            Sidebar.Categories.Add(new VersionCategoryItemVM("愚人节", VersionCategory.AprilFools, april.Count, "4 月 1 日特别版"));

            Sidebar.SetAllEntries(all);
            Sidebar.SelectedCategory = Sidebar.Categories[0];
            Status = $"共 {all.Count} 个版本";
        }
        catch (Exception ex)
        {
            Status = $"加载失败: {ex.Message}";
        }
    }

    private void OnInstalled(string versionId)
    {
        _svc.RescanInstalled();
        var installedSet = new HashSet<string>(
            _svc.Entries.Where(e => e.Installed).Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        Sidebar.RefreshInstalled(installedSet);
        Detail.RefreshInstalled(installedSet);
    }
}

/// <summary>下载详情（简化：信息 + 下载/重新下载 + 进度）</summary>
public partial class DownloadDetailVM : ObservableObject
{
    private readonly VersionInstaller _installer;
    private readonly Action<string> _onInstalled;

    [ObservableProperty]
    public partial string Id { get; set; } = "";

    /// <summary>8-31 类型角标（列表行选中后带入详情）</summary>
    [ObservableProperty]
    public partial string TypeLabel { get; set; } = "";

    [ObservableProperty]
    public partial IBrush TypeBadgeBg { get; set; } = new SolidColorBrush(Color.Parse("#1E3A2E"));

    [ObservableProperty]
    public partial IBrush TypeBadgeFg { get; set; } = new SolidColorBrush(Color.Parse("#5AD07C"));

    [ObservableProperty]
    public partial string ReleaseDate { get; set; } = "";

    [ObservableProperty]
    public partial string SizeText { get; set; } = "";

    [ObservableProperty]
    public partial bool Installed { get; set; }

    [ObservableProperty]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    public partial double DownloadProgressPercent { get; set; }

    [ObservableProperty]
    public partial string ErrorText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasSelection { get; set; }

    public string? ManifestUrl { get; private set; }
    public bool ShowDownloadButton => !Installed && !IsDownloading;
    public bool ShowRepairButton => Installed && !IsDownloading;
    public bool ShowProgress => IsDownloading;
    public bool HasError => ErrorText.Length > 0;

    public DownloadDetailVM(VersionInstaller installer, Action<string> onInstalled)
    {
        _installer = installer;
        _onInstalled = onInstalled;
    }

    public void Select(VersionListItemVM item)
    {
        if (HasSelection && Id == item.Id) return;
        Id = item.Id;
        ReleaseDate = item.ReleaseDate;
        Installed = item.Installed;
        ManifestUrl = item.ManifestUrl;
        TypeLabel = item.TypeLabel;
        TypeBadgeBg = item.TypeBadgeBg;
        TypeBadgeFg = item.TypeBadgeFg;
        ErrorText = "";
        DownloadProgressPercent = 0;
        HasSelection = true;
    }

    public void RefreshInstalled(HashSet<string> installedSet)
    {
        if (HasSelection && installedSet.Contains(Id)) Installed = true;
    }

    [RelayCommand]
    public async Task Download()
    {
        if (IsDownloading) return;
        // 8-31 已装版本不拦：提示后照常走加载器选择 → 重新下载覆盖（用户明确要"不拦下载"）
        if (Installed) NotificationService.Info($"{Id} 已存在此版本，将重新下载");
        // PCL 式：先选加载器（纯净/四家 + 版本），[开始下载] 才执行
        var owner = DialogService.MainWindow();
        if (owner is null) return;
        var choice = await Views.LoaderChoiceDialog.ShowAsync(owner, Id);
        if (choice is null) return;
        await DownloadCoreAsync(repair: false, choice);
    }

    /// <summary>重新下载（损坏修复）</summary>
    [RelayCommand]
    private async Task Repair()
    {
        if (IsDownloading) return;
        var owner = DialogService.MainWindow();
        if (owner is null || !await DialogService.Confirm(owner,
                $"重新下载 {Id} 缺失或损坏的文件（已有的自动跳过）。继续？",
                "重新下载", "重新下载", "取消"))
        {
            return;
        }
        await DownloadCoreAsync(repair: true);
    }

    /// <summary>
    /// 安装：带加载器时由加载器阶段的合并下载全包（原版+加载器文件并列一个子任务列表）——
    /// AL10 去掉原版预下载阶段（LoaderService 的 DownloadVersionAsync 传 merged 版本，覆盖 client jar + 全部 libraries），
    /// 下载记录一体显示，不再"原版一坨 + 加载器一坨"。
    /// </summary>
    private async Task InstallWithLoaderAsync(
        VersionInstaller installer, Launcher.Core.Model.Mojang.VersionJson version,
        Views.LoaderChoice? choice, DownloadGroupContext ctx, CancellationToken ct)
    {
        // 纯原版：直接下载
        if (choice is null or { IsVanilla: true })
        {
            await installer.InstallAsync(version, ctx, ct);
            return;
        }
        // 带加载器：加载器服务下载合并版本全部文件（组内子任务，进度/取消级联自动生效）
        if (choice is { Kind: { } kind, Version: { } loaderVersion })
        {
            var service = new LoaderService(gameDirectory: GameDirectory.InstallDir());
            var plan = await service.CreatePlanAsync(kind, version.Id, loaderVersion, ct)
                with { InstallFabricApi = choice.InstallFabricApi };
            // AL10.2：调组重载（ctx）——LoaderService 内部 AddChild"加载器配置" + DownloadVersionAsync(version, ctx)
            // 全部文件子任务并列，有真实 weight/进度/大小。旧写法 (p, c) 匹配 progress 重载 → 扁平单任务
            // "一次性"且 TotalBytes=0 显示 "0 B"
            await service.InstallAsync(plan, ctx, ct);
            // 8-23：加载器安装完成 → 记录 loader id（主页自动选中；targetId 是 MC 原版 id，别用）
            if (service.LastInstalledVersionId is { } loaderId)
                Launcher.Core.AppState.SetLastInstalledVersion(loaderId);
        }
    }

    private async Task DownloadCoreAsync(bool repair, Views.LoaderChoice? choice = null)
    {
        if (IsDownloading) return;
        var targetId = Id;
        var targetUrl = ManifestUrl;
        IsDownloading = true;
        ErrorText = "";
        DownloadProgressPercent = 0;
        try
        {
            // AL31：每次下载重建 installer 并传当前设置——DownloadService 构造时读 settings 快照，
            // 缓存 _installer 会冻结滑块改动（限速/并发/分片要重开下载页才生效，与第三方下载/修复路径不一致）
            var installer = new VersionInstaller(
                downloads: new DownloadService(null, null, DownloadOptions.FromSettings(LauncherSettings.Current), null),
                gameDirectory: repair ? GameDirectory.InstallDir() : GameDirectory.Detect());
            var version = await installer.GetOrFetchVersionJsonAsync(targetId, targetUrl, CancellationToken.None);
            var task = DownloadManager.Instance.EnqueueGroup($"下载 {targetId}{(choice is { IsVanilla: false } ? $" + {choice.Kind}" : "")}", (ctx, ct) =>
                InstallWithLoaderAsync(installer, version, choice, ctx, ct));
            // 跳转①：入队即去下载记录看进度；完成后跳回版本页（跳转②由下载中心统一处理）
            MainViewModel.Current?.NavigateToDownloadQueue("version");

            void Sync(object? _, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(DownloadTask.ProgressPercent))
                    DownloadProgressPercent = task.ProgressPercent;
                if (e.PropertyName == nameof(DownloadTask.Error) && task.Error is { } err)
                    ErrorText = err;
            }
            task.PropertyChanged += Sync;
            try { await task.Completion; }
            finally { task.PropertyChanged -= Sync; }

            if (task.State == DownloadTaskState.Completed)
            {
                if (Id == targetId) Installed = true;
                // 8-23：记录最近安装版本（主页下拉自动选中）——加载器安装的 loader id 已在
                // InstallWithLoaderAsync 写入；纯原版这里写 targetId，加载器场景不覆盖
                if (choice is null or { IsVanilla: true })
                    Launcher.Core.AppState.SetLastInstalledVersion(targetId);
                _onInstalled(targetId);
                NotificationService.Success(repair ? $"{targetId} 修复完成" : $"{targetId} 安装完成");
            }
            else if (task.Error is { } failed)
            {
                ErrorText = failed;
            }
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsDownloading = false;
            OnPropertyChanged(nameof(ShowDownloadButton));
            OnPropertyChanged(nameof(ShowRepairButton));
            OnPropertyChanged(nameof(ShowProgress));
            OnPropertyChanged(nameof(HasError));
        }
    }
}

/// <summary>左侧分类项（副标题解释分类含义）</summary>
public sealed record VersionCategoryItemVM(string Title, VersionCategory Kind, int Count, string Subtitle);

/// <summary>版本列表：分类 + 搜索 + 全量版本（8-31 去分页，滚动浏览；行点击/行内下载由外层 VM 处理）</summary>
public partial class VersionSidebarViewModel : ObservableObject
{
    private List<VersionManifestService.GameVersionEntry> _all = [];
    private readonly Dictionary<string, VersionListItemVM> _itemsById = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<VersionCategoryItemVM> Categories { get; } = [];
    public ObservableCollection<VersionListItemVM> Items { get; } = [];

    [ObservableProperty]
    public partial VersionCategoryItemVM? SelectedCategory { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    /// <summary>列表区透明度（分类/搜索切换时 0→1 淡入过渡，去硬切感）</summary>
    [ObservableProperty]
    public partial double ListOpacity { get; set; } = 1;

    public void SetAllEntries(List<VersionManifestService.GameVersionEntry> all)
    {
        _all = all;
        _itemsById.Clear();
        foreach (var e in all)
            _itemsById[e.Id] = new VersionListItemVM(e.Id, e.Type, e.Installed,
                e.ReleaseTime.ToString("yyyy-MM-dd"), e.ManifestUrl, e.GameDirectory,
                VersionClassifier.IsAprilFools(e));
    }

    partial void OnSelectedCategoryChanged(VersionCategoryItemVM? value) => RebuildItems();

    partial void OnSearchTextChanged(string value) => RebuildItems();

    private void RebuildItems()
    {
        Items.Clear();
        IEnumerable<VersionManifestService.GameVersionEntry> source;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            // 搜索跨分类过滤（英文 id 子串 + 中文关键词）
            source = _all.Where(e => Matches(e, SearchText));
        }
        else
        {
            source = SelectedCategory?.Kind switch
            {
                VersionCategory.LatestRelease => _all.Where(e => e.Type == "release" && !VersionClassifier.IsAprilFools(e))
                                                    .Take(VersionClassifier.LatestReleaseCount),
                VersionCategory.AllReleases => _all.Where(e => e.Type == "release" && !VersionClassifier.IsAprilFools(e)),
                VersionCategory.Snapshots => _all.Where(e => e.Type == "snapshot" && !VersionClassifier.IsAprilFools(e)),
                VersionCategory.Ancient => _all.Where(e => e.Type is "old_alpha" or "old_beta"),
                VersionCategory.AprilFools => _all.Where(VersionClassifier.IsAprilFools),
                _ => [],
            };
        }

        // 8-31 无分页：全量加入（虚拟化列表滚动浏览，不翻页）
        foreach (var e in source)
            Items.Add(_itemsById[e.Id]);

        // 列表内容切换：先透明再淡入（DoubleTransition 平滑过渡）
        ListOpacity = 0;
        Dispatcher.UIThread.Post(() => ListOpacity = 1);
    }

    /// <summary>版本匹配：英文 id 子串或中文关键词（正式/稳定→release，快照→snapshot，远古→old_*，愚人→愚人节）</summary>
    private static bool Matches(VersionManifestService.GameVersionEntry e, string kw)
    {
        if (e.Id.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        return kw switch
        {
            "正式" or "稳定" => e.Type == "release",
            "快照" => e.Type == "snapshot",
            "远古" => e.Type is "old_alpha" or "old_beta",
            "愚人" => VersionClassifier.IsAprilFools(e),
            _ => false,
        };
    }

    /// <summary>安装完成重扫后点亮所有行</summary>
    public void RefreshInstalled(HashSet<string> installedSet)
    {
        foreach (var item in _itemsById.Values)
            item.Installed = installedSet.Contains(item.Id);
    }
}

/// <summary>左栏行（轻量，仅展示 + 选中）</summary>
public partial class VersionListItemVM : ObservableObject
{
    public string Id { get; }
    public string Type { get; }
    public string ReleaseDate { get; }
    public string? ManifestUrl { get; }

    /// <summary>版本所在游戏目录（安装/管理落点；空 = 未安装）</summary>
    public string GameDirectory { get; }

    /// <summary>8-31 愚人节标记（构造时由 GameVersionEntry 算好——愚人节版本 Type 字段仍是 release/snapshot，必须单独标）</summary>
    private readonly bool _isAprilFools;

    [ObservableProperty]
    public partial bool Installed { get; set; }

    /// <summary>8-31 类型中文标签（愚人节优先——愚人节版本 Type 字段仍是 release/snapshot）</summary>
    public string TypeLabel => _isAprilFools
        ? "愚人节"
        : Type switch
        {
            "snapshot" => "快照",
            "old_alpha" or "old_beta" => "远古",
            _ => "正式",
        };

    /// <summary>8-31 类型角标底色（与「已装」角标同风格）</summary>
    public IBrush TypeBadgeBg => _isAprilFools ? Badge("#3E2A3E")
        : Type switch
        {
            "snapshot" => Badge("#3A331E"),
            "old_alpha" or "old_beta" => Badge("#2A2A3E"),
            _ => Badge("#1E3A2E"),
        };

    /// <summary>8-31 类型角标文字色</summary>
    public IBrush TypeBadgeFg => _isAprilFools ? Badge("#D07AD0")
        : Type switch
        {
            "snapshot" => Badge("#D0B45A"),
            "old_alpha" or "old_beta" => Badge("#9A9AD0"),
            _ => Badge("#5AD07C"),
        };

    private static IBrush Badge(string hex) => new SolidColorBrush(Color.Parse(hex));

    public VersionListItemVM(string id, string type, bool installed, string releaseDate, string? manifestUrl,
        string gameDirectory = "", bool isAprilFools = false)
    {
        Id = id;
        Type = type;
        Installed = installed;
        ReleaseDate = releaseDate;
        ManifestUrl = manifestUrl;
        GameDirectory = gameDirectory;
        _isAprilFools = isAprilFools;
    }
}
