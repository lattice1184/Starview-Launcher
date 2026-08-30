using System.Reflection;
using System.Runtime.Loader;
using Launcher.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Launcher.Core.Plugin;

/// <summary>
/// 插件加载器（8-31 MVP）：扫描 plugins/*.dll → 每个用独立 collectible ALC 加载 →
/// 反射找 IStarviewPlugin 实现 → OnLoad → 登记。坏插件跳过不拖垮。
/// 防投毒：plugins/.starview-plugins.json 记录每个 dll 的 SHA1（首次加载记录），
/// 后续加载前重算比对——不一致 = 掉包 → 跳过 + 日志警告（对齐 mod 哈希投毒检测思路）。
/// </summary>
public sealed class PluginManager
{
    public static PluginManager Instance { get; } = new();

    public sealed record LoadedPlugin(string Id, string Name, string Version, string FilePath);

    private readonly List<LoadedPlugin> _plugins = [];
    private readonly object _gate = new();

    /// <summary>已加载插件快照</summary>
    public IReadOnlyList<LoadedPlugin> Plugins
    {
        get { lock (_gate) return _plugins.ToArray(); }
    }

    /// <summary>总开关（LauncherSettings.EnablePlugins，默认关——插件未成熟前不开）</summary>
    public bool Enabled => LauncherSettings.Current.EnablePlugins;

    private static string PluginsDir => System.IO.Path.Combine(AppPaths.DataRoot, "plugins");
    private static string HashFile => System.IO.Path.Combine(PluginsDir, ".starview-plugins.json");

    /// <summary>扫描加载 plugins/ 目录的插件。静默失败不阻断启动器（坏插件跳过）。</summary>
    public void Load()
    {
        if (!Enabled) return;
        try
        {
            var dir = PluginsDir;
            if (!Directory.Exists(dir)) return;
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
            {
                try
                {
                    var name = Path.GetFileName(dll);
                    if (!PluginHashManifest.VerifyOrRecord(dll, HashFile))
                    {
                        AppLog.Instance?.LogWarning("[plugin] 跳过 {Name}：哈希与登记不一致（可能被掉包/投毒）", name);
                        continue;
                    }
                    LoadOne(dll);
                }
                catch { /* 单个插件失败不拖垮其余 */ }
            }
        }
        catch { /* 插件目录整体异常不阻断启动 */ }
    }

    private void LoadOne(string dll)
    {
        // 独立 collectible ALC：插件代码与启动器隔离加载（可卸载），依赖从插件目录解析
        var alc = new AssemblyLoadContext(Path.GetFileName(dll), isCollectible: true);
        alc.Resolving += ResolvePluginDependency;
        var asm = alc.LoadFromAssemblyPath(dll);
        foreach (var type in asm.GetTypes())
        {
            if (type.IsAbstract || !typeof(IStarviewPlugin).IsAssignableFrom(type)) continue;
            if (Activator.CreateInstance(type) is not IStarviewPlugin plugin) continue;
            var settingsDir = Path.Combine(AppPaths.DataRoot, "plugins", plugin.Id);
            Directory.CreateDirectory(settingsDir);
            var ctx = new PluginContext(plugin.Id, settingsDir, msg => AppLog.Instance?.LogInformation(msg));
            plugin.OnLoad(ctx);
            lock (_gate) _plugins.Add(new LoadedPlugin(plugin.Id, plugin.Name, plugin.Version, dll));
            AppLog.Instance?.LogInformation("[plugin] 已加载 {Name} {Version}（{File}）", plugin.Name, plugin.Version, Path.GetFileName(dll));
            return; // 一个 dll 一个插件
        }
        alc.Unload(); // 无插件实现 → 卸载释放
    }

    private static Assembly? ResolvePluginDependency(AssemblyLoadContext ctx, AssemblyName name)
    {
        try
        {
            var candidate = Path.Combine(PluginsDir, name.Name + ".dll");
            return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
        }
        catch { return null; }
    }

}
