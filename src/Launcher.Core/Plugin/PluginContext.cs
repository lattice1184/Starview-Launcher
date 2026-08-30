using Launcher.Core.Events;

namespace Launcher.Core.Plugin;

/// <summary>
/// 插件上下文（8-31 MVP 白名单 API）：插件唯一能拿到的能力面。
/// 不给裸 AppEvents / 全局单例 / File 任意写——防投毒的第一层：插件只能做声明的操作。
/// 进程内隔离是"软"的（插件代码理论上能反射绕开），配合哈希登记 + 总开关 + 试运行兜底。
/// 8-31 升级：记录插件经 ctx 创建的订阅，卸载前一次性释放（DisposeSubscriptions）。
/// </summary>
public sealed class PluginContext
{
    /// <summary>插件日志（落启动器日志，前缀 [插件 {Id}]，故障可查）</summary>
    public Action<string> Log { get; }

    /// <summary>插件自己的配置目录（{DataRoot}/plugins/{Id}/，插件可读写；启动器保证存在）</summary>
    public string SettingsDir { get; }

    private readonly string _id;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly object _gate = new();

    internal PluginContext(string id, string settingsDir, Action<string> log)
    {
        _id = id;
        SettingsDir = settingsDir;
        Log = msg => log($"[插件 {id}] {msg}");
    }

    /// <summary>订阅启动器事件（桥接 AppEvents；返回解除订阅对象）。事件类型见 Launcher.Core.Events。</summary>
    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
    {
        var sub = AppEvents.Subscribe(handler);
        lock (_gate) _subscriptions.Add(sub);
        return sub;
    }

    /// <summary>释放经本上下文创建的全部订阅（运行时停用插件前调用）。幂等。</summary>
    internal void DisposeSubscriptions()
    {
        List<IDisposable>? subs;
        lock (_gate) { subs = _subscriptions; _subscriptions.Clear(); }
        foreach (var s in subs) { try { s.Dispose(); } catch { /* 单个订阅释放失败不扩散 */ } }
    }
}
