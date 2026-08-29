using System.Net.Http;
using System.Text.Json;
using Launcher.Core.Model.Mojang;
using Launcher.Core.Utils;
using Launcher.Core.Download;

namespace Launcher.Core.Services;

/// <summary>
/// 版本清单服务：拉取 Mojang 官方 manifest、磁盘缓存、合并本地已安装版本。
/// </summary>
public sealed class VersionManifestService
{
    /// <summary>Mojang 版本清单</summary>
    public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    /// <summary>BMCLAPI 清单镜像（2026-08-08 实测：200 + 273,470 字节完整清单 + 0.5s；GET 302 跳 CDN，HttpClient 自动跟随）</summary>
    public const string ManifestMirrorUrl = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";

    /// <summary>清单候选链（依序尝试，首个成功者胜出）：官方 piston-meta → BMCLAPI 镜像</summary>
    public static readonly string[] ManifestUrls = [ManifestUrl, ManifestMirrorUrl];

    /// <summary>逐候选拉取清单 JSON：官方失败自动换镜像；用户取消照常传播；全失败抛 HttpRequestException</summary>
    public static async Task<string> FetchManifestJsonAsync(HttpClient http, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var url in ManifestUrls)
        {
            try
            {
                return await http.GetStringAsync(url, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                last = ex;
            }
        }
        throw new HttpRequestException($"版本清单拉取失败（{ManifestUrls.Length} 个源均不可用）", last);
    }

    private readonly HttpClient _http;
    private readonly string _cacheDirectory;

    /// <summary>解析后的版本条目（已安装标记 + 官方清单合并）</summary>
    public IReadOnlyList<GameVersionEntry> Entries => _entries;
    private List<GameVersionEntry> _entries = [];

    public VersionManifestService(HttpClient? http = null, string? gameDirectory = null, string? cacheDirectory = null)
    {
        // 清单是元数据小请求：15s 总超时（国内直连官方清单慢/失败时快速失败，不卡修复/刷新）
        _http = http ?? HttpClientPool.CreateSharedClient(TimeSpan.FromSeconds(15));
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Launcher.Core.Utils.AppPaths.DataRoot, "cache");
    }

    /// <summary>
    /// 拉取并合并版本清单。force=true 时忽略磁盘缓存强制刷新。
    /// 已安装判定跨所有扫描源（自建目录 + PCL/官方等已有环境），条目记录版本所在目录。
    /// </summary>
    /// <summary>按版本 id 查清单 URL（复用 24h 缓存清单；查不到返回 null）——整合包导入预取父版本 json 用</summary>
    public async Task<string?> GetVersionJsonUrlAsync(string versionId, CancellationToken ct = default)
    {
        var manifest = await LoadManifestAsync(false, ct);
        return manifest.Versions.FirstOrDefault(v => v.Id == versionId)?.Url;
    }

    public async Task RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        var manifest = await LoadManifestAsync(force, ct);
        var installed = ScanUsableInstances(GameDirectory.ScanSourceDirs().Select(x => x.Dir), cleanForeignMarkers: true);
        _entries = manifest.Versions
            .Select(v => new GameVersionEntry(
                v.Id, v.Type, installed.TryGetValue(v.Id, out var dir), v.ReleaseTime, v.Url,
                installed.TryGetValue(v.Id, out var gd) ? gd : ""))
            .OrderByDescending(v => v.ReleaseTime)
            .ToList();
    }

    private async Task<VersionManifest> LoadManifestAsync(bool force, CancellationToken ct)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var cachePath = Path.Combine(_cacheDirectory, "version_manifest_v2.json");

        // TTL 24h：缓存超期后强制重新拉取（否则新发布版本永远不可见）
        if (!force && File.Exists(cachePath))
        {
            try
            {
                var info = new FileInfo(cachePath);
                if (DateTime.UtcNow - info.LastWriteTimeUtc < TimeSpan.FromHours(24))
                {
                    var cached = JsonSerializer.Deserialize<VersionManifest>(await File.ReadAllTextAsync(cachePath, ct));
                    if (cached is not null && cached.Versions.Count > 0) return cached;
                }
            }
            catch (Exception) { /* 缓存损坏则重新拉取 */ }
        }

        var json = await FetchManifestJsonAsync(_http, ct);
        await File.WriteAllTextAsync(cachePath, json, ct);
        return JsonSerializer.Deserialize<VersionManifest>(json)!;
    }

    /// <summary>磁盘重扫，就地更新 Installed 标记与所在目录（版本/加载器安装完成后调用）</summary>
    public void RescanInstalled()
    {
        var installed = ScanUsableInstances(GameDirectory.ScanSourceDirs().Select(x => x.Dir), cleanForeignMarkers: true);
        _entries = _entries.Select(e => e with
        {
            Installed = installed.TryGetValue(e.Id, out var dir),
            GameDirectory = installed.TryGetValue(e.Id, out var gd) ? gd : "",
        }).ToList();
    }

    /// <summary>
    /// 8-14 可用实例扫描（id → 所在目录；json 存在 + client jar 三路可用）：
    /// ① 自身目录 jar；② inheritsFrom 父版本目录 jar；③ 引用本版本的已装子版本目录 jar。
    /// 与版本页行徽章（VersionScan.HasUsableClientJar）同口径。此前用严格 json+jar 判定，
    /// 原版 26.2（jar 落 fabric 子目录）被漏标——真机：下载 26.2+fabric 后版本页侧栏 26.2 不亮。
    /// 只有 json 且无任何可用 jar 的预取残件不计入（AL29 C1 的防谎报语义保留）。
    /// cleanForeignMarkers：非自建目录（PCL/官方扫描源）的标记是历史误打，顺带移除。
    /// </summary>
    public static Dictionary<string, string> ScanUsableInstances(IEnumerable<string> dirs, bool cleanForeignMarkers)
    {
        var candidates = new List<(string Dir, string Id)>();
        foreach (var dir in dirs)
        {
            var versionsDir = Path.Combine(dir, "versions");
            if (!Directory.Exists(versionsDir)) continue;
            foreach (var d in Directory.EnumerateDirectories(versionsDir))
            {
                var id = Path.GetFileName(d);
                if (cleanForeignMarkers && !GameDirectory.IsOwnInstallDir(dir))
                {
                    InstallMarker.Unmark(dir, id);
                    InstallMarker.UnmarkPrefetched(dir, id);
                }
                if (File.Exists(Path.Combine(d, $"{id}.json")))
                    candidates.Add((dir, id));
            }
        }
        var children = BuildChildrenMap(candidates);
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, id) in candidates)
            if (HasUsableClientJar(dir, id, ParentOf(dir, id), children))
                installed.TryAdd(id, dir);
        return installed;
    }

    /// <summary>读版本 json 的 inheritsFrom（父版本 id；缺失/损坏 → null）</summary>
    private static string? ParentOf(string gameDir, string id)
    {
        try
        {
            var json = Path.Combine(gameDir, "versions", id, $"{id}.json");
            if (!File.Exists(json)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            return doc.RootElement.TryGetProperty("inheritsFrom", out var p) ? p.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 合并已装候选：manifest 已装原版（Installed + ShouldShowInPage）+ 目录扫描补漏
    /// （加载器 + 三路 jar 判定，ScanUsableInstances 同口径）。manifest 条目可为空集
    /// （网络失败时传入空）——磁盘扫描兜底，调用方无需区分「manifest 失败」与「manifest 无命中」。
    /// 8-23 主页版本消失修复的核心：manifest 拉取失败不再整体吞掉重建，磁盘结果始终兜底。
    /// </summary>
    public static List<(string Dir, string Id)> CollectInstalledCandidates(
        IEnumerable<GameVersionEntry> manifestEntries,
        IEnumerable<string> scanDirs,
        bool cleanForeignMarkers)
    {
        var candidates = new List<(string Dir, string Id)>();
        foreach (var e in manifestEntries.Where(e => e.Installed && InstallMarker.ShouldShowInPage(e.GameDirectory, e.Id)))
            candidates.Add((e.GameDirectory, e.Id));
        foreach (var (id, dir) in ScanUsableInstances(scanDirs, cleanForeignMarkers))
        {
            if (candidates.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
            if (!InstallMarker.ShouldShowInPage(dir, id)) continue;
            candidates.Add((dir, id));
        }
        return candidates;
    }

    /// <summary>父版本 id → 引用它的子版本清单（跨目录；全量扫描一次，各处复用）</summary>
    public static Dictionary<string, List<(string ChildId, string ChildDir)>> BuildChildrenMap(
        IEnumerable<(string Dir, string Id)> candidates)
    {
        var map = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, id) in candidates)
        {
            var parent = ParentOf(dir, id);
            if (parent is null) continue;
            if (!map.TryGetValue(parent, out var list))
                map[parent] = list = [];
            list.Add((id, dir));
        }
        return map;
    }

    /// <summary>
    /// client jar 三路可用判定（与 VersionScan.HasUsableClientJar 同口径——App 侧委托本方法，勿各写一份）：
    /// ① 自身目录 {id}.jar；② inheritsFrom 父版本目录 jar（官方 Forge 安装器落父目录）；
    /// ③ 引用本版本的已装子版本目录 jar（Lattice 下载 jar 落加载器子目录）。
    /// </summary>
    public static bool HasUsableClientJar(string gameDir, string id, string? parent,
        IReadOnlyDictionary<string, List<(string ChildId, string ChildDir)>> childrenByParent)
    {
        if (File.Exists(Path.Combine(gameDir, "versions", id, $"{id}.jar"))) return true;
        if (!string.IsNullOrEmpty(parent)
            && File.Exists(Path.Combine(gameDir, "versions", parent, $"{parent}.jar")))
            return true;
        if (childrenByParent.TryGetValue(id, out var kids)
            && kids.Any(k => File.Exists(Path.Combine(k.ChildDir, "versions", k.ChildId, $"{k.ChildId}.jar"))))
            return true;
        return false;
    }

    /// <summary>
    /// 严格双文件判定（json + 同目录 jar）：低层谓词保留——上层「已装集合」已改用
    /// ScanUsableInstances 的三路口径，本方法不再充当权威判定。
    /// </summary>
    public static bool IsInstalled(string gameDir, string id)
        => File.Exists(Path.Combine(gameDir, "versions", id, $"{id}.json"))
        && File.Exists(Path.Combine(gameDir, "versions", id, $"{id}.jar"));

    /// <summary>
    /// 实例判定（MOD 安装目标）：json 存在即可——26.2 这类 Fabric 父版本的 client jar 沿
    /// inheritsFrom 链落加载器子目录，双文件同目录判定会漏掉（版本页已是 json-only 口径）。
    /// 预取残留（.prefetched 且未正式安装）排除——半成品目录不算实例。
    /// 注意：IsInstalled（json+jar）保持不动——仍是版本页 Installed 标记的权威口径。
    /// </summary>
    public static bool IsInstanceTarget(string gameDir, string id)
        => File.Exists(Path.Combine(gameDir, "versions", id, $"{id}.json"))
        && InstallMarker.ShouldShowInPage(gameDir, id);

    /// <summary>合并后的条目（含已安装标记 + 所在目录，未安装为 ""）</summary>
    public sealed record GameVersionEntry(
        string Id,
        string Type,
        bool Installed,
        DateTime ReleaseTime,
        string? ManifestUrl,
        string GameDirectory);

    /// <summary>
    /// 生态页版本筛选候选：release（非愚人节）且 &gt;= minVersion，语义降序（26.2 排最上、1.21.10 &gt; 1.21.6）。
    /// 下限 1.16：更老版本的 mod 生态早已沉寂，全列只会让下拉臃肿。纯离线（测试友好）。
    /// </summary>
    public static List<string> FilterGameVersionOptions(IEnumerable<GameVersionEntry> entries, string minVersion = "1.16")
        => entries.Where(e => e.Type == "release" && !VersionClassifier.IsAprilFools(e))
                  .Select(e => e.Id)
                  .Where(id => EcosystemService.CompareGameVersions(id, minVersion) >= 0)
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .OrderByDescending(id => id, Comparer<string>.Create(EcosystemService.CompareGameVersions))
                  .ToList();
}
