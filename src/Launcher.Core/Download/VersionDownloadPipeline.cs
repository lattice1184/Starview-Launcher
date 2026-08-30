using System.Text.Json;
using Launcher.Core.Model.Mojang;
using Launcher.Core.Utils;

namespace Launcher.Core.Download;

/// <summary>
/// 版本下载编排（组任务、阶段全并行）：
/// 阶段 1：client jar / libraries（每文件子任务，并发门）/ assets index / logging 同时启动；
/// 阶段 2：assets 差量（依赖 index 完成）——单个计数子任务（2000+ 文件绝不建 2000 行）。
/// 每个文件是一个子任务 → 下载页可展开看独立进度；父任务按 Weight 加权聚合。
/// </summary>
public sealed class VersionDownloadPipeline
{
    private readonly DownloadService _downloads;
    private readonly DownloadOptions _options;
    private readonly string _gameDirectory;

    public VersionDownloadPipeline(DownloadService downloads, DownloadOptions options, string gameDirectory)
    {
        _downloads = downloads;
        _options = options;
        _gameDirectory = gameDirectory;
    }

    public async Task RunAsync(VersionJson version, DownloadGroupContext ctx, CancellationToken ct)
    {
        // 加载器版本：解析 inheritsFrom 链（父版本必须已安装）
        if (version.InheritsFrom is not null)
        {
            version = VersionJsonMerger.ResolveChain(version, LoadParentJson);
            if (version.InheritsFrom is { } unresolved)
                throw new FileNotFoundException(
                    $"依赖的父版本 {unresolved} 未安装（请先在版本页安装原版 {unresolved}）");
        }

        var versionDir = Path.Combine(_gameDirectory, "versions", version.Id);
        var librariesDir = Path.Combine(_gameDirectory, "libraries");
        var assetsDir = Path.Combine(_gameDirectory, "assets");

        // ---- AL36：先取资源清单定总量——进度全程单调不跳 ----
        // 旧版阶段 2 挂载 assets 时总量从 109.5MB 变 540MB，进度 99%→21% 跳水（真机 08-09 用户观察「进度会跳」）。
        // 现在 index 前置（无子任务，~1-2s），差量算出后所有子任务一次建齐，聚合 Weight 一开始就是完整总量。
        string? indexPath = null;
        List<(string Hash, long Size)> missingAssets = [];
        if (version.AssetIndex is { } assetIndex)
        {
            indexPath = Path.Combine(assetsDir, "indexes", $"{assetIndex.Id}.json");
            // AL70：index 已存在且 SHA1 匹配 → 直接复用。旧实现无条件重下——真机 08-11 16:17
            // 重装 26.2 时 32.json（586KB）重下遇网络静默，组任务无叶子阶段显示「正在完成…」卡 15s+
            if (File.Exists(indexPath) && await DownloadService.Sha1MatchesAsync(indexPath, assetIndex.Sha1, ct))
            {
                missingAssets = ReadMissingObjects(indexPath, assetsDir);
            }
            else
            {
                ctx.SetStage("获取资源清单…"); // AL70：无子任务阶段组 Stage 兜底显示（组自己设的 Stage）
                await _downloads.DownloadFileAsync(assetIndex.Url, indexPath, assetIndex.Sha1, assetIndex.Size, null, ct);
                if (File.Exists(indexPath)) missingAssets = ReadMissingObjects(indexPath, assetsDir);
            }
        }

        // ---- 阶段 1：全并行（含 assets 差量，Weight 已含全部字节）----
        var tasks = new List<Task>();

        // 1. client jar
        if (version.Downloads?.Client is { } client)
        {
            var clientPath = Path.Combine(versionDir, $"{version.Id}.jar");
            tasks.Add(ctx.AddChild($"{version.Id}.jar", client.Size ?? 0, (p, c) =>
                _downloads.DownloadFileAsync(client.Url, clientPath,
                    client.Sha1, client.Size, p, c), clientPath).Completion);
        }

        // 2. libraries（每库文件一个子任务，共享并发门——创建即排队，不阻塞编排）
        using var libGate = new SemaphoreSlim(_options.LibraryConcurrency);
        foreach (var lib in version.Libraries ?? [])
        {
            var artifact = lib.Downloads?.Artifact;
            // AL30：url 为空的 artifact 是"继承引用"（forge 的 client classifier 库 url=""，安装器标记 Invalid 跳过），
            // 无实体下载目标——镜像 VerifyFiles 同规则跳过，否则建子任务下载空 URL 抛 UriFormatException → 组任务误报失败
            // （真机 08-07 10:37「修复 1.21.10-forge-60.1.0」Failed + Error=null 即此根因，Vanilla 启动器同样跳过）。
            if (artifact is not null && !string.IsNullOrEmpty(artifact.Url))
            {
                var path = Path.Combine(librariesDir, MavenPath.FullPath(lib.Name));
                tasks.Add(ctx.AddChild(MavenPath.FileName(lib.Name), artifact.Size ?? 0, async (p, c) =>
                {
                    await libGate.WaitAsync(c);
                    try { await _downloads.DownloadFileAsync(artifact.Url, path, artifact.Sha1, artifact.Size, p, c); }
                    finally { libGate.Release(); }
                }, path).Completion);
            }

            if (lib.Natives is { } natives && PlatformNatives.ResolveKey(natives) is { } classifierKey
                && lib.Downloads?.Classifiers?.TryGetValue(classifierKey, out var nativeFile) == true)
            {
                var nativeName = MavenPath.FileName(lib.Name + ":" + classifierKey);
                var nativePath = Path.Combine(librariesDir, MavenPath.DirectoryPath(lib.Name), nativeName);
                tasks.Add(ctx.AddChild(nativeName, nativeFile.Size ?? 0, async (p, c) =>
                {
                    await libGate.WaitAsync(c);
                    try { await _downloads.DownloadFileAsync(nativeFile.Url, nativePath, nativeFile.Sha1, nativeFile.Size, p, c); }
                    finally { libGate.Release(); }
                }, nativePath).Completion);
            }

            // AL10.1：Fabric/Forge 库无 downloads.artifact，顶层 url + Maven 坐标拼下载地址（如 maven.fabricmc.net）
            if (artifact is null && lib.Url is { } repoUrl)
            {
                var path = Path.Combine(librariesDir, MavenPath.FullPath(lib.Name));
                var dlUrl = repoUrl.TrimEnd('/') + "/" + MavenPath.FullPath(lib.Name).Replace('\\', '/');
                tasks.Add(ctx.AddChild(MavenPath.FileName(lib.Name), lib.Size ?? 0, async (p, c) =>
                {
                    await libGate.WaitAsync(c);
                    try { await _downloads.DownloadFileAsync(dlUrl, path, lib.Sha1, lib.Size, p, c); }
                    finally { libGate.Release(); }
                }, path).Completion);
            }
        }

        // 3. logging 配置
        if (version.Logging?.Client?.File is { } logFile)
        {
            var fileName = Path.GetFileName(new Uri(logFile.Url).LocalPath);
            var logPath = Path.Combine(assetsDir, "log_configs", fileName);
            tasks.Add(ctx.AddChild(fileName, logFile.Size ?? 0, (p, c) =>
                _downloads.DownloadFileAsync(logFile.Url, logPath, logFile.Sha1, logFile.Size, p, c), logPath).Completion);
        }

        // 4. assets 差量（index 已前置，Weight 直接入聚合——进度单调，无挂载跳水）
        if (missingAssets.Count > 0)
        {
            var assetsWeight = missingAssets.Sum(m => m.Size);
            tasks.Add(ctx.AddChild($"资源文件 ({missingAssets.Count} 个)", assetsWeight,
                (p, c) => DownloadAssetsBatchAsync(missingAssets, assetsWeight, p, c)).Completion);
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>读 index 并计算缺失对象（已存在且大小匹配的跳过）。
    /// 注意：index 的 key 是文件路径（如 "minecraft/lang/zh_cn.json"），下载 hash 在 value 里。</summary>
    private List<(string Hash, long Size)> ReadMissingObjects(string indexPath, string assetsDir)
    {
        var index = JsonSerializer.Deserialize<AssetsIndex>(File.ReadAllText(indexPath));
        if (index is null) return [];
        var objectsDir = Path.Combine(assetsDir, "objects");
        var missing = new List<(string, long)>();
        foreach (var (_, obj) in index.Objects)
        {
            var objPath = Path.Combine(objectsDir, obj.Hash[..2], obj.Hash);
            if (File.Exists(objPath) && new FileInfo(objPath).Length == obj.Size) continue;
            missing.Add((obj.Hash, obj.Size));
        }
        return missing;
    }

    /// <summary>资源批量下载（文件级并行；计数报告：FileBytesDone 按权重缩放）</summary>
    private async Task DownloadAssetsBatchAsync(
        List<(string Hash, long Size)> missing, long assetsWeight,
        DownloadProgressHandler? progress, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(_options.AssetConcurrency);
        var total = missing.Count;
        var done = 0;
        var tasks = missing.Select(obj => Task.Run(async () =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var url = $"https://resources.download.minecraft.net/{obj.Hash[..2]}/{obj.Hash}";
                var path = Path.Combine(_gameDirectory, "assets", "objects", obj.Hash[..2], obj.Hash);
                await _downloads.DownloadFileAsync(url, path, obj.Hash, obj.Size, null, ct);
                var n = Interlocked.Increment(ref done);
                if (progress is not null)
                    progress(new DownloadProgress($"下载资源 {n}/{total}", obj.Hash,
                        assetsWeight * n / total, assetsWeight, Math.Min(n * 100.0 / total, 99)));
            }
            finally { gate.Release(); }
        }, ct)).ToList();
        await Task.WhenAll(tasks);
    }

    /// <summary>读磁盘上的父版本 JSON（inheritsFrom 链用）</summary>
    private VersionJson? LoadParentJson(string id)
    {
        var path = Path.Combine(_gameDirectory, "versions", id, $"{id}.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(path)); }
        catch (Exception) { return null; }
    }

    private sealed record AssetsIndex(
        [property: System.Text.Json.Serialization.JsonPropertyName("objects")]
        Dictionary<string, AssetObject> Objects);

    private sealed record AssetObject(
        [property: System.Text.Json.Serialization.JsonPropertyName("hash")] string Hash,
        [property: System.Text.Json.Serialization.JsonPropertyName("size")] long Size);
}
