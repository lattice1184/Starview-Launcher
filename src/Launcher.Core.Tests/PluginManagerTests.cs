using Launcher.Core.Plugin;
using Launcher.Core.Utils;

namespace Launcher.Core.Tests;

/// <summary>插件管理（8-31 升级）：导入/删除/停用/列表，PluginsDirOverride 隔离真实 AppData。</summary>
public class PluginManagerTests : IDisposable
{
    private readonly string _root;
    private readonly string _pluginsDir;
    private readonly bool _origEnabled;

    public PluginManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "plugin-mgr-" + Guid.NewGuid().ToString("N"));
        _pluginsDir = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(_pluginsDir);
        PluginManager.PluginsDirOverride = _pluginsDir;
        _origEnabled = LauncherSettings.Current.EnablePlugins;
        LauncherSettings.Current.EnablePlugins = true;
    }

    public void Dispose()
    {
        // 卸载已加载插件（放掉 AppEvents 订阅，避免单例残留跨测试泄漏）
        foreach (var d in PluginManager.Instance.ListPlugins())
            try { PluginManager.Instance.Disable(d.FilePath); } catch { }
        PluginManager.PluginsDirOverride = null;
        LauncherSettings.Current.EnablePlugins = _origEnabled;
        try { Directory.Delete(_root, true); } catch { }
    }

    private string ProbeDll => Path.Combine(AppContext.BaseDirectory, "TrialProbe.dll");

    private string CopyToSource()
    {
        // 保留 TrialProbe.dll 原名（Import 按源文件名复制，测试按此断言 dest）
        var dir = Path.Combine(_root, "import-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "TrialProbe.dll");
        File.Copy(ProbeDll, src);
        return src;
    }

    [Fact]
    public void Import_CopiesAndLoads()
    {
        var src = CopyToSource();
        try
        {
            var r = PluginManager.Instance.Import(src);
            Assert.True(r.Ok, r.Message);
            var dest = Path.Combine(_pluginsDir, "TrialProbe.dll");
            Assert.True(File.Exists(dest), "插件应复制进 plugins/");
            var list = PluginManager.Instance.ListPlugins();
            Assert.Contains(list, d => d.Name == "试运行探针" && d.IsLoaded && d.Enabled);
        }
        finally { try { File.Delete(src); } catch { } }
    }

    [Fact]
    public void Import_SameNameDifferentContent_Rejected()
    {
        var src = CopyToSource();
        try
        {
            Assert.True(PluginManager.Instance.Import(src).Ok);
            File.WriteAllBytes(src, new byte[] { 9, 9, 9 }); // 同名不同内容
            var r = PluginManager.Instance.Import(src);
            Assert.False(r.Ok);
            Assert.Contains("内容不同", r.Message);
        }
        finally { try { File.Delete(src); } catch { } }
    }

    [Fact]
    public void Disable_SkipsOnNextLoad()
    {
        var src = CopyToSource();
        try
        {
            Assert.True(PluginManager.Instance.Import(src).Ok);
            var dest = Path.Combine(_pluginsDir, "TrialProbe.dll");
            Assert.True(PluginManager.Instance.Disable(dest).Ok);
            var list = PluginManager.Instance.ListPlugins();
            Assert.Contains(list, d => d.Status == PluginStatus.Disabled && !d.IsLoaded);

            PluginManager.Instance.Load(); // 重新 Load：禁用应被跳过
            list = PluginManager.Instance.ListPlugins();
            Assert.DoesNotContain(list, d => d.IsLoaded);
        }
        finally { try { File.Delete(src); } catch { } }
    }

    [Fact]
    public void Enable_AfterDisable_LoadsAgain()
    {
        var src = CopyToSource();
        try
        {
            Assert.True(PluginManager.Instance.Import(src).Ok);
            var dest = Path.Combine(_pluginsDir, "TrialProbe.dll");
            PluginManager.Instance.Disable(dest);
            Assert.True(PluginManager.Instance.Enable(dest).Ok);
            var list = PluginManager.Instance.ListPlugins();
            Assert.Contains(list, d => d.IsLoaded && d.Enabled);
        }
        finally { try { File.Delete(src); } catch { } }
    }

    [Fact]
    public void Delete_RemovesFileAndEntry()
    {
        // 不加载直接删（未占用文件场景）：文件 + 登记条目一并清除
        var dest = Path.Combine(_pluginsDir, "TrialProbe.dll");
        File.Copy(ProbeDll, dest);
        Assert.True(PluginHashManifest.VerifyOrRecord(dest, Path.Combine(_pluginsDir, ".starview-plugins.json")));
        Assert.True(File.Exists(dest));
        var r = PluginManager.Instance.Delete(dest);
        Assert.True(r.Ok, "Delete 失败：" + r.Message);
        Assert.False(File.Exists(dest), "删除后文件应不存在");
        Assert.Empty(PluginManager.Instance.ListPlugins());
    }

    [Fact]
    public void Delete_LoadedPlugin_RemovesFromList_EvenIfFileLocked()
    {
        // 已加载插件删除：Windows 上运行中 dll 文件可能被 ALC 锁住、当场删不掉
        // （卸载是异步的）→ Delete 落墓碑；列表必须立即消失（防复活）。
        var src = CopyToSource();
        try
        {
            Assert.True(PluginManager.Instance.Import(src).Ok);
            var dest = Path.Combine(_pluginsDir, "TrialProbe.dll");
            var r = PluginManager.Instance.Delete(dest);
            Assert.True(r.Ok, "Delete 失败：" + r.Message);
            Assert.Empty(PluginManager.Instance.ListPlugins());
        }
        finally { try { File.Delete(src); } catch { } }
    }
}
