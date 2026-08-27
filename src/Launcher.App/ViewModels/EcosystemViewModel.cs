using System.Collections.ObjectModel;
using System.Diagnostics;
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
/// 资源下载面板（下载板块的一个 tab）：防抖搜索 + 实例过滤 + 卡片流 + 四态 + 分页。
/// 类型在构造时固定（下载页为每种类型建一个实例）；tab 切换由外层 DownloadViewModel 控制。
/// 来源筛选：全部 = Modrinth + CurseForge 双源并行合并。
/// </summary>
public partial class EcosystemViewModel : ViewModelBase
{
    private readonly CurseForgeService _cf; // key 直连（设置 DPAPI 密文落盘）
    private readonly EcosystemService _eco; // 8-24 CF 中文搜索共享同一 CF 实例
    private readonly ProjectType _type;
    private CancellationTokenSource? _searchCts;
    private int _requestSeq;

    private const int PageSize = 20;

    private bool _suppressSearch;
    private bool _searchStarted;
    /// <summary>8-19 第二批：初始化赋值 SelectedInstance 时抑制一次实例搜索（Activate 统一首搜）</summary>
    private bool _suppressInstanceSearch;

    public EcosystemViewModel(ProjectType type = ProjectType.Mod)
    {
        _cf = new CurseForgeService();
        _eco = new EcosystemService(curseforge: _cf);
        _type = type;
        SelectedSort = SortOptions[0];
        SelectedGameVersion = GameVersionOptions[0];
        BuildSourceOptions();
        _suppressSearch = true; // 构造期不搜——预加载 4 个标签只建 VM 不请求，首次激活才搜
        // 8-22 默认「全部」源：有 CF key 时 Modrinth+CF 并行，用户能看到 CF 结果——
        // 旧默认 Modrinth 单源导致「装了 CF key 却搜不到 CF 内容」（用户反馈）；datapack 分支只有 Modrinth
        SelectedSource = SourceOptions.Count > 0 ? SourceOptions[0] : SourceOptions[^1];
        _suppressSearch = false;
        // 全局版本绑定：主页切换版本 → 本页实例下拉跟随（AF1）
        if (MainViewModel.Current is { } main)
            main.PropertyChanged += OnMainPropertyChanged;
    }

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentVersion)) return;
        if (!Launcher.Core.Utils.LauncherSettings.Current.EcoFollowInstance) return; // 8-19 开关：关 = 不自动跟随实例
        if (MainViewModel.Current?.CurrentVersion is not { } cur) return;
        var hit = Instances.FirstOrDefault(i => i.Name.Equals(cur.Name, StringComparison.OrdinalIgnoreCase));
        if (hit is not null) SelectedInstance = hit;
    }

    /// <summary>tab 显示名（MOD/整合包/材质包/光影包）</summary>
    public string TabName => _type switch
    {
        ProjectType.Modpack => "整合包",
        ProjectType.Resourcepack => "材质包",
        ProjectType.Shader => "光影包",
        _ => "MOD",
    };

    /// <summary>仅 MOD 类型显示加载器 chips（材质包/光影无加载器概念）</summary>
    public bool IsModType => _type == ProjectType.Mod;

    // ---------- 三级筛选选项 ----------

    /// <summary>加载器 chips（"全部"=null）</summary>
    public static IReadOnlyList<string> LoaderOptions { get; } = ["全部", "Fabric", "Forge", "NeoForge", "Quilt"];

    /// <summary>
    /// 游戏版本下拉（"跟随实例"=null + manifest release 动态生成，语义降序——26.2 这类 YY.M 排最上）。
    /// 8-12 起不再硬编码：Mojang 清单 24h 缓存拉取后填充；拉取失败（断网无缓存）回退内置常用列表。
    /// </summary>
    public ObservableCollection<GameVersionOption> GameVersionOptions { get; } = [new GameVersionOption("跟随实例", null)];

    /// <summary>manifest 拉取失败的兜底常用版本（1.21.6 新格式后是 26.2 系——语义序）</summary>
    private static readonly string[] FallbackGameVersions =
        ["26.2", "1.21.10", "1.21.9", "1.21.8", "1.21.7", "1.21.6", "1.21.5", "1.21.4", "1.21.3", "1.21.1",
         "1.20.4", "1.20.1", "1.19.4", "1.18.2"];

    public sealed record GameVersionOption(string Display, string? Value);

    /// <summary>排序选项（下载量/更新时间/关注/最新）</summary>
    public static IReadOnlyList<SortOption> SortOptions { get; } =
    [
        new SortOption("相关度", EcosystemService.SortIndex.Relevance),
        new SortOption("下载量", EcosystemService.SortIndex.Downloads),
        new SortOption("最近更新", EcosystemService.SortIndex.Updated),
        new SortOption("关注数", EcosystemService.SortIndex.Follows),
        new SortOption("最新发布", EcosystemService.SortIndex.Newest),
    ];

    public sealed record SortOption(string Display, EcosystemService.SortIndex Index);

    /// <summary>来源筛选（全部 = 双源并行合并）。CurseForge 未配置 key 时选项带标记（视觉置灰提示）。</summary>
    public IReadOnlyList<SourceOption> SourceOptions { get; private set; } = [];

    public sealed record SourceOption(string Display, string? Key);

    private void BuildSourceOptions()
    {
        // 8-16 批次 54：数据包 CF 无分类（classId=0）——源只留 Modrinth
        if (_type == ProjectType.Datapack)
        {
            SourceOptions =
            [
                new SourceOption("Modrinth", "modrinth"),
            ];
            return;
        }
        SourceOptions =
        [
            new SourceOption("全部", null),
            new SourceOption("Modrinth", "modrinth"),
            new SourceOption(CurrentCfLabel(), "curseforge"),
        ];
    }

    /// <summary>8-22 CF 源 label 动态化：`_cf.IsEnabled` 每次读设置（含内置 key），
    /// 用户填 key 后下拉立即显示「CurseForge」而非「CurseForge（未配置 Key）」</summary>
    private string CurrentCfLabel() => _cf.IsEnabled ? "CurseForge" : "CurseForge（未配置 Key）";

    /// <summary>8-22 刷新来源下拉（CF key 变化后调用；保留当前选中源）</summary>
    private void RefreshSourceLabels()
    {
        var current = SelectedSource?.Key;
        BuildSourceOptions();
        SelectedSource = SourceOptions.FirstOrDefault(s => s.Key == current) ?? SourceOptions[0];
        OnPropertyChanged(nameof(SourceOptions));
    }

    [ObservableProperty]
    public partial SourceOption? SelectedSource { get; set; }

    partial void OnSelectedSourceChanged(SourceOption? value)
    {
        if (_suppressSearch) return;
        _ = RunSearchAsync(reset: true);
    }

    /// <summary>功能分类（Modrinth categories，中文显示；"全部"=null）</summary>
    public static IReadOnlyList<CategoryOption> CategoryOptions { get; } =
    [
        new CategoryOption("全部", null),
        new CategoryOption("优化", "optimization"),
        new CategoryOption("辅助", "utility"),
        new CategoryOption("冒险", "adventure"),
        new CategoryOption("装饰", "decorations"),
        new CategoryOption("魔法", "magic"),
        new CategoryOption("世界生成", "worldgen"),
        new CategoryOption("科技", "technology"),
        new CategoryOption("存储", "storage"),
        new CategoryOption("装备", "equipment"),
        new CategoryOption("库", "library"),
        new CategoryOption("生物", "mobs"),
        new CategoryOption("红石", "redstone"),
    ];

    public sealed record CategoryOption(string Display, string? Key);

    /// <summary>加载器筛选（null=跟随实例猜测）</summary>
    [ObservableProperty]
    public partial string? SelectedLoader { get; set; }

    /// <summary>游戏版本筛选（选中"跟随实例"时 Value=null → 跟随实例解析）</summary>
    [ObservableProperty]
    public partial GameVersionOption? SelectedGameVersion { get; set; }

    /// <summary>功能分类筛选（null=全部）</summary>
    [ObservableProperty]
    public partial CategoryOption? SelectedCategory { get; set; }

    /// <summary>排序（默认相关度）</summary>
    [ObservableProperty]
    public partial SortOption SelectedSort { get; set; }

    /// <summary>只看收藏（星标项目；从 FavoritesService 拉取）</summary>
    [ObservableProperty]
    public partial bool FavoritesOnly { get; set; }

    partial void OnFavoritesOnlyChanged(bool value) => _ = RunSearchAsync(reset: true);

    [RelayCommand]
    private void ToggleFavorites() => FavoritesOnly = !FavoritesOnly;

    public ObservableCollection<ProjectCardVM> Cards { get; } = [];
    public ObservableCollection<VersionInstanceVM> Instances { get; } = [];

    [ObservableProperty]
    public partial VersionInstanceVM? SelectedInstance { get; set; }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsError { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    // 分页状态（◀ 页码 ▶）
    [ObservableProperty]
    public partial int CurrentPage { get; set; }

    [ObservableProperty]
    public partial int TotalPages { get; set; } = 1;

    [ObservableProperty]
    public partial bool HasPrev { get; set; }

    [ObservableProperty]
    public partial bool HasNext { get; set; }

    [ObservableProperty]
    public partial string PageText { get; set; } = "1/1";

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    /// <summary>8-14 官网手动下载入口（CF 无 key / 源不可用时置位；非空则空态区显示「前往官网」按钮）</summary>
    [ObservableProperty]
    public partial string? ManualBrowseUrl { get; set; }

    [RelayCommand]
    private void OpenManualBrowse()
    {
        if (string.IsNullOrWhiteSpace(ManualBrowseUrl)) return;
        try { Process.Start(new ProcessStartInfo(ManualBrowseUrl) { UseShellExecute = true }); } catch { }
    }

    [ObservableProperty]
    public partial ProjectDetailViewModel? Detail { get; set; }

    [ObservableProperty]
    public partial bool IsDetailOpen { get; set; }

    partial void OnDetailChanged(ProjectDetailViewModel? value) => IsDetailOpen = value is not null;

    // 筛选变化立即搜索（不走防抖——Modrinth facets 服务器筛选快，延迟全在防抖；竞态 seq 丢弃旧响应）
    partial void OnSelectedLoaderChanged(string? value) => _ = RunSearchAsync(reset: true);
    partial void OnSelectedGameVersionChanged(GameVersionOption? value) => _ = RunSearchAsync(reset: true);

    /// <summary>切换目标实例 → 立即按新实例重新搜索（列表与实例保持一致）；已打开的详情页跟随刷新（AL56）。
    /// 8-19 第二批：初始化赋值（_suppressInstanceSearch）不触发搜索——首搜统一由 Activate 门控制</summary>
    partial void OnSelectedInstanceChanged(VersionInstanceVM? value)
    {
        if (_suppressInstanceSearch) _suppressInstanceSearch = false;
        else if (_searchStarted) _ = RunSearchAsync(reset: true);
        if (Detail is { } d) d.UpdateContext(value);
    }
    partial void OnSelectedCategoryChanged(CategoryOption? value) => _ = RunSearchAsync(reset: true);
    partial void OnSelectedSortChanged(SortOption value) => _ = RunSearchAsync(reset: true);

    /// <summary>加载器 chips 选择（"全部"=null；值转小写——Modrinth facets 要求 fabric/forge/neoforge/quilt）</summary>
    [RelayCommand]
    private void SelectLoader(string loader)
        => SelectedLoader = loader == "全部" ? null : loader.ToLowerInvariant();

    /// <summary>
    /// 实例 → 版本项（8-27 修复「模组显示 1.21」）：此前只传 LoaderBadge 漏传 McVersion，
    /// fabric 实例（fabric-loader-0.19.4-26.1.2）ResolvedGameVersion 走实例名解析失败 → 空 → 详情页不按游戏版本过滤
    /// → 自动匹配从全量选最新（选到 1.21 的 Sodium）。改用 VersionScan.Inspect 读 version.json 的 inheritsFrom 填 McVersion
    /// （与主页/版本页/开服页口径一致）；LoaderDetector.Detect 只给 loader 徽章，不读继承版本。
    /// </summary>
    internal static VersionInstanceVM BuildInstanceVM(string id, string gameDir)
    {
        var (loader, mc) = VersionScan.Inspect(gameDir, id);
        return new VersionInstanceVM(id,
            Launcher.Core.Utils.GameDirectory.SourceLabel(Launcher.Core.Utils.GameDirectory.SourceOf(gameDir)),
            gameDir, loader, mc);
    }

    /// <summary>初始化：扫描实例（json-only 判定——26.2 父版本 jar 落加载器子目录也能选）并触发首搜</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var all = new List<VersionInstanceVM>();
            var svc = new VersionManifestService();
            // 版本清单单独容错：失败只回退动态下拉到内置常用列表，不阻塞实例扫描
            try
            {
                await svc.RefreshAsync();
                foreach (var id in VersionManifestService.FilterGameVersionOptions(svc.Entries))
                    GameVersionOptions.Add(new GameVersionOption(id, id));
            }
            catch
            {
                // 断网且无缓存：内置常用版本兜底（语义序，26.2 在最上）
                foreach (var id in FallbackGameVersions)
                    GameVersionOptions.Add(new GameVersionOption(id, id));
            }
            // 跨扫描源枚举全部版本目录（manifest 覆盖不全 fabric/forge 等加载器版本——直接扫目录最全）
            foreach (var (dir, _) in Launcher.Core.Utils.GameDirectory.ScanSourceDirs())
            {
                var versionsDir = Path.Combine(dir, "versions");
                if (!Directory.Exists(versionsDir)) continue;
                foreach (var d in Directory.EnumerateDirectories(versionsDir))
                {
                    var id = Path.GetFileName(d);
                    // 实例判定 = json 存在即可 + 预取残留排除（IsInstanceTarget）——带来源目录（MOD 落点关键）
                    if (VersionManifestService.IsInstanceTarget(dir, id))
                        all.Add(BuildInstanceVM(id, dir));
                }
            }
            // 分批填充：前 5 立即，剩余每批 8 静默补全（大列表不卡，复用 LoaderChoiceDialog 模式）
            foreach (var v in all.Take(5)) Instances.Add(v);
            var rest = all.Skip(5).ToList();
            for (var i = 0; i < rest.Count; i += 8)
            {
                await Task.Delay(25);
                foreach (var v in rest.Skip(i).Take(8)) Instances.Add(v);
            }
        }
        catch { /* 实例扫描失败不阻塞搜索 */ }

        // 全局版本绑定：主页当前版本优先选中（AF1），否则第一个；8-19 开关关 = 只取第一个不跟随
        if (Instances.Count > 0)
        {
            _suppressInstanceSearch = true; // 8-19 第二批：赋值不触发搜索（Activate 统一首搜）
            SelectedInstance = Launcher.Core.Utils.LauncherSettings.Current.EcoFollowInstance
                && MainViewModel.Current?.CurrentVersion is { } cur
                && Instances.FirstOrDefault(i => i.Name.Equals(cur.Name, StringComparison.OrdinalIgnoreCase)) is { } hit
                ? hit
                : Instances[0];
        }
    }

    /// <summary>标签激活：首次调用才触发搜索（幂等；切回标签不重搜）。
    /// 8-19 修复：实例未就绪/无实例时直接首搜（null 实例 = 不按实例过滤，RunSearchAsync 合法）——
    /// 原实现挂起等实例，Instances 为空时永不补搜，搜索死锁（表现：列表一直转圈/空白）</summary>
    public void Activate()
    {
        if (_searchStarted) return;
        _searchStarted = true;
        _ = RunSearchAsync(reset: true);
    }

    partial void OnQueryChanged(string value) => DebouncedSearch();

    /// <summary>防抖搜索（150ms，取消旧请求——仅搜索框需要防抖）</summary>
    private async void DebouncedSearch()
    {
        RefreshSourceLabels(); // 8-22：搜索前刷新来源 label（CF key 变化后下拉立即变「CurseForge」）
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(150, cts.Token);
            await RunSearchAsync(reset: true, cts.Token);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunSearchAsync(bool reset, CancellationToken ct = default)
    {
        var seq = ++_requestSeq;
        if (reset) CurrentPage = 0; // 搜索/筛选变化回第 1 页
        IsLoading = true;
        IsError = false;
        IsEmpty = false;
        ManualBrowseUrl = null; // 每次搜索重置官网跳转入口
        // 8-22 防旧结果误导：reset 搜索立即清列表——切到 CurseForge 后 CF 请求挂起（15s 超时窗口）
        // 期间不清的话，屏上整页还是上一次（如 Modrinth）的结果，用户误以为「筛选 CurseForge 出 M 网」。
        // 翻页（reset=false）不清——分页需要保留当前页直到新页返回
        if (reset) Cards.Clear();
        // 8-22 搜索中状态明示（中文链路走 MC百科 2-10s——不提示用户以为死了）
        Status = McmodSearchService.ContainsChinese(Query)
            ? "正在通过 MC百科搜索中文结果（较慢，请稍候）…"
            : "正在搜索…";
        try
        {
            if (FavoritesOnly)
            {
                await LoadFavoritesAsync(seq, ct);
                return;
            }
            var instance = SelectedInstance;
            // 三级筛选：显式选择优先，否则跟随实例（真实加载器徽章优先——AG1，名字猜测兜底）。
            // 8-19 补：光影包/材质包无加载器概念——派生的 fabric/forge facet 会把 Modrinth 结果滤没
            // （光影包几乎不标 loader，实测 26.2 带 fabric 只剩 3 个、不带显示全部）；用户显式选不受影响
            var loader = SelectedLoader
                ?? (IsModType && instance is not null && instance.LoaderBadge.Length > 0 ? instance.LoaderBadge
                    : IsModType && instance is not null ? EcosystemService.GuessLoader(instance.Name) : null);
            var gameVersion = SelectedGameVersion?.Value
                ?? (Launcher.Core.Utils.LauncherSettings.Current.EcoFollowInstance
                    && instance is not null && instance.ResolvedGameVersion.Length > 0 ? instance.ResolvedGameVersion : null);
            var category = SelectedCategory?.Key;

            var source = SelectedSource?.Key;
            if (source == "curseforge")
                await RunCfSearchAsync(seq, loader, gameVersion, ct);
            else if (source == "modrinth")
                await RunMrSearchAsync(seq, loader, gameVersion, category, ct);
            else
                await RunBothSearchAsync(seq, loader, gameVersion, category, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (seq != _requestSeq) return;
            IsError = true;
            ErrorMessage = ex.Message;
        }
        finally
        {
            if (seq == _requestSeq) IsLoading = false;
        }
    }

    private async Task RunMrSearchAsync(int seq, string? loader, string? gameVersion, string? category, CancellationToken ct)
    {
        // AL63 中文分流：查询含中文 → MC百科汉化链路（Modrinth 索引是英文标题，中文查询 0 命中）
        var resp = McmodSearchService.ContainsChinese(Query)
            ? await SlowQueryNotifier.WatchAsync(_eco.SearchChineseAsync(_type, Query, gameVersion, loader, ct),
                "正在通过 MC百科搜索中文结果（较慢），请稍候…", TimeSpan.FromSeconds(3))
            : await SlowQueryNotifier.WatchAsync(_eco.SearchAsync(_type, Query, gameVersion, loader, category,
                index: SelectedSort?.Index ?? EcosystemService.SortIndex.Relevance,
                limit: PageSize, offset: CurrentPage * PageSize, ct),
                "仍在搜索（网络较慢），请稍候…", TimeSpan.FromSeconds(3));
        if (seq != _requestSeq) return; // 竞态：旧响应直接丢弃
        Cards.Clear(); // 服务器分页：每次重建当前页
        AddCards(resp?.Hits ?? [], h => h.Title, h => h.Description, h => new ProjectCardVM(h));
        FinishPage(seq, resp?.TotalHits ?? 0, gameVersion, resp is null ? "无响应" : null);
    }

    private async Task RunCfSearchAsync(int seq, string? loader, string? gameVersion, CancellationToken ct)
    {
        if (!_cf.IsEnabled)
        {
            if (seq != _requestSeq) return;
            Cards.Clear();
            // 8-14 CF 无 key 平替：提示填 key + 提供官网跳转入口（PCL 式「去网页下」）
            ManualBrowseUrl = "https://www.curseforge.com/minecraft";
            FinishPage(seq, 0, gameVersion, "你还没配 CurseForge API Key。去设置页填一个，或用下方按钮到官网手动下载。");
            return;
        }
        if (Launcher.Core.Services.McmodSearchService.ContainsChinese(Query))
        {
            // 8-26 快路径优先（对齐 Verse，真机实测 CF 中文走 mcmod 链 90s+ 干等）：本地别名表命中
            // （钠/机械动力/小地图…）→ 直接 Modrinth 快搜 ~1s（SearchChineseAsync 内部有命中短路）。
            if (Launcher.Core.Services.ModAliasTable.Resolve(Query).Count > 0)
            {
                var resp = await SlowQueryNotifier.WatchAsync(
                    _eco.SearchChineseAsync(_type, Query, gameVersion, loader, ct),
                    "正在搜索…", TimeSpan.FromSeconds(3));
                if (seq != _requestSeq) return;
                Cards.Clear();
                AddCards(resp?.Hits ?? [], h => h.Title, h => h.Description, h => new ProjectCardVM(h));
                FinishPage(seq, resp?.TotalHits ?? 0, gameVersion, null);
                return;
            }
            // 8-24 兜底 CF 中文搜索：MC百科链解出 CF slug → 按 slug 反查 CF API（需有效 key；
            // 无命中（MC百科没 CF 条目 / slug 搜不到）显示空提示而非白打官网）
            var results = await SlowQueryNotifier.WatchAsync(
                _eco.SearchChineseCurseforgeAsync(_type, Query, gameVersion, ct),
                "正在通过 MC百科搜索中文结果（较慢），请稍候…", TimeSpan.FromSeconds(3));
            if (seq != _requestSeq) return;
            Cards.Clear();
            AddCards(results ?? [], p => p.name, p => p.summary, p => new ProjectCardVM(p));
            FinishPage(seq, results?.Count ?? 0, gameVersion,
                results is { Count: 0 } ? "中文搜索没在 CurseForge 找到匹配的模组（只有 MC百科有 CF 条目的才会出现）。" : null);
            return;
        }
        var sort = CfSortOf(SelectedSort?.Index);
        // REVIEW-C：CF API index 语义是「偏移量」不是页码——旧代码传 CurrentPage 导致第 2 页起与
        // 第 1 页 19/20 条重复（Modrinth 侧 offset=CurrentPage*PageSize 正确，两侧不对称）
        var page = await TryCfSearchAsync(() => _cf.SearchAsync(_type, Query, gameVersion, sort, PageSize, CurrentPage * PageSize, ct));
        if (seq != _requestSeq) return;
        Cards.Clear();
        AddCards(page?.Projects ?? [], p => p.name, p => p.summary, p => new ProjectCardVM(p));
        FinishPage(seq, page?.TotalCount ?? 0, gameVersion, page is null ? "无响应"
            : page.VersionFilterDropped ? "该版本 CurseForge 暂不支持过滤，已显示全部版本" : null);
    }

    /// <summary>CF 搜索（GetJsonAsync 内已有 404/5xx 一次重试；此处不再叠加外层重试——15s 超时兜底慢源）</summary>
    private static Task<CurseForgeSearchPage?> TryCfSearchAsync(Func<Task<CurseForgeSearchPage?>> search)
        => search();

    /// <summary>8-24 双源中文：CF 侧走 MC百科链（包装成 CurseForgeSearchPage 形状并入双源合并）</summary>
    private async Task<CurseForgeSearchPage?> SearchChineseCfAsync(int seq, string? gameVersion, CancellationToken ct,
        List<(string? MrSlug, string? CfSlug, string ChineseTitle)>? sharedCandidates = null)
    {
        // 8-26 别名表命中时 Modrinth 侧已秒出，CF 侧直接返回空不拖后腿（否则「全部」双源等最慢的 mcmod 链）
        if (Launcher.Core.Services.ModAliasTable.Resolve(Query).Count > 0)
            return new CurseForgeSearchPage([], 0);
        var results = await SlowQueryNotifier.WatchAsync(
            _eco.SearchChineseCurseforgeAsync(_type, Query, gameVersion, ct, sharedCandidates),
            "正在通过 MC百科搜索中文结果（较慢），请稍候…", TimeSpan.FromSeconds(3));
        if (seq != _requestSeq) return null;
        return results is null ? null : new CurseForgeSearchPage(results, results.Count);
    }

    private async Task RunBothSearchAsync(int seq, string? loader, string? gameVersion, string? category, CancellationToken ct)
    {
        var sort = CfSortOf(SelectedSort?.Index);
        // 双源并行发起、独立捕获：单源失败（超时/网络/限流）只降级该源，另一源照常显示。
        // B5：中文 query 在「全部」双源模式也走 MC百科链（Modrinth 索引是英文，直搜 0 命中）
        var isChinese = Launcher.Core.Services.McmodSearchService.ContainsChinese(Query);
        // 8-26 修「还是慢」真根因：双源中文查询先判别名表——命中就直接走快路径（Modrinth 秒出 + CF 空），
        // 绝不在快路径前爬 mcmod（旧代码无条件 FetchChineseCandidatesAsync 爬 mcmod，最坏 10-75s 干等，
        // 别名短路被挡在后面根本轮不到）。别名未命中才抓 mcmod 候选供兜底反查。
        var aliasHit = isChinese && Launcher.Core.Services.ModAliasTable.Resolve(Query).Count > 0;
        List<(string? MrSlug, string? CfSlug, string ChineseTitle)>? sharedCandidates = null;
        if (isChinese && !aliasHit) sharedCandidates = await _eco.FetchChineseCandidatesAsync(Query, ct);
        var mrTask = isChinese
            ? _eco.SearchChineseAsync(_type, Query, gameVersion, loader, ct, sharedCandidates)
            : _eco.SearchAsync(_type, Query, gameVersion, loader, category,
                index: SelectedSort?.Index ?? EcosystemService.SortIndex.Relevance,
                limit: PageSize, offset: CurrentPage * PageSize, ct);
        // 8-24 中文 query + 「全部」：CF 侧走 MC百科链反查（原 8-22 直接跳过不白等——现已支持）；英文走常规 CF 搜索
        var cfTask = !_cf.IsEnabled
            ? Task.FromResult<CurseForgeSearchPage?>(null)
            : isChinese
                ? SearchChineseCfAsync(seq, gameVersion, ct, sharedCandidates)
                : TryCfSearchAsync(() => _cf.SearchAsync(_type, Query, gameVersion, sort, PageSize, CurrentPage * PageSize, ct));
        string? mrErr = null, cfErr = null;
        var mr = await TrySearchAsync(mrTask, ex => mrErr = ex.Message);
        var cf = await TrySearchAsync(cfTask, ex => cfErr = ex.Message);
        if (seq != _requestSeq) return;
        Cards.Clear();
        // 双源先合并成同型列表再统一重排——否则整块 Modrinth 前置，中文 query 时 CF 的标题匹配
        // （BLZYの自定义进度）被 Modrinth 的「描述匹配」（字幕高亮）压在后面，重排失去意义
        var all = new List<ProjectCardVM>();
        foreach (var h in mr?.Hits ?? []) all.Add(new ProjectCardVM(h));
        foreach (var p in cf?.Projects ?? []) all.Add(new ProjectCardVM(p));
        AddCards(all, c => c.Title, c => c.Description, c => c);
        var total = (mr?.TotalHits ?? 0) + (cf?.TotalCount ?? 0);
        var cfDropped = cf?.VersionFilterDropped == true;
        var note = mrErr is null && cfErr is null
            ? (mr is null && cf is null ? "无响应" : (cfDropped ? "该版本 CurseForge 暂不支持过滤，已显示全部版本" : null))
            : mrErr is null ? $"CurseForge 搜索失败（{cfErr}），仅显示 Modrinth 结果"
            : cfErr is null ? $"Modrinth 搜索失败（{mrErr}），仅显示 CurseForge 结果"
            : "双源搜索均失败";
        FinishPage(seq, total, gameVersion, note);
    }

    /// <summary>结果填充：中文 query 先按匹配质量重排（标题匹配&gt;描述匹配&gt;无），英文信任源排序</summary>
    private void AddCards<T>(IEnumerable<T> items, Func<T, string> titleOf, Func<T, string> descOf, Func<T, ProjectCardVM> toCard)
    {
        if (EcosystemService.IsChineseQuery(Query))
            items = EcosystemService.ReorderMatches(items, Query, titleOf, descOf);
        foreach (var x in items) Cards.Add(toCard(x));
    }

    /// <summary>单源搜索容错：失败只记录不抛（双源模式用）。取消必须向上（新请求竞态），不能吞。</summary>
    private static async Task<T?> TrySearchAsync<T>(Task<T> task, Action<Exception> onError)
    {
        try { return await task; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { onError(ex); return default; }
    }

    /// <summary>分页状态统一收尾（CF 无分页信息时总数=当前页条数，分页栏按此算）</summary>
    private void FinishPage(int seq, int total, string? gameVersion, string? errorStatus)
    {
        TotalPages = Math.Max(1, (total + PageSize - 1) / PageSize);
        HasPrev = CurrentPage > 0;
        HasNext = CurrentPage < TotalPages - 1;
        PageText = $"{CurrentPage + 1}/{TotalPages}";
        IsEmpty = Cards.Count == 0;
        Status = errorStatus ?? (gameVersion is not null
            ? $"共 {total} 个结果 · 已按 {gameVersion} 过滤"
            : $"共 {total} 个结果");
    }

    /// <summary>Modrinth 排序 → CF 排序（关注数无对应 → 相关度）</summary>
    private static CurseForgeService.SortIndex CfSortOf(EcosystemService.SortIndex? index) => index switch
    {
        EcosystemService.SortIndex.Downloads => CurseForgeService.SortIndex.Downloads,
        EcosystemService.SortIndex.Updated => CurseForgeService.SortIndex.Updated,
        EcosystemService.SortIndex.Newest => CurseForgeService.SortIndex.Newest,
        _ => CurseForgeService.SortIndex.Relevance,
    };

    // 无参命令：避免 RelayCommand<bool> 与 XAML string CommandParameter 的类型不匹配崩溃
    [RelayCommand]
    private Task Search() => RunSearchAsync(reset: true);

    [RelayCommand]
    private void PrevPage()
    {
        if (CurrentPage <= 0) return;
        CurrentPage--;
        _ = RunSearchAsync(reset: false);
    }

    [RelayCommand]
    private void NextPage()
    {
        if (CurrentPage >= TotalPages - 1) return;
        CurrentPage++;
        _ = RunSearchAsync(reset: false);
    }

    /// <summary>项目类型匹配（大小写不敏感；MOD 匹配全部非特殊类型）</summary>
    private bool TypeMatches(string? projectType)
        => _type == ProjectType.Mod
            ? projectType is not ("modpack" or "resourcepack" or "shader")
            : string.Equals(projectType, _type.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>收藏模式：逐项目拉详情组装卡片（收藏数小，直拉可接受）</summary>
    private async Task LoadFavoritesAsync(int seq, CancellationToken ct)
    {
        var ids = FavoritesService.All;
        Cards.Clear();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (source, rawId) = ProjectCardVM.ParseId(id);
                if (source == "curseforge")
                {
                    if (!int.TryParse(rawId, out var modId)) continue;
                    var p = await SlowQueryNotifier.WatchAsync(_cf.GetProjectAsync(modId, ct),
                        "仍在查询 CurseForge 项目（网络较慢），请稍候…", TimeSpan.FromSeconds(3));
                    if (p is not null)
                    {
                        var card = new ProjectCardVM(p);
                        if (TypeMatches(card.Type.ToString())) Cards.Add(card);
                    }
                }
                else
                {
                    var detail = await SlowQueryNotifier.WatchAsync(_eco.GetProjectAsync(id, ct),
                        "仍在查询项目详情（网络较慢），请稍候…", TimeSpan.FromSeconds(3));
                    if (detail is not null && TypeMatches(detail.ProjectType))
                        Cards.Add(new ProjectCardVM(detail));
                }
            }
            catch { /* 单个拉取失败跳过 */ }
        }
        if (seq != _requestSeq) return;
        TotalPages = 1;
        HasPrev = false;
        HasNext = false;
        PageText = "1/1";
        IsEmpty = Cards.Count == 0;
        Status = $"收藏 {Cards.Count} 个项目";
        if (seq == _requestSeq) IsLoading = false;
    }

    /// <summary>卡片一键安装：匹配版本 → 依赖确认（全部/仅主文件）→ 全局下载中心执行 → Toast</summary>
    [RelayCommand]
    private async Task InstallCard(ProjectCardVM card)
    {
        var instance = SelectedInstance;
        var gameVersion = instance is not null && instance.ResolvedGameVersion.Length > 0 ? instance.ResolvedGameVersion : null;
        if (card.Source == "curseforge")
        {
            await InstallCfCardAsync(card, instance, gameVersion);
            return;
        }
        // 8-19：光影包/材质包无加载器概念——派生 loader 会把 Modrinth 版本滤没（同 RunSearchAsync gate）
        var loader = card.Type == ProjectType.Mod
            && instance is not null && instance.LoaderBadge.Length > 0 ? instance.LoaderBadge
            : card.Type == ProjectType.Mod && instance is not null ? EcosystemService.GuessLoader(instance.Name) : null;
        // 8-26 整合包自带游戏版本（mrpack 里），不该拿选中实例版本过滤（会错配/卡匹配）——直接取最新 release
        var effGameVersion = card.Type == ProjectType.Modpack ? null : gameVersion;
        try
        {
            var version = await _eco.FindBestVersionAsync(card.Id, effGameVersion, loader, CancellationToken.None);
            if (version is null)
            {
                NotificationService.Error($"{card.Title} 没有适配当前实例的版本");
                return;
            }

            // 实例先判空——路径确认与依赖确认都建立在目标实例有效之上
            if (instance is null)
            {
                NotificationService.Error("先选目标实例");
                return;
            }
            var instanceName = instance.Name;
            // 安装前路径确认（8-22 可编辑目录 + 实时预览落点）——null = 取消；改了就用新目录
            // 8-19 生态修缮：外来（PCL/官方）实例只读不写——下载落点归类启动器目录
            var installDir = Launcher.Core.Utils.GameDirectory.ModInstallBaseDir(instance.GameDir);
            // 8-23 整合包豁免强制选目标：装独立实例 downloads/modpacks，不弹目录选择器
            if (card.Type != ProjectType.Modpack && DialogService.MainWindow() is { } pathOwner)
            {
                var chosen = await DialogService.ConfirmInstallPath(pathOwner, installDir, instanceName, card.Type);
                if (chosen is null) return;
                installDir = chosen;
            }

            // 依赖解析内部同步等网络（EcosystemDependencyAdapter .GetResult()）——必须离线 UI 线程，否则永久死锁
            var deps = await Task.Run(() =>
                _eco.ResolveDependencyNamesAsync(version, gameVersion, loader, CancellationToken.None));
            var includeDeps = true;
            if (deps.Count > 0 && DialogService.MainWindow() is { } owner)
            {
                var list = string.Join("、", deps.Take(6)) + (deps.Count > 6 ? "…" : "");
                includeDeps = await DialogService.Confirm(owner,
                    $"要装 {deps.Count} 个前置：{list}", $"安装 {card.Title}", "全部安装", "仅主文件");
            }

            DependencyInstallReport? report = null;
            var task = DownloadManager.Instance.EnqueueGroup($"安装 {card.Title}", async (gctx, ct) =>
            {
                report = includeDeps
                    ? await _eco.InstallWithDependenciesAsync(card.Id, version, instanceName, card.Type,
                        gameVersion, loader, null, ct, gameDirOverride: installDir, ctx: gctx) // AF2：装实例真实目录（8-22 可改）
                    : await InstallMainOnlyAsync(card.Id, version, instanceName, card.Type, gctx, ct, installDir);
            }, targetPath: installDir);           // 跳转①：入队即去下载记录看进度；完成后跳回本 tab（跳转②由下载中心统一处理）
            MainViewModel.Current?.NavigateToDownloadQueue($"download:{DownloadViewModel.TabFor(_type)}");
            await task.Completion;
            // 8-26 装完通知版本页刷新对应实例 mods（跨 VM 联动，补 watcher 边界）
            MainViewModel.Current?.Versions?.NotifyModsInstalled(instanceName);
            if (task.State == DownloadTaskState.Completed)
            {
                var path = report is { Installed.Count: > 0 } ? report.Installed[0].Path : "";
                if (card.Type == ProjectType.Modpack && path.Length > 0
                    && DialogService.MainWindow() is { } dlg
                    && await DialogService.Confirm(dlg,
                        "整合包已下载完成，立即导入并创建可启动的版本实例？",
                        "导入整合包", "立即导入", "稍后"))
                {
                    // AL47 断链修复：整合包下载完直接导入建实例
                    ModpackImportFlow.StartAsync(path);
                }
                else
                {
                    // 8-19 生态修缮：前置失败不再静默——Toast 明示 N 个前置未装 + 首个原因
                    var failedNote = report is { Failed.Count: > 0 }
                        ? $"（{report.Failed.Count} 个前置未安装：{report.Failed[0].Reason}）"
                        : "";
                    NotificationService.Success(
                        report is { Installed.Count: > 0 }
                            ? $"{card.Title} 安装完成 → {report.Installed[0].Path}{failedNote}"
                            : $"{card.Title} 安装完成{failedNote}", 6000);
                }
            }
            else if (task.Error is { } err)
            {
                NotificationService.Error(err);
                // AL69：坦言网络原因未装成 + 给手动下载入口——浏览器自己下，放 mods 即用
                await OfferManualDownloadAsync(card.Title, $"https://modrinth.com/mod/{card.Id}", err);
            }
        }
        catch (Exception ex)
        {
            NotificationService.Error($"安装失败: {ex.Message}");
        }
    }

    /// <summary>AL69：安装失败（多为网络原因）→ 弹窗坦言 + 「打开下载页」按钮（默认浏览器）</summary>
    private static async Task OfferManualDownloadAsync(string title, string url, string err)
    {
        if (DialogService.MainWindow() is not { } owner) return;
        var open = await DialogService.Confirm(owner,
            $"因网络原因未安装成功：{err}\n\n打开 {title} 的下载页，手动下载后放入对应实例的 mods 文件夹即可。",
            "安装失败", "打开下载页", "关闭");
        if (open) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>仅安装主文件（依赖可选跳过路径）；返回报告供路径 Toast。有组上下文 → 子任务</summary>
    private async Task<DependencyInstallReport?> InstallMainOnlyAsync(string projectId, ModrinthVersion version,
        string instanceName, ProjectType type, DownloadGroupContext? ctx, CancellationToken ct, string? gameDirOverride = null)
    {
        string? path = null;
        if (ctx is null)
            path = await _eco.InstallAsync(projectId, version, instanceName, type, null, ct, gameDirOverride);
        else
        {
            var child = ctx.AddChild($"主文件 {version.Name}", EcosystemService.PickPrimaryFile(version.Files)?.Size ?? 0,
                (p, c) => _eco.InstallAsync(projectId, version, instanceName, type, p, c, gameDirOverride));
            await child.Completion.WaitAsync(ct);
        }
        var r = new DependencyInstallReport();
        r.Installed.Add(new InstalledDependency(projectId, version.Id, path ?? ""));
        return r;
    }

    /// <summary>CurseForge 卡片一键安装：最佳文件匹配 → 依赖确认 → 全局下载中心执行 → Toast
    /// 8-22 修复：loader 传递 + 依赖补查（搜索页路径与详情页对齐——否则双加载器装错变体、前置永不解析）</summary>
    private async Task InstallCfCardAsync(ProjectCardVM card, VersionInstanceVM? instance, string? gameVersion)
    {
        if (!int.TryParse(ProjectCardVM.ParseId(card.Id).RawId, out var modId)) return;
        var loader = instance is not null ? EcosystemService.GuessLoader(instance.Name) : null;
        try
        {
            var file = await _cf.FindBestFileAsync(modId, gameVersion, CancellationToken.None, loader);
            if (file is null)
            {
                NotificationService.Error($"{card.Title} 没有适配当前实例的文件");
                return;
            }

            if (instance is null)
            {
                NotificationService.Error("先选目标实例");
                return;
            }
            var instanceName = instance.Name;
            // 安装前路径确认（8-22 可编辑目录 + 实时预览落点）——null = 取消；改了就用新目录
            // 8-19 生态修缮：外来（PCL/官方）实例只读不写——下载落点归类启动器目录
            var installDir = Launcher.Core.Utils.GameDirectory.ModInstallBaseDir(instance.GameDir);
            // 8-23 整合包豁免强制选目标：装独立实例 downloads/modpacks，不弹目录选择器
            if (card.Type != ProjectType.Modpack && DialogService.MainWindow() is { } pathOwner)
            {
                var chosen = await DialogService.ConfirmInstallPath(pathOwner, installDir, instanceName, card.Type);
                if (chosen is null) return;
                installDir = chosen;
            }

            // 8-22 修复：列表响应的 dependencies 恒空（实测）——单文件详情补查真实依赖（与详情页 RefreshCfDependenciesAsync 对齐）
            try
            {
                var detail = await _cf.GetFileAsync(modId, file.id);
                if (detail is not null) file = detail;
            }
            catch { /* 详情拉取失败用列表数据 */ }
            var depCount = (file.dependencies ?? []).Count(d => d.relationType == 1);
            var includeDeps = true;
            if (depCount > 0 && DialogService.MainWindow() is { } owner)
            {
                includeDeps = await DialogService.Confirm(owner,
                    $"要装 {depCount} 个前置依赖", $"安装 {card.Title}", "全部安装", "仅主文件");
            }

            DependencyInstallReport? report = null;
            var task = DownloadManager.Instance.EnqueueGroup($"安装 {card.Title}", async (gctx, ct) =>
            {
                if (includeDeps)
                {
                    report = await _cf.InstallWithDependenciesAsync(modId, file, instanceName, card.Type,
                        gameVersion, loader, null, ct, gameDirOverride: installDir, ctx: gctx); // AF2：装实例真实目录（8-22 可改）
                }
                else
                {
                    string? path = null;
                    var child = gctx.AddChild($"主文件 {file.fileName}", file.fileLength,
                        (p, c) => _cf.InstallAsync(modId, file, instanceName, card.Type, p, c, gameDirOverride: installDir));
                    await child.Completion.WaitAsync(ct);
                    var r = new DependencyInstallReport();
                    r.Installed.Add(new InstalledDependency(modId.ToString(), file.id.ToString(), path ?? ""));
                    report = r;
                }
            }, targetPath: installDir);           // 跳转①：入队即去下载记录看进度；完成后跳回本 tab（跳转②由下载中心统一处理）
            MainViewModel.Current?.NavigateToDownloadQueue($"download:{DownloadViewModel.TabFor(_type)}");
            await task.Completion;
            // 8-26 装完通知版本页刷新对应实例 mods（跨 VM 联动，补 watcher 边界）
            MainViewModel.Current?.Versions?.NotifyModsInstalled(instanceName);
            if (task.State == DownloadTaskState.Completed)
            {
                var path = report is { Installed.Count: > 0 } ? report.Installed[0].Path : "";
                if (card.Type == ProjectType.Modpack && path.Length > 0
                    && DialogService.MainWindow() is { } dlg
                    && await DialogService.Confirm(dlg,
                        "整合包已下载完成，立即导入并创建可启动的版本实例？",
                        "导入整合包", "立即导入", "稍后"))
                {
                    // AL47 断链修复：整合包下载完直接导入建实例
                    ModpackImportFlow.StartAsync(path);
                }
                else
                {
                    // 8-19 生态修缮：前置失败不再静默——Toast 明示 N 个前置未装 + 首个原因
                    var failedNote = report is { Failed.Count: > 0 }
                        ? $"（{report.Failed.Count} 个前置未安装：{report.Failed[0].Reason}）"
                        : "";
                    NotificationService.Success(
                        report is { Installed.Count: > 0 }
                            ? $"{card.Title} 安装完成 → {report.Installed[0].Path}{failedNote}"
                            : $"{card.Title} 安装完成{failedNote}", 6000);
                }
            }
            else if (task.Error is { } err)
            {
                NotificationService.Error(err);
                // AL69：坦言 + 手动下载入口（CurseForge 版）
                await OfferManualDownloadAsync(card.Title, $"https://www.curseforge.com/minecraft/mc-mods/{modId}", err);
            }
        }
        catch (Exception ex)
        {
            NotificationService.Error($"安装失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenDetail(ProjectCardVM card) =>
        Detail = new ProjectDetailViewModel(_eco, _cf, card, SelectedInstance, () => Detail = null);

    [RelayCommand]
    private void CloseDetail() => Detail = null;
}
