using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Launcher.App.Services;
using Launcher.Core.Diagnostics;
using Launcher.Core.Utils;

namespace Launcher.App.Views;

/// <summary>
/// 崩溃报告窗口（PCL 式错误窗口）：错误信息 + 日志预览 + 导出错误报告（zip）。
/// </summary>
public partial class CrashReportWindow : Window
{
    private string _error = "";
    private string? _fixVersionId;
    private string? _fixGameDir;
    private string _diagLog = ""; // 诊断用完整日志（供禁用冲突模组提取 mod id）

    public CrashReportWindow()
    {
        InitializeComponent();
        global::Launcher.App.Animations.UiAnim.AttachDialog(this, Root);
    }

    /// <summary>展示崩溃窗口（主窗口存在时作为模态；否则独立）</summary>
    public static void Show(string error) => Show("启动器遇到问题", error, RecentLogs());

    /// <summary>展示崩溃窗口（自定义标题/错误/日志预览——游戏崩溃与启动器崩溃共用）。
    /// AL9：可选传入诊断结果（规则命中列表）与修复目标——非纯建议类问题显示「一键修复」按钮。</summary>
    public static void Show(string title, string error, string logPreview,
        IReadOnlyList<DiagnosticHit>? diagnostics = null,
        string? fixVersionId = null, string? fixGameDir = null)
    {
        var win = new CrashReportWindow { _error = error };
        win.Title = title;
        win.ErrorText.Text = error;
        win.LogPreview.Text = logPreview;
        win._diagLog = logPreview; // 完整日志供禁用冲突模组提取 mod id
        if (diagnostics is { Count: > 0 })
        {
            win.DiagSection.IsVisible = true;
            var hasFixable = false;
            foreach (var h in diagnostics)
            {
                var fixable = h.Fix is FixKind.Redownload or FixKind.ReExtractNatives or FixKind.DisableConflictingMods;
                hasFixable |= fixable;
                win.DiagList.Items.Add(new DiagLine($"▸ 匹配：{h.Snippet}\n  说明：{h.Explanation}",
                    fixable ? "· 可自动修复" : "· 需手动处理",
                    fixable ? new SolidColorBrush(Color.Parse("#5AD07C")) : new SolidColorBrush(Color.Parse("#E8C46B")), h.Fix));
            }
            win.RepairBtn.IsVisible = hasFixable && !string.IsNullOrEmpty(fixVersionId);
            win._fixVersionId = fixVersionId;
            win._fixGameDir = fixGameDir;
            // 8-23：报错弹窗出现时自动修复就自动运行，不需要用户点按钮——窗口显示后自动触发（模态/独立都覆盖）
            if (win.RepairBtn.IsVisible)
            {
                win.Opened += async (_, _) =>
                {
                    try
                    {
                        await Task.Delay(200); // 等窗口完全渲染，避免 UI 未就绪
                        await win.RunRepairAsync(autoRun: true);
                    }
                    catch { /* 自动修复失败走按钮重试 */ }
                };
            }
        }
        if (Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } main }
            && main.PlatformImpl is not null && main.IsVisible)
        {
            try { win.ShowDialog(main); return; }
            catch { /* 兜底独立窗口 */ }
        }
        win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.Show();
    }

    /// <summary>最近错误日志尾部（AppData\Launcher\logs\crash-*.log 最新 3 个，各尾部 40 行）</summary>
    private static string RecentLogs()
    {
        try
        {
            var logDir = Path.Combine(AppPaths.DataRoot, "logs");
            if (!Directory.Exists(logDir)) return "（无日志）";
            var files = Directory.EnumerateFiles(logDir, "crash-*.log")
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc).Take(3).ToList();
            if (files.Count == 0) return "（无日志）";
            var sb = new StringBuilder();
            foreach (var f in files)
            {
                sb.AppendLine($"===== {Path.GetFileName(f)} =====");
                var lines = File.ReadAllLines(f);
                foreach (var line in lines.Skip(Math.Max(0, lines.Length - 40)))
                    sb.AppendLine(line);
            }
            return sb.ToString();
        }
        catch { return "（日志读取失败）"; }
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择报告保存位置",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || !folders[0].Path.IsAbsoluteUri) return;
        var outDir = folders[0].Path.LocalPath;
        var zipPath = Path.Combine(outDir, $"Starview-错误报告-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        try
        {
            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                // 1. 错误信息
                var err = zip.CreateEntry("错误信息.txt");
                using (var sw = new StreamWriter(err.Open(), new UTF8Encoding(false)))
                    sw.Write(_error + Environment.NewLine + LogExportHelper.SystemInfo());
                // 2. 最近崩溃日志 + 游戏日志
                var logDir = Path.Combine(AppPaths.DataRoot, "logs");
                if (Directory.Exists(logDir))
                {
                    foreach (var f in Directory.EnumerateFiles(logDir, "crash-*.log")
                                 .OrderByDescending(x => new FileInfo(x).LastWriteTimeUtc).Take(3))
                    {
                        zip.CreateEntryFromFile(f, $"logs/{Path.GetFileName(f)}");
                    }
                    foreach (var f in Directory.EnumerateFiles(logDir, "launch-*.log")
                                 .OrderByDescending(x => new FileInfo(x).LastWriteTimeUtc).Take(2))
                    {
                        zip.CreateEntryFromFile(f, $"logs/{Path.GetFileName(f)}");
                    }
                }
                // 3. 设置（不含账号 token）
                var settingsPath = Path.Combine(
                    AppPaths.DataRoot, "settings.json");
                if (File.Exists(settingsPath))
                    zip.CreateEntryFromFile(settingsPath, "settings.json");
                // 4. 游戏日志（latest.log / crash-reports / JVM hs_err）——游戏启动即崩（如退出码 134）
                // 时启动器日志只有进程退出码，真正崩因在游戏侧；8-31 朋友 Mac 134 排查就缺这些
                try
                {
                    var gameDir = GameDirectory.InstallDir();
                    var gameLogs = Path.Combine(gameDir, "logs");
                    if (Directory.Exists(gameLogs))
                    {
                        foreach (var name in new[] { "latest.log", "debug.log" })
                        {
                            var f = Path.Combine(gameLogs, name);
                            if (File.Exists(f)) zip.CreateEntryFromFile(f, $"logs/game/{name}");
                        }
                    }
                    var crashDir = Path.Combine(gameDir, "crash-reports");
                    if (Directory.Exists(crashDir))
                    {
                        foreach (var f in Directory.EnumerateFiles(crashDir, "crash-*")
                                     .OrderByDescending(x => new FileInfo(x).LastWriteTimeUtc).Take(2))
                            zip.CreateEntryFromFile(f, $"logs/game/{Path.GetFileName(f)}");
                    }
                    foreach (var f in Directory.EnumerateFiles(gameDir, "hs_err_pid*.log")
                                 .OrderByDescending(x => new FileInfo(x).LastWriteTimeUtc).Take(1))
                        zip.CreateEntryFromFile(f, $"logs/game/{Path.GetFileName(f)}");
                }
                catch { /* 游戏日志缺失/读失败不阻塞报告导出 */ }
            });
            ErrorText.Text += Environment.NewLine + $"报告已导出：{zipPath}";
        }
        catch (Exception ex)
        {
            ErrorText.Text += Environment.NewLine + $"导出失败：{ex.Message}";
        }
    }

    /// <summary>系统信息（OS/内存/CPU/启动器版本）</summary>
    private static string SystemInfo()
        => Environment.NewLine
           + "----- 系统信息 -----" + Environment.NewLine
           + $"系统：{Environment.OSVersion}" + Environment.NewLine
           + $"CPU：{Environment.ProcessorCount} 核" + Environment.NewLine
           + $"可用内存：{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024} MB" + Environment.NewLine
           + $"启动器：Starview" + Environment.NewLine
           + $"游戏目录：{GameDirectory.InstallDir()}" + Environment.NewLine;

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        try
        {
            var text = _error + Environment.NewLine + SystemInfo();
            if (OperatingSystem.IsWindows())
            {
                // Windows：clip.exe 写剪贴板（Avalonia 12 API 大改的可靠兜底）
                await Task.Run(() =>
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("clip.exe")
                    {
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                    };
                    using var p = System.Diagnostics.Process.Start(psi)!;
                    p.StandardInput.Write(text);
                    p.StandardInput.Close();
                    p.WaitForExit(3000);
                });
            }
            else
            {
                // Linux/macOS：Avalonia 剪贴板 API（无外部依赖）
                var top = Avalonia.Controls.TopLevel.GetTopLevel(this);
                if (top?.Clipboard is { } cb) await cb.SetTextAsync(text);
            }
            CopyBtn.Content = "已复制 ✓";
        }
        catch { }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>AL9 一键修复：补全重下走下载队列/重解压 natives/禁用冲突模组，完成后提示用户重新启动。
    /// B1 修复：去掉 Task.Run——FixRedownloadAsync 内部 EnqueueGroup 要在 UI 线程入队
    /// （DownloadManager.Tasks 是 UI 绑定 ObservableCollection，后台线程 Add 会跨线程崩溃）；
    /// 全程 await IO 不阻塞 UI，后台执行由下载队列自身承担。
    /// 8-23：autoRun=true（报错弹窗自动触发）时不做 ModRepairFlow 确认弹窗（模态窗口叠加会冲突）。</summary>
    private async void OnRepair(object? sender, RoutedEventArgs e) => await RunRepairAsync(autoRun: false);

    private async Task RunRepairAsync(bool autoRun)
    {
        var versionId = _fixVersionId;
        // 8-22 步骤7：修复路径统一到当前实例——_fixGameDir 来自启动链路（已正确），空时兜底 AppState.InstanceRoot
        var gameDir = string.IsNullOrEmpty(_fixGameDir) ? global::Launcher.Core.AppState.InstanceRoot : _fixGameDir;
        if (string.IsNullOrEmpty(versionId)) return;
        RepairBtn.IsEnabled = false;
        RepairBtn.Content = "正在修复…";
        try
        {
            var kind = DiagList.Items.OfType<DiagLine>()
                .FirstOrDefault(l => l.Kind is FixKind.Redownload or FixKind.ReExtractNatives or FixKind.DisableConflictingMods)?.Kind ?? FixKind.Redownload;
            string result;
            try
            {
                result = kind switch
                {
                    FixKind.ReExtractNatives => AutoRepairService.FixNatives(versionId, gameDir),
                    FixKind.DisableConflictingMods => AutoRepairService.FixConflictingMods(gameDir, versionId, _diagLog),
                    _ => await AutoRepairService.FixRedownloadAsync(versionId, gameDir),
                };
            }
            catch (Exception ex) { result = $"修复失败：{ex.Message}"; }
            RepairBtn.Content = result.StartsWith("修复失败") ? "修复失败（看日志）" : "修复完成，请重新启动";
            // AL57 模组缺失自愈：版本文件修复后读游戏日志，缺失前置 → 确认 → 自动补全（自动路径不弹确认）
            if (!result.StartsWith("修复失败") && gameDir.Length > 0)
                await ModRepairFlow.TryRepairAsync(gameDir, versionId, autoRun ? null : this, requireConfirm: !autoRun);
        }
        finally { RepairBtn.IsEnabled = true; }
    }
}

/// <summary>崩溃窗诊断区单行（AL9；顶层类型——嵌套私有类型 XAML 编译器无法解析）</summary>
public sealed record DiagLine(string Text, string FixText, IBrush FixBrush, FixKind Kind);
