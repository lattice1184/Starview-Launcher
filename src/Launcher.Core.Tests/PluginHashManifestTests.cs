using Launcher.Core.Plugin;

namespace Launcher.Core.Tests;

/// <summary>插件哈希防投毒（8-31 MVP）：首次记录基准，二次比对，掉包拒绝。</summary>
public class PluginHashManifestTests : IDisposable
{
    private readonly string _dir;
    public PluginHashManifestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "plugin-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Dll(string content) { var p = Path.Combine(_dir, "test-plugin.dll"); File.WriteAllBytes(p, System.Text.Encoding.UTF8.GetBytes(content)); return p; }
    private string HashFile => Path.Combine(_dir, ".starview-plugins.json");

    [Fact]
    public void FirstLoad_RecordsHash_ReturnsTrue()
    {
        var dll = Dll("original-plugin-bytes");
        Assert.True(PluginHashManifest.VerifyOrRecord(dll, HashFile));
        Assert.True(File.Exists(HashFile), "首次加载应记录哈希文件");
    }

    [Fact]
    public void SecondLoad_Unchanged_ReturnsTrue()
    {
        var dll = Dll("stable-plugin");
        Assert.True(PluginHashManifest.VerifyOrRecord(dll, HashFile));
        Assert.True(PluginHashManifest.VerifyOrRecord(dll, HashFile), "未变动的插件二次加载应放行");
    }

    [Fact]
    public void TamperedFile_ReturnsFalse()
    {
        var dll = Dll("original-plugin");
        Assert.True(PluginHashManifest.VerifyOrRecord(dll, HashFile));
        File.WriteAllBytes(dll, System.Text.Encoding.UTF8.GetBytes("EVIL-PAYLOAD")); // 掉包（同名不同内容）
        Assert.False(PluginHashManifest.VerifyOrRecord(dll, HashFile), "掉包的插件应拒绝加载");
    }

    [Fact]
    public void MissingFile_ReturnsFalse()
    {
        // 读失败保守拒绝：不加载无法确认的插件
        Assert.False(PluginHashManifest.VerifyOrRecord(Path.Combine(_dir, "ghost.dll"), HashFile));
    }
}
