using System.Reflection;
using System.Runtime.Loader;

namespace Launcher.Core.Plugin;

/// <summary>
/// 插件加载单例动作（8-31 从 PluginManager 抽出，主进程与试运行宿主共用）：
/// 独立 collectible ALC 加载 dll → 反射找 IStarviewPlugin 实现 → 实例化 → OnLoad。
/// 返回可卸载句柄（PluginContext + ALC）；settingsDir = {pluginsDir}/{Id}（试运行传 scratch 目录即隔离配置）。
/// </summary>
public static class PluginLoader
{
    /// <summary>一次成功加载的完整句柄（停用/删除时用来卸载）。</summary>
    public sealed record Loaded(IStarviewPlugin Plugin, PluginContext Context, AssemblyLoadContext Alc);

    /// <summary>
    /// 加载并运行 OnLoad。返回 null = dll 无插件实现（已卸载释放）。
    /// OnLoad 抛异常 → 卸载 ALC 后向上抛（调用方决定：主进程跳过、试运行记崩溃）。
    /// </summary>
    public static Loaded? LoadOne(string dll, string pluginsDir, Action<string> log)
    {
        var alc = new AssemblyLoadContext(System.IO.Path.GetFileName(dll), isCollectible: true);
        alc.Resolving += (ctx, name) => ResolvePluginDependency(ctx, name, pluginsDir);
        try
        {
            var asm = alc.LoadFromAssemblyPath(dll);
            foreach (var type in asm.GetTypes())
            {
                if (type.IsAbstract || !typeof(IStarviewPlugin).IsAssignableFrom(type)) continue;
                if (Activator.CreateInstance(type) is not IStarviewPlugin plugin) continue;
                var settingsDir = System.IO.Path.Combine(pluginsDir, plugin.Id);
                Directory.CreateDirectory(settingsDir);
                var ctx = new PluginContext(plugin.Id, settingsDir, log);
                plugin.OnLoad(ctx);
                return new Loaded(plugin, ctx, alc);
            }
            alc.Unload(); // 无插件实现 → 卸载释放
            return null;
        }
        catch
        {
            try { alc.Unload(); } catch { /* 卸载失败不影响上抛 */ }
            throw;
        }
    }

    private static Assembly? ResolvePluginDependency(AssemblyLoadContext ctx, AssemblyName name, string pluginsDir)
    {
        try
        {
            var candidate = System.IO.Path.Combine(pluginsDir, name.Name + ".dll");
            return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
        }
        catch { return null; }
    }
}
