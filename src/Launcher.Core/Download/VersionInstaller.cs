using System.Net.Http;
using System.Text.Json;
using Launcher.Core.Diagnostics;
using Launcher.Core.Model.Mojang;
using Launcher.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Launcher.Core.Download;

/// <summary>
/// 游戏本体安装：取/缓存版本 JSON（versions/{id}/{id}.json）→ 编排全量下载。
/// 版本 id 拼入路径前净化（拒绝 .. 与分隔符）。
/// </summary>
public sealed class VersionInstaller
{
    private readonly DownloadService _downloads;
    private readonly HttpClient _http;
    private readonly string _gameDirectory;

    /// <summary>8-14 守卫开关：默认只对自建目录打标记（修复路径以版本实际目录——PCL/官方扫描源——
    /// 构造本类，误标根因）；整合包导入明确要在目标目录预取父版本（allowForeignMarkers: true）。</summary>
    private readonly bool _allowForeignMarkers;

    public VersionInstaller(HttpClient? http = null, DownloadService? downloads = null, string? gameDirectory = null,
        bool allowForeignMarkers = false)
    {
        _http = http ?? HttpClientPool.CreateSharedClient();
        _downloads = downloads ?? new DownloadService();
        _gameDirectory = gameDirectory ?? GameDirectory.Detect();
        _allowForeignMarkers = allowForeignMarkers;
    }

    /// <summary>优先读磁盘缓存 versions/{id}/{id}.json；缺失时从清单地址拉取并写入（一次性缓存）</summary>
    public async Task<VersionJson> GetOrFetchVersionJsonAsync(string id, string? manifestUrl, CancellationToken ct)
    {
        var safeId = SafeId(id);
        var jsonPath = Path.Combine(_gameDirectory, "versions", safeId, $"{safeId}.json");

        if (File.Exists(jsonPath))
        {
            // 8-18 批次 74：移除缓存命中的 .prefetched 补写——浏览/点选（LoadSizeAsync 等读路径）
            // 写盘标记 → FileSystemWatcher 触发重扫 → 版本页按「预取残留」过滤 → 行消失 → 主页
            // （可用判定）又显示 → 1.21.10 点一下就没、循环往复（真机 8-18 第 2 轮循环测试发现）。
            // 预取标记只在真正拉取（下载）分支写（下方）；AL42 时代的旧缓存残件不再补标，
            // 清理判定依赖新流程自带的标记，缺失时作为可见残件（json-only）可修可删。
            try
            {
                var cached = JsonSerializer.Deserialize<VersionJson>(await File.ReadAllTextAsync(jsonPath, ct));
                if (cached is not null) return cached;
            }
            catch (Exception) { /* 损坏则重新拉取 */ }
        }

        if (string.IsNullOrEmpty(manifestUrl))
            throw new InvalidOperationException($"版本 {id} 缺少清单下载地址");

        var json = await _http.GetStringAsync(manifestUrl, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        await File.WriteAllTextAsync(jsonPath, json, ct);
        // AL42：预取 json 打标记——仅供加载器继承用，版本页不显示该条目
        //（下载「1.21.10 + Fabric」后不再出现分开的「1.21.10 缺文件」；正式安装完成时 Mark 会移除）
        // 守卫同缓存命中分支：json 被删后重拉也不覆盖已安装语义；非自建目录（PCL 等）不写标记
        if ((_allowForeignMarkers || GameDirectory.IsOwnInstallDir(_gameDirectory))
            && !InstallMarker.IsMarked(_gameDirectory, safeId))
            InstallMarker.MarkPrefetched(_gameDirectory, safeId);
        return JsonSerializer.Deserialize<VersionJson>(json)
            ?? throw new InvalidDataException($"版本 JSON 解析失败: {id}");
    }

    /// <summary>全量安装（client jar / libraries / assets / logging），进度经 DownloadProgressHandler 上报（旧展平路径）</summary>
    public Task InstallAsync(VersionJson version, DownloadProgressHandler? progress, CancellationToken ct)
        => InstallCoreAsync(version, ctx: null, progress, ct);

    /// <summary>全量安装（组任务路径：阶段全并行 + 文件级子任务）</summary>
    public Task InstallAsync(VersionJson version, DownloadGroupContext ctx, CancellationToken ct)
        => InstallCoreAsync(version, ctx, progress: null, ct);

    /// <summary>
    /// 事务化安装：下载 → 先校验、后打完整安装标记（半装版本不带标记）；
    /// 任一步失败删除本次新建的 client jar——json 留作缓存（重试免拉取），libraries 共享目录不删。
    /// 半装态消失后「已安装」判定（json+jar）恢复诚实，不再"显示已安装→启动才报缺文件"。
    /// </summary>
    private async Task InstallCoreAsync(VersionJson version, DownloadGroupContext? ctx,
        DownloadProgressHandler? progress, CancellationToken ct)
    {
        // BUGS#2 修复：记录下载前 jar 是否存在——修复路径（对已装版本补库）失败时
        // 只删「本次新建的 jar」，绝不删原本有效的 jar（旧代码无条件删 → 修复把好版本搞坏）
        var jarPath = Path.Combine(_gameDirectory, "versions", version.Id, $"{version.Id}.jar");
        var jarExistedBefore = File.Exists(jarPath);
        try
        {
            await _downloads.DownloadVersionAsync(version, ctx, progress, ct);
            // AL62 质检员：下载完成后统计 + 哈希校验（本地读取）——通过才打标记；
            // REVIEW-卡完成：质检进行中（全盘 SHA1 10-20s）先亮「质检中」——旧代码只在完成后
            // SetStage，质检期间组任务显示兜底「正在完成…」死寂（成功路径最后一段卡点）
            ctx?.SetStage("正在质检文件完整性…");
            var report = await VerifyInstalledAsync(version);
            ctx?.SetStage($"质检：{report.SummaryText}");
            // 8-14 误标根因：修复/自动修复以版本实际目录（PCL/官方扫描源）构造本类——标记只打
            // 自建目录（PCL 的版本归 PCL 管，补文件≠本启动器安装；整合包导入 allowForeignMarkers 放行）
            if (_allowForeignMarkers || GameDirectory.IsOwnInstallDir(_gameDirectory))
                InstallMarker.Mark(_gameDirectory, version.Id); // 完整安装后才打标记
            Launcher.Core.Utils.AppLog.Instance?.LogInformation("[install] complete: {Version}", version.Id);
        }
        catch
        {
            Launcher.Core.Utils.AppLog.Instance?.LogWarning("[install] failed: {Version}", version.Id);

            // 半装清理：只删本次新建的 client jar（安装前本不存在；原本存在的绝不删）
            if (!jarExistedBefore)
            {
                try { File.Delete(jarPath); } catch { }
            }
            throw;
        }
    }

    /// <summary>
    /// AL29 H6：安装后完整性校验——下载完成必须 == 文件完整，不得「虚假成功」
    /// （下载列表曾静默跳过 url 形式库；缺失如实报错，由修复路径补全）。
    /// AL62 升级为质检：存在性 + SHA1 哈希 + 统计，返回报告（Stage 展示用）。
    /// 父 json 缺失时链保留 → 只校验子版本自身文件。
    /// </summary>
    private async Task<AutoRepairService.FileIntegrityReport> VerifyInstalledAsync(VersionJson version)
    {
        var report = await AutoRepairService.VerifyVersionAsync(version, _gameDirectory, verifyHashes: true);
        if (!report.IsComplete)
            throw new InvalidOperationException(
                $"安装完成但校验失败：缺 {report.Missing} 个文件（首例：{report.MissingFiles[0]}）。可重新下载补全");
        return report;
    }

    /// <summary>路径安全化：拒绝 .. 与分隔符（与启动管道一致）</summary>
    public static string SafeId(string id) => id.Replace("..", "").Replace('/', '_').Replace('\\', '_');

    /// <summary>
    /// AL41/AL42 删除完整性：沿 inheritsFrom 链清理「预取残留」的父版本目录。
    /// 下载「1.21.10 + Fabric」只装合并的 fabric 版本，原版 json 是预取（供继承，带 .prefetched 标记）——
    /// 删 fabric 后原版残留 → 版本页出现删不掉的「缺文件」幽灵条目（真机 08-09：删 1.21.10 (Fabric) 后 1.21.10 红字）。
    /// 判定：父版本带 .prefetched 标记（预取专用）+ 不被其他版本引用 → 删；正式安装（.yanla-installed）
    /// 与无标记残件（下载中断，需保留可修）不碰。
    /// </summary>
    public static void CleanupOrphanParents(string gameDir, string versionId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { versionId };
        var current = versionId;
        while (true)
        {
            var jsonPath = Path.Combine(gameDir, "versions", current, $"{current}.json");
            if (!File.Exists(jsonPath)) break;
            try
            {
                var v = JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(jsonPath));
                var parent = v?.InheritsFrom;
                if (string.IsNullOrEmpty(parent) || !seen.Add(parent)) break;
                var parentDir = Path.Combine(gameDir, "versions", parent);
                var parentJson = Path.Combine(parentDir, $"{parent}.json");
                // 目录或 json 缺失即链断
                if (!Directory.Exists(parentDir) || !File.Exists(parentJson)) break;
                // 只清预取残留：正式安装（标记）与无标记残件（可修）不碰
                // 守卫：双标记（.yanla-installed + .prefetched 误打残留）的已装版本绝不删
                if (InstallMarker.IsMarked(gameDir, parent) || !InstallMarker.IsPrefetched(gameDir, parent)) break;
                // 预取残留但还被其他版本引用（多 fabric 版本共享同一原版）→ 不删
                if (IsReferencedByOthers(gameDir, parent, seen)) break;
                Directory.Delete(parentDir, true);
                current = parent;
            }
            catch { break; } // 单版损坏不阻断删除流程
        }
    }

    /// <summary>8-23 删除联动：预捕获版本 json 的 inheritsFrom 父链（删除前调用——删后 json 没了无法再走链）。
    /// 用户已确认「删加载器连带删被吸收原版」。</summary>
    public static List<string> ParentChain(string gameDir, string versionId)
    {
        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { versionId };
        var current = versionId;
        while (true)
        {
            var jsonPath = Path.Combine(gameDir, "versions", current, $"{current}.json");
            if (!File.Exists(jsonPath)) break;
            try
            {
                var v = JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(jsonPath));
                var parent = v?.InheritsFrom;
                if (string.IsNullOrEmpty(parent) || !seen.Add(parent)) break;
                chain.Add(parent);
                current = parent;
            }
            catch { break; }
        }
        return chain;
    }

    /// <summary>8-23 删除后连带清理父链中的预取残留（被加载器吸收的原版 .prefetched）。
    /// 守卫同 CleanupOrphanPrefetches：带 .prefetched、未被正式安装（.yanla-installed）、不被任何版本 json 引用才删——
    /// 正式安装/双标记/无标记残件不碰。删除成功后才调用，避免删除失败时父版本被误删、加载器变残。</summary>
    public static void CleanupParentsAfterDelete(string gameDir, IReadOnlyList<string> parentChain)
    {
        var versionsDir = Path.Combine(gameDir, "versions");
        if (!Directory.Exists(versionsDir)) return;
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var vd in Directory.GetDirectories(versionsDir))
        {
            try
            {
                var id = Path.GetFileName(vd);
                var jsonPath = Path.Combine(vd, $"{id}.json");
                if (!File.Exists(jsonPath)) continue;
                var v = JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(jsonPath));
                if (v?.InheritsFrom is { } p && !string.IsNullOrEmpty(p)) referenced.Add(p);
            }
            catch { /* 单版损坏跳过 */ }
        }
        foreach (var parent in parentChain)
        {
            try
            {
                if (referenced.Contains(parent)) continue; // 仍被其他版本引用 → 保留
                var dir = Path.Combine(versionsDir, parent);
                if (!Directory.Exists(dir)) continue;
                // 8-23 守卫：正式安装（.yanla-installed）的父版本绝不删（双标记残留也保留）
                if (InstallMarker.IsMarked(gameDir, parent) || !InstallMarker.IsPrefetched(gameDir, parent)) continue;
                Directory.Delete(dir, true);
            }
            catch { /* 单目录清理失败跳过（占用等） */ }
        }
    }

    /// <summary>
    /// 8-19 启动清理：删除「预取残留」且不再被任何已装版本引用的父版本目录。
    /// 预取目录（.prefetched）在主页/版本页都被隐藏——用户视角的「删了版本但数据夹里还残留」多数是它们：
    /// 下载加载器版本时预取的原版，引用它的版本被删除后链上清理只沿自身链，其他孤立预取没人管。
    /// 判定：带 .prefetched 标记 + 未被正式安装（.yanla-installed）+ 不在任何版本 json 的 inheritsFrom 引用集中 → 删。
    /// 8-23 守卫补漏：正式安装（IsMarked）版本绝不删——双标记目录（.prefetched+.yanla-installed）即使无引用也保留。
    /// </summary>
    public static void CleanupOrphanPrefetches(string gameDir)
    {
        try
        {
            var versionsDir = Path.Combine(gameDir, "versions");
            if (!Directory.Exists(versionsDir)) return;
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var vd in Directory.GetDirectories(versionsDir))
            {
                try
                {
                    var id = Path.GetFileName(vd);
                    var jsonPath = Path.Combine(vd, $"{id}.json");
                    if (!File.Exists(jsonPath)) continue;
                    var v = JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(jsonPath));
                    if (v?.InheritsFrom is { } p && !string.IsNullOrEmpty(p)) referenced.Add(p);
                }
                catch { /* 单版损坏跳过 */ }
            }
            foreach (var vd in Directory.GetDirectories(versionsDir))
            {
                try
                {
                    var id = Path.GetFileName(vd);
                    if (referenced.Contains(id)) continue;
                    var dir = Path.Combine(versionsDir, id);
                    // 8-23 守卫：正式安装（.yanla-installed）绝不删——双标记残留也保留
                    if (InstallMarker.IsMarked(gameDir, id) || !InstallMarker.IsPrefetched(gameDir, id)) continue;
                    Directory.Delete(dir, true);
                }
                catch { /* 单目录清理失败跳过（占用等） */ }
            }
        }
        catch { /* 清理失败不影响启动 */ }
    }

    /// <summary>除 seen（自身链）外，是否还有其他版本的 json 以 parent 为父</summary>
    private static bool IsReferencedByOthers(string gameDir, string parent, HashSet<string> seen)
    {
        var versionsDir = Path.Combine(gameDir, "versions");
        if (!Directory.Exists(versionsDir)) return false;
        foreach (var d in Directory.EnumerateDirectories(versionsDir))
        {
            var id = Path.GetFileName(d);
            if (seen.Contains(id)) continue;
            var p = Path.Combine(d, $"{id}.json");
            if (!File.Exists(p)) continue;
            try
            {
                var v = JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(p));
                if (string.Equals(v?.InheritsFrom, parent, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { /* 损坏 json 跳过 */ }
        }
        return false;
    }
}
