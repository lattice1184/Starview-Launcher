using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.Core.Download;
using Launcher.Core.Ecosystem;
using Launcher.Core.Model.Modrinth;
using Launcher.Core.Utils;
using PCL.Core.Minecraft.ResourceProject.Curseforge;

namespace Launcher.Core.Services;

/// <summary>
/// 生态下载服务：Modrinth 搜索 / 详情 / 版本匹配 / 安装到实例目录。
/// 注意：Modrinth API 强制要求 User-Agent 头，缺失返回 403。
/// </summary>
public sealed class EcosystemService
{
    private const string ApiBaseOfficial = "https://api.modrinth.com/v2";

    /// <summary>8-20 下载提速：Modrinth API 镜像基地址（mcimirror 同构路径，实测快 4 倍）。
    /// 仅当设置开启镜像时替换（第三方镜像默认关——官方直连优先）；路径同构（/v2 后不变）</summary>
    public static string ApiBase =>
        Launcher.Core.Utils.LauncherSettings.Current.ModrinthMirrorEnabled
            ? "https://mod.mcimirror.top/modrinth/v2"
            : ApiBaseOfficial;

    private readonly HttpClient _http;
    private readonly DownloadService _downloads;
    private readonly string _gameDirectory;
    private readonly McmodSearchService _mcmod;
    private readonly CurseForgeService? _cf;
    private readonly string _cacheDir;

    public EcosystemService(HttpClient? http = null, DownloadService? downloads = null, string? gameDirectory = null,
        McmodSearchService? mcmod = null, string? cacheDir = null, CurseForgeService? curseforge = null)
    {
        _http = http ?? HttpClientPool.Create();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("YanKa-Launcher/0.1");
        _downloads = downloads ?? new DownloadService();
        _gameDirectory = gameDirectory ?? GameDirectory.Detect();
        // 8-19 生态修缮：mcmod 搜索/详情页共享同一缓存目录（TTL 24h，重复搜索零网络）
        _mcmod = mcmod ?? new McmodSearchService(cacheDir);
        // 8-24 CF 中文搜索：可注入 CurseForgeService（ViewModel 共享实例；null 时 CF 中文链不启用）
        _cf = curseforge;
        // 8-16 批次 53：缓存目录可注入（测试隔离——磁盘缓存跨测试共享会污染请求计数断言）
        _cacheDir = cacheDir ?? Path.Combine(
            Launcher.Core.Utils.AppPaths.DataRoot, "cache");
    }

    /// <summary>8-25 双源共享 mcmod 候选：一次抓取喂中文 Modrinth + CF 双源（旧各自重抓两遍 mcmod）。
    /// 8-26 加 10s 上限：mcmod 连接挂起最坏 75s，长尾查询不干等（别名命中时根本不会调到这里）。</summary>
    public Task<List<(string? MrSlug, string? CfSlug, string ChineseTitle)>> FetchChineseCandidatesAsync(
        string query, CancellationToken ct = default)
        => FetchCandidatesCappedAsync(query, null, ct);

    /// <summary>8-26 mcmod 链硬上限：mcmod 对应用连接挂起不响应时最坏 ~75s（搜索页 15s + 详情页
    /// 门4×4波×15s）——本地表未命中的长尾中文查询不再干等，10s 超时即放弃、快速空结果。
    /// FetchAsync 会把取消当普通失败返回 null → SearchCandidatesAsync 返回 []，无需抛异常。</summary>
    private async Task<List<(string? MrSlug, string? CfSlug, string ChineseTitle)>> FetchCandidatesCappedAsync(
        string query, List<(string? MrSlug, string? CfSlug, string ChineseTitle)>? prefetched, CancellationToken ct)
    {
        if (prefetched is not null) return prefetched;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { return await _mcmod.SearchCandidatesAsync(query, maxResults: 10, timeout.Token); }
        catch (OperationCanceledException) { return []; }
    }

    /// <summary>
    /// 中文搜索（AL63）：MC百科汉化链路——中文 → mcmod 条目 → 解 Modrinth slug → 项目详情 → 搜索结果。
    /// 无分页（mcmod 搜索不分页；结果上限 10）。中文查询走此路，英文查询走 SearchAsync。
    /// 8-19 生态修缮：gameVersion/loader 命中项目按版本列表过滤（不支持该版本/加载器的项目不出现在结果）
    /// </summary>
    public async Task<ModrinthSearchResponse?> SearchChineseAsync(
        ProjectType type, string query, string? gameVersion = null, string? loader = null, CancellationToken ct = default,
        List<(string? MrSlug, string? CfSlug, string ChineseTitle)>? prefetchedCandidates = null)
    {
        // 8-22 别名直搜优先（PCL 式精准）：中文 query 命中内置映射 → 直接查 Modrinth slug
        // （缓存秒回）——「钠」直接出 Sodium 本体；MC百科结果合并去重（<em> 高亮/无外链都不再挡）
        var hits = new List<ModrinthSearchHit>();
        var typeName = type.ToString().ToLowerInvariant();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenLock = new object();
        // 8-25 提速：别名多 slug 由串行改门限并行（旧：8.6s × N 逐条干等）
        var aliasSlugs = ModAliasTable.Resolve(query).ToArray();
        if (aliasSlugs.Length > 0)
        {
            using var aliasGate = new SemaphoreSlim(4);
            var aliasTasks = aliasSlugs.Select(async aliasSlug =>
            {
                await aliasGate.WaitAsync(ct);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(8));
                    // 8-26 快路径改用 search 端点（实测官方 search 稳定 ~0.8s，project 端点 0.7-7s 抖动；
                    // Verse 正是用 search 才「秒出」）。别名表已知 slug → 一次 search 顶条即命中，
                    // 无需 project 详情 + 版本检查两连跳。search 有 5min 缓存，重复搜索秒回。
                    var search = await SearchAsync(type, aliasSlug, gameVersion, loader,
                        null, SortIndex.Relevance, 1, 0, timeout.Token);
                    var top = search?.Hits.FirstOrDefault(h =>
                        h.Slug.Equals(aliasSlug, StringComparison.OrdinalIgnoreCase))
                        ?? search?.Hits.FirstOrDefault();
                    if (top is null) return;
                    lock (seenLock) { if (!seen.Add(top.Slug)) return; }
                    // 标题用别名表中文名（搜「钠」看到「钠」）；其余数据取 search 命中
                    var hit = new ModrinthSearchHit(top.ProjectId, top.ProjectType, top.Slug, "",
                        ModAliasTable.TitleFor(query, top.Slug), top.Description, top.Categories, null,
                        top.Versions, top.IconUrl, top.Downloads, top.Follows,
                        top.DateCreated, top.DateModified, null);
                    lock (hits) { hits.Add(hit); }
                }
                catch { /* 别名单条失败跳过 */ }
                finally { aliasGate.Release(); }
            }).ToArray();
            await Task.WhenAll(aliasTasks);
        }
        // 8-26 对齐 Verse：本地表命中即短路返回——不再爬 mcmod（403/429 限流 + 慢），秒出。
        // 表已扩到 ~79 条，常见中文模组名都走这里。
        if (hits.Count > 0) return new ModrinthSearchResponse(hits, hits.Count, 0, 10);
        var candidates = await FetchCandidatesCappedAsync(query, prefetchedCandidates, ct);
        // 8-22 并行查询（旧串行：Modrinth API 国内直连 8.6s/请求 × 10 = 86s 干等——
        // 「中文搜不到」的真相是慢到用户放弃）。门 4 + 单条 10s 超时：最坏 ~20s，
        // 常见 2-3 条有 Modrinth 外链 → 几秒出结果。
        // 8-25 提速：门 4→8——Modrinth 反查是主瓶颈（每候选 detail+versions ≈17s 串行），
        // 10 候选门4 = 3 波 ≈51s → 门8 = 2 波 ≈34s。版本过滤依赖 detail.Id 无法单候选内并行。
        using var gate = new SemaphoreSlim(8);
        var tasks = candidates.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                // 8-26 兜底反查也走镜像直查（官方 2-7s 抖动 vs 镜像 1.7s，实测）
                var detail = await GetProjectFastAsync(item.MrSlug, timeout.Token);
                if (detail is null || !detail.ProjectType.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                    return (Hit: (ModrinthSearchHit?)null, item.ChineseTitle);
                lock (seenLock) { if (!seen.Add(detail.Slug)) return (Hit: (ModrinthSearchHit?)null, item.ChineseTitle); } // 别名已出，去重
                ChineseNameCache.Put("mr:" + detail.Slug, item.ChineseTitle); // 8-24 养缓存：下次英文搜也显示中文
                // 8-19 生态修缮：目标版本/加载器无匹配构建 → 过滤（版本列表走缓存，重复搜索零网络）
                if (gameVersion is not null || loader is not null)
                {
                    var support = await GetVersionsAsync(detail.Id, gameVersion, NormalizeLoaderForDependency(loader), timeout.Token);
                    if (support.Count == 0) return (Hit: (ModrinthSearchHit?)null, item.ChineseTitle);
                }
                // 8-22 标题用 MC百科中文名（搜「钠」看到「钠」而不是 Sodium——用户友好）；
                // 描述/图标/下载量仍取 Modrinth
                return (Hit: (ModrinthSearchHit?)new ModrinthSearchHit(detail.Id, detail.ProjectType,
                    detail.Slug, "", item.ChineseTitle, detail.Description, detail.Categories, null, detail.Versions,
                    detail.IconUrl, detail.Downloads, detail.Follows, detail.DateCreated, detail.DateModified,
                    null), item.ChineseTitle);
            }
            catch { return (Hit: (ModrinthSearchHit?)null, item.ChineseTitle); }
            finally { gate.Release(); }
        }).ToArray();
        foreach (var t in tasks)
        {
            var (hit, _) = await t;
            if (hit is not null) hits.Add(hit);
        }
        return new ModrinthSearchResponse(hits, hits.Count, 0, 10);
    }

    /// <summary>CF 中文搜索（8-24）：MC百科链解出的 CurseForge slug → 复用 _cf.SearchAsync 按 slug 反查
    /// （CF API 无按 slug 直取的端点，slug 精确搜索通常首条即命中）→ 精确匹配 → 标题替换为中文。
    /// 需有效 CF API key（无 key / 未注入 _cf 返回空，调用方提示填 key）。</summary>
    public async Task<List<CurseforgeProject>> SearchChineseCurseforgeAsync(
        ProjectType type, string query, string? gameVersion = null, CancellationToken ct = default,
        List<(string? MrSlug, string? CfSlug, string ChineseTitle)>? prefetchedCandidates = null)
    {
        if (_cf is null || !_cf.IsEnabled) return [];
        var candidates = await FetchCandidatesCappedAsync(query, prefetchedCandidates, ct);
        var cfItems = candidates
            .Where(c => c.CfSlug is not null)
            .Select(c => (Slug: c.CfSlug!, c.ChineseTitle))
            .DistinctBy(c => c.Slug, StringComparer.OrdinalIgnoreCase)
            .ToList();
        using var gate = new SemaphoreSlim(4);
        var tasks = cfItems.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var page = await _cf.SearchAsync(type, item.Slug, gameVersion, ct: timeout.Token);
                var proj = page?.Projects.FirstOrDefault(p =>
                    string.Equals(p.slug, item.Slug, StringComparison.OrdinalIgnoreCase));
                if (proj is null) return (Project: (CurseforgeProject?)null, item.ChineseTitle);
                ChineseNameCache.Put("cf:" + proj.slug, item.ChineseTitle); // 8-24 养缓存：下次英文搜也显示中文
                return (Project: proj with { name = item.ChineseTitle }, item.ChineseTitle);
            }
            catch { return (Project: (CurseforgeProject?)null, item.ChineseTitle); }
            finally { gate.Release(); }
        }).ToArray();
        var results = new List<CurseforgeProject>();
        foreach (var t in tasks)
        {
            var (proj, _) = await t;
            if (proj is not null) results.Add(proj);
        }
        return results;
    }

    /// <summary>搜索（facets 按 类型|游戏版本|加载器|功能分类 过滤，offset 分页）</summary>
    /// <summary>排序方式（Modrinth search index 参数）</summary>
    public enum SortIndex { Relevance, Downloads, Follows, Newest, Updated }

    public async Task<ModrinthSearchResponse?> SearchAsync(
        ProjectType type, string? query = null, string? gameVersion = null,
        string? loader = null, string? category = null,
        SortIndex index = SortIndex.Relevance,
        int limit = 20, int offset = 0, CancellationToken ct = default)
    {
        var facets = BuildFacets(type, gameVersion, loader, category);
        var indexName = index switch
        {
            SortIndex.Downloads => "downloads",
            SortIndex.Follows => "follows",
            SortIndex.Newest => "newest",
            SortIndex.Updated => "updated",
            _ => "relevance",
        };
        var path = $"/search?query={Uri.EscapeDataString(query ?? "")}"
                  + $"&facets={Uri.EscapeDataString(facets)}&index={indexName}&limit={limit}&offset={offset}";
        return await GetWithFallbackAsync<ModrinthSearchResponse>(path, SearchCacheTtl, ct);
    }

    /// <summary>缓存 TTL 分级（8-16 批次 53 后按数据新鲜度分级）：搜索 5 分钟（结果要新鲜）、
    /// 版本列表 30 分钟（安装链重复查，变化慢）、项目详情 24 小时（几乎不变——依赖名/详情页反复打）</summary>
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan VersionsCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ProjectCacheTtl = TimeSpan.FromHours(24);

    /// <summary>搜索响应磁盘缓存（TTL 分级见上）：切页/重复搜索不重复打 API——模组页首屏慢的元凶之一</summary>
    private async Task<T?> GetJsonAsyncCached<T>(string url, TimeSpan ttl, CancellationToken ct) where T : class
    {
        var key = "eco-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..16];
        var cachePath = Path.Combine(_cacheDir, key + ".json");
        try
        {
            if (File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < ttl)
                return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(cachePath, ct));
        }
        catch { /* 缓存损坏忽略 */ }
        var result = await GetJsonAsync<T>(url, ct);
        if (result is not null)
        {
            try
            {
                Directory.CreateDirectory(_cacheDir);
                await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(result), ct);
            }
            catch { /* 缓存写入失败不影响结果 */ }
        }
        return result;
    }

    /// <summary>镜像→官方回退（8-30 修：主链路单源镜像，境外网络不通即全挂——Mac 下载失败根因之一）。
    /// 镜像 enabled 时先试镜像，失败/空回退官方；镜像关直接用官方。</summary>
    private async Task<T?> GetWithFallbackAsync<T>(string path, TimeSpan ttl, CancellationToken ct) where T : class
    {
        if (!Launcher.Core.Utils.LauncherSettings.Current.ModrinthMirrorEnabled)
            return await GetJsonAsyncCached<T>(ApiBaseOfficial + path, ttl, ct);
        try
        {
            var mirror = await GetJsonAsyncCached<T>(
                "https://mod.mcimirror.top/modrinth/v2" + path, ttl, ct);
            if (mirror is not null) return mirror;
        }
        catch { /* 镜像失败/超时回退官方 */ }
        return await GetJsonAsyncCached<T>(ApiBaseOfficial + path, ttl, ct);
    }

    /// <summary>项目详情（8-16 批次 53：走磁盘缓存——依赖名前查询/详情页重复打开不再重复打 API；24h TTL）</summary>
    public Task<ModrinthProjectDetail?> GetProjectAsync(string projectIdOrSlug, CancellationToken ct = default)
        => GetWithFallbackAsync<ModrinthProjectDetail>($"/project/{projectIdOrSlug}", ProjectCacheTtl, ct);

    /// <summary>8-26 快路径镜像直查：官方 api.modrinth.com 国内 2-7s 抖动（实测），mcimirror 镜像稳定 ~1.7s。
    /// 仅中文快路径（已知 slug）用镜像，失败回退官方。不影响全局 ApiBase（镜像默认关的顾虑不扩散）。</summary>
    private async Task<ModrinthProjectDetail?> GetProjectFastAsync(string slug, CancellationToken ct)
    {
        try
        {
            var mirror = await GetJsonAsyncCached<ModrinthProjectDetail>(
                $"https://mod.mcimirror.top/modrinth/v2/project/{slug}", ProjectCacheTtl, ct);
            if (mirror is not null) return mirror;
        }
        catch { /* 镜像失败/超时回退官方 */ }
        return await GetProjectAsync(slug, ct);
    }

    /// <summary>匹配最新可用版本（按游戏版本+加载器过滤后取最新）。
    /// 8-26 修：26.x 年份号 API 剥掉了 game_versions 过滤（GetVersionsAsync 全量返回）→ 这里客户端补
    /// 过滤，防止「自动匹配」选到声明旧 MC 系（如 [1.21.x]）的版本装进 26.1.2 游戏（entityculling 实锤）。</summary>
    public async Task<ModrinthVersion?> FindBestVersionAsync(
        string projectId, string? gameVersion, string? loader, CancellationToken ct = default)
    {
        var versions = await GetVersionsAsync(projectId, gameVersion, loader, ct);
        if (gameVersion is not null && IsYearFormatVersion(gameVersion))
            versions = FilterByGameVersion(versions, gameVersion);
        return SelectBestVersion(versions);
    }

    /// <summary>版本列表（手动选择用，懒加载）。8-19：年份号（26.2）Modrinth versions API 不认（search facet 认、versions 参数不认）
    /// → 空结果自动去 gameVersion 重查一次（保留 loader；传统 1.x 空结果不降级——真实语义）。
    /// 8-22 改：年份号直接全量一次——旧实现「先查空再降级」= 8.6s×2 串行（安装链 Fabric API 查询
    /// 卡半分钟的主因）；且全量 URL 无 game_versions 参数 → 缓存键跨年份号共享，第二次直接秒回。</summary>
    public async Task<List<ModrinthVersion>> GetVersionsAsync(
        string projectId, string? gameVersion = null, string? loader = null, CancellationToken ct = default)
    {
        if (IsYearFormatVersion(gameVersion))
            return await GetVersionsCoreAsync(projectId, null, loader, ct);
        return await GetVersionsCoreAsync(projectId, gameVersion, loader, ct);
    }

    private async Task<List<ModrinthVersion>> GetVersionsCoreAsync(
        string projectId, string? gameVersion, string? loader, CancellationToken ct)
    {
        var query = new List<string>();
        if (gameVersion is not null)
            query.Add($"game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion }))}");
        if (loader is not null)
            query.Add($"loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader }))}");
        var path = $"/project/{projectId}/version"
                  + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        // 8-16 批次 53：版本列表走缓存（30 分钟 TTL）——安装流程主文件/依赖/手动选择会重复查同一版本列表
        // （api.modrinth.com 国内直连实测 8.6s/次，缓存后重复查询秒回；Fabric API 附带安装也吃这个缓存）
        var list = await GetWithFallbackAsync<List<ModrinthVersion>>(path, VersionsCacheTtl, ct);
        return list ?? [];
    }

    /// <summary>8-26 依赖解析快路径：镜像优先（官方 project/version 端点 2-7s 抖动，mcimirror 稳定 ~1.7s）。
    /// 前置解析的同步调用链（.GetAwaiter().GetResult()）原走官方，网络抖动静默失败 → 「前置不起作用」。
    /// 镜像失败回退官方。仅依赖解析用，不影响全局 ApiBase。</summary>
    public async Task<List<ModrinthVersion>> GetVersionsFastAsync(
        string projectId, string? gameVersion = null, string? loader = null, CancellationToken ct = default)
    {
        if (IsYearFormatVersion(gameVersion)) gameVersion = null; // 对齐 GetVersionsAsync：年份号全量一次
        var query = new List<string>();
        if (gameVersion is not null)
            query.Add($"game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion }))}");
        if (loader is not null)
            query.Add($"loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader }))}");
        var qs = query.Count > 0 ? "?" + string.Join("&", query) : "";
        try
        {
            var mirror = await GetJsonAsyncCached<List<ModrinthVersion>>(
                $"https://mod.mcimirror.top/modrinth/v2/project/{projectId}/version{qs}", VersionsCacheTtl, ct);
            if (mirror is { Count: > 0 }) return mirror;
        }
        catch { /* 镜像失败回退官方 */ }
        return await GetVersionsAsync(projectId, gameVersion, loader, ct);
    }

    /// <summary>
    /// 安装：下载主文件到实例目录（mods/resourcepacks/shaderpacks），整合包到 downloads/modpacks。
    /// 幂等：文件已存在且 SHA1 匹配时直接跳过。
    /// </summary>
    public async Task<string> InstallAsync(
        string projectId, ModrinthVersion version, string instanceId, ProjectType type,
        DownloadProgressHandler? progress = null, CancellationToken ct = default, string? gameDirOverride = null)
    {
        var file = PickPrimaryFile(version.Files)
            ?? throw new InvalidOperationException("该版本没有可下载文件");
        // gameDirOverride：版本来源目录（PCL/自建）——MOD 必须装进版本真实目录（AF2）
        var targetDir = ResolveInstallPath(gameDirOverride ?? _gameDirectory, instanceId, type);
        // 目标目录兜底创建（自定义实例名时 versions/{name}/mods 可能不存在——否则下载失败/落错位）
        Directory.CreateDirectory(targetDir);
        // 8-22 冲突检测：同名文件已存在且内容不同 → 用 UniquePath.Resolve 加 (1) 后缀，不覆盖旧文件。
        // 同名但 SHA1 一致（就是目标版本，重装）→ 保持原路径走幂等跳过。避免「重装同一版本复制成 (1)」。
        var desired = Path.Combine(targetDir, Path.GetFileName(file.FileName));
        var destPath = desired;
        if (File.Exists(desired) && file.Hashes?.Sha1 is { } sha1
            && !await Sha1MatchesAsync(desired, sha1))
        {
            destPath = Launcher.Core.Download.UniquePath.Resolve(desired);
        }
        await _downloads.DownloadFileAsync(file.Url, destPath, file.Hashes?.Sha1, file.Size, progress, ct);
        // 8-30 投毒检测：记录官方哈希（启动预检比对，防文件被替换/投毒）
        if (type == ProjectType.Mod)
            Launcher.Core.Diagnostics.ModHashManifest.Record(
                targetDir, Path.GetFileName(destPath), file.Hashes?.Sha1, file.Hashes?.Sha512, "modrinth");
        return destPath;
    }

    /// <summary>
    /// 解析依赖树并返回前置项目显示名（安装前提示用："将安装 N 个前置：A、B"）。
    /// 最多查 5 个标题（防滥用）；查询失败回退 ProjectId。
    /// </summary>
    public async Task<List<string>> ResolveDependencyNamesAsync(
        ModrinthVersion version, string? gameVersion, string? loader, CancellationToken ct = default)
    {
        // 8-19 生态修缮：iris/optifine → 承载 loader（依赖匹配与版本查询两处共用）
        loader = NormalizeLoaderForDependency(loader);
        var resolver = new ModDependencyResolver();
        var request = new ModDependencyRequest
        {
            TargetMinecraftVersion = gameVersion ?? "",
            TargetLoaders = loader is null ? [] : [loader],
            RequiredDependencies = EcosystemDependencyAdapter.ToDependencyReferences(version),
            ProjectResolver = EcosystemDependencyAdapter.CreateResolver(this, gameVersion, loader),
        };
        var result = resolver.Resolve(request);

        // 依赖显示名：项目标题 + 一句话说明（用户能看懂装的是什么——如 AANobbMI 是 Iris 的渲染 API 库）。
        // 8-16 批次 53：串行 → 并行（门 4）——api.modrinth.com 国内 8.6s/次，串行 5 个依赖 = 43s 干等
        var names = new List<string>(result.ToInstall.Count);
        var lockObj = new object();
        using var gate = new SemaphoreSlim(4);
        var tasks = new List<Task>();
        foreach (var dep in result.ToInstall.Take(5))
        {
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    // 8-26 名字查询走镜像快路径（官方 2-7s 抖动，镜像稳定 1.7s）
                    var detail = await GetProjectFastAsync(dep.ProjectId, ct);
                    string label;
                    if (detail is null) { label = dep.ProjectId; }
                    else
                    {
                        var hint = detail.Description;
                        if (hint is { Length: > 28 }) hint = hint[..28] + "…";
                        label = string.IsNullOrEmpty(hint) ? detail.Title : $"{detail.Title}——{hint}";
                    }
                    lock (lockObj) names.Add(label);
                }
                catch { lock (lockObj) names.Add(dep.ProjectId); }
                finally { gate.Release(); }
            }, ct));
        }
        await Task.WhenAll(tasks);
        // 8-26 解析失败兜底：依赖网络查询全挂时 result.ToInstall 为空 → 确认框不弹 =「前置不起作用」假象。
        // 用版本原始 required 依赖数兜底，让「要装 N 个前置」确认框至少弹出来（装不装得到由安装步决定）
        if (names.Count == 0 && EcosystemDependencyAdapter.ToDependencyReferences(version).Count > 0)
            names.Add($"{EcosystemDependencyAdapter.ToDependencyReferences(version).Count} 个前置（网络解析受限，仍将尝试安装）");
        return names;
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        string? json = null;
        try
        {
            json = await _http.GetStringAsync(url, ct);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            // 8-22 CF 诊断用：Modrinth 路径失败留痕（URL + 异常类型）——详情页「匹配失败」真凶定位
            try { System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cf-debug.log"),
                $"[{DateTime.Now:HH:mm:ss}] modrinth GET {url} -> {ex.GetType().Name}: {ex.Message} (body={(json is null ? "<none>" : json[..Math.Min(80, json.Length)].Replace('\n', ' '))})\n"); } catch { }
            throw;
        }
    }

    /// <summary>
    /// 安装主文件 + 解析并递归安装全部必需依赖（PCL2 式一键安装体验）。
    /// ctx 非空时主文件与每个依赖各成一个组子任务（下载中心可见、可暂停/重试）；
    /// 依赖并行安装（门 4，与 CF 侧一致）。
    /// </summary>
    public async Task<DependencyInstallReport> InstallWithDependenciesAsync(
        string projectId, ModrinthVersion version, string instanceId, ProjectType type,
        string? gameVersion, string? loader,
        DownloadProgressHandler? progress = null, CancellationToken ct = default, string? gameDirOverride = null,
        DownloadGroupContext? ctx = null)
    {
        // 8-19 生态修缮：iris/optifine → 承载 loader（依赖解析 TargetLoaders 与依赖版本查询共用）
        loader = NormalizeLoaderForDependency(loader);
        var report = new DependencyInstallReport();

        // 1. 主文件（8-19 生态修缮：weight 传真实大小——此前 0 导致组聚合 total=0 进度恒空）
        try
        {
            var primary = PickPrimaryFile(version.Files);
            var mainPath = await InstallOneAsync(ctx, $"主文件 {version.Name}", primary?.Size ?? 0,
                (p, c) => InstallAsync(projectId, version, instanceId, type, p, c, gameDirOverride), ct,
                targetPath: primary is null ? null : Path.Combine(
                    ResolveInstallPath(gameDirOverride ?? _gameDirectory, instanceId, type),
                    Path.GetFileName(primary.FileName)));
            report.Installed.Add(new InstalledDependency(projectId, version.Id, mainPath));
        }
        catch (Exception ex)
        {
            report.Failed.Add(new FailedDependency(projectId, ex.Message));
            return report;
        }

        // 2. 解析依赖树——8-19 生态修缮：解析是网络密集（逐依赖拉版本列表），放子任务占位，
        // 组 Stage 显示「解析依赖」而非无响应（原实现同步阻塞且组内无活跃子任务 = 卡死观感）
        var resolver = new ModDependencyResolver();
        var request = new ModDependencyRequest
        {
            TargetMinecraftVersion = gameVersion ?? "",
            TargetLoaders = loader is null ? [] : [loader],
            RequiredDependencies = EcosystemDependencyAdapter.ToDependencyReferences(version),
            ProjectResolver = EcosystemDependencyAdapter.CreateResolver(this, gameVersion, loader),
        };
        ModDependencyResolutionResult result;
        if (ctx is not null)
        {
            ModDependencyResolutionResult? resolved = null;
            var child = ctx.AddChild("解析依赖", 0, (_, _) => Task.Run(() =>
            {
                resolved = resolver.Resolve(request);
                return Task.CompletedTask;
            }));
            await child.Completion.WaitAsync(ct);
            result = resolved ?? new ModDependencyResolutionResult();
        }
        else
        {
            result = await Task.Run(() => resolver.Resolve(request), ct);
        }

        // 3. 依赖并行安装（依赖均为 MOD 类型，装到实例 mods 目录；结果收集加锁——多线程写 report）
        using var gate = new SemaphoreSlim(4);
        var depTasks = new List<Task>();
        foreach (var dep in result.ToInstall)
        {
            if (ct.IsCancellationRequested) break;
            depTasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var versions = await GetVersionsAsync(dep.ProjectId, gameVersion, loader, ct);
                    var depVersion = versions.FirstOrDefault(v => v.Id == dep.File.Id);
                    if (depVersion is null)
                    {
                        lock (report) report.Failed.Add(new FailedDependency(dep.ProjectId, "依赖版本已不存在"));
                        return;
                    }
                    var depPrimary = PickPrimaryFile(depVersion.Files);
                    var path = await InstallOneAsync(ctx, $"依赖 {depVersion.Name}", depPrimary?.Size ?? 0,
                        (p, c) => InstallAsync(dep.ProjectId, depVersion, instanceId, ProjectType.Mod, p, c, gameDirOverride), ct,
                        targetPath: depPrimary is null ? null : Path.Combine(
                            ResolveInstallPath(gameDirOverride ?? _gameDirectory, instanceId, ProjectType.Mod),
                            Path.GetFileName(depPrimary.FileName)));
                    lock (report) report.Installed.Add(new InstalledDependency(dep.ProjectId, depVersion.Id, path));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* 组取消：其余任务一并终止 */ }
                catch (Exception ex)
                {
                    lock (report) report.Failed.Add(new FailedDependency(dep.ProjectId, ex.Message));
                }
                finally { gate.Release(); }
            }, ct));
        }
        await Task.WhenAll(depTasks);

        // 4. 未解析依赖
        foreach (var un in result.Unresolved)
            report.Failed.Add(new FailedDependency(un.ProjectId, un.Reason));

        return report;
    }

    /// <summary>安装单文件：有组上下文 → 子任务（下载中心可见）；否则直接装（测试/叶子调用兼容）。
    /// targetPath：预知的最终落点——8-19 生态修缮阶段3，子任务取消/失败自动清 .parts 中间产物</summary>
    private async Task<string> InstallOneAsync(DownloadGroupContext? ctx, string name, long weight,
        Func<DownloadProgressHandler, CancellationToken, Task<string>> work, CancellationToken ct,
        string? targetPath = null)
    {
        if (ctx is null) return await work(null!, ct);
        string? path = null;
        var child = ctx.AddChild(name, weight, async (p, c) => { path = await work(p, c); }, targetPath);
        await child.Completion.WaitAsync(ct);
        return path ?? throw new InvalidOperationException($"{name} 未产生文件");
    }

    // ---------- 静态工具（离线可单测） ----------

    /// <summary>构建 facets JSON，如 [["project_type:mod"],["versions:1.21.1"],["categories:fabric"],["categories:optimization"]]。
    /// 加载器与功能分类同用 categories 键（Modrinth 同键多值取 OR）；facets 值强制小写（API 要求）。</summary>
    public static string BuildFacets(ProjectType type, string? gameVersion, string? loader, string? category = null)
    {
        var outer = new List<string[]> { new[] { $"project_type:{FacetName(type)}" } };
        if (gameVersion is not null) outer.Add(new[] { $"versions:{gameVersion}" });
        if (loader is not null) outer.Add(new[] { $"categories:{loader.ToLowerInvariant()}" });
        if (category is not null) outer.Add(new[] { $"categories:{category.ToLowerInvariant()}" });
        return JsonSerializer.Serialize(outer);
    }

    public static string FacetName(ProjectType type) => type switch
    {
        ProjectType.Mod => "mod",
        ProjectType.Modpack => "modpack",
        ProjectType.Resourcepack => "resourcepack",
        ProjectType.Shader => "shader",
        ProjectType.Datapack => "datapack", // 8-26 修：缺此分支曾落入 "mod" → 数据包页搜出模组
        _ => "mod",
    };

    /// <summary>安装子目录；整合包返回 null（走 downloads/modpacks）。
    /// 8-16 批次 54：数据包 → datapacks（1.13+ 全局数据包目录，所有世界生效）</summary>
    public static string? ResolveSubDir(ProjectType type) => type switch
    {
        ProjectType.Mod => "mods",
        ProjectType.Resourcepack => "resourcepacks",
        ProjectType.Shader => "shaderpacks",
        ProjectType.Datapack => "datapacks",
        _ => null,
    };

    public static string ResolveInstallPath(string gameDirectory, string instanceId, ProjectType type)
    {
        var norm = gameDirectory.TrimEnd('\\', '/').Replace('\\', '/');
        if (type == ProjectType.Modpack)
        {
            // 幂等（8-19 生态修缮）：输入已是 downloads\modpacks（弹窗默认值/浏览选中落点）→ 不重复拼接
            if (norm.EndsWith("downloads/modpacks", StringComparison.OrdinalIgnoreCase)) return gameDirectory;
            return Path.Combine(gameDirectory, "downloads", "modpacks");
        }
        var sub = ResolveSubDir(type)!;
        // 8-19 第二批：落点跟随版本隔离设置，不再用「目录是否存在」猜——旧启发式在隔离开时
        // 把 mod 装进实例目录（游戏 game_directory=根、只读根 mods → 装完游戏不加载）；
        // 隔离关恒装共享目录；隔离开恒装实例目录（缺则创建——手改路径后新建实例也装对地方）
        var isolated = Launcher.Core.Utils.LauncherSettings.Current.VersionIsolation;
        // 幂等（8-19 生态修缮）：输入已是最终落点不再重复拼接——隔离开识别 {base}\versions\{某实例}\{sub}；
        // 隔离关识别 {base}\{sub}（PCL 式：弹窗默认值/浏览选中 mods 文件夹直达安装）。
        // 8-23 修复（TACZ 嵌套路径 bug）：隔离开旧守卫按 instanceId 精确匹配——instanceId 与路径内实例名
        // 不一致时失配，二次拼接成 {sub}\versions\{id}\{sub}（如 ...\mods\versions\TACZgun\mods）。
        // 改为识别「任意实例」的 versions/{X}/{sub}：用户手改/浏览选了别的实例的 mods 目录也直接使用。
        if (isolated)
        {
            if (Regex.IsMatch(norm, @"/versions/[^/]+/" + Regex.Escape(sub) + "$", RegexOptions.IgnoreCase))
                return gameDirectory;
        }
        else if (norm.EndsWith("/" + sub, StringComparison.OrdinalIgnoreCase))
        {
            return gameDirectory;
        }
        var baseDir = isolated
            ? Path.Combine(gameDirectory, "versions", instanceId)
            : gameDirectory;
        if (isolated) Directory.CreateDirectory(baseDir);
        return Path.Combine(baseDir, sub);
    }

    /// <summary>从实例名解析游戏版本：1.21.1 → true/"1.21.1"；1.21.1-Fabric → true；自定义名 → false。
    /// 注意：只匹配「版本号开头」的实例名——启动器 fabric 实例（fabric-loader-0.19.3-26.1.2）开头是 fabric，
    /// 解析不出，须走 ResolveGameVersion（McVersion/inheritsFrom）。</summary>
    public static bool TryParseGameVersion(string instanceId, out string version)
    {
        var m = Regex.Match(instanceId, @"^\d+\.\d+(\.\d+)?");
        if (m.Success) { version = m.Value; return true; }
        version = "";
        return false;
    }

    /// <summary>实例游戏版本通用解析（8-26，修复 fabric 实例自动匹配裸奔）：优先 McVersion（由版本 json 的
    /// inheritsFrom 解析，fabric-loader-… 实例名也是正确值）；空则回退从实例名开头解析（原生版 /「1.21.1-Fabric」式
    /// 命名）。空串 = 解析不出（快照/自定义名），调用方跳过——不瞎猜。</summary>
    public static string ResolveGameVersion(string? mcVersion, string instanceName)
    {
        if (!string.IsNullOrWhiteSpace(mcVersion)) return mcVersion!;
        return TryParseGameVersion(instanceName, out var v) ? v : "";
    }

    /// <summary>8-19：PCL 年份号版本（26.2/26.10/99.1——`^\d{2}\.\d+`，非 1.x 传统格式）。
    /// 年份号在 CF/Modrinth 文件版本（1.21.6 格式）中永不匹配——空结果必为假阴性 → 允许降级/放宽；
    /// 传统 1.x 的空结果是真实语义，绝不降级（否则 1.21.6 实例会高亮 1.20.1 版本装崩）。</summary>
    public static bool IsYearFormatVersion(string? version)
        => !string.IsNullOrEmpty(version) && Regex.IsMatch(version, @"^\d{2}\.\d+");

    /// <summary>从实例名猜测加载器（fabric/forge/neoforge/quilt/iris/optifine），未知返回 null</summary>
    public static string? GuessLoader(string instanceId)
    {
        var lower = instanceId.ToLowerInvariant();
        foreach (var (keyword, loader) in new[]
                 {
                     ("fabric", "fabric"), ("neoforge", "neoforge"), ("forge", "forge"),
                     ("quilt", "quilt"), ("iris", "iris"), ("optifine", "optifine"),
                 })
        {
            if (lower.Contains(keyword)) return loader;
        }
        return null;
    }

    /// <summary>8-19 生态修缮（依赖只装单个根治）：iris/optifine 不是 Modrinth loader 分类——
    /// 依赖解析按「承载 loader」过滤：iris 实例的 sodium 等依赖 TargetLoaders=[fabric] 才匹配
    /// （此前 iris → TargetLoaders=["iris"] → IsCompatibleFile 对 fabric 依赖全部失败 → 依赖静默跳过只装主文件）；
    /// optifine 保守映射 forge；未知透传</summary>
    public static string? NormalizeLoaderForDependency(string? loader) => loader switch
    {
        "iris" => "fabric",
        "optifine" => "forge",
        _ => loader,
    };

    /// <summary>
    /// 游戏版本语义比较：点分数字逐段比（26.2 &gt; 1.21.6、1.21.10 &gt; 1.21.6——字符串序会判反）；
    /// 非数字段回落序号比较。2026 起版本号用 YY.M 新格式，1.x 与 26.x 混排必须走语义序。
    /// </summary>
    public static int CompareGameVersions(string? x, string? y)
    {
        var xp = (x ?? "").Split('.');
        var yp = (y ?? "").Split('.');
        for (var i = 0; i < Math.Min(xp.Length, yp.Length); i++)
        {
            if (int.TryParse(xp[i], out var xn) && int.TryParse(yp[i], out var yn))
            {
                if (xn != yn) return xn.CompareTo(yn);
            }
            else
            {
                var c = string.Compare(xp[i], yp[i], StringComparison.Ordinal);
                if (c != 0) return c;
            }
        }
        return xp.Length.CompareTo(yp.Length);
    }

    /// <summary>
    /// 选最新版本：过滤无文件项；release &gt; beta &gt; alpha &gt; null 优先（快照/预发布不抢正式版——
    /// 8-13 真机：26.2 的 beta 日期最新被选中，用户装正式版却匹配到快照），同级 featured 优先，
    /// 其次 date_published 降序。null 排最后与依赖解析器 NormalizeReleaseType 一致。
    /// </summary>
    public static ModrinthVersion? SelectBestVersion(IEnumerable<ModrinthVersion> versions)
        => versions.Where(v => v.Files is { Count: > 0 })
                   .OrderBy(v => ReleaseRank(v.VersionType))
                   .ThenByDescending(v => v.Featured ?? false)
                   .ThenByDescending(v => v.DatePublished)
                   .FirstOrDefault();

    /// <summary>客户端按游戏版本过滤版本列表（8-26）：Modrinth versions API 不认年份号（26.x），
    /// 查询时剥掉 game_versions → 返回全量。这里补过滤，避免「自动匹配」选到声明旧 MC 系（[1.21.x]）的版本
    /// 装进 26.1.2 游戏。保留声明支持目标游戏版本的项：d == gv、d 是 gv 前缀（"26.1" 覆盖 26.1.2）、
    /// 或通配 "26.1.x"。过滤后为空 → 返回空（调用方明示「无适配版本」，不静默回落最新版）。</summary>
    public static List<ModrinthVersion> FilterByGameVersion(IEnumerable<ModrinthVersion> versions, string gameVersion)
    {
        var gv = gameVersion.Trim();
        if (gv.Length == 0) return versions.ToList();
        return versions.Where(v => v.GameVersions is { Count: > 0 }
            && v.GameVersions.Any(d => MatchesGameVersion(d, gv))).ToList();
    }

    private static bool MatchesGameVersion(string declared, string gv)
    {
        var d = declared.Trim();
        if (d.Length == 0) return false;
        if (string.Equals(d, gv, StringComparison.Ordinal)) return true;
        if (gv.StartsWith(d + ".", StringComparison.Ordinal)) return true;        // "26.1" 覆盖 26.1.2
        if (d.EndsWith(".x", StringComparison.OrdinalIgnoreCase)
            && gv.StartsWith(d[..^1], StringComparison.Ordinal)) return true;      // "26.1.x" 覆盖 26.1.2
        return false;
    }

    /// <summary>Modrinth version_type 排名（release=0 beta=1 alpha=2 null=3——未知信任度最低）</summary>
    public static int ReleaseRank(string? type) => type switch
    {
        "release" => 0,
        "beta" => 1,
        "alpha" => 2,
        _ => 3,
    };

    /// <summary>选主文件：Primary 优先，否则第一个</summary>
    public static ModrinthVersionFile? PickPrimaryFile(List<ModrinthVersionFile>? files)
    {
        if (files is null || files.Count == 0) return null;
        return files.FirstOrDefault(f => f.Primary) ?? files[0];
    }

    // ---------- 中文搜索重排（A 修复：Modrinth relevance 对中文把「描述子串匹配」当强相关，
    // 「字幕高亮」因描述含「自定义」排第一 → 客户端按匹配质量稳定重排） ----------

    /// <summary>Query 含 CJK（中文搜索）→ 源 relevance 不可靠，需重排；纯英文信任源排序</summary>
    public static bool IsChineseQuery(string? q)
        => !string.IsNullOrEmpty(q) && q.Any(c => c is >= '一' and <= '鿿');

    /// <summary>匹配分：标题包含 query=3（强相关），描述/摘要包含=2（弱相关），无=0</summary>
    public static int MatchScore(string title, string description, string query)
    {
        if (title.Contains(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (description.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
        return 0;
    }

    /// <summary>按匹配分降序稳定重排（同分保持源顺序）；非中文 query 调用方不应调用</summary>
    public static List<T> ReorderMatches<T>(IEnumerable<T> items, string? query,
        Func<T, string> titleOf, Func<T, string> descriptionOf)
        => [.. items.OrderByDescending(x => MatchScore(titleOf(x), descriptionOf(x), query ?? ""))];

    /// <summary>8-22 本地 SHA1 比对（冲突检测用：同名文件是否就是目标版本；缺失/读取失败 = 不匹配）</summary>
    private static async Task<bool> Sha1MatchesAsync(string path, string expected)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            var hash = await System.Security.Cryptography.SHA1.HashDataAsync(fs);
            return Convert.ToHexStringLower(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
