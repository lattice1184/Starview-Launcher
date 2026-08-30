using Launcher.Core.Events;
using Launcher.Core.Plugin;

namespace SamplePlugin;

/// <summary>示例插件：订阅启动事件打日志。编译出 SamplePlugin.dll 放进 plugins/ 目录即可被加载。</summary>
public sealed class SamplePlugin : IStarviewPlugin
{
    public string Id => "sample";
    public string Name => "示例插件";
    public string Version => "1.0.0";

    public void OnLoad(PluginContext ctx)
    {
        ctx.Log($"示例插件已加载（版本 {Version}），配置目录：{ctx.SettingsDir}");
        ctx.Subscribe<LaunchStartedEvent>(e => ctx.Log($"插件收到启动事件：{e.VersionId}"));
        ctx.Subscribe<LaunchCompletedEvent>(e => ctx.Log($"插件收到退出事件：{e.VersionId} 退出码 {e.ExitCode}"));
        ctx.Subscribe<LaunchFailedEvent>(e => ctx.Log($"插件收到启动失败：{e.VersionId} {e.Error}"));
    }
}
