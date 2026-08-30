using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.Core.Diagnostics;
using Launcher.Core.Model.Loader;
using Launcher.Core.Model.Modrinth;
using Launcher.Core.Model.Mojang;
using Launcher.Core.Ecosystem;
using Launcher.Core.Services;
using Launcher.Core.Utils;

namespace Launcher.Core.Download;

/// <summary>整合包导入报告（完成 Toast 用：已装/跳过/警告）</summary>
public sealed record ModpackImportReport(
    string PackId, int ModsInstalled, int ModsSkipped,
    IReadOnlyList<(string Name, string Reason)> Skipped, string? Warning);

/// <summary>
/// 整合包安装编排（AL47）：CF zip / mrpack → ① 基座安装（生成可启动 version json + 全量下载）
/// ② 内容安装（mrpack 在线下 mods / CF 解压 + API 兜底）→ InstallMarker。
/// 走全局下载中心组任务（ctx.AddChild 每文件一子任务，进度/暂停/重试现成）。
/// </summary>
public sealed class ModpackInstaller
{
    private readonly HttpClient _http;
    private readonly DownloadService _downloads;
    private readonly string _gameDirectory;
    private readonly string? _cfApiBase; // 测试传官方；生产 null（默认官方直连）
    private readonly string? _cfApiKey; // 显式 key 覆盖（null = 动态读设置/环境变量；"" = 显式禁用——测试隔离用）
    private readonly string? _manifestCacheDir; // 测试注入临时缓存目录（隔离真实 AppData 清单缓存）

    public ModpackInstaller(HttpClient? http = null, DownloadService? downloads = null,
        string? gameDirectory = null, string? curseForgeApiBase = null, string? manifestCacheDir = null,
        string? curseForgeApiKey = null)
    {
        _http = http ?? HttpClientPool.CreateSharedClient();
        _downloads = downloads ?? new DownloadService();
        _gameDirectory = gameDirectory ?? GameDirectory.Detect();
        _cfApiBase = curseForgeApiBase;
        _cfApiKey = curseForgeApiKey;
        _manifestCacheDir = manifestCacheDir;
    }

    /// <summary>导入整合包（解析 → 基座 → 内容 → 报告）。zip 需为可识别格式。</summary>
    public async Task<ModpackImportReport> ImportAsync(
        string zipPath, string gameDir, DownloadGroupContext ctx, CancellationToken ct)
    {
        var info = ModpackImporter.Parse(zipPath, out var reason)
            ?? throw new InvalidDataException(reason ?? "不支持的整合包格式");

        var packId = await EnsureInstanceBaseAsync(info, ctx, ct);
        var (installed, skipped) = await InstallContentAsync(info, zipPath, packId, ctx, ct);
        return new ModpackImportReport(packId, installed, skipped.Count, skipped, null);
    }

    // ---------- ① 基座安装 ----------

    /// <summary>生成可启动版本 json + 全量下载（client jar/libraries）；返回实例 id（重名消解后）</summary>
    private async Task<string> EnsureInstanceBaseAsync(ModpackImportInfo info, DownloadGroupContext ctx, CancellationToken ct)
    {
        var mc = ResolveMcVersion(info.McVersion)
            ?? throw new InvalidDataException($"无法解析整合包的 Minecraft 版本: {info.McVersion}");
        var kind = ParseLoaderKind(info.Loader);
        var packId = ModpackImporter.ResolvePackId(_gameDirectory, info.VersionId, mc);

        // 父版本 json 预取（versions/{mc}/{mc}.json，打 .prefetched 自动隐藏）
        var manifest = new VersionManifestService(_http, _gameDirectory, _manifestCacheDir);
        var parentUrl = await manifest.GetVersionJsonUrlAsync(mc, ct)
            ?? throw new InvalidOperationException($"清单中未找到 Minecraft {mc}（版本过旧或拼写有误）");
        var installer = new VersionInstaller(_http, _downloads, _gameDirectory, allowForeignMarkers: true);
        await installer.GetOrFetchVersionJsonAsync(mc, parentUrl, ct);

        switch (kind)
        {
            case null:
                return await CopyVersionAsync(mc, packId, ctx, ct);
            case LoaderKind.Fabric or LoaderKind.Quilt:
                return await InstallMetaProfileAsync(kind.Value, mc, info.LoaderVersion, packId, ctx, ct);
            default: // Forge / NeoForge：安装器进程生成实例目录，装完改名
                return await InstallViaInstallerAsync(kind.Value, mc, info.LoaderVersion, packId, ctx, ct);
        }
    }

    /// <summary>无加载器：父版本 json 重写 id → 全量下载（client jar 相同则 SHA1 幂等秒跳）</summary>
    private async Task<string> CopyVersionAsync(string fromId, string toId, DownloadGroupContext ctx, CancellationToken ct)
    {
        var installer = new VersionInstaller(_http, _downloads, _gameDirectory, allowForeignMarkers: true);
        var json = await installer.GetOrFetchVersionJsonAsync(fromId, null, ct);
        var rewritten = json with { Id = toId };
        var dir = Path.Combine(_gameDirectory, "versions", toId);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, $"{toId}.json"), JsonSerializer.Serialize(rewritten), ct);
        await installer.InstallAsync(rewritten, ctx, ct);
        return toId;
    }

    /// <summary>Fabric/Quilt：meta profile json 重写 id → 全量下载</summary>
    private async Task<string> InstallMetaProfileAsync(LoaderKind kind, string mc, string? declaredVersion,
        string packId, DownloadGroupContext ctx, CancellationToken ct)
    {
        var loader = new LoaderService(_http, _downloads, _gameDirectory);
        var installer = new VersionInstaller(_http, _downloads, _gameDirectory, allowForeignMarkers: true);
        var plan = await CreatePlanWithFallbackAsync(loader, kind, mc, declaredVersion, ct);
        var jsonStr = await _http.GetStringAsync(plan.ProfileJsonUrl!, ct);
        var profile = JsonSerializer.Deserialize<VersionJson>(jsonStr)
            ?? throw new InvalidDataException($"加载器 profile json 解析失败: {kind}");
        var rewritten = profile with { Id = packId };
        var dir = Path.Combine(_gameDirectory, "versions", packId);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, $"{packId}.json"), JsonSerializer.Serialize(rewritten), ct);
        await installer.InstallAsync(rewritten, ctx, ct);
        return packId;
    }

    /// <summary>Forge/NeoForge：安装器进程建实例 → 改名为 packId（安装前已存在同 id → 复制）</summary>
    private async Task<string> InstallViaInstallerAsync(LoaderKind kind, string mc, string? declaredVersion,
        string packId, DownloadGroupContext ctx, CancellationToken ct)
    {
        var loader = new LoaderService(_http, _downloads, _gameDirectory);
        var before = SnapshotVersionDirs(_gameDirectory);
        var plan = await CreatePlanWithFallbackAsync(loader, kind, mc, declaredVersion, ct);
        await loader.InstallAsync(plan, ctx, ct);
        var installedId = loader.LastInstalledVersionId
            ?? throw new InvalidOperationException($"加载器安装器未报告版本 id: {kind}");
        if (before.Contains(installedId))
            return await CopyVersionAsync(installedId, packId, ctx, ct);
        return RenameVersion(installedId, packId);
    }

    /// <summary>声明版本优先，meta 404 → 最新稳定重试一次；仍失败明确报错（不静默降级装半成品）</summary>
    private static async Task<LoaderInstallPlan> CreatePlanWithFallbackAsync(
        LoaderService loader, LoaderKind kind, string mc, string? declaredVersion, CancellationToken ct)
    {
        try
        {
            if (declaredVersion is not null)
                return await loader.CreatePlanAsync(kind, mc, declaredVersion, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // meta 404 / 安装器不存在 → 回退最新稳定
        }
        return await loader.CreatePlanAsync(kind, mc, null, ct);
    }

    /// <summary>改名已安装的加载器实例（目录移动 + json id 重写 + jar 重命名；.yanla-installed 随目录走）</summary>
    private string RenameVersion(string fromId, string toId)
    {
        var from = Path.Combine(_gameDirectory, "versions", fromId);
        var to = Path.Combine(_gameDirectory, "versions", toId);
        if (!Directory.Exists(from))
            throw new InvalidOperationException($"加载器实例目录缺失: {fromId}");
        Directory.Move(from, to);
        var jsonPath = Path.Combine(to, $"{fromId}.json");
        if (File.Exists(jsonPath))
        {
            var json = JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(jsonPath));
            if (json is not null)
                File.WriteAllText(Path.Combine(to, $"{toId}.json"), JsonSerializer.Serialize(json with { Id = toId }));
            File.Delete(jsonPath);
        }
        var jarPath = Path.Combine(to, $"{fromId}.jar");
        if (File.Exists(jarPath)) File.Move(jarPath, Path.Combine(to, $"{toId}.jar"));
        return toId;
    }

    private static HashSet<string> SnapshotVersionDirs(string gameDir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var vd = Path.Combine(gameDir, "versions");
        if (Directory.Exists(vd))
            foreach (var d in Directory.EnumerateDirectories(vd))
                set.Add(Path.GetFileName(d));
        return set;
    }

    /// <summary>mrpack 版本范围（&gt;=1.21）取首个版本号；取不到 null（调用方明确报错）</summary>
    private static string? ResolveMcVersion(string raw)
    {
        var m = Regex.Match(raw ?? "", @"\d+\.\d+(\.\d+)?");
        return m.Success ? m.Value : null;
    }

    private static LoaderKind? ParseLoaderKind(string? loader) => loader?.ToLowerInvariant() switch
    {
        "fabric" => LoaderKind.Fabric,
        "quilt" => LoaderKind.Quilt,
        "forge" => LoaderKind.Forge,
        "neoforge" => LoaderKind.NeoForge,
        _ => null,
    };

    // ---------- ② 内容安装 ----------

    private async Task<(int Installed, List<(string Name, string Reason)> Skipped)> InstallContentAsync(
        ModpackImportInfo info, string zipPath, string packId, DownloadGroupContext ctx, CancellationToken ct)
        => info.Format switch
        {
            ModpackFormat.Modrinth => await InstallMrpackAsync(info, zipPath, packId, ctx, ct),
            ModpackFormat.CurseForge => await InstallCurseForgeAsync(info, zipPath, packId, ctx, ct),
            // REVIEW-C：自家 ZIP 格式——旧代码返回 (0, []) 内容永不落盘（注释声称「Import 已解压」但
            // 该流程从未调用 Import，导出整合包 (ZIP) → 再导入只装出基座、mods/config/saves 全丢还报成功）。
            // 补上解压：内容进实例目录（zip 内不含 versions/，不会覆盖基座 json）+ 打安装标记。
            _ => ImportOwnContent(info, zipPath, gameDir: _gameDirectory, packId, ct),
        };

    private static (int, List<(string, string)>) ImportOwnContent(
        ModpackImportInfo info, string zipPath, string gameDir, string packId, CancellationToken ct)
    {
        ModpackImporter.Import(zipPath, gameDir, ct, packId);
        return (info.FileCount, []); // FileCount = zip 内文件数（确认框展示口径）
    }

    /// <summary>mrpack：files[] 直链下载（并发门 8）+ overrides 去前缀解压</summary>
    private async Task<(int Installed, List<(string, string)> Skipped)> InstallMrpackAsync(
        ModpackImportInfo info, string zipPath, string packId, DownloadGroupContext ctx, CancellationToken ct)
    {
        var installed = 0;
        var skipped = new List<(string Name, string Reason)>();
        var versionDir = Path.Combine(_gameDirectory, "versions", packId);
        // 并发门跟随设置档位（设置页可调；clamp 4-16 防极端值）
        using var gate = new SemaphoreSlim(
            Math.Clamp(DownloadOptions.FromSettings(LauncherSettings.Current).LibraryConcurrency, 4, 16));
        var tasks = new List<Task>();
        foreach (var f in info.MrpackFiles ?? [])
        {
            if (f.ClientUnsupported) { skipped.Add((f.Path, "仅服务端环境")); continue; }
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    // REVIEW-C：mrpack 导出 files.downloads 为空 → 自导自入全跳过断链——
                    // 无直链时按 sha1 反查 Modrinth 补 URL（PCL 等导出的过期直链同样兜底）
                    var url = f.Url;
                    if (string.IsNullOrEmpty(url) && f.Sha1 is not null)
                        url = await ResolveUrlBySha1Async(f.Sha1, ct);
                    if (string.IsNullOrEmpty(url)) { lock (skipped) skipped.Add((f.Path, "无下载地址")); return; }
                    // REVIEW-C：files[].path 路径穿越——与 ExtractZipEntries 同款包含性防护
                    // （恶意 mrpack 可 ../ 写任意位置，覆盖 options.txt/版本 json 等）
                    var target = Path.GetFullPath(Path.Combine(versionDir, f.Path));
                    if (!target.StartsWith(versionDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        lock (skipped) skipped.Add((f.Path, "非法路径"));
                        return;
                    }
                    // 8-22 全栈排查：子任务 Completion 永不抛——失败被计为「已装」（导入报告全绿但缺模组）
                    // 8-19 生态修缮阶段3：targetPath 传入——失败/取消自动清 .parts 中间产物
                    var child = ctx.AddChild($"模组 {Path.GetFileName(f.Path)}", f.Size, async (p, c) =>
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        await _downloads.DownloadFileAsync(url, target, f.Sha1, f.Size, p, c);
                        // 8-31 自动信任：启动器自己装的 mod 记哈希（预检不再标未校验）；mrpack 文件装进 mods/ 才记
                        if (target.StartsWith(Path.Combine(versionDir, "mods") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            && target.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                            ModHashManifest.Record(Path.Combine(versionDir, "mods"), Path.GetFileName(target), f.Sha1, null, "mrpack");
                    }, target);
                    await child.Completion.WaitAsync(ct);
                    if (child.TerminalState == DownloadTaskState.Failed)
                        lock (skipped) skipped.Add((f.Path, child.Error ?? "下载失败"));
                    else
                        Interlocked.Increment(ref installed);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex) { lock (skipped) skipped.Add((f.Path, ex.Message)); }
                finally { gate.Release(); }
            }, ct));
        }
        await Task.WhenAll(tasks);
        // overrides 解压（条目去前缀）；8-31 修「整合包假大小」：带 zip 大小权重 + 报进度
        var overridesBytes = new FileInfo(zipPath).Length;
        await ctx.AddChild("解压 overrides", overridesBytes, async (p, c) =>
        {
            using var zip = ZipFile.OpenRead(zipPath);
            ModpackImporter.ExtractZipEntries(zip, versionDir, _ => true, rel =>
                rel.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)
                    ? rel["overrides/".Length..] : null, c,
                onBytes: b => p(new DownloadProgress("解压 overrides", null, b, overridesBytes, 0)));
        }).Completion.WaitAsync(ct);

        // 8-26 修「整合包不下前置」：mrpack dependencies[] 里非 minecraft/loader 的模组前置
        // （fabric-api 等，作者没塞进 files[] 的）→ 按版本 id 直装到 mods/。单条失败只计跳过。
        foreach (var dep in info.ModDependencies ?? [])
        {
            try
            {
                var json = await _http.GetStringAsync(
                    $"{EcosystemService.ApiBase}/version/{Uri.EscapeDataString(dep.VersionId)}", ct);
                var v = JsonSerializer.Deserialize<ModrinthVersion>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                var primary = v?.Files?.FirstOrDefault(f => f.Primary) ?? v?.Files?.FirstOrDefault();
                if (v is null || primary is null) { lock (skipped) skipped.Add((dep.ProjectKey, "版本解析失败")); continue; }
                var modsDir = Path.Combine(versionDir, "mods");
                Directory.CreateDirectory(modsDir);
                var target = Path.Combine(modsDir, Path.GetFileName(primary.FileName));
                var child = ctx.AddChild($"前置 {dep.ProjectKey}", primary.Size, async (p, c) =>
                {
                    await _downloads.DownloadFileAsync(primary.Url, target, primary.Hashes?.Sha1, primary.Size, p, c);
                    // 8-31 自动信任：启动器自己装的 mod 记哈希
                    ModHashManifest.Record(modsDir, Path.GetFileName(target), primary.Hashes?.Sha1, primary.Hashes?.Sha512, "mrpack-dep");
                }, target);
                await child.Completion.WaitAsync(ct);
                if (child.TerminalState == DownloadTaskState.Failed)
                    lock (skipped) skipped.Add((dep.ProjectKey, child.Error ?? "下载失败"));
                else
                    Interlocked.Increment(ref installed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { lock (skipped) skipped.Add((dep.ProjectKey, ex.Message)); }
        }
        return (installed, skipped);
    }

    /// <summary>REVIEW-C：mrpack 文件 sha1 → Modrinth 下载直链反查（自导自入断链兜底——
    /// 自己导出的 mrpack files.downloads 为空，导入时无 URL 的文件按 sha1 反查补直链）。
    /// 8s 单条超时，失败返回 null（调用方跳过该文件）；仅无直链时调用。</summary>
    private async Task<string?> ResolveUrlBySha1Async(string sha1, CancellationToken ct)
    {
        try
        {
            // 走注入的 _http（测试可路由 stub；生产用共享连接池）；8s 单条超时防慢源拖死导入
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var json = await _http.GetStringAsync(
                $"https://api.modrinth.com/v2/version_file/{Uri.EscapeDataString(sha1)}?algorithm=sha1", cts.Token);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0
                && files[0].TryGetProperty("url", out var url))
                return url.GetString();
        }
        catch { /* 反查失败 → null，跳过该文件 */ }
        return null;
    }

    /// <summary>CF zip：单子任务解压（跳过清单；overrides/clientoverrides 去前缀）；mods 下 0 jar 且清单非空 → API 兜底</summary>
    private async Task<(int Installed, List<(string, string)> Skipped)> InstallCurseForgeAsync(
        ModpackImportInfo info, string zipPath, string packId, DownloadGroupContext ctx, CancellationToken ct)
    {
        var installed = 0;
        var skipped = new List<(string Name, string Reason)>();
        var versionDir = Path.Combine(_gameDirectory, "versions", packId);
        var jarCount = 0;
        var extractedJars = new List<string>(); // 8-31 自动信任：记录 CF zip 里落进 mods/ 的 jar（同步解压，无需锁）
        // 8-31 修「整合包假大小」：解压子任务带 zip 真实大小权重 + 逐条报进度——
        // 之前 weight=0 且不报进度，550MB 的 zip 解压对总数贡献 0，UI 只显示基座 ~8MB
        var zipBytes = new FileInfo(zipPath).Length;
        await ctx.AddChild("解压整合包", zipBytes, async (p, c) =>
        {
            using var zip = ZipFile.OpenRead(zipPath);
            ModpackImporter.ExtractZipEntries(zip, versionDir, rel =>
            {
                if (rel.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref jarCount);
                if (rel.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) && rel.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                    extractedJars.Add(rel);
                return rel != "manifest.json" && rel != "modlist.html";
            }, rel =>
            {
                if (rel.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)) return rel["overrides/".Length..];
                if (rel.StartsWith("clientoverrides/", StringComparison.OrdinalIgnoreCase)) return rel["clientoverrides/".Length..];
                return rel;
            }, c,
            onBytes: b => p(new DownloadProgress("解压整合包", null, b, zipBytes, 0)));
        }).Completion.WaitAsync(ct);
        // 8-31 自动信任：CF zip 解压装的 mod 记哈希（官方 sha1 缺失 → 自动算本地基线）
        var cfModsDir = Path.Combine(versionDir, "mods");
        foreach (var jar in extractedJars)
            ModHashManifest.Record(cfModsDir, Path.GetFileName(jar), null, null, "curseforge-zip");

        // 兜底：zip 缺 jar 实体（仅清单）→ 按 projectID/fileID 从 CF API 下载（顺序执行避限流）
        if (info.CurseForgeFiles is { Count: > 0 } files && jarCount == 0)
        {
            var cf = new CurseForgeService(_cfApiKey, _http, _downloads, _gameDirectory, apiBase: _cfApiBase);
            if (!cf.IsEnabled)
                throw new InvalidOperationException("此 zip 内缺少模组文件且未配置 CurseForge API Key，无法完整导入");
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                var file = await cf.GetFileAsync(f.ProjectId, f.FileId, ct);
                if (file is null) { skipped.Add(($"mods/{f.ProjectId}-{f.FileId}.jar", "CF 文件不存在")); continue; }
                try
                {
                    await ctx.AddChild($"模组 {file.fileName}", file.fileLength, async (p, c) =>
                    {
                        await cf.InstallAsync(f.ProjectId, file, packId, ProjectType.Mod, p, c);
                    }, Path.Combine(
                        EcosystemService.ResolveInstallPath(_gameDirectory, packId, ProjectType.Mod), file.fileName))
                        .Completion.WaitAsync(ct);
                    installed++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { skipped.Add((file.fileName, ex.Message)); }
            }
        }
        return (installed, skipped);
    }
}
