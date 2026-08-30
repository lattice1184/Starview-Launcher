using Launcher.Core.Plugin;

namespace Launcher.App.ViewModels;

/// <summary>插件列表行（纯数据，操作命令在父 PluginsViewModel 上）。</summary>
public sealed class PluginItemVM
{
    public required PluginManager.PluginDescriptor Source { get; init; }

    public string FilePath => Source.FilePath;
    public string DisplayName => Source.Name ?? Source.FileName;
    public string VersionText => Source.Version ?? "—";
    public string IdText => Source.IsLoaded ? Source.Id! : "未加载";
    /// <summary>状态角标：被掉包 / 已停用 / 已启用</summary>
    public string StatusText => Source.Status switch
    {
        PluginStatus.Tampered => "被掉包",
        PluginStatus.Disabled => "已停用",
        PluginStatus.Unknown => "未加载",
        _ => "已启用",
    };

    /// <summary>启停按钮文案</summary>
    public string ToggleText => Source.Enabled ? "停用" : "启用";
}
