using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Launcher.Core.Diagnostics;
using Launcher.Core.Launch;
using Launcher.Core.Model.Loader;
using Launcher.Core.Model.Modrinth;
using Launcher.Core.Model.Mojang;
using Launcher.Core.Services;
using Launcher.Core.Utils;

namespace Launcher.Core.Download;

/// <summary>
/// 加载器下载源（四家）：
/// - Fabric / Quilt：meta API 直装（profile json 继承原版 → 写版本目录 → 全量下载，无进程）；
/// - Forge / NeoForge：官方安装器 jar + 安装器进程（--installClient）。
/// 前置条件：目标原版版本已安装（inheritsFrom 链需要父版本 JSON 在磁盘上）。
/// </summary>
public sealed class LoaderService
{
    private const string FabricMeta = "https://meta.fabricmc.net/v2/versions/loader";
    // 8-22：bmclapi fabric-meta 镜像（verse 已验证，国内 0.67s vs 官方 3.9s）——加载器下拉不再 20s 超时
    private const string FabricMetaMirror = "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader";
    private const string QuiltMeta = "https://meta.quiltmc.org/v3/versions/loader";
    private const string ForgePromos = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    private const string ForgeInstallerBase = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private const string NeoForgeMetadata = "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";
    private const string NeoForgeInstallerBase = "https://maven.neoforged.net/releases/net/neoforged/neoforge";

    private readonly HttpClient _http;
    private readonly DownloadService _downloads;
    private readonly string _gameDirectory;
    private readonly string? _loaderProfileCacheDir;
    private readonly string? _ecoCacheDir;
    // AL29 测试缝：真实环境跑 java 安装器进程；测试注入 stub（控制退出码与写文件行为）
    private readonly Func<string, string[], Action<string>?, CancellationToken, Task<int>> _installerProcess;

    public LoaderService(HttpClient? http = null, DownloadService? downloads = null, string? gameDirectory = null,
        Func<string, string[], Action<string>?, CancellationToken, Task<int>>? installerProcess = null,
        string? loaderProfileCacheDir = null, // REVIEW-前摇：profile json 缓存目录（测试注入临时目录隔离全局 AppData）
        string? ecoCacheDir = null) // 8-16 批次 53：Modrinth API 磁盘缓存目录（测试隔离，同 loaderProfileCacheDir 思路）
    {
        _loaderProfileCacheDir = loaderProfileCacheDir;
        _ecoCacheDir = ecoCacheDir;
        // AL28 显式超时：默认 100s 太慢——meta.fabricmc.net 国内访问实测 12s+，超时让失败快速可见（而非干等）
        _http = http ?? HttpClientPool.CreateSharedClient(TimeSpan.FromSeconds(20));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("YanKa-Launcher/0.1");
        // 探针实测 08-09：不传 downloads 时自建服务必须带 gameDirectory，否则内部下载写到
        // GameDirectory.Detect()（默认安装位）而本服务 gameDirectory 指向别处 → Verify 查错目录报"缺文件"
        _downloads = downloads ?? new DownloadService(gameDirectory: gameDirectory);
        _gameDirectory = gameDirectory ?? GameDirectory.Detect();
        _installerProcess = installerProcess ?? InstallerProcess.RunAsync;
    }

    // ---------- 版本列表 ----------

    public async Task<List<LoaderMetaVersion>> GetLoaderVersionsAsync(LoaderKind kind, string mcVersion, CancellationToken ct)
    {
        // AL28 本地缓存 + AL37 stale-while-revalidate：meta.fabricmc.net 国内实测 6-26s（08-09 真机），
        // PCL 秒出靠内置数据——这里过期缓存也立即返回（新版本延迟可见可接受），后台静默拉新，UI 永不阻塞。
        var cachePath = CacheFilePath(kind, mcVersion);
        if (TryLoadCache(cachePath, out var cached))
        {
            if (IsStale(cachePath)) _ = RefreshCacheAsync(kind, mcVersion, cachePath);
            return cached;
        }

        var list = await FetchVersionsAsync(kind, mcVersion, ct);
        TrySaveCache(cachePath, list);
        return list;
    }

    private async Task<List<LoaderMetaVersion>> FetchVersionsAsync(LoaderKind kind, string mcVersion, CancellationToken ct)
        => kind switch
        {
            LoaderKind.Fabric => await GetFabricVersionsAsync(mcVersion, ct),
            LoaderKind.Quilt => await GetQuiltVersionsAsync(mcVersion, ct),
            LoaderKind.NeoForge => await GetNeoForgeVersionsAsync(mcVersion, ct),
            _ => await GetForgeVersionsAsync(mcVersion, ct),
        };

    /// <summary>后台刷新（fire-and-forget）：失败保留旧缓存，不影响已返回的列表</summary>
    private async Task RefreshCacheAsync(LoaderKind kind, string mcVersion, string cachePath)
    {
        try
        {
            var list = await FetchVersionsAsync(kind, mcVersion, CancellationToken.None);
            TrySaveCache(cachePath, list);
        }
        catch { /* 刷新失败保留旧缓存 */ }
    }

    private static bool IsStale(string path)
    {
        try { return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromHours(24); }
        catch { return true; }
    }

    // ---------- AL28 版本列表本地缓存（TTL 24h，损坏/空列表回退网络） ----------

    private static string CacheFilePath(LoaderKind kind, string mcVersion)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Launcher", "cache", $"loader-{kind}-{mcVersion}.json");

    private static bool TryLoadCache(string path, out List<LoaderMetaVersion> list)
    {
        list = [];
        try
        {
            if (!File.Exists(path)) return false;
            // AL37：不再按 TTL 拒绝——过期缓存照样返回（IsStale 触发后台刷新），文件存在即可用
            var cached = JsonSerializer.Deserialize<List<LoaderMetaVersion>>(File.ReadAllText(path));
            if (cached is null || cached.Count == 0) return false;
            list = cached;
            return true;
        }
        catch { return false; } // 缓存损坏则重新拉取
    }

    private static void TrySaveCache(string path, List<LoaderMetaVersion> list)
    {
        try
        {
            if (list.Count == 0) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(list));
        }
        catch { /* 缓存失败不影响主流程 */ }
    }

    /// <summary>Fabric：meta.fabricmc.net/v2/versions/loader/{mc}（最新在前，stable 优先展示）。
    /// 8-22 双源竞速：bmclapi fabric-meta 镜像优先（国内 0.67s vs 官方 3.9s），官方失败回退——
    /// 加载器下拉不再 20s 超时（此前直连官方，国内慢必超时）。</summary>
    private async Task<List<LoaderMetaVersion>> GetFabricVersionsAsync(string mcVersion, CancellationToken ct)
    {
        var mirror = $"{FabricMetaMirror}/{mcVersion}";
        var official = $"{FabricMeta}/{mcVersion}";
        var list = await GetJsonFirstAsync<List<FabricMetaEntry>>(mirror, official, ct) ?? [];
        return list.Select(e => new LoaderMetaVersion(e.Loader?.Version ?? "", e.Loader?.Stable == true))
                   .Where(m => m.Version.Length > 0).ToList();
    }

    /// <summary>8-22 双 URL 竞速拉 JSON：先试 primary（镜像），失败/超时回退 secondary（官方）。
    /// 复用 GetJsonAsync 的序列化；镜像快则秒回，镜像挂了官方兜底。</summary>
    private async Task<T?> GetJsonFirstAsync<T>(string primary, string secondary, CancellationToken ct) where T : class
    {
        try { return await GetJsonAsync<T>(primary, ct); }
        catch { /* 镜像失败 → 官方 */ }
        return await GetJsonAsync<T>(secondary, ct);
    }

    /// <summary>Quilt：meta.quiltmc.org/v3/versions/loader/{mc}（无 stable 字段，无 -beta/-alpha 视为稳定）</summary>
    private async Task<List<LoaderMetaVersion>> GetQuiltVersionsAsync(string mcVersion, CancellationToken ct)
    {
        var list = await GetJsonAsync<List<FabricMetaEntry>>($"{QuiltMeta}/{mcVersion}", ct) ?? [];
        return list.Select(e => new LoaderMetaVersion(e.Loader?.Version ?? "",
                   e.Loader?.Version is { } v && !v.Contains('-'))).Where(m => m.Version.Length > 0).ToList();
    }

    /// <summary>
    /// Forge：promotions_slim.json 的 {mc}-recommended / {mc}-latest。
    /// 注：maven.minecraftforge.net 的 maven-metadata 已 404（Reposilite），promos 缺失即无可用版本。
    /// </summary>
    private async Task<List<LoaderMetaVersion>> GetForgeVersionsAsync(string mcVersion, CancellationToken ct)
    {
        var promos = await GetJsonAsync<ForgePromotions>(ForgePromos, ct);
        var list = new List<LoaderMetaVersion>();
        var recommended = promos?.Promos?.GetValueOrDefault($"{mcVersion}-recommended");
        var latest = promos?.Promos?.GetValueOrDefault($"{mcVersion}-latest");
        if (recommended is not null) list.Add(new LoaderMetaVersion(recommended, true));
        if (latest is not null && latest != recommended) list.Add(new LoaderMetaVersion(latest, false));
        return list;
    }

    /// <summary>
    /// NeoForge：meta.neoforged.net v1 端点当前不可达，走 maven 元数据按版本前缀筛选
    /// （NeoForge 版本 = MC 版本去掉 "1." 前缀：1.21.1 → 21.1.x；安装器 URL 用完整版本号，无 mc 前缀）。
    /// </summary>
    private async Task<List<LoaderMetaVersion>> GetNeoForgeVersionsAsync(string mcVersion, CancellationToken ct)
    {
        // NeoForge 版本 = MC 去掉 "1." 且补全补丁号：1.21 → 21.0，1.21.1 → 21.1（防止 21. 前缀误匹配 21.1-21.8）
        var parts = mcVersion.Split('.');
        var prefix = mcVersion.StartsWith("1.", StringComparison.Ordinal)
            ? (parts.Length >= 3 ? parts[1] + "." + parts[2] : parts[1] + ".0") + "."
            : mcVersion + ".";
        var xml = await _http.GetStringAsync(NeoForgeMetadata, ct);
        var doc = XDocument.Parse(xml);
        return doc.Descendants("version").Select(v => v.Value)
            .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
            .Select(v => new LoaderMetaVersion(v, IsStableNeoForge(v)))
            .OrderByDescending(v => v.Version, new VersionComparer())
            .ThenByDescending(v => v.IsStable)
            .ToList();
    }

    private static bool IsStableNeoForge(string v)
        => !v.Contains("-beta", StringComparison.OrdinalIgnoreCase)
           && !v.Contains("-alpha", StringComparison.OrdinalIgnoreCase)
           && !v.Contains("-rc", StringComparison.OrdinalIgnoreCase);

    // ---------- 安装计划 ----------

    public async Task<LoaderInstallPlan> CreatePlanAsync(LoaderKind kind, string mcVersion, string? loaderVersion, CancellationToken ct)
    {
        switch (kind)
        {
            case LoaderKind.Fabric:
            {
                var lv = loaderVersion ?? await PickFirstAsync(GetFabricVersionsAsync(mcVersion, ct), "该版本暂无 Fabric 加载器");
                return new LoaderInstallPlan(kind, mcVersion, lv, $"{FabricMeta}/{mcVersion}/{lv}/profile/json", null, null, null);
            }
            case LoaderKind.Quilt:
            {
                var lv = loaderVersion ?? await PickFirstAsync(GetQuiltVersionsAsync(mcVersion, ct), "该版本不支持 Quilt（1.18.2+）");
                return new LoaderInstallPlan(kind, mcVersion, lv, $"{QuiltMeta}/{mcVersion}/{lv}/profile/json", null, null, null);
            }
            case LoaderKind.NeoForge:
            {
                var lv = loaderVersion ?? await PickFirstAsync(GetNeoForgeVersionsAsync(mcVersion, ct), "该版本暂无 NeoForge 版本");
                return new LoaderInstallPlan(kind, mcVersion, lv, null,
                    $"{NeoForgeInstallerBase}/{lv}/neoforge-{lv}-installer.jar", null, null);
            }
            default:
            {
                var lv = loaderVersion ?? await PickFirstAsync(GetForgeVersionsAsync(mcVersion, ct), "该版本暂无 Forge 版本");
                return new LoaderInstallPlan(kind, mcVersion, lv, null,
                    $"{ForgeInstallerBase}/{mcVersion}-{lv}/forge-{mcVersion}-{lv}-installer.jar", null, null);
            }
        }
    }

    private static async Task<string> PickFirstAsync(Task<List<LoaderMetaVersion>> versionsTask, string emptyMessage)
    {
        var list = await versionsTask;
        return list.FirstOrDefault()?.Version ?? throw new InvalidOperationException(emptyMessage);
    }

    // ---------- 安装 ----------

    /// <summary>安装（旧展平路径，兼容测试与旧调用）</summary>
    public Task InstallAsync(LoaderInstallPlan plan, DownloadProgressHandler? progress, CancellationToken ct)
        => InstallCoreAsync(plan, progress, null, ct);

    /// <summary>安装（组任务路径：加载器配置/安装器为子任务，版本下载并入同一组）</summary>
    public Task InstallAsync(LoaderInstallPlan plan, DownloadGroupContext ctx, CancellationToken ct)
        => InstallCoreAsync(plan, null, ctx, ct);

    private string? _lastInstalledVersionId;

    /// <summary>上次安装的加载器版本 id（Forge/NeoForge 安装器生成；Fabric/Quilt meta 写入）——整合包导入改名用</summary>
    public string? LastInstalledVersionId => _lastInstalledVersionId;

    private async Task InstallCoreAsync(LoaderInstallPlan plan, DownloadProgressHandler? progress,
        DownloadGroupContext? ctx, CancellationToken ct)
    {
        _lastInstalledVersionId = null;
        // 8-22 Fabric API 并行预取：安装主链一开始就查 fabric-api 元数据（写磁盘缓存）——
        // 客户端 jar/加载器下载期间（10s+）查询完成（8.6s 被下载时间掩盖），
        // 到附带安装阶段直接读缓存零等待——消灭「进度 100% 后卡查询」的观感
        if (plan is { Kind: LoaderKind.Fabric, InstallFabricApi: true })
            PrefetchFabricApiAsync(plan.McVersion);
        switch (plan.Kind)
        {
            case LoaderKind.Fabric:
            case LoaderKind.Quilt:
                await InstallMetaAsync(plan, progress, ctx, ct);
                break;
            default:
                await InstallInstallerAsync(plan, progress, ctx, ct);
                break;
        }
        // AL29 真机教训：先校验后标记——校验抛异常时绝不能留下 .yanla-installed
        // （否则版本页把失败的安装计为「本启动器已装」，实测 22:41 真机即此错误）
        if (_lastInstalledVersionId is { } id)
        {
            await VerifyInstalledVersionAsync(id);
            InstallMarker.Mark(_gameDirectory, id);
            // 8-23：加载器覆盖原版（装时吸收）——同 MC 原版已正式安装时降级隐藏（.yanla-installed → .prefetched），
            // 加载器成为唯一条目；删加载器后 CleanupOrphanPrefetches 连带清理孤立原版（删得干净）
            AbsorbVanilla(plan.McVersion, id);
            // 附带安装 Fabric API（用户勾选时）：失败只记日志不阻断——加载器已装完，API 是增强
            if (plan is { Kind: LoaderKind.Fabric, InstallFabricApi: true })
            {
                if (ctx is not null)
                {
                    // REVIEW-卡完成：fabric-api 挂组内子任务（有进度/速度/Stage）——旧代码组路径下
                    // progress 参数无效 → 主任务「148.5/148.5 满进度 + 2.8MB/s 在下载」却无任何表达
                    // （真机 8-11 用户误以为引擎坏了）；子任务 weight=0 不定条 + 下载速度可见。
                    // 8-22 根治「卡进度条」：查询到文件大小后动态补 weight——父进度条从 148.5
                    // 继续走到 150.1，Fabric API 阶段进度不再静止（聚合每轮按 Children 重算 total）
                    DownloadTask? fabChild = null;
                    fabChild = ctx.AddChild("Fabric API", 0,
                        (p, c) => InstallFabricApiAsync(id, plan.McVersion, c,
                            new ProgressReporter("正在准备 Fabric API…", p), fabChild));
                    await fabChild.Completion;
                }
                else
                {
                    // AL46.1：非组路径的进度表达——主文件 100% 后不静默（Modrinth 查询/下载 30s 兜底）
                    progress?.Invoke(new DownloadProgress("正在准备 Fabric API…", null, 0, 0, 0));
                    await InstallFabricApiAsync(id, plan.McVersion, ct);
                }
            }
        }
    }

    /// <summary>
    /// 8-23：加载器覆盖原版（装时吸收）。原版已正式安装（.yanla-installed）时降级为预取
    /// （.prefetched），主页/版本页即隐藏原版、加载器成唯一条目；删加载器后由
    /// CleanupOrphanParents/CleanupOrphanPrefetches 连带清理孤立原版。
    /// 守卫：仅当加载器 json 确实 inheritsFrom 该原版才吸收（防 Forge/NeoForge 独立结构误吸收
    /// → 原版被当孤立预取删掉、加载器失去父版本）。
    /// </summary>
    private void AbsorbVanilla(string mcVersion, string loaderId)
    {
        if (string.IsNullOrEmpty(mcVersion)
            || string.Equals(mcVersion, loaderId, StringComparison.OrdinalIgnoreCase)
            || !InstallMarker.IsMarked(_gameDirectory, mcVersion)) return;
        try
        {
            var loaderJson = Path.Combine(_gameDirectory, "versions", loaderId, $"{loaderId}.json");
            var v = JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(loaderJson));
            if (!string.Equals(v?.InheritsFrom, mcVersion, StringComparison.OrdinalIgnoreCase)) return;
        }
        catch { return; }
        InstallMarker.Unmark(_gameDirectory, mcVersion);
        InstallMarker.MarkPrefetched(_gameDirectory, mcVersion);
    }

    /// <summary>
    /// 附带安装 Fabric API（PCL 式）：从 Modrinth 按 slug 查项目 → 按 (mcVersion, fabric) 取版本 →
    /// 选最新 → 下载主文件到本加载器版本目录的 mods/（ResolveInstallPath 命中已写入的 versions/{id}）。
    /// 任何失败/无版本都静默（Debug.WriteLine）——26.2 等新版本 Modrinth 可能还没有 fabric-api 发布。
    /// 不用 ctx.AddChild：失败子任务会把下载组置 Failed（LoaderServiceTests 已验证该语义）。
    /// 元数据大多已被 PrefetchFabricApiAsync 预热（缓存命中秒回）；未命中时此处仍兜底直查。
    /// </summary>
    private async Task InstallFabricApiAsync(string versionId, string mcVersion, CancellationToken ct,
        ProgressReporter? rep = null, DownloadTask? child = null)
    {
        // 8-19 生态修缮阶段3：预知落点（try 外声明——catch 显式清理要用）
        string? destPath = null;
        try
        {
            // AL46.1：Modrinth 境外慢——30s 超时兜底（实测卡 2 分钟不可接受）；超时走 catch 静默
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var c2 = timeout.Token;
            // 8-22 查询阶段明示（消灭「进度静止不知在等什么」）：预取命中时秒回；未命中如实告知
            rep?.ReportStage("正在查询 Fabric API（Modrinth 网络较慢时约需几秒）…");
            var eco = new EcosystemService(_http, _downloads, _gameDirectory, cacheDir: _ecoCacheDir);
            var project = await eco.GetProjectAsync("fabric-api", c2);
            var versions = await eco.GetVersionsAsync(project.Id, mcVersion, "fabric", c2);
            // 8-19：GetVersionsAsync 对年份号（26.2）会降级返回全量——fabric-api 必须精确匹配 mcVersion
            // 构建（26.2 无对应构建则保持静默跳过——防装 1.21.6 构建进 26.2 实例崩，fabric.mod.json 版本锁定）
            var best = EcosystemService.SelectBestVersion(
                versions.Where(v => v.GameVersions?.Contains(mcVersion) == true));
            if (best is null)
            {
                Debug.WriteLine($"[Loader] {mcVersion} 无 fabric-api 版本，跳过");
                rep?.ReportStage($"Fabric API 无 {mcVersion} 构建，已跳过");
                rep?.Complete();
                return;
            }
            // 8-22 查到文件大小 → 补子任务 weight：父进度条从 148.5 继续走到 150.1，
            // Fabric API 阶段进度不再静止（「卡进度条」根治——聚合每轮按 Children 重算 total）
            var size = best.Files?.FirstOrDefault(f => f.Primary)?.Size ?? 0;
            if (size > 0 && child is not null) child.Weight = size;
            // 8-19 生态修缮阶段3：预知落点挂子任务——终态失败/取消自动清 .parts（残留根治：
            // 此前子任务无 TargetPath，中途失败吞异常 → 子任务 Completed → 清理链不触发，.parts 永久残留）
            var primary = best.Files?.FirstOrDefault(f => f.Primary);
            destPath = primary is null ? null : Path.Combine(
                EcosystemService.ResolveInstallPath(_gameDirectory, versionId, ProjectType.Mod),
                Path.GetFileName(primary.FileName));
            if (child is not null && destPath is not null) child.TargetPath = destPath;
            rep?.ReportStage("正在下载 Fabric API…");
            // REVIEW-卡完成：reporter 透传——组内子任务有真实下载速度（真机 2.8MB/s 可见）+ 节流
            await eco.InstallAsync(project.Id, best, versionId, ProjectType.Mod,
                rep is null ? null : p => rep.Report(p.FileBytesDone, p.FileTotalBytes), c2);
            rep?.Complete();
            Debug.WriteLine($"[Loader] Fabric API {best.VersionNumber} 已装到 {versionId}/mods");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Loader] Fabric API 安装失败（不阻断）：{ex.Message}");
            // 8-19 生态修缮阶段3：吞异常 = 子任务 Completed → 失败清理链不触发，这里显式清残留
            // （保留「API 失败不阻断加载器安装」语义，LoaderServiceTests 已锁定）
            if (child is not null && destPath is not null) DownloadService.CleanupResiduals(destPath);
        }
    }

    /// <summary>预取 fabric-api 元数据到磁盘缓存（安装主链并行预热；失败静默——正式阶段仍会兜底直查）</summary>
    private async void PrefetchFabricApiAsync(string mcVersion)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var eco = new EcosystemService(_http, _downloads, _gameDirectory, cacheDir: _ecoCacheDir);
            var project = await eco.GetProjectAsync("fabric-api", timeout.Token);
            if (project is null) return;
            await eco.GetVersionsAsync(project.Id, mcVersion, "fabric", timeout.Token);
        }
        catch { /* 预取失败无妨——InstallFabricApiAsync 会再查（缓存未命中走原路径） */ }
    }

    /// <summary>Fabric/Quilt：拉 profile json（inheritsFrom 原版）→ 写版本目录 → 全量下载（链解析下载 client jar 与库）</summary>
    private async Task InstallMetaAsync(LoaderInstallPlan plan, DownloadProgressHandler? progress,
        DownloadGroupContext? ctx, CancellationToken ct)
    {
        RequireVanilla(plan.McVersion);

        // 组路径：配置为子任务（weight=0 不定进度），版本下载并入同一组
        if (ctx is not null)
        {
            VersionJson? version = null;
            Exception? metaError = null; // 8-22 全栈排查：子任务 Completion 永不抛 → meta 失败被吞成 NRE
            await ctx.AddChild($"加载器配置 {plan.Kind}", 0, async (p, c) =>
            {
                try
                {
                // AL40：文案明确——「正在安装加载器」让用户知道卡在加载器（meta 源国内慢），
                // 而非笼统的「排队等待」/「下载中」让人误以为 UI 卡死。
                // REVIEW-治本：ProgressReporter 统一上报（阶段文字即时可见——meta 拉取 2-26s
                // 期间用户看到明确文字而非「下载中」死寂）
                var rep = new ProgressReporter("正在拉取加载器信息…", p);
                var json = await FetchProfileJsonAsync(plan, c); // REVIEW-前摇：profile json 磁盘缓存（内容由 mc+loader 版本确定，永不变）
                version = JsonSerializer.Deserialize<VersionJson>(json)
                    ?? throw new InvalidDataException("加载器版本 JSON 解析失败");
                var id = VersionInstaller.SafeId(version.Id);
                _lastInstalledVersionId = id;
                var versionDir = Path.Combine(_gameDirectory, "versions", id);
                Directory.CreateDirectory(versionDir);
                await File.WriteAllTextAsync(Path.Combine(versionDir, $"{id}.json"), json, c);
                rep.ReportStage("加载器配置完成");
                rep.Complete();
                }
                catch (Exception ex) { metaError = ex; throw; }
            }).Completion;
            if (version is null)
                throw metaError ?? new InvalidOperationException($"加载器信息拉取失败（{plan.Kind} meta 获取失败或版本 JSON 解析失败）");

            await _downloads.DownloadVersionAsync(version!, ctx, null, ct);
            return;
        }

        progress?.Invoke(new DownloadProgress("查询加载器版本", null, 0, 0, 0));

        var json = await FetchProfileJsonAsync(plan, ct); // REVIEW-前摇：profile json 磁盘缓存
        var legacyVersion = JsonSerializer.Deserialize<VersionJson>(json)
            ?? throw new InvalidDataException("加载器版本 JSON 解析失败");
        var id = VersionInstaller.SafeId(legacyVersion.Id);
        _lastInstalledVersionId = id;
        var versionDir = Path.Combine(_gameDirectory, "versions", id);
        Directory.CreateDirectory(versionDir);
        await File.WriteAllTextAsync(Path.Combine(versionDir, $"{id}.json"), json, ct);

        progress?.Invoke(new DownloadProgress($"下载 {plan.Kind} 加载器与库文件", null, 0, 0, 0));
        await _downloads.DownloadVersionAsync(legacyVersion, null, progress, ct);
    }

    /// <summary>Forge/NeoForge：下载官方安装器 → 安装器进程 --installClient 写入版本目录</summary>
    private async Task InstallInstallerAsync(LoaderInstallPlan plan, DownloadProgressHandler? progress,
        DownloadGroupContext? ctx, CancellationToken ct)
    {
        RequireVanilla(plan.McVersion);

        // AL29 真机修复：Forge/NeoForge 官方安装器要求目标目录存在官方启动器的 launcher_profiles.json，
        // 否则直接中止（实测 "There is no minecraft launcher profile in ..."）。第三方启动器须预写 stub（HMCL/PCL 同法）。
        EnsureLauncherProfiles();

        var installerDir = Path.Combine(_gameDirectory, "installers");
        Directory.CreateDirectory(installerDir);
        var installerPath = Path.Combine(installerDir, Path.GetFileName(new Uri(plan.InstallerUrl!).LocalPath));

        // 组路径：安装器下载 + 运行各为一个子任务（运行阶段输出行实时进 Stage）
        if (ctx is not null)
        {
            await ctx.AddChild($"安装器 {Path.GetFileName(installerPath)}", plan.InstallerSize ?? 0, (p, c) =>
                _downloads.DownloadFileAsync(plan.InstallerUrl!, installerPath, plan.InstallerSha1, plan.InstallerSize, p, c)).Completion;

            var runChild = ctx.AddChild($"运行 {plan.Kind} 安装器", 0, async (p, c) =>
            {
                var java = JavaSelector.Pick(null);
                var exitCode = await _installerProcess(java,
                    ["-jar", installerPath, "--installClient", _gameDirectory],
                    line => p(new DownloadProgress(line, null, 0, 0, 0)), c);
                if (exitCode != 0)
                    throw new InvalidOperationException($"安装器执行失败（退出码 {exitCode}），请查看安装器输出");
            });
            await runChild.Completion;
            // AL29 真机教训：子任务 Failed 不抛（Completion 永不抛）——必须显式传播安装器失败原因，
            // 否则会继续走 FindNewestVersionDir+校验，把「安装器执行失败」误报成「缺 N 个文件」掩盖根因
            if (runChild.TerminalState == DownloadTaskState.Failed)
                throw new InvalidOperationException(runChild.Error ?? $"运行 {plan.Kind} 安装器失败");
            // 8-22 全栈排查：用户取消（Canceled）也必须停——继续 FindNewestVersionDir+Mark
            // 会把从未被本启动器安装的目录打上 .yanla-installed 标记（污染版本页）
            if (runChild.TerminalState == DownloadTaskState.Canceled)
                throw new OperationCanceledException(ct.IsCancellationRequested ? "安装已取消" : $"运行 {plan.Kind} 安装器被取消");

            // 安装器写出的版本目录名不确定 → 取安装后最新修改的版本目录
            _lastInstalledVersionId = FindNewestVersionDir();
            DeleteInstaller(installerPath); // 8-19 第二批：装完即删，不留 .minecraft/installers 残留
            return;
        }

        progress?.Invoke(new DownloadProgress("下载安装器", Path.GetFileName(installerPath), 0, 0, 0));
        await _downloads.DownloadFileAsync(plan.InstallerUrl!, installerPath, plan.InstallerSha1, plan.InstallerSize, progress, ct);

        progress?.Invoke(new DownloadProgress($"运行 {plan.Kind} 安装器", null, 0, 0, 0));
        var java = JavaSelector.Pick(null);
        var exitCode = await _installerProcess(java,
            ["-jar", installerPath, "--installClient", _gameDirectory],
            line => progress?.Invoke(new DownloadProgress(line, null, 0, 0, 0)), ct);
        if (exitCode != 0)
            throw new InvalidOperationException($"安装器执行失败（退出码 {exitCode}），请查看安装器输出");
        DeleteInstaller(installerPath); // 8-19 第二批：装完即删（旧路径同样清理）
    }

    /// <summary>8-19 第二批：安装器跑完即删——此前 installers 目录只增不减（每次装 Forge/NeoForge 留一个 jar）</summary>
    private static void DeleteInstaller(string installerPath)
    {
        try { File.Delete(installerPath); } catch { /* 删除失败不阻塞安装结果 */ }
    }

    /// <summary>官方安装器要求 launcher_profiles.json（否则 "no minecraft launcher profile" 中止）——
    /// 只补缺失的，不覆盖已有文件（可能是官方启动器的真实配置）。</summary>
    private void EnsureLauncherProfiles()
    {
        var path = Path.Combine(_gameDirectory, "launcher_profiles.json");
        if (File.Exists(path)) return;
        File.WriteAllText(path,
            """{"clientToken":"","launcherVersion":1,"profiles":{},"settings":{},"selectedProfile":null}""");
    }

    /// <summary>REVIEW-前摇：加载器 profile json 磁盘缓存——内容由 (kind, mc, loaderVersion) 三元组完全确定，
    /// 永不变更，网络拉取（meta 源国内 2-26s）纯属浪费。命中秒开，未命中拉取后落盘。</summary>
    private async Task<string> FetchProfileJsonAsync(LoaderInstallPlan plan, CancellationToken ct)
    {
        var cacheDir = _loaderProfileCacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Launcher", "cache", "loader-profiles");
        var cachePath = Path.Combine(cacheDir, $"{plan.Kind}-{plan.McVersion}-{plan.LoaderVersion}.json");
        if (File.Exists(cachePath))
        {
            try { return await File.ReadAllTextAsync(cachePath, ct); }
            catch { /* 损坏则重拉 */ }
        }
        var json = await _http.GetStringAsync(plan.ProfileJsonUrl!, ct);
        try
        {
            Directory.CreateDirectory(cacheDir);
            await File.WriteAllTextAsync(cachePath, json, ct);
        }
        catch { /* 缓存失败不影响安装 */ }
        return json;
    }

    /// <summary>AL29 H3/H6 补位：安装完成 != 文件完整——安装器内部下载（Forge/NeoForge）与
    /// 下载阶段静默跳过（Fabric/Quilt）都沿版本 json 全量校验，缺失如实报错（与 VersionInstaller 同口径）。</summary>
    private async Task VerifyInstalledVersionAsync(string versionId)
    {
        var jsonPath = Path.Combine(_gameDirectory, "versions", versionId, $"{versionId}.json");
        if (!File.Exists(jsonPath))
            throw new InvalidOperationException($"版本 {versionId} 安装完成但版本 json 缺失");
        VersionJson version;
        try { version = JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(jsonPath))!; }
        catch { throw new InvalidOperationException($"版本 {versionId} 安装完成但版本 json 解析失败"); }
        var report = await AutoRepairService.VerifyVersionAsync(version, _gameDirectory);
        if (!report.IsComplete)
            throw new InvalidOperationException(
                $"安装完成但文件不完整：缺 {report.Missing} 个文件（首例：{report.MissingFiles[0]}）。可重新下载补全");
    }

    /// <summary>安装器写出的版本目录名不确定 → 取最近修改的版本目录（带 {id}.json）。
    /// AL29 Test 4 实证：目录 mtime 在 NTFS 有缓存延迟（刚写入会排错序）→ 改按目录内 {id}.json 的文件 mtime 排序。
    /// REVIEW-flake：mtime 精确并列时（密集写入同刻落盘，真机/测试均出现过）稳定排序按枚举顺序取
    /// 「1.21.10」父版本 → 校验/标记打在原版目录 → forge 版本页不显示已装。并列时优先带 inheritsFrom
    /// 的 json——安装器（Forge/NeoForge）产出物必有该字段，原版 json 没有——确定性选对目标。</summary>
    private string? FindNewestVersionDir()
    {
        var versionsDir = Path.Combine(_gameDirectory, "versions");
        if (!Directory.Exists(versionsDir)) return null;
        return Directory.EnumerateDirectories(versionsDir)
            .Where(d => File.Exists(Path.Combine(d, $"{Path.GetFileName(d)}.json")))
            .OrderByDescending(d => File.GetLastWriteTime(Path.Combine(d, $"{Path.GetFileName(d)}.json")))
            .ThenByDescending(d => JsonInheritsFrom(Path.Combine(d, $"{Path.GetFileName(d)}.json")) is not null)
            .Select(Path.GetFileName)
            .FirstOrDefault();
    }

    /// <summary>版本 json 的 inheritsFrom 字段（null = 原版/独立版本——非安装器产出物）</summary>
    private static string? JsonInheritsFrom(string jsonPath)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
            return doc.RootElement.TryGetProperty("inheritsFrom", out var v) ? v.GetString() : null;
        }
        catch { return null; } // 解析失败视为无继承（校验环节会如实报错）
    }

    /// <summary>加载器版本 JSON 通过 inheritsFrom 继承原版，父版本必须已安装</summary>
    private void RequireVanilla(string mcVersion)
    {
        var jsonPath = Path.Combine(_gameDirectory, "versions", mcVersion, $"{mcVersion}.json");
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException($"请先在版本页安装原版 {mcVersion}，再安装加载器");
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        var json = await _http.GetStringAsync(url, ct);
        return JsonSerializer.Deserialize<T>(json);
    }

    /// <summary>数字感知版本比较（21.1.110 &gt; 21.1.99；-beta 后缀靠 IsStable 排序）</summary>
    private sealed class VersionComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            var xp = x!.Split(['.', '-']);
            var yp = y!.Split(['.', '-']);
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
    }
}
