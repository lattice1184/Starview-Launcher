using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Services;
using Launcher.Core.Download;
using Launcher.Core.Ecosystem;
using Launcher.Core.Model.Modrinth;
using Launcher.Core.Services;
using PCL.Core.Minecraft.ResourceProject.Curseforge;

namespace Launcher.App.ViewModels;

/// <summary>
/// 项目详情页：项目信息 + 截图画廊 + 版本匹配/手动选择 + 更新日志 + 一键安装（含依赖解析）。
/// Modrinth / CurseForge 双源：按 card.Source 分支。
/// </summary>
public partial class ProjectDetailViewModel : ViewModelBase
{
    private readonly EcosystemService _eco;
    private readonly CurseForgeService _cf;
    private readonly ProjectCardVM _card;
    private VersionInstanceVM? _instance; // AL56：实例可变（详情页打开后切换实例 → UpdateContext 刷新）
    private readonly Action _closeCallback;
    private ModrinthVersion? _matchedVersion;
    private CurseforgeFile? _cfFile;
    private int _cfModId;
    private string? _cfGameVersion;

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string Author { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    [ObservableProperty]
    public partial string Stats { get; set; }

    [ObservableProperty]
    public partial string IconUrl { get; set; }

    [ObservableProperty]
    public partial string VersionHint { get; set; } = "匹配版本中…";

    [ObservableProperty]
    public partial string License { get; set; } = "";

    [ObservableProperty]
    public partial Bitmap? Icon { get; set; }

    [ObservableProperty]
    public partial Bitmap? Screenshot { get; set; }

    [ObservableProperty]
    public partial string Changelog { get; set; } = "";

    // ---------- 截图画廊（左右切换） ----------

    private List<string> _galleryUrls = [];

    [ObservableProperty]
    public partial int GalleryIndex { get; set; }

    [ObservableProperty]
    public partial bool HasGallery { get; set; }

    [ObservableProperty]
    public partial string GalleryCountText { get; set; } = "";

    public bool HasPrevScreenshot => GalleryIndex > 0;
    public bool HasNextScreenshot => GalleryIndex < _galleryUrls.Count - 1;

    [RelayCommand]
    private void PrevScreenshot()
    {
        if (GalleryIndex <= 0) return;
        GalleryIndex--;
        LoadScreenshot(GalleryIndex);
    }

    [RelayCommand]
    private void NextScreenshot()
    {
        if (GalleryIndex >= _galleryUrls.Count - 1) return;
        GalleryIndex++;
        LoadScreenshot(GalleryIndex);
    }

    partial void OnGalleryIndexChanged(int value)
    {
        OnPropertyChanged(nameof(HasPrevScreenshot));
        OnPropertyChanged(nameof(HasNextScreenshot));
    }

    /// <summary>载入第 index 张截图（去重防闪烁：先清再载；8-26 换图显式 Dispose 旧图——640px ≈1MB，
    /// 靠 GC 延迟回收会瞬时叠加两张）</summary>
    private void LoadScreenshot(int index)
    {
        if (Screenshot is IDisposable old) { try { old.Dispose(); } catch { } }
        Screenshot = null;
        if (index < 0 || index >= _galleryUrls.Count) return;
        _ = ImageLoader.LoadAsync(_galleryUrls[index], bmp => Screenshot = bmp, 640);
    }

    // ---------- 文件列表（当前所选版本的安装文件） ----------

    public ObservableCollection<VersionFileVM> Files { get; } = [];

    [ObservableProperty]
    public partial string FilesHeaderText { get; set; } = "";

    /// <summary>文件区显示条件（有文件才展开）</summary>
    public bool HasFiles => Files.Count > 0;

    /// <summary>项目主页 URL（详情页"打开主页"）</summary>
    [ObservableProperty]
    public partial string ProjectPageUrl { get; set; } = "";

    /// <summary>浏览器打开项目主页（source_url 或 Modrinth 页面）</summary>
    public void OpenProjectPage()
    {
        if (string.IsNullOrEmpty(ProjectPageUrl)) return;
        try { Process.Start(new ProcessStartInfo(ProjectPageUrl) { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>版本列表（PCL 式：打开即加载直显最新 10 条，每行独立安装；命中自动匹配的行带推荐标记）</summary>
    public ObservableCollection<VersionOptionVM> Versions { get; } = [];

    public bool HasVersions => Versions.Count > 0;

    /// <summary>直显版本行初始上限（「展开更多版本」逐批追加）</summary>
    private const int InitialVersionRows = 10;
    private const int ExpandStep = 20;

    /// <summary>完整版本行缓存（展开更多用；FillVersionRows 全量排序后存入，Versions 只显示前 VisibleVersionCount）</summary>
    private List<VersionOptionVM> _allVersionOptions = [];

    /// <summary>当前显示行数（初始 10，点「展开更多版本」每次 +20）</summary>
    [ObservableProperty]
    public partial int VisibleVersionCount { get; set; } = InitialVersionRows;

    public bool CanExpandMore => VisibleVersionCount < _allVersionOptions.Count;

    public string ShowMoreText => $"展开更多版本（剩余 {_allVersionOptions.Count - VisibleVersionCount}）";

    [RelayCommand]
    private void ExpandVersions()
    {
        if (!CanExpandMore) return;
        VisibleVersionCount = Math.Min(_allVersionOptions.Count, VisibleVersionCount + ExpandStep);
        ReapplyVisibleVersions();
    }

    /// <summary>按 VisibleVersionCount 重填显示行 + 刷新展开按钮状态</summary>
    private void ReapplyVisibleVersions()
    {
        Versions.Clear();
        foreach (var v in _allVersionOptions.Take(VisibleVersionCount)) Versions.Add(v);
        OnPropertyChanged(nameof(HasVersions));
        OnPropertyChanged(nameof(CanExpandMore));
        OnPropertyChanged(nameof(ShowMoreText));
    }

    // 匹配文件块（8-22：单独展示 + 直接下载 jar 到下载缓存，不经过安装/依赖流程）
    [ObservableProperty]
    public partial string MatchedFileText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasMatchedFile { get; set; }

    [ObservableProperty]
    public partial bool IsDownloadingMatched { get; set; }

    [ObservableProperty]
    public partial double MatchedDownloadProgress { get; set; }

    [ObservableProperty]
    public partial string MatchedDownloadState { get; set; } = "";

    // 安装状态
    [ObservableProperty]
    public partial bool CanInstall { get; set; }

    [ObservableProperty]
    public partial string InstallButtonText { get; set; } = "安装";

    [ObservableProperty]
    public partial bool IsInstalling { get; set; }

    [ObservableProperty]
    public partial bool InstallDone { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string ProgressState { get; set; } = "";

    [ObservableProperty]
    public partial string InstalledPath { get; set; } = "";

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = "";

    [ObservableProperty]
    public partial string DependenciesText { get; set; } = "";

    /// <summary>前置提示（"将安装 2 个前置：A、B"）；安装按钮文字随之更新</summary>
    [ObservableProperty]
    public partial string DependencyHint { get; set; } = "";

    /// <summary>下载中"查看下载进度"跳转</summary>
    [RelayCommand]
    private void GoToDownloadQueue() => MainViewModel.Current?.NavigateToDownloadQueue();

    public ProjectDetailViewModel(EcosystemService eco, CurseForgeService cf, ProjectCardVM card,
        VersionInstanceVM? instance, Action closeCallback)
    {
        _eco = eco;
        _cf = cf;
        _card = card;
        _instance = instance;
        _closeCallback = closeCallback;
        Title = card.Title;
        Author = card.Author;
        Description = card.Description;
        Stats = $"{card.DownloadsText} 下载 · {card.FollowsText} 关注";
        IconUrl = card.IconUrl;
        CanInstall = false;
        _ = ImageLoader.LoadAsync(IconUrl, bmp => Icon = bmp);
        _ = LoadAsync();
    }

    [RelayCommand]
    private void Close() => _closeCallback();

    private async Task LoadAsync()
    {
        if (_card.Source == "curseforge")
        {
            await LoadCfAsync();
            return;
        }
        try
        {
            var captured = _instance; // REVIEW-C：实例切换守卫——await 后检查，旧实例结果不再覆盖
            string? gameVersion = null;
            string? loader = null;
            if (captured is not null)
            {
                if (captured.ResolvedGameVersion.Length > 0) gameVersion = captured.ResolvedGameVersion;
                // 8-19：光影包/材质包无加载器概念——派生 loader 会把 Modrinth 版本列表滤没（同搜索页 IsModType gate）；
                // 用户显式选加载器不受影响
                loader = _card.Type == ProjectType.Mod ? EcosystemService.GuessLoader(captured.Name) : null;
            }
            // PCL 式：一次请求拿全量版本——匹配（SelectBestVersion）与直显列表（最新 10 条）共用
            // 8-26 整合包自带游戏版本，不拿选中实例过滤——直接取最新 release
            var effGameVersion = _card.Type == ProjectType.Modpack ? null : gameVersion;
            var all = await SlowQueryNotifier.WatchAsync(_eco.GetVersionsAsync(_card.Id, effGameVersion, loader),
                "仍在查询版本信息（网络较慢），请稍候…", TimeSpan.FromSeconds(3));
            if (!ReferenceEquals(_instance, captured)) return; // 实例已切换 → 放弃旧实例结果
            // 8-26 自动匹配选兼容版本：26.x 年份号全量返回（API 不认年份号）→ 客户端按游戏版本过滤后再选；
            // 手动版本列表（FillVersionRows）仍全量显示，用户可自行挑其他版本
            var candidates = effGameVersion is not null && EcosystemService.IsYearFormatVersion(effGameVersion)
                ? EcosystemService.FilterByGameVersion(all, effGameVersion)
                : all;
            var version = EcosystemService.SelectBestVersion(candidates);
            FillVersionRows(all, version?.Id);
            _matchedVersion = version;
            // 8-27 初始即填充选中版本的文件列表（否则文件区空着，用户得先点一次版本才见文件）
            if (version is not null) RefreshFiles(version);
            // 匹配文件块：匹配版本的主文件（直接下载用；安装仍走完整依赖流程）
            var matchedFile = version is null ? null : EcosystemService.PickPrimaryFile(version.Files);
            MatchedFileText = matchedFile is null ? ""
                : $"{matchedFile.FileName} · {FormatSize(matchedFile.Size)} · {string.Join("/", version.GameVersions?.Take(2) ?? [])}";
            HasMatchedFile = matchedFile is not null;
            VersionHint = version is null
                ? (_instance is null
                    ? "你还没选目标实例。去生态页顶部选一个。"
                    : $"没有 {_instance.Name} 能用的版本，在下面列表里选一个试试")
                : $"匹配版本: {version.Name} ({version.VersionNumber})";
            CanInstall = version is not null;
            if (version is not null) Changelog = version.Changelog ?? "";
            if (version is not null) _ = ResolveDependencyHintAsync(version, gameVersion, loader);

            // 项目详情（截图/许可证）
            try
            {
                var detail = await SlowQueryNotifier.WatchAsync(_eco.GetProjectAsync(_card.Id),
                    "仍在查询项目详情（网络较慢），请稍候…", TimeSpan.FromSeconds(3));
                if (detail is not null)
                {
                    License = detail.License?.Name is { } ln ? $"许可: {ln}" : "";
                    ProjectPageUrl = detail.SourceUrl ?? $"https://modrinth.com/project/{detail.Slug}";
                    _galleryUrls = detail.Gallery?.Select(g => g.Url).ToList() ?? [];
                    HasGallery = _galleryUrls.Count > 1;
                    GalleryCountText = _galleryUrls.Count > 1 ? $"1/{_galleryUrls.Count}" : "";
                    GalleryIndex = 0;
                    if (_galleryUrls.Count > 0) LoadScreenshot(0);
                }
            }
            catch { /* 详情拉取失败不阻塞 */ }
        }
        catch (Exception ex)
        {
            VersionHint = $"匹配失败: {ex.Message}";
            LogCfFailure(ex);
        }
    }

    /// <summary>CurseForge 详情：项目信息 + 最佳文件匹配 + 依赖计数（CF 无 changelog/关注字段）</summary>
    private async Task LoadCfAsync()
    {
        try
        {
            if (!int.TryParse(ProjectCardVM.ParseId(_card.Id).RawId, out var modId)) return;
            var capturedInstance = _instance; // 8-22 实例切换守卫（对齐 Modrinth 路径 REVIEW-C）：await 后检查，旧实例结果不再覆盖
            _cfModId = modId;
            string? gameVersion = null;
            string? loader = null;
            if (capturedInstance is not null)
            {
                // 8-22：加载器一并解析——CF files 双加载器变体（JEI/cloth-config 等 neoforge+fabric）
                // 不带 gameVersionTypeId 会混入敌对加载器版本
                if (capturedInstance.ResolvedGameVersion.Length > 0) gameVersion = capturedInstance.ResolvedGameVersion;
                loader = EcosystemService.GuessLoader(capturedInstance.Name);
            }
            _cfGameVersion = gameVersion;

            // PCL 式：一次请求拿全量文件——匹配（SelectBestFile）与直显列表（最新 10 条）共用。
            // 8-19：GetFilesWithFallbackAsync 带 dropped——26.2 年份号 CF 返回空已降级全量，不能再按 26.2 过滤
            var (files, dropped) = await SlowQueryNotifier.WatchAsync(_cf.GetFilesWithFallbackAsync(modId, gameVersion, default, loader),
                "仍在查询 CurseForge 文件（网络较慢），请稍候…", TimeSpan.FromSeconds(3));
            if (!ReferenceEquals(_instance, capturedInstance)) return; // 实例已切换 → 放弃旧实例结果（否则旧变体装进新实例目录）
            var file = CurseForgeService.SelectBestFile(files, dropped ? null : gameVersion, loader);
            FillVersionRowsCf(files, file?.id.ToString());
            _cfFile = file;
            VersionHint = file is null
                ? (_instance is null
                    ? "你还没选目标实例。去生态页顶部选一个。"
                    : $"未匹配到 {_instance.Name} 的版本，在下面列表里选一个试试")
                : $"匹配文件: {file.fileName}";
            MatchedFileText = file is null ? ""
                : $"{file.displayName ?? file.fileName} · {FormatSize(file.fileLength)} · {string.Join("/", file.gameVersions?.Take(2) ?? [])}";
            HasMatchedFile = file is not null;
            CanInstall = file is not null;
            if (file is not null)
            {
                // 8-22 修复：CF files 列表响应的 dependencies 恒为空数组（tweakeroo/minihud 实测）——
                // 依赖只在单文件详情端点返回 → 后台补查，提示从「正在查询」更新为真实依赖数
                DependencyHint = "正在查询前置依赖…";
                _ = RefreshCfDependenciesAsync(modId, file.id, gameVersion, loader);
            }

            try
            {
                var detail = await SlowQueryNotifier.WatchAsync(_cf.GetProjectAsync(modId),
                    "仍在查询 CurseForge 项目（网络较慢），请稍候…", TimeSpan.FromSeconds(3));
                if (detail is not null)
                {
                    Title = detail.name;
                    Author = detail.authors is { Count: > 0 } ? string.Join("、", detail.authors.Select(a => a.name)) : "";
                    Description = detail.summary ?? "";
                    Stats = $"{ProjectCardVM.FormatCount(detail.downloadCount)} 下载";
                    IconUrl = detail.logo?.thumbnailUrl ?? "";
                    ProjectPageUrl = detail.links?.websiteUrl is { Length: > 0 } u
                        ? u
                        : $"https://www.curseforge.com/minecraft/mc-mods/{detail.slug}";
                    _galleryUrls = (detail.screenshots ?? []).Select(s => s.thumbnailUrl).ToList();
                    HasGallery = _galleryUrls.Count > 1;
                    GalleryCountText = _galleryUrls.Count > 1 ? $"1/{_galleryUrls.Count}" : "";
                    GalleryIndex = 0;
                    if (_galleryUrls.Count > 0) LoadScreenshot(0);
                }
            }
            catch { /* 详情拉取失败不阻塞 */ }
        }
        catch (Exception ex)
        {
            VersionHint = $"匹配失败: {ex.Message}";
            LogCfFailure(ex);
        }
    }

    /// <summary>8-22 后台补查前置依赖：CF 三端点（列表/单文件详情/项目 latestFiles）对 tweakeroo/minihud 等
    /// 依赖数据全空（实测）——CF 有数据用 CF，否则按名搜 Modrinth 拿最新版本依赖计数</summary>
    private async Task RefreshCfDependenciesAsync(int modId, int fileId, string? gameVersion, string? loader)
    {
        try
        {
            var detail = await SlowQueryNotifier.WatchAsync(_cf.GetFileAsync(modId, fileId),
                "仍在查询文件详情（网络较慢），请稍候…", TimeSpan.FromSeconds(3));
            var depCount = (detail?.dependencies ?? []).Count(d => d.relationType == 1);
            if (depCount > 0) { DependencyHint = $"将安装 {depCount} 个前置依赖"; return; }
            var m = await CountModrinthRequiredDepsAsync(gameVersion, loader);
            if (m > 0) { DependencyHint = $"将安装 {m} 个前置依赖"; return; }
            // 8-22 修复：m==0 确认无依赖；m<0（搜不到/网络失败=未知）不得误报「无需」——显示占位避免误导安装确认
            DependencyHint = m == 0 ? "无需前置依赖" : "前置依赖未知（网络慢或该 mod 未在 Modrinth 收录）";
        }
        catch { DependencyHint = "前置依赖未知"; } // 8-22 修复：catch 不得留「正在查询…」占位（安装确认弹窗会展示它）
    }

    /// <summary>8-22 Modrinth 按名搜 + 最新版本 required 依赖数；-1 = 搜不到/网络失败（未知）</summary>
    private async Task<int> CountModrinthRequiredDepsAsync(string? gameVersion, string? loader)
    {
        try
        {
            var page = await SlowQueryNotifier.WatchAsync(_eco.SearchAsync(_card.Type, _card.Title, gameVersion, loader, limit: 1),
                "仍在跨源查找项目（网络较慢），请稍候…", TimeSpan.FromSeconds(3));
            var hit = page?.Hits?.FirstOrDefault();
            if (hit is null) return -1;
            var versions = await SlowQueryNotifier.WatchAsync(_eco.GetVersionsAsync(hit.ProjectId, gameVersion, loader),
                "仍在查询 Modrinth 版本（网络较慢），请稍候…", TimeSpan.FromSeconds(3));
            return (versions?.FirstOrDefault()?.Dependencies ?? [])
                .Count(d => d.DependencyType == "required");
        }
        catch { return -1; }
    }

    /// <summary>8-22 直接下载匹配文件：jar 落到当前版本实例 mods（versions/{CurrentVersionId}/mods）——
    /// 8-22 修复：原落下载缓存 downloads/mods，用户期望直接装进版本（路径错误反馈）；无选中版本回退下载缓存。
    /// CF 用 SHA1 校验（幂等：已下载过直接跳过）；Modrinth 主文件同路径</summary>
    [RelayCommand]
    private async Task DownloadMatchedFile()
    {
        string? url = null, fileName = null, sha1 = null;
        long size = 0;
        if (_cfFile is { downloadUrl.Length: > 0 } cf)
        {
            url = cf.downloadUrl;
            fileName = Path.GetFileName(cf.fileName);
            sha1 = cf.hashes?.FirstOrDefault(h => h.algo == 1)?.value;
            size = cf.fileLength;
        }
        else if (_matchedVersion?.Files is { Count: > 0 })
        {
            var f = EcosystemService.PickPrimaryFile(_matchedVersion.Files);
            url = f?.Url;
            fileName = f?.FileName;
            sha1 = f?.Hashes?.Sha1;
            size = f?.Size ?? 0;
        }
        // 8-22 修复：两分支统一消毒——剥目录成分 + 清洗 Windows 非法字符（否则路径逃逸/落盘失败）
        if (fileName is not null)
        {
            fileName = Path.GetFileName(fileName);
            foreach (var c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');
        }
        if (url is null || string.IsNullOrEmpty(fileName)) return;

        // 8-23 强制选目标（用户拍板：所有下载强制选实例，除第三方；整合包走独立实例）——
        // 无当前版本 → 拦截提示，不静默回退 downloads\mods；有则弹目录选择器确认落点，取消不下载。
        if (string.IsNullOrEmpty(Launcher.Core.AppState.CurrentVersionId))
        {
            NotificationService.Error("先选目标实例（主页版本下拉选中后再匹配下载）");
            return;
        }
        var baseDir = _instance is { GameDir.Length: > 0 } inst
            ? Launcher.Core.Utils.GameDirectory.ModInstallBaseDir(inst.GameDir)
            : Launcher.Core.Utils.GameDirectory.InstallDir();
        var defaultDir = Launcher.Core.Services.EcosystemService.ResolveInstallPath(
            baseDir, Launcher.Core.AppState.CurrentVersionId, _card.Type);
        if (DialogService.MainWindow() is { } pickerOwner)
        {
            var chosen = await DialogService.ConfirmInstallPath(
                pickerOwner, defaultDir, Launcher.Core.AppState.CurrentVersionId, _card.Type);
            if (chosen is null) return; // 取消 → 不下载
            defaultDir = chosen;
        }
        var destPath = Path.Combine(defaultDir, fileName);
        IsDownloadingMatched = true;
        MatchedDownloadState = "";
        MatchedDownloadProgress = 0;
        // 8-19 生态修缮：走全局下载引擎队列——自动进下载记录/历史（可重新下载/打开位置），
        // 同 URL 文件带 .parts 断点续传与暂停/重试；此前直接 DownloadFileAsync 绕队列（用户反馈「匹配下载不进记录」）
        var task = DownloadManager.Instance.Enqueue($"下载 {fileName}",
            (p, ct) => new DownloadService().DownloadFileAsync(url, destPath, sha1, size > 0 ? size : null, p, ct),
            url, destPath);
        // 内联进度保持（与下载中心同源：订阅任务进度，不再走独立进度回调）
        void Sync(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DownloadTask.ProgressPercent)) MatchedDownloadProgress = task.ProgressPercent;
        }
        task.PropertyChanged += Sync;
        try
        {
            await task.Completion;
            MatchedDownloadProgress = 100;
            // 8-22 路径修复后：落版本 mods 即装好；有前置才提示补装依赖
            var depNote = DependencyHint.StartsWith("将安装")
                ? "（该 mod 有前置依赖，建议用「安装」自动补装）"
                : "（已装进当前版本 mods，重启游戏生效）";
            MatchedDownloadState = $"已装到：{destPath} {depNote}";
            NotificationService.Success($"已装到 {Launcher.Core.AppState.CurrentVersionId} 的 mods：{fileName}");
        }
        catch (Exception ex)
        {
            MatchedDownloadState = $"下载失败：{ex.Message}";
        }
        finally
        {
            task.PropertyChanged -= Sync;
            IsDownloadingMatched = false;
        }
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024.0 / 1024:0.#} MB"
        : bytes >= 1024 ? $"{bytes / 1024:0} KB" : $"{bytes} B";

    /// <summary>8-22 CF 诊断：CF 侧全参数矩阵实测永不产生「格式异常」——失败必留痕（真实 modId/版本/异常全貌），
    /// 复现后读 exe 目录 cf-debug.log 定位（怀疑本地加速器/代理对 CF 请求返回 200+HTML 或超时）</summary>
    private void LogCfFailure(Exception ex)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cf-debug.log"),
                $"[{DateTime.Now:HH:mm:ss}] source={_card.Source} mod={_cfModId} game={_cfGameVersion} {ex.GetType().Name}: {ex.Message}\n");
        }
        catch { /* 日志失败不影响 UI */ }
    }

    /// <summary>后台解析依赖：前置提示 + 安装按钮文字（"安装（含 N 个前置）"）</summary>
    private async Task ResolveDependencyHintAsync(ModrinthVersion version, string? gameVersion, string? loader)
    {
        try
        {
            var names = await SlowQueryNotifier.WatchAsync(Task.Run(() => _eco.ResolveDependencyNamesAsync(version, gameVersion, loader, CancellationToken.None)),
                "仍在查询前置依赖（网络较慢），请稍候…", TimeSpan.FromSeconds(3));
            if (names.Count == 0)
            {
                DependencyHint = "无需前置依赖";
                return;
            }
            DependencyHint = $"将安装 {names.Count} 个前置：{string.Join("、", names)}";
            if (!IsInstalling && InstallButtonText == "安装")
                InstallButtonText = $"安装（含 {names.Count} 个前置）";
        }
        catch { /* 解析失败不阻塞安装 */ }
    }

    /// <summary>版本行填充（Modrinth：最新 10 条直显；命中自动匹配的版本带推荐标记——与匹配同源同请求）</summary>
    private void FillVersionRows(IEnumerable<ModrinthVersion> all, string? versionId)
    {
        // 8-27 展开更多：全量排序缓存进 _allVersionOptions，Versions 只显示前 VisibleVersionCount（初始 10）；
        // 自动匹配命中的行初始 IsSelected（默认高亮 + 文件列表跟随）
        _allVersionOptions = all.Where(v => v.Files is { Count: > 0 })
            .OrderByDescending(v => v.DatePublished)
            .Select(v => VersionOptionVM.FromModrinth(v) with { IsRecommended = v.Id == versionId })
            .ToList();
        foreach (var o in _allVersionOptions) o.IsSelected = o.IsRecommended;
        VisibleVersionCount = InitialVersionRows;
        ReapplyVisibleVersions();
    }

    /// <summary>版本行填充（CurseForge：无发布时间字段，按 fileId 降序近似"最新"——沿用现有语义）</summary>
    private void FillVersionRowsCf(IEnumerable<CurseforgeFile> files, string? fileId)
    {
        _allVersionOptions = files.OrderByDescending(f => f.id)
            .Select(f => VersionOptionVM.FromCf(f) with { IsRecommended = f.id.ToString() == fileId })
            .ToList();
        foreach (var o in _allVersionOptions) o.IsSelected = o.IsRecommended;
        VisibleVersionCount = InitialVersionRows;
        ReapplyVisibleVersions();
    }

    /// <summary>界面内查看指定版本（8-27：点击版本行「只选不装」——刷新文件列表/变更日志/匹配信息/依赖提示，高亮该行）</summary>
    [RelayCommand]
    private void SelectVersion(VersionOptionVM option)
    {
        if (option.Source is ModrinthVersion mv)
        {
            _matchedVersion = mv;
            _cfFile = null;
            Changelog = mv.Changelog ?? "";
            VersionHint = $"已选择: {mv.Name} ({mv.VersionNumber})";
            RefreshFiles(mv);
            // 8-19 生态修缮（对齐 CF 分支）：选新版本后重查依赖——不同版本依赖树不同，
            // 此前留旧版本提示、安装装新版本（提示与实装不一致；安装内部会再解析，此处只为提示正确）
            DependencyHint = "正在查询前置依赖…";
            _ = ResolveDependencyHintAsync(mv,
                _instance is not null && _instance.ResolvedGameVersion.Length > 0 ? _instance.ResolvedGameVersion : null,
                _instance is not null ? EcosystemService.GuessLoader(_instance.Name) : null);
        }
        else if (option.Source is CurseforgeFile cf)
        {
            _cfFile = cf;
            _matchedVersion = null;
            VersionHint = $"已选择: {cf.fileName}";
            RefreshFilesCf(cf);
            // 8-22 修复：匹配文件块同步刷新（否则块显示旧文件、下载按钮实际下新选的）；依赖提示重查
            MatchedFileText = $"{cf.displayName ?? cf.fileName} · {FormatSize(cf.fileLength)} · {string.Join("/", cf.gameVersions?.Take(2) ?? [])}";
            HasMatchedFile = true;
            MatchedDownloadState = "";
            _cfModId = cf.modId;
            _cfGameVersion = _instance is not null && _instance.ResolvedGameVersion.Length > 0 ? _instance.ResolvedGameVersion : null;
            DependencyHint = "正在查询前置依赖…";
            _ = RefreshCfDependenciesAsync(cf.modId, cf.id, _cfGameVersion,
                _instance is not null ? EcosystemService.GuessLoader(_instance.Name) : null);
        }
        else return;
        // 行高亮：清其他行选中，仅当前行保持
        foreach (var v in _allVersionOptions) v.IsSelected = false;
        option.IsSelected = true;
    }

    /// <summary>行内安装指定版本（PCL 式：点击即装；推荐高亮不阻断其他行；复用底部安装管线）</summary>
    [RelayCommand]
    private Task InstallVersion(VersionOptionVM option)
    {
        if (IsInstalling) return Task.CompletedTask; // 防连点双装
        if (option.Source is not (ModrinthVersion or CurseforgeFile)) return Task.CompletedTask;
        SelectVersion(option); // 先选中+刷新查看（不装），再走安装管线
        return Install(default); // 依赖/冲突/路径确认/下载中心全走现有管线
    }

    /// <summary>实例/上下文切换（AL56）：重解析版本参数 → 自动匹配重跑 + 手动列表自动重载。
    /// REVIEW-F：去 Task.Run——UI 线程启动 async，Avalonia AutoInstall 保证 continuation 回 UI 线程
    /// （原 Task.Run 使 LoadVersions 的 AllVersions.Clear/Add 在池线程执行 → 跨线程异常）；
    /// REVIEW-C：实例再切换时放弃本次结果（seq 捕获），防旧实例响应晚到覆盖新实例
    /// （_matchedVersion 与 _instance 错配 → 安装装错目录）。</summary>
    public void UpdateContext(VersionInstanceVM? instance)
    {
        _instance = instance;
        _matchedVersion = null;
        _cfFile = null;
        Files.Clear();
        Versions.Clear(); // 切实例先清旧行，防旧实例数据闪烁
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasVersions));
        _ = LoadAndReloadVersionsAsync();
    }

    private async Task LoadAndReloadVersionsAsync()
    {
        var captured = _instance;
        await LoadAsync(); // LoadAsync 内含版本列表加载（PCL 式直显）——切实例自动刷新匹配+列表+高亮
        if (!ReferenceEquals(_instance, captured)) return; // 实例已切换 → 放弃本次结果
    }

    /// <summary>文件列表：主文件 + 附带文件（名称/大小）</summary>
    private void RefreshFiles(ModrinthVersion version)
    {
        Files.Clear();
        if (version.Files is null) return;
        foreach (var f in version.Files)
            Files.Add(new VersionFileVM(f.FileName, f.Size));
        FilesHeaderText = Files.Count > 0 ? $"文件（{Files.Count}）" : "";
        OnPropertyChanged(nameof(HasFiles));
    }

    /// <summary>CF 文件列表（单文件：名称/大小）</summary>
    private void RefreshFilesCf(CurseforgeFile file)
    {
        Files.Clear();
        Files.Add(new VersionFileVM(file.fileName, file.fileLength));
        FilesHeaderText = "文件（1）";
        OnPropertyChanged(nameof(HasFiles));
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task Install(CancellationToken ct)
    {
        IsInstalling = true;
        InstallDone = false;
        ErrorMessage = "";
        CanInstall = false;
        InstallButtonText = "取消";
        Progress = 0;
        // 8-16 批次 53：版本信息查询（api.modrinth.com 国内 8.6s/次）期间明示耗时——避免「卡住」错觉
        ProgressState = "正在获取版本信息…（网络较慢时可能需要一些时间）";

        if (_card.Source == "curseforge")
        {
            await InstallCfAsync(ct);
            return;
        }

        try
        {
            if (_instance is null && _card.Type != ProjectType.Modpack)
                throw new InvalidOperationException("你还没选目标实例。去生态页顶部选一个。");

            var version = _matchedVersion
                ?? throw new InvalidOperationException("没有匹配的可用版本");

            var gameVersion = _instance is not null && _instance.ResolvedGameVersion.Length > 0
                ? _instance.ResolvedGameVersion
                : version.GameVersions?.FirstOrDefault() ?? "";
            var loader = EcosystemService.GuessLoader(_instance?.Name ?? "");
            var instanceName = _instance?.Name ?? "modpack";
            // MOD 落点：版本来源目录（PCL 扫描版本 → PCL 目录；自建版本 → 自建目录）——AF2
            // 8-19 生态修缮：外来（PCL/官方）实例只读不写——下载落点归类启动器目录
            var gameDirFor = _instance is { GameDir.Length: > 0 } inst
                ? Launcher.Core.Utils.GameDirectory.ModInstallBaseDir(inst.GameDir)
                : Launcher.Core.Utils.GameDirectory.InstallDir();

            // 安装前路径确认（8-22 可编辑目录 + 实时预览落点）——null = 取消；改了就用新目录。
            // 8-23 整合包豁免强制选目标：装独立实例 downloads/modpacks，不弹目录选择器
            if (_card.Type != ProjectType.Modpack && DialogService.MainWindow() is { } owner2)
            {
                var chosen = await DialogService.ConfirmInstallPath(owner2, gameDirFor, instanceName, _card.Type);
                if (chosen is null) return;
                gameDirFor = chosen;
            }

            // 依赖可选跳过：全部安装 / 仅主文件（依赖数来自安装前的解析提示）
            var includeDeps = true;
            if (DependencyHint.Length > 0 && DialogService.MainWindow() is { } owner)
            {
                includeDeps = await DialogService.Confirm(owner,
                    DependencyHint, $"安装 {_card.Title}", "全部安装", "仅主文件");
            }

            // 冲突提示：目标文件夹已有同名文件 / 已安装同 mod（fabric.mod.json id 匹配）——AF3
            if (_card.Type != ProjectType.Modpack && !await EnsureNoConflictAsync(gameDirFor, instanceName, version))
                return;

            // 经全局下载中心执行（后台线程 + 队列 UI + 内联进度双显示，一处真相）
            await ExecuteInstallAsync(async (gctx, dp, t) =>
            {
                if (includeDeps)
                    return await _eco.InstallWithDependenciesAsync(_card.Id, version, instanceName, _card.Type,
                        gameVersion, loader, dp, t, gameDirOverride: gameDirFor, ctx: gctx);
                string? path = null;
                if (gctx is null)
                    path = await _eco.InstallAsync(_card.Id, version, instanceName, _card.Type, dp, t, gameDirFor);
                else
                {
                    var child = gctx.AddChild($"主文件 {version.Name}", EcosystemService.PickPrimaryFile(version.Files)?.Size ?? 0,
                        (p, c) => _eco.InstallAsync(_card.Id, version, instanceName, _card.Type, p, c, gameDirFor));
                    await child.Completion.WaitAsync(t);
                }
                var r = new DependencyInstallReport();
                r.Installed.Add(new InstalledDependency(_card.Id, version.Id, path ?? ""));
                return r;
            }, instanceName, ct);
        }
        catch (OperationCanceledException)
        {
            ProgressState = "已取消";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            ProgressState = "安装失败";
        }
        finally
        {
            IsInstalling = false;
            if (!InstallDone) CanInstall = true;
        }
    }

    /// <summary>冲突提示（AF3）：目标目录已有同名文件 / 已安装同 mod（fabric.mod.json id 匹配）→ 确认弹窗；false = 取消安装</summary>
    private async Task<bool> EnsureNoConflictAsync(string gameDir, string instanceId, ModrinthVersion version)
    {
        var owner = DialogService.MainWindow();
        if (owner is null) return true;
        var targetDir = EcosystemService.ResolveInstallPath(gameDir, instanceId, _card.Type);
        // 同名文件
        var fileName = version.Files?.FirstOrDefault()?.FileName ?? "";
        if (fileName.Length > 0 && File.Exists(Path.Combine(targetDir, fileName)))
            return await DialogService.Confirm(owner,
                $"目标文件夹已有同名文件：{fileName}\n覆盖下载？", "文件已存在", "覆盖", "取消");
        // 同 mod id（扫描 mods 下 jar 的 fabric.mod.json）
        if (_card.Type == ProjectType.Mod && Directory.Exists(targetDir))
        {
            foreach (var jar in Directory.EnumerateFiles(targetDir, "*.jar"))
            {
                if (JarModId(jar) != _card.Id) continue;
                return await DialogService.Confirm(owner,
                    $"「{_card.Title}」已经装在这个版本的 mods 文件夹里（检测到 {Path.GetFileName(jar)}）。\n还要下载？",
                    "已安装此模组", "仍要下载", "取消");
            }
        }
        return true;
    }

    /// <summary>读 jar 的 fabric.mod.json id（Forge mods 无此文件返回空；读取失败静默）</summary>
    private static string JarModId(string jarPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(jarPath);
            var entry = zip.GetEntry("fabric.mod.json") ?? zip.GetEntry("META-INF/fabric.mod.json");
            if (entry is null) return "";
            using var sr = new StreamReader(entry.Open());
            var doc = System.Text.Json.JsonDocument.Parse(sr.ReadToEnd());
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    /// <summary>经全局下载中心执行安装：组任务（下载中心可见依赖子任务）+ 内联进度同步 + 状态收尾（Modrinth/CurseForge 共用）</summary>
    private async Task ExecuteInstallAsync(
        Func<DownloadGroupContext?, DownloadProgressHandler?, CancellationToken, Task<DependencyInstallReport?>> work,
        string instanceName, CancellationToken ct)
    {
        DependencyInstallReport? report = null;
        var task = DownloadManager.Instance.EnqueueGroup($"安装 {_card.Title}", async (gctx, t) =>
        {
            report = await work(gctx, null, t); // ctx 模式下进度走子任务，外层 progress 不需要
        });
        // 跳转①：入队即去下载记录看进度；完成后跳回本 tab（详情层叠还在，跳转②由下载中心统一处理）
        MainViewModel.Current?.NavigateToDownloadQueue($"download:{DownloadViewModel.TabFor(_card.Type)}");
        if (ct.CanBeCanceled) ct.Register(() => task.Cancel());

        // 内联进度区订阅同一任务属性
        void Sync(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DownloadTask.ProgressPercent)) Progress = task.ProgressPercent;
            else if (e.PropertyName == nameof(DownloadTask.Stage)) ProgressState = task.Stage;
            else if (e.PropertyName == nameof(DownloadTask.State)) { ProgressState = task.StateText; IsInstalling = task.IsActive; }
            else if (e.PropertyName == nameof(DownloadTask.Error) && task.Error is { } err) ErrorMessage = err;
        }
        task.PropertyChanged += Sync;
        try { await task.Completion; }
        finally { task.PropertyChanged -= Sync; }

        if (ct.IsCancellationRequested)
        {
            ProgressState = "已取消";
        }
        else if (task.State == DownloadTaskState.Completed && report is { AllSucceeded: true })
        {
            InstalledPath = report.Installed.Count > 0 ? report.Installed[0].Path : "";
            var depCount = report.Installed.Count - 1;
            DependenciesText = depCount > 0
                ? $"已安装 {depCount} 个依赖"
                : report.Failed.Count > 0
                    ? $"{report.Failed.Count} 个依赖解析失败（不影响主文件）"
                    : "";
            InstallDone = true;
            ProgressState = "安装完成";
            Progress = 100;
            InstallButtonText = "已安装";
            // 长通知告知保存位置（Toast 支持换行；用户明确要求知道文件放哪了）
            if (InstalledPath.Length > 0 && _card.Type != ProjectType.Modpack)
                NotificationService.Success($"已安装到：{InstalledPath}");
            if (_card.Type == ProjectType.Modpack)
            {
                // AL47 断链修复：下载完成即询问导入创建可启动实例（不再手动去版本页）
                if (InstalledPath.Length > 0
                    && DialogService.MainWindow() is { } owner
                    && await DialogService.Confirm(owner,
                        "整合包下载好了。现在导入，创建能启动的版本实例？",
                        "导入整合包", "立即导入", "稍后"))
                {
                    ModpackImportFlow.StartAsync(InstalledPath);
                }
                else
                {
                    NotificationService.Info("整合包已保存到 downloads/modpacks。去【版本】页点「导入整合包」就能创建实例。");
                }
            }
        }
        else if (task.State == DownloadTaskState.Completed)
        {
            var failed = report is null ? "" : string.Join("; ", report.Failed.Select(f => $"{f.ProjectId}: {f.Reason}"));
            ErrorMessage = $"部分安装失败: {failed}";
            ProgressState = "部分失败";
            InstallButtonText = "安装";
        }
        else if (task.State == DownloadTaskState.Failed)
        {
            ErrorMessage = task.Error ?? "未知错误";
            ProgressState = "安装失败";
            InstallButtonText = "安装";
        }
        else
        {
            ProgressState = task.StateText;
            InstallButtonText = "安装";
        }
    }

    /// <summary>CurseForge 安装：最佳匹配文件 → 依赖确认 → 共享执行管道</summary>
    private async Task InstallCfAsync(CancellationToken ct)
    {
        try
        {
            if (_instance is null && _card.Type != ProjectType.Modpack)
                throw new InvalidOperationException("你还没选目标实例。去生态页顶部选一个。");
            var file = _cfFile ?? throw new InvalidOperationException("没有匹配的可用文件");
            // 8-22 修复：列表响应的 dependencies 恒空（tweakeroo/minihud 实测）——安装前换单文件详情，
            // 否则前置依赖（如 malilib）永远不会被解析安装
            try
            {
                var detail = await _cf.GetFileAsync(_cfModId, file.id);
                if (detail is not null) file = detail;
            }
            catch { /* 详情拉取失败用列表数据（依赖未知则按无依赖处理） */ }
            var gameVersion = _instance is not null && _instance.ResolvedGameVersion.Length > 0
                ? _instance.ResolvedGameVersion : null;
            var loader = _instance is not null ? EcosystemService.GuessLoader(_instance.Name) : null;
            var instanceName = _instance?.Name ?? "modpack";
            // MOD 落点：版本来源目录（PCL 扫描版本 → PCL 目录；自建版本 → 自建目录）——AF2
            // 8-19 生态修缮：外来（PCL/官方）实例只读不写——下载落点归类启动器目录
            var gameDirFor = _instance is { GameDir.Length: > 0 } inst
                ? Launcher.Core.Utils.GameDirectory.ModInstallBaseDir(inst.GameDir)
                : Launcher.Core.Utils.GameDirectory.InstallDir();

            // 安装前路径确认（8-22 可编辑目录 + 实时预览落点）——null = 取消；改了就用新目录。
            // 8-23 整合包豁免强制选目标：装独立实例 downloads/modpacks，不弹目录选择器
            if (_card.Type != ProjectType.Modpack && DialogService.MainWindow() is { } ownerPath)
            {
                var chosen = await DialogService.ConfirmInstallPath(ownerPath, gameDirFor, instanceName, _card.Type);
                if (chosen is null) return;
                gameDirFor = chosen;
            }

            // 8-22 跨源兜底：CF 依赖数据缺失（tweakeroo/minihud 等实测全空）→ Modrinth 按名全套安装
            // （主文件 + 前置依赖一起装；搜索/版本查询失败则回落原 CF 流程）
            // 8-22 收紧：仅当 Modrinth 命中标题强匹配（防同名不同 mod 装错）+ 用户确认后才兜底
            if ((file.dependencies ?? []).Count(d => d.relationType == 1) == 0 && _instance is not null)
            {
                try
                {
                    var page = await SlowQueryNotifier.WatchAsync(_eco.SearchAsync(_card.Type, _card.Title, gameVersion, loader, limit: 5),
                        "仍在跨源查找项目（网络较慢），请稍候…", TimeSpan.FromSeconds(3));
                    var hit = page?.Hits?.FirstOrDefault(h => h.Title.Equals(_card.Title, StringComparison.OrdinalIgnoreCase))
                              ?? page?.Hits?.FirstOrDefault();
                    if (hit is not null)
                    {
                        var mrVersions = await SlowQueryNotifier.WatchAsync(_eco.GetVersionsAsync(hit.ProjectId, gameVersion, loader),
                            "仍在查询 Modrinth 版本（网络较慢），请稍候…", TimeSpan.FromSeconds(3));
                        var mrVersion = mrVersions?.FirstOrDefault();
                        if (mrVersion is not null && DialogService.MainWindow() is { } fallbackOwner)
                        {
                            var ok = await DialogService.Confirm(fallbackOwner,
                                $"CurseForge 对该 mod 无依赖数据（tweakeroo 等常见），将改用 Modrinth 源安装「{hit.Title}」并自动装入前置依赖。继续？",
                                "改用 Modrinth 源", "继续", "取消");
                            if (!ok) return;
                            await ExecuteInstallAsync(async (gctx, dp, t) => await _eco.InstallWithDependenciesAsync(
                                hit.ProjectId, mrVersion, instanceName, _card.Type, gameVersion, loader, dp, t,
                                gameDirOverride: gameDirFor, ctx: gctx), instanceName, ct);
                            return;
                        }
                    }
                }
                catch { /* 兜底失败回落原流程 */ }
            }

            var includeDeps = true;
            if (DependencyHint.Length > 0 && DialogService.MainWindow() is { } owner)
            {
                includeDeps = await DialogService.Confirm(owner,
                    DependencyHint, $"安装 {_card.Title}", "全部安装", "仅主文件");
            }

            await ExecuteInstallAsync(async (gctx, dp, t) =>
            {
                if (includeDeps)
                    return await _cf.InstallWithDependenciesAsync(_cfModId, file, instanceName, _card.Type,
                        gameVersion, loader, dp, t, gameDirOverride: gameDirFor, ctx: gctx);
                string? path = null;
                if (gctx is null)
                    path = await _cf.InstallAsync(_cfModId, file, instanceName, _card.Type, dp, t, gameDirFor);
                else
                {
                    var child = gctx.AddChild($"主文件 {file.fileName}", file.fileLength,
                        (p, c) => _cf.InstallAsync(_cfModId, file, instanceName, _card.Type, p, c, gameDirFor));
                    await child.Completion.WaitAsync(t);
                }
                var r = new DependencyInstallReport();
                r.Installed.Add(new InstalledDependency(_card.Id, file.id.ToString(), path ?? ""));
                return r;
            }, instanceName, ct);
        }
        catch (OperationCanceledException) { ProgressState = "已取消"; }
        catch (Exception ex) { ErrorMessage = ex.Message; ProgressState = "安装失败"; }
        finally { IsInstalling = false; if (!InstallDone) CanInstall = true; }
    }
}

/// <summary>版本选项（PCL 式版本行，8-12 起直显）；推荐 = 自动匹配命中（FillVersionRows 时置位）；Source 供安装分派</summary>
public sealed record VersionOptionVM(string Id, string Display, bool IsRecommended, DateTime Published,
    long SizeBytes, object? Source) : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>当前选中（8-27 界面内查看：点击版本行选中高亮；初始 = IsRecommended）</summary>
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string PublishedText => Published.Year > 2000 ? Published.ToString("yyyy-MM-dd") : "";

    public string SizeText => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024:0.#} MB"
        : SizeBytes >= 1024 ? $"{SizeBytes / 1024:0} KB" : $"{SizeBytes} B";

    public static VersionOptionVM FromModrinth(ModrinthVersion v)
    {
        var games = v.GameVersions is { Count: > 0 } ? string.Join("/", v.GameVersions.Take(2)) : "?";
        var loaders = v.Loaders is { Count: > 0 } ? string.Join("/", v.Loaders.Take(2)) : "any";
        return new VersionOptionVM(v.Id, $"{v.VersionNumber} · {games} · {loaders}", false,
            v.DatePublished, EcosystemService.PickPrimaryFile(v.Files)?.Size ?? 0, v);
    }

    public static VersionOptionVM FromCf(CurseforgeFile f)
    {
        var games = f.gameVersions is { Count: > 0 } ? string.Join("/", f.gameVersions.Take(2)) : "?";
        var name = string.IsNullOrEmpty(f.displayName) ? f.fileName : f.displayName;
        return new VersionOptionVM(f.id.ToString(), $"{name} · {games}", false,
            DateTime.MinValue, f.fileLength, f); // PCL.Core 未映射 fileDate，发布日留空
    }
}

/// <summary>版本文件行（文件名/大小）</summary>
public sealed record VersionFileVM(string Name, long SizeBytes)
{
    public string SizeText => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024:0.#} MB"
        : SizeBytes >= 1024 ? $"{SizeBytes / 1024:0} KB" : $"{SizeBytes} B";
}
