using Launcher.Core.Events;
using Launcher.Core.Plugin;

namespace TrialProbe;

/// <summary>
/// 单测用试运行探针插件（与 Launcher.Core.Tests 同构建，dll 落到测试输出目录）：
/// OnLoad 写 Path.GetTempPath()/trial-probe.txt（试运行时 TEMP 被重定向 → 落在沙盒内）。
/// 环境变量 TRIALPROBE_THROW=1 时改抛异常（测崩溃分类）。
/// </summary>
public sealed class TrialProbe : IStarviewPlugin
{
    public string Id => "trial-probe";
    public string Name => "试运行探针";
    public string Version => "1.0.0";

    public void OnLoad(PluginContext ctx)
    {
        ctx.Log("探针已加载");
        ctx.Subscribe<LaunchStartedEvent>(_ => ctx.Log("探针收到启动事件"));
        if (Environment.GetEnvironmentVariable("TRIALPROBE_THROW") == "1")
            throw new InvalidOperationException("probe boom（测崩溃分类）");
        var target = Path.Combine(Path.GetTempPath(), "trial-probe.txt");
        File.WriteAllText(target, $"probe {DateTime.Now:O}\n");
    }
}
