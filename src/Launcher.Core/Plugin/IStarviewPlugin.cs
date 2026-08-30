namespace Launcher.Core.Plugin;

/// <summary>
/// 启动器插件（8-31 MVP）：第三方编译的 dll 实现此接口，丢进 plugins/ 目录被加载。
/// 通过 PluginContext 拿白名单能力（日志/订阅事件/自己的配置目录），通过 AppEvents 事件钩子扩展启动器。
/// 防投毒：加载前比对 .starview-plugins.json 记录的 SHA1，掉包即跳过。
/// </summary>
public interface IStarviewPlugin
{
    /// <summary>插件唯一 id（也作为配置目录名）</summary>
    string Id { get; }

    /// <summary>展示名（设置页插件列表显示）</summary>
    string Name { get; }

    /// <summary>插件版本</summary>
    string Version { get; }

    /// <summary>加载回调：订阅事件钩子、初始化。抛异常 = 该插件跳过（不拖垮启动器）。</summary>
    void OnLoad(PluginContext ctx);
}
