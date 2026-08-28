using System.IO.Compression;
using Launcher.App.ViewModels;
using Launcher.Core.Download;
using Launcher.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Launcher.App.Services;

/// <summary>
/// 整合包导入统一入口（AL47）：版本页按钮 / 窗口拖拽 / 在线下载完成 三处共用。
/// 解析 → 确认框 → 全局下载中心组任务（进度/暂停/重试现成）→ 完成 Toast + 版本页刷新选中。
/// </summary>
public static class ModpackImportFlow
{
    public static async void StartAsync(string zipPath)
    {
        var owner = DialogService.MainWindow();
        try
        {
            // 8-28 日志埋点：导入全链路写启动日志（%AppData%\Launcher\logs\Launch-*.log），失败可查
            AppLog.Instance?.LogInformation("[modpack] 导入开始 {Zip}", zipPath);
            var info = ModpackImporter.Parse(zipPath, out var reason);
            if (info is null)
            {
                // 失败也记录 zip 内容前几项——诊断拖的到底是什么（模组包/普通 zip/损坏文件）
                AppLog.Instance?.LogWarning("[modpack] 导入失败（不支持格式）{Zip} 原因:{Reason} 内容:[{Entries}]",
                    zipPath, reason, DescribeZip(zipPath));
                NotificationService.Error(reason ?? "不支持的整合包格式");
                return;
            }
            AppLog.Instance?.LogInformation("[modpack] 解析成功 {Zip} → {Id}（{Format}，{Count} 文件，MC {Mc}）",
                zipPath, info.VersionId, info.Format, info.FileCount, info.McVersion);
            if (owner is not null && !await DialogService.Confirm(owner,
                    BuildConfirmText(info), "导入整合包", "导入", "取消"))
            {
                AppLog.Instance?.LogInformation("[modpack] 用户取消导入 {Zip}", zipPath);
                return;
            }

            ModpackImportReport? report = null;
            var task = DownloadManager.Instance.EnqueueGroup($"导入整合包 {info.VersionId}", async (ctx, ct) =>
            {
                report = await new ModpackInstaller()
                    .ImportAsync(zipPath, GameDirectory.InstallDir(), ctx, ct);
            });
            // 自动跳下载板块；完成后跳回版本页并选中新实例
            MainViewModel.Current?.NavigateToDownloadQueue("version");
            await task.Completion;
            if (task.State == DownloadTaskState.Completed && report is not null)
            {
                AppLog.Instance?.LogInformation("[modpack] 导入完成 {Zip} → {PackId}（跳过 {Skipped} 项）",
                    zipPath, report.PackId, report.ModsSkipped);
                var skip = report.ModsSkipped > 0
                    ? $"（跳过 {report.ModsSkipped} 项：{string.Join("、", report.Skipped.Take(3).Select(s => s.Name))}）"
                    : "";
                NotificationService.Success($"整合包已导入：{report.PackId}{skip}");
                if (MainViewModel.Current is { } main)
                {
                    await main.Versions.LoadAsync();
                    main.Versions.SelectById(report.PackId);
                }
            }
            else
            {
                AppLog.Instance?.LogWarning("[modpack] 导入任务失败 {Zip}:{Error}", zipPath, task.Error);
                NotificationService.Error(task.Error ?? "导入失败");
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance?.LogError(null, "[modpack] 导入异常 {Zip}:{Error}", zipPath, ex.Message);
            NotificationService.Error($"导入失败: {ex.Message}");
        }
    }

    /// <summary>zip 内前 20 个条目（失败诊断用：看拖的到底是什么）</summary>
    private static string DescribeZip(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return string.Join(", ", zip.Entries.Take(20).Select(e => e.FullName));
        }
        catch (Exception ex) { return $"（无法读取：{ex.Message}）"; }
    }

    private static string BuildConfirmText(ModpackImportInfo info)
    {
        var lines = new List<string>
        {
            $"整合包：{info.VersionId}",
            $"Minecraft：{info.McVersion}",
        };
        if (info.Loader is not null)
            lines.Add($"加载器：{info.Loader}{(info.LoaderVersion is null ? "" : $" {info.LoaderVersion}")}");
        lines.Add(info.Format == ModpackFormat.Modrinth
            ? $"模组：{info.FileCount} 个（在线下载）"
            : $"文件：{info.FileCount} 个");
        lines.Add("");
        lines.Add("导入会创建能启动的版本实例，并下载原版与加载器文件。文件有几百 MB。");
        return string.Join("\n", lines);
    }
}
