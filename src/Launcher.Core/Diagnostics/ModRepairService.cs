using System.Text;
using System.Text.RegularExpressions;
using Launcher.Core.Download;
using Launcher.Core.Ecosystem;
using Launcher.Core.Model.Modrinth;
using Launcher.Core.Services;
using Launcher.Core.Utils;

namespace Launcher.Core.Diagnostics;

/// <summary>模组缺失自愈报告：缺失清单 / 已补全 / 失败（含原因）</summary>
public sealed record ModRepairReport(
    List<string> Missing, List<string> Repaired, List<(string ModId, string Reason)> Failed);

/// <summary>
/// 模组缺失自愈（AL57）：读实例日志提取缺失前置 → 生态 API 查询（slug 直查 + 搜索兜底）→
/// 匹配版本 → 下载补全到实例 mods 目录。大部分 MC 崩溃是模组兼容问题——这是自动修复最该出现的地方。
/// 无日志/无命中返回空（不误报）。
/// </summary>
public sealed class ModRepairService
{
    private readonly EcosystemService _eco;

    public ModRepairService(HttpClient? http = null, DownloadService? downloads = null, string? gameDirectory = null)
        => _eco = new EcosystemService(http, downloads, gameDirectory);

    /// <summary>实例运行目录（隔离开 → versions/{id}，隔离关 → 共享根 gameDir）——与
    /// GameLaunchService/JavaArgumentsBuilder 的 game_directory 计算一致（8-23 修：硬编码
    /// versions/{id} 在隔离关闭时找不到 mods/logs，模组冲突禁用/缺失自愈静默失效）。</summary>
    public static string InstanceRoot(string gameDir, string instanceId)
        => LauncherSettings.Current.VersionIsolation
            ? Path.Combine(gameDir, "versions", instanceId)
            : gameDir;

    public static string LatestLogPath(string gameDir, string instanceId)
        => Path.Combine(InstanceRoot(gameDir, instanceId), "logs", "latest.log");

    public static string? LatestCrashReportPath(string gameDir, string instanceId)
    {
        var dir = Path.Combine(InstanceRoot(gameDir, instanceId), "crash-reports");
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "*.txt")
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();
    }

    /// <summary>读 latest.log（尾部 200KB 防大文件）+ 最新 crash-report，提取缺失模组 id（去重）。</summary>
    public static List<string> ScanInstanceLogs(string gameDir, string instanceId)
    {
        var sb = new StringBuilder();
        var log = LatestLogPath(gameDir, instanceId);
        if (File.Exists(log)) sb.AppendLine(ReadTail(log, 200 * 1024));
        if (LatestCrashReportPath(gameDir, instanceId) is { } cr && File.Exists(cr))
            sb.AppendLine(File.ReadAllText(cr)); // crash-report 几十 KB，全读
        var text = sb.ToString();
        if (text.Length == 0) return [];

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Fabric 行内列表: "Missing mods: [a, b]" / "Missing required mods: a, b"
        foreach (Match m in Regex.Matches(text, @"Missing (?:required )?mods?:?\s*\[?([^\]\r\n]*?)\]?"))
            AddIds(ids, m.Groups[1].Value);
        // Fabric 分行列表（真实格式）: "Missing mods:" 行后连续 "- id" 行（允许日志前缀 [main/ERROR]:）
        // 按行精确取：从 Missing 行往后，行首（可选前缀）+ "- id" 才算，遇到非列表行即停（防误读日志其他部分）
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("Missing", StringComparison.OrdinalIgnoreCase)
                || !lines[i].Contains("mods", StringComparison.OrdinalIgnoreCase)) continue;
            for (var j = i + 1; j < Math.Min(lines.Length, i + 16); j++)
            {
                var item = Regex.Match(lines[j], @"^\s*(?:\[[^\]]*\]\s*:\s*)?-\s+([A-Za-z0-9_\-]+)");
                if (!item.Success) break;
                AddIds(ids, item.Groups[1].Value);
            }
        }
        // Fabric: "Couldn't load mod <x> because it is missing <dep>."
        foreach (Match m in Regex.Matches(text, @"Couldn't load mod \S+ because it is missing (\S+)"))
            AddIds(ids, m.Groups[1].Value);
        // Forge/NeoForge: "requires mod 'bookshelf'" / "requires 'bookshelf'"
        foreach (Match m in Regex.Matches(text, @"requires (?:mod )?['""]([A-Za-z0-9_\-]+)['""]"))
            AddIds(ids, m.Groups[1].Value);
        return ids.ToList();
    }

    private static void AddIds(HashSet<string> set, string raw)
    {
        foreach (var part in raw.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var id = part.Trim('[', ']', '\'', '"', '.', ';');
            if (id.Length >= 2 && !char.IsDigit(id[0])
                && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
                && !Excluded.Contains(id))
                set.Add(id);
        }
    }

    /// <summary>误报过滤：日志高频非模组词（Missing 列表、requires 附近的修饰词）</summary>
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "java", "api", "mods", "minecraft", "forge", "fabric", "quilt", "neoforge", "fml",
        "mod", "required", "missing", "dependency", "dependencies", "version", "versions",
        "list", "load", "loader", "optifine", "mixins", "core", "lib", "client", "server",
    };

    /// <summary>补全缺失模组：slug 直查 → 搜索兜底（下载量排序取首个）→ 匹配版本 → 装进实例 mods。
    /// ctx 非空 = 每项成下载中心子任务（进度/暂停/重试可见）。</summary>
    public async Task<ModRepairReport> RepairAsync(
        IReadOnlyList<string> modIds, string gameDir, string instanceId,
        string? gameVersion, string? loader, DownloadGroupContext? ctx, CancellationToken ct)
    {
        var report = new ModRepairReport(modIds.ToList(), [], []);
        foreach (var id in modIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // 1. slug 直查（Fabric/Forge 的 mod id 通常就是 Modrinth slug）
                var project = await _eco.GetProjectAsync(id, ct);
                string? projectId;
                if (project is not null)
                {
                    projectId = project.Id;
                }
                else
                {
                    // 兜底：按 id 搜索取下载量最高命中
                    var hit = (await _eco.SearchAsync(ProjectType.Mod, id, gameVersion, loader,
                        null, EcosystemService.SortIndex.Downloads, 1, 0, ct))?.Hits?.FirstOrDefault();
                    if (hit is null) { report.Failed.Add((id, "未找到该项目")); continue; }
                    projectId = hit.ProjectId;
                }
                // 2. 匹配当前实例可用的版本
                var version = await _eco.FindBestVersionAsync(projectId, gameVersion, loader, ct);
                if (version is null) { report.Failed.Add((id, "没有适配当前实例的版本")); continue; }
                // 3. 下载补全到实例 mods 目录（gameDirOverride 保证落版本真实目录，ResolveInstallPath 内部处理）
                if (ctx is null)
                {
                    await _eco.InstallAsync(projectId, version, instanceId, ProjectType.Mod, null, ct, gameDir);
                }
                else
                {
                    var child = ctx.AddChild($"补全 {version.Name}", 0,
                        (p, c) => _eco.InstallAsync(projectId, version, instanceId, ProjectType.Mod, p, c, gameDir));
                    await child.Completion.WaitAsync(ct);
                    // REVIEW-A2：Completion 只保证「任务已终态」——失败也完成。必须检查终态结果，
                    // 否则下载失败（网络/404）被记入 Repaired → 误报「已补全」成功
                    // （子任务是 IsGroupChild 无自动重试，失败即最终结果，错误完全静默）
                    if (child.TerminalState != DownloadTaskState.Completed)
                    {
                        report.Failed.Add((id, child.Error ?? "补全下载失败"));
                        continue;
                    }
                }
                report.Repaired.Add($"{version.Name} ({version.VersionNumber})");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { report.Failed.Add((id, ex.Message)); }
        }
        return report;
    }

    /// <summary>读文件尾部（防止 latest.log 超大时全读拖慢）</summary>
    private static string ReadTail(string path, int maxBytes)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length <= maxBytes)
            {
                using var sr = new StreamReader(fs, Encoding.UTF8, true);
                return sr.ReadToEnd();
            }
            fs.Seek(-maxBytes, SeekOrigin.End);
            using var tail = new StreamReader(fs, Encoding.UTF8, true);
            return tail.ReadToEnd();
        }
        catch { return ""; }
    }
}
