using Launcher.Core.Events;

namespace Launcher.Core.Plugin;

/// <summary>
/// 插件上下文（8-31 MVP 白名单 API）：插件唯一能拿到的能力面。
/// 不给裸 AppEvents / 全局单例 / File 任意写——防投毒的第一层：插件只能做声明的操作。
/// 进程内隔离是"软"的（插件代码理论上能反射绕开），配合哈希登记 + 总开关 + 设置页可见兜底。
/// </summary>
public sealed class PluginContext
{
    /// <summary>插件日志（落启动器日志，前缀 [插件 {Id}]，故障可查）</summary>
    public Action<string> Log { get; }

    /// <summary>插件自己的配置目录（{DataRoot}/plugins/{Id}/，插件可读写；启动器保证存在）</summary>
    public string SettingsDir { get; }

    private readonly string _id;

    internal PluginContext(string id, string settingsDir, Action<string> log)
    {
        _id = id;
        SettingsDir = settingsDir;
        Log = msg => log($"[插件 {id}] {msg}");
    }

    /// <summary>订阅启动器事件（桥接 AppEvents；返回解除订阅对象）。事件类型见 Launcher.Core.Events。</summary>
    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) => AppEvents.Subscribe(handler);
}
