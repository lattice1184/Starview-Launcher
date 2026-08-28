using Avalonia.Controls;
using Launcher.Core.Diagnostics;
using Launcher.Core.Download;
using Launcher.Core.Services;
using Launcher.Core.Utils;

namespace Launcher.App.Services;

/// <summary>
/// 模组缺失自愈统一入口（AL57）：扫描实例日志 → 有缺失则确认框 → 下载中心补全 → 结果 Toast。
/// CrashReportWindow 一键修复 / 启动失败自动修复 / 版本页手动修复 三处共用。
/// </summary>
public static class ModRepairFlow
{
    /// <summary>扫描实例日志并补全缺失前置；返回是否检测到缺失（处理与否无关）。
    /// requireConfirm=false（启动失败自动修复路径）：无人值守直接补全，不弹框——避免与崩溃窗模态冲突，
    /// 且符合「自动」语义（用户要求的就是全自动）。主动路径（修复按钮）保留确认。</summary>
    public static async Task<bool> TryRepairAsync(string gameDir, string instanceId, Window? owner, bool requireConfirm = true)
    {
        var missing = ModRepairService.ScanInstanceLogs(gameDir, instanceId);
        if (missing.Count == 0) return false;
        var list = string.Join("、", missing.Take(5)) + (missing.Count > 5 ? "…" : "");
        if (requireConfirm)
        {
            if (owner is null || !await DialogService.Confirm(owner,
                    $"你缺了前置模组：{list}。要自动从 Modrinth 补全吗？", "模组自动修复", "自动补全", "暂不"))
                return true;
        }

        var repair = new ModRepairService();
        ModRepairReport? rpt = null;
        // 8-26 通用化：fabric-loader-… 实例名 TryParseGameVersion 解析不出 → VersionScan.Inspect（inheritsFrom）拿真版本
        string? gv = EcosystemService.ResolveGameVersion(VersionScan.Inspect(gameDir, instanceId).McVersion, instanceId);
        if (gv.Length == 0) gv = null;
        var loader = EcosystemService.GuessLoader(instanceId);
        var task = DownloadManager.Instance.EnqueueGroup($"修复模组 {instanceId}", async (ctx, ct) =>
        {
            rpt = await repair.RepairAsync(missing, gameDir, instanceId, gv, loader, ctx, ct);
        });
        await task.Completion;
        if (task.State != DownloadTaskState.Completed) return true;
        if (rpt is { Repaired.Count: > 0 })
            NotificationService.Success(
                $"已补全 {rpt.Repaired.Count} 个缺失前置：{string.Join("、", rpt.Repaired.Take(3))}" +
                (rpt.Repaired.Count > 3 ? "…" : ""), 4500);
        if (rpt is { Failed.Count: > 0 })
            NotificationService.Error($"补全失败：{string.Join("、", rpt.Failed.Select(f => $"{f.ModId}（{f.Reason}）"))}");
        return true;
    }

    /// <summary>替换不兼容模组为兼容版（8-26）：调用方已把不兼容 jar 改名 .disabled（先停用保证即使下载
    /// 失败也能启动），这里复用 ModRepairService.RepairAsync 下载兼容版装进实例 mods。
    /// gameVersion 必须是解析过的真游戏版本（McVersion/inheritsFrom 兜底链）——不能像 TryRepairAsync 用
    /// TryParseGameVersion(instanceId)，对 fabric-loader-… 实例名解析不出会装错版本。
    /// 有界等待 90s：下载超时不拖死启动（mod 已停用，下载留在下载中心后台继续）。
    /// 永不抛异常（失败 → DisabledOnly 全量，调用方启动照常）。</summary>
    public static async Task<ReplaceReport> TryReplaceModsAsync(
        string gameDir, string instanceId, string gameVersion, string? loader, IReadOnlyList<string> modIds,
        CancellationToken ct = default)
    {
        var report = new ReplaceReport([], []);
        if (modIds.Count == 0) return report;
        try
        {
            var repair = new ModRepairService();
            ModRepairReport? rpt = null;
            var task = DownloadManager.Instance.EnqueueGroup($"替换不兼容模组 {instanceId}", async (ctx, c) =>
            {
                rpt = await repair.RepairAsync(modIds, gameDir, instanceId, gameVersion, loader, ctx, c);
            });
            // rpt 只在 RepairAsync 全部处理完后赋值——90s 超时内没完成则 rpt 为 null
            await Task.WhenAny(task.Completion, Task.Delay(TimeSpan.FromSeconds(90), ct));
            if (ct.IsCancellationRequested) // 用户跳过 → 不等修复完成，启动照常（mod 已停用可后处理）
            {
                report.DisabledOnly.AddRange(modIds.Select(m => $"{m}（用户跳过修复）"));
                return report;
            }
            if (rpt is not null)
            {
                report.Replaced.AddRange(rpt.Repaired);
                report.DisabledOnly.AddRange(rpt.Failed.Select(f => $"{f.ModId}（{f.Reason}）"));
            }
            else
            {
                var reason = task.State == DownloadTaskState.Failed ? "下载失败" : "下载超时";
                report.DisabledOnly.AddRange(modIds.Select(m => $"{m}（{reason}）"));
            }
        }
        catch (Exception ex)
        {
            report.DisabledOnly.AddRange(modIds.Select(m => $"{m}（修复异常：{ex.Message}）"));
        }
        return report;
    }

    /// <summary>替换结果：Replaced=已下载兼容版；DisabledOnly=无适配版/下载失败，仅停用。</summary>
    public sealed record ReplaceReport(List<string> Replaced, List<string> DisabledOnly);
}
