using System.Text.Json;

namespace Launcher.Core.Plugin;

/// <summary>
/// 插件沙箱试运行——宿主侧（8-31）：在独立子进程里跑插件的 OnLoad。
/// 进程级隔离：插件崩溃/挂起不影响启动器；TEMP/TMP/CWD 重定向到一次性沙盒目录，
/// 插件经 Path.GetTempPath() 写的文件全部落在沙盒内（可事后审查）。
/// 报告 JSON 写 handoff 文件（参照 Terracotta 模式）供父进程读取。
/// 由 Program.cs 的 --plugin-trial 分支在 Avalonia 初始化前调用（单文件 exe 复用，零体积增加）。
/// </summary>
public static class PluginTrial
{
    /// <summary>试运行内部报告（宿主写盘、父进程读）。Status: ok / exception / no-plugin。</summary>
    public sealed record TrialReport(string Status, List<string> Logs, List<string> ScratchWrites);

    /// <summary>解析 args：--plugin-trial &lt;dll&gt; --scratch &lt;dir&gt;，运行并返回退出码。</summary>
    public static int RunFromArgs(string[] args)
    {
        var dll = args[1];
        var scratch = args[3];
        try { Directory.CreateDirectory(scratch); } catch { }
        return Run(dll, scratch, Path.Combine(scratch, "report.json"));
    }

    /// <summary>重定向 TEMP/CWD → scratch 后加载插件跑 OnLoad，报告写 reportPath。</summary>
    public static int Run(string dll, string scratchDir, string reportPath)
    {
        var logs = new List<string>();
        var status = "ok";
        try
        {
            Environment.SetEnvironmentVariable("TEMP", scratchDir);
            Environment.SetEnvironmentVariable("TMP", scratchDir);
            Environment.SetEnvironmentVariable("TMPDIR", scratchDir); // Linux/macOS 惯例，Windows 忽略
            Directory.SetCurrentDirectory(scratchDir);

            var loaded = PluginLoader.LoadOne(dll, scratchDir, logs.Add);
            if (loaded is null) { logs.Add("dll 中没有 IStarviewPlugin 实现"); status = "no-plugin"; }
            else logs.Add($"插件已加载：{loaded.Plugin.Name} {loaded.Plugin.Version}");
        }
        catch (Exception ex) { logs.Add("OnLoad 异常：" + ex); status = "exception"; }

        try
        {
            var report = new TrialReport(status, logs, EnumerateRelative(scratchDir));
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report));
        }
        catch { /* 报告写失败不阻断退出 */ }
        return status == "ok" ? 0 : 1;
    }

    private static List<string> EnumerateRelative(string dir)
    {
        var list = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                list.Add(Path.GetRelativePath(dir, f));
        }
        catch { }
        return list;
    }
}
