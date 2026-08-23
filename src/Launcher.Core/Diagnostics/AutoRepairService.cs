using System.Text.Json;
using Launcher.Core.Download;
using Launcher.Core.Launch;
using Launcher.Core.Model.Mojang;
using Launcher.Core.Server;
using Launcher.Core.Utils;

namespace Launcher.Core.Diagnostics;

/// <summary>
/// 自修复执行层（AL9）：按诊断命中的 FixKind 执行修复。
/// Redownload → VersionInstaller 幂等补全重下（缺失才下，走下载队列可见进度）；
/// ReExtractNatives → 删 natives 目录后从库 jar 重新解压（清残留 dll）。
/// </summary>
public sealed class AutoRepairService
{
    /// <summary>
    /// 文件完整性质检报告（AL62「质检员」）：存在性 + SHA1 哈希 + 统计——
    /// 下载完成后 / CheckIntegrity 用，让「跑满进度条」对应到真实文件状态。
    /// </summary>
    public sealed record FileIntegrityReport(
        int TotalExpected,   // 期望文件数（client jar + 适用 libraries）
        int Present,         // 本地存在
        int Missing,         // 缺失
        int VerifiedByHash,  // SHA1 验证通过数（仅 verifyHashes 时有意义）
        long TotalBytes,     // 实际字节（存在的文件）
        List<string> MissingFiles)
    {
        public bool IsComplete => Missing == 0;

        /// <summary>质检完成文案（下载完成 Stage / CheckIntegrity UI 共用）：如「125/125 文件完整 · 540MB · 32 个哈希验证通过」</summary>
        public string SummaryText
        {
            get
            {
                var bytes = TotalBytes >= 1024 * 1024 ? $"{TotalBytes / 1024.0 / 1024.0:0}MB" : $"{TotalBytes / 1024.0:0}KB";
                var hash = VerifiedByHash > 0 ? $" · {VerifiedByHash} 个哈希验证通过" : "";
                return IsComplete
                    ? $"{Present}/{TotalExpected} 文件完整 · {bytes}{hash}"
                    : $"缺 {Missing} 个文件（{MissingFiles.FirstOrDefault()}）";
            }
        }
    }

    /// <summary>
    /// 版本文件补全重下（client jar + libraries + assets + log4j，幂等差量）。
    /// AL10：① 判定用 TerminalState（State 经 UI Post 异步生效，Completion 同步——读 State 读到旧值
    /// Downloading 误判失败，2026-08-05 日志实证）② inheritsFrom 父 json 缺失先递归补父
    /// ③ 下载用合并版本（client jar URL/全部 libraries 继承父链——覆盖加载器 profile 无 downloads 的结构）。
    /// </summary>
    public static async Task<string> FixRedownloadAsync(string versionId, string gameDir, int depth = 0)
    {
        try
        {
            var installer = new VersionInstaller(gameDirectory: gameDir);
            var version = await installer.GetOrFetchVersionJsonAsync(versionId, null, CancellationToken.None);
            // 父版本补全：父 json 缺失 → 递归先补（深度上限防环）
            if (depth < 3 && version.InheritsFrom is { } parentId
                && !File.Exists(Path.Combine(gameDir, "versions", parentId, $"{parentId}.json")))
            {
                try { await FixRedownloadAsync(parentId, gameDir, depth + 1); }
                catch { /* 补父失败不阻断主修复（主下载的 merged 链可能已覆盖） */ }
            }
            var merged = VersionJsonMerger.ResolveChain(version, id => LoadParentJson(gameDir, id));
            // AL31 修复快路径：先诊断缺失清单——0 缺失直接返回，不排下载队列（修复时"缺 0 个也全流程跑"是
            // 慢的体感来源之一；诊断本身是本地磁盘遍历，秒级）
            var preReport = await VerifyFilesAsync(merged, gameDir, version.InheritsFrom);
            if (preReport.IsComplete)
            {
                // 8-23 修：0 字节 client jar 视为损坏——存在性快路径不再把空文件当「已完整」跳过
                // （用户反馈「显示文件需补全但自动修复失效」的一种静默形态）
                var jarCandidates = new[]
                {
                    Path.Combine(gameDir, "versions", merged.Id, $"{merged.Id}.jar"),
                    version.InheritsFrom is { } pid ? Path.Combine(gameDir, "versions", pid, $"{pid}.jar") : null,
                };
                if (jarCandidates.Any(p => p is not null && File.Exists(p) && new FileInfo(p).Length == 0))
                    return "客户端 jar 为空（0 字节，疑似损坏），需要重新下载";
                return "文件已完整，无需修复";
            }
            var task = DownloadManager.Instance.EnqueueGroup($"自动修复 {versionId}",
                (ctx, ct) => installer.InstallAsync(merged, ctx, ct));
            await task.Completion;
            if (task.TerminalState != DownloadTaskState.Completed)
                throw new InvalidOperationException($"补全未完成（{task.TerminalState}）");
            // AL10.2：下载后质检（含哈希）——修复不得"虚假成功"（下载列表曾静默跳过 url 形式库），缺失如实报告
            var report = await VerifyFilesAsync(merged, gameDir, verifyHashes: true);
            if (!report.IsComplete)
                throw new InvalidOperationException($"补全后仍缺 {report.Missing} 个文件（首例：{report.MissingFiles[0]}）");
            // 8-22 步骤3：修复完成事件（UI/日志订阅）
            Launcher.Core.Events.AppEvents.Publish(new Launcher.Core.Events.RepairCompletedEvent(versionId, report.Present, DateTime.Now));
            return $"补全完成（{report.SummaryText}）";
        }
        catch (Exception ex)
        {
            // 8-23 修：修复失败发布事件（全局订阅者弹错误提示）——此前失败原因只进日志，UI 无感知
            Launcher.Core.Events.AppEvents.Publish(new Launcher.Core.Events.RepairFailedEvent(versionId, ex.Message, DateTime.Now));
            throw;
        }
    }

    /// <summary>校验版本文件完整性：client jar + 本 OS 实际需要的 libraries 本地存在；返回质检报告（AL62）。
    /// AL11：按 OS 规则过滤——Linux/Mac natives 库不会下载，不纳入校验，否则误报"仍缺 N 个文件"假失败。
    /// AL29 真机修正：① client jar 落盘兼容两种语义——下载器落子版本目录（H6），官方安装器落父版本目录
    /// （Forge 1.21.10 真机实测 30MB jar 在 versions/{父id}/{父id}.jar）；② artifact url 为空的库是"继承引用"
    /// （forge 的 client classifier 库 url=""，安装器标记 Invalid 跳过），无实体文件，不校验。
    /// verifyHashes：有 sha1 元数据的文件（client jar / libraries artifact）做 SHA1 验证——下载后质检场景用
    /// （文件已本地，纯读取）；启动前快查传 false（存在性秒查，不哈希几百 MB）。</summary>
    public static async Task<FileIntegrityReport> VerifyFilesAsync(VersionJson merged, string gameDir,
        string? clientParentId = null, bool verifyHashes = false)
    {
        var missing = new List<string>();
        var present = new List<(string Path, string? Sha1)>();
        var clientPath = Path.Combine(gameDir, "versions", merged.Id, $"{merged.Id}.jar");
        if (File.Exists(clientPath))
            present.Add((clientPath, merged.Downloads?.Client?.Sha1));
        else if (clientParentId is not null
                 && File.Exists(Path.Combine(gameDir, "versions", clientParentId, $"{clientParentId}.jar")))
            present.Add((Path.Combine(gameDir, "versions", clientParentId, $"{clientParentId}.jar"),
                merged.Downloads?.Client?.Sha1));
        else
            missing.Add(clientPath);
        var librariesDir = Path.Combine(gameDir, "libraries");
        var resolver = new RulesResolver();
        foreach (var lib in merged.Libraries ?? [])
        {
            if (!resolver.IsAllowed(lib.Rules)) continue; // 非本 OS/特性不满足的库不下载 → 不校验
            var artifact = lib.Downloads?.Artifact;
            if (artifact is not null && string.IsNullOrEmpty(artifact.Url)) continue; // 继承引用，无实体文件
            var p = Path.Combine(librariesDir, MavenPath.FullPath(lib.Name));
            if (File.Exists(p)) present.Add((p, artifact?.Sha1));
            else missing.Add(p);

            // 8-14 natives（classifier）文件也参与校验——路径生成与下载侧一致
            // （DownloadService 下载 loop 同款 natives-windows 逻辑）：删了 natives jar 却报
            // 「已完整」会在启动解压时报错，质检误导用户（BUGS.md:55-59）
            if (lib.Natives is { } natives && natives.TryGetValue("windows", out var classifierKey)
                && lib.Downloads?.Classifiers?.TryGetValue(classifierKey, out var nativeFile) == true)
            {
                var nativeName = MavenPath.FileName(lib.Name + ":" + classifierKey);
                var nativePath = Path.Combine(librariesDir, MavenPath.DirectoryPath(lib.Name), nativeName);
                if (File.Exists(nativePath)) present.Add((nativePath, nativeFile.Sha1));
                else missing.Add(nativePath);
            }
        }

        long totalBytes = 0;
        var verifiedByHash = 0;
        // AL62 哈希质检（并行）：仅 verifyHashes 且有 sha1 元数据的文件——本地读取无网络。
        // AL71 死锁根治：旧 Task.WaitAll 阻塞池线程等 Task.Run 排队任务 = 线程池饥饿死锁
        // （真机 08-11 16:28 26.2 装完卡「正在完成」12 分钟+：67 线程 UserRequest 阻塞）——
        // 改 async/await 非阻塞（WhenAll 不占线程），无论调用线程是否池线程都不会饿死。
        if (verifyHashes)
        {
            var hashables = present.Where(e => !string.IsNullOrEmpty(e.Sha1)).ToList();
            var results = new bool[hashables.Count];
            var tasks = new Task[hashables.Count];
            for (var i = 0; i < hashables.Count; i++)
            {
                var idx = i;
                tasks[i] = Task.Run(async () => results[idx] = await Sha1MatchesAsync(hashables[idx].Path, hashables[idx].Sha1!));
            }
            await Task.WhenAll(tasks);
            verifiedByHash = results.Count(r => r);
            foreach (var e in present) totalBytes += FileLen(e.Path);
        }
        else
        {
            foreach (var e in present) totalBytes += FileLen(e.Path);
        }
        return new FileIntegrityReport(present.Count + missing.Count, present.Count, missing.Count,
            verifiedByHash, totalBytes, missing);
    }

    /// <summary>
    /// 沿 inheritsFrom 链合并后校验版本文件完整性（AL29 H5/H6 共用）。
    /// 父 json 缺失时链保留 InheritsFrom → 只校验子版本自身（此时启动路径会抛
    /// ParentVersionMissingException，见 JavaArgumentsBuilder）。
    /// </summary>
    public static async Task<FileIntegrityReport> VerifyVersionAsync(VersionJson version, string gameDir, bool verifyHashes = false)
    {
        var merged = version.InheritsFrom is null ? version
            : VersionJsonMerger.ResolveChain(version, id => LoadParentJson(gameDir, id));
        // 官方安装器把 client jar 落父版本目录（Forge 实测）→ 传父 id 作备选路径
        return await VerifyFilesAsync(merged, gameDir, version.InheritsFrom, verifyHashes);
    }

    private static long FileLen(string path) => new FileInfo(path).Length;

    /// <summary>SHA1 比对（文件缺失/读取失败 = 不匹配）</summary>
    private static async Task<bool> Sha1MatchesAsync(string path, string expected)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            var hash = await System.Security.Cryptography.SHA1.HashDataAsync(fs);
            return Convert.ToHexStringLower(hash) == expected;
        }
        catch { return false; }
    }

    /// <summary>读磁盘父版本 json（inheritsFrom 链解析用）；缺失/损坏返回 null</summary>
    private static VersionJson? LoadParentJson(string gameDir, string id)
    {
        var p = Path.Combine(gameDir, "versions", id, $"{id}.json");
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(p)); }
        catch { return null; }
    }

    /// <summary>
    /// 修复服务端（开服专用）：删除现有 server.jar 后重新下载（幂等）。
    /// 客户端自修复只补客户端文件；服务端 jar 缺失/损坏由开服崩溃诊断（FixKind.Redownload）触发此修复。
    /// </summary>
    public static async Task<string> FixServerJarAsync(string versionId, string gameDir,
        ServerInstaller? installer = null, CancellationToken ct = default)
    {
        var dir = ServerInstaller.ServerDir(gameDir, versionId);
        var jar = Path.Combine(dir, "server.jar");
        if (File.Exists(jar))
        {
            try { File.Delete(jar); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法删除损坏的 server.jar（{ex.Message}），可手动删除后重试");
            }
        }
        await (installer ?? new ServerInstaller()).InstallAsync(versionId, gameDir, null, ct);
        return "服务端文件已重新下载";
    }

    /// <summary>重解压 natives：先递归删 natives 目录清残留，再从库 jar 提取 dll/so/dylib。返回处理描述。</summary>
    public static string FixNatives(string versionId, string gameDir)
    {
        var vjPath = Path.Combine(gameDir, "versions", versionId, $"{versionId}.json");
        if (!File.Exists(vjPath)) return $"版本 JSON 缺失：{vjPath}";
        var version = JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(vjPath))
            ?? throw new InvalidDataException($"版本 JSON 解析失败: {versionId}");
        // Build 对 java/account 无磁盘访问，空串安全；只需 NativeJars/NativesDirectory
        var profile = new JavaArgumentsBuilder().Build(version, gameDir, "", "", "", "", 0);
        GameLaunchService.ExtractNatives(profile.NativeJars, profile.NativesDirectory, clearFirst: true);
        return $"已重新解压 {profile.NativeJars.Length} 个 natives 库";
    }
}
