using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Launcher.Core.Utils;

namespace Launcher.Core.Plugin;

/// <summary>试运行分类结果（UI 显示用）。</summary>
public enum PluginTrialStatus
{
    /// <summary>没写任何文件（最干净）</summary>
    Clean,
    /// <summary>只在沙盒临时目录里写了文件（正常，Temp 被重定向吸收）</summary>
    WroteScratchOnly,
    /// <summary>写到敏感目录（桌面/文档/下载/启动器数据）——可疑越界</summary>
    WroteOutside,
    /// <summary>OnLoad 抛异常 / 宿主崩溃</summary>
    Crashed,
    /// <summary>超时挂起被强杀</summary>
    TimedOut,
    /// <summary>dll 没有 IStarviewPlugin 实现，不是插件</summary>
    NotAPlugin,
}

/// <summary>一次试运行的完整结果（供 UI 面板展示）。</summary>
public sealed record PluginTrialResult(
    PluginTrialStatus Status,
    IReadOnlyList<string> OutsideWrites,
    IReadOnlyList<string> ScratchWrites,
    IReadOnlyList<string> Logs,
    string? Note);

/// <summary>试运行选项：超时时长、是否同时断网（netsh 按 exe 路径拦整个进程出站，默认关）。</summary>
public sealed record PluginTrialOptions(TimeSpan? Timeout = null, bool BlockNetwork = false);

/// <summary>
/// 插件沙箱试运行——父进程侧（8-31）：spawn 自身 exe 的 --plugin-trial 隐藏模式（复用单文件，零体积增加）→
/// 监听敏感根目录（桌面/文档/下载/DataRoot/plugins）捕获越界写入 → 超时强杀 → 读宿主 handoff 报告合并分类。
/// Windows 为行为监测（TEMP 重定向 + 监视），非硬文件系统隔离——诚实边界见计划。
/// </summary>
public static class PluginTrialRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>跑一次沙箱试运行。dll 未启用也能试（不改任何状态）。</summary>
    public static async Task<PluginTrialResult> RunAsync(string dll, PluginTrialOptions? options = null)
    {
        var opt = options ?? new PluginTrialOptions();
        var timeout = opt.Timeout ?? DefaultTimeout;
        var scratch = Path.Combine(Path.GetTempPath(), "starview-trial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var reportPath = Path.Combine(scratch, "report.json");
        var outside = new ConcurrentBag<string>();
        var watchers = new List<FileSystemWatcher>();
        string? ruleName = null;
        var blockedNote = (string?)null;

        try
        {
            foreach (var root in SensitiveRoots())
                if (Directory.Exists(root)) watchers.Add(Watch(root, outside));

            if (opt.BlockNetwork)
            {
                ruleName = TryAddNetworkBlock();
                if (ruleName is null) blockedNote = "断网失败（需管理员权限），本次仍按可联网试运行";
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return new PluginTrialResult(PluginTrialStatus.Crashed, [], [], [], "无法定位自身可执行文件");
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--plugin-trial");
            psi.ArgumentList.Add(dll);
            psi.ArgumentList.Add("--scratch");
            psi.ArgumentList.Add(scratch);

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            _ = DrainAsync(proc.StandardOutput);
            _ = DrainAsync(proc.StandardError);

            var timedOut = false;
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try { proc.Kill(entireProcessTree: true); } catch { /* 已被杀 */ }
            }

            PluginTrial.TrialReport? report = null;
            try { if (File.Exists(reportPath)) report = JsonSerializer.Deserialize<PluginTrial.TrialReport>(File.ReadAllText(reportPath)); } catch { }
            var result = Classify(timedOut, report, outside.ToList().Distinct().ToList(), blockedNote);
            return result;
        }
        finally
        {
            foreach (var w in watchers) { try { w.Dispose(); } catch { } }
            if (ruleName is not null) RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); } catch { /* 清理失败不影响结果 */ }
        }
    }

    // ---------- 内部 ----------

    /// <summary>合并宿主报告与父进程监听，分类试运行结果（internal 供单测）。</summary>
    internal static PluginTrialResult Classify(bool timedOut, PluginTrial.TrialReport? report, List<string> outside, string? blockedNote)
    {
        var note = blockedNote;
        if (timedOut)
        {
            note = string.IsNullOrEmpty(note) ? "试运行超时，已强制终止（插件可能后台挂起）" : note + "；试运行超时，已强制终止";
            return new PluginTrialResult(PluginTrialStatus.TimedOut, outside, report?.ScratchWrites ?? [], report?.Logs ?? [], note);
        }
        if (report is null)
            return new PluginTrialResult(PluginTrialStatus.Crashed, outside, [], [], note ?? "宿主进程异常退出（崩溃在报告落盘前）");
        if (report.Status == "no-plugin")
            return new PluginTrialResult(PluginTrialStatus.NotAPlugin, outside, report.ScratchWrites, report.Logs, "该 dll 不是有效的插件（无 IStarviewPlugin 实现）");
        if (report.Status == "exception")
            return new PluginTrialResult(PluginTrialStatus.Crashed, outside, report.ScratchWrites, report.Logs, note ?? "插件 OnLoad 抛异常（崩溃）");
        var status = outside.Count > 0 ? PluginTrialStatus.WroteOutside
            : report.ScratchWrites.Count > 0 ? PluginTrialStatus.WroteScratchOnly
            : PluginTrialStatus.Clean;
        return new PluginTrialResult(status, outside, report.ScratchWrites, report.Logs, note);
    }

    /// <summary>敏感根目录：桌面/文档/下载/启动器数据目录/插件目录（不存在跳过）。</summary>
    private static IEnumerable<string> SensitiveRoots()
    {
        var roots = new List<string>();
        foreach (var special in new[] { Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments })
        {
            var p = Environment.GetFolderPath(special);
            if (!string.IsNullOrEmpty(p)) roots.Add(p);
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) roots.Add(Path.Combine(home, "Downloads"));
        roots.Add(AppPaths.DataRoot);
        roots.Add(Path.Combine(AppPaths.DataRoot, "plugins"));
        return roots;
    }

    private static FileSystemWatcher Watch(string root, ConcurrentBag<string> outside)
    {
        var w = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
        };
        // 排除启动器自身日志/缓存的写入（父进程持续写日志会误报）
        string? logsDir = null, cacheDir = null;
        try { logsDir = AppPaths.LogsDir; cacheDir = AppPaths.CacheDir; } catch { }
        void Capture(string path)
        {
            if (logsDir is not null && path.StartsWith(logsDir, StringComparison.OrdinalIgnoreCase)) return;
            if (cacheDir is not null && path.StartsWith(cacheDir, StringComparison.OrdinalIgnoreCase)) return;
            outside.Add(path);
        }
        w.Created += (_, e) => Capture(e.FullPath);
        w.Changed += (_, e) => Capture(e.FullPath);
        w.Renamed += (_, e) => Capture(e.FullPath);
        w.EnableRaisingEvents = true;
        return w;
    }

    /// <summary>给自身 exe 加出站阻断规则（拦的是整个进程，故默认关）。失败（非管理员）返回 null。</summary>
    private static string? TryAddNetworkBlock()
    {
        try
        {
            var rule = "StarviewTrialBlock" + Guid.NewGuid().ToString("N")[..8];
            var exe = Environment.ProcessPath ?? "";
            var r = RunNetsh($"advfirewall firewall add rule name=\"{rule}\" dir=out program=\"{exe}\" action=block profile=any");
            return r.ExitCode == 0 ? rule : null;
        }
        catch { return null; }
    }

    private static (int ExitCode, string Error) RunNetsh(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (-1, "启动失败");
            var err = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode, err);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync() is not null) { } } catch { }
    }
}
