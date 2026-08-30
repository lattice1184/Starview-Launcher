using System.Security.Cryptography;
using System.Text;
using Launcher.Core.Plugin;

namespace Launcher.Core.Tests;

/// <summary>插件登记升级（8-31）：哈希 + 启停状态注册表，旧扁平格式迁移。</summary>
public class PluginHashManifestRegistryTests : IDisposable
{
    private readonly string _dir;
    public PluginHashManifestRegistryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "plugin-reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Dll(string content)
    {
        var p = Path.Combine(_dir, "plugin.dll");
        File.WriteAllBytes(p, Encoding.UTF8.GetBytes(content));
        return p;
    }
    private string HashFile => Path.Combine(_dir, ".starview-plugins.json");

    [Fact]
    public void SetEnabled_ThenGetStatus_ReflectsDisabled()
    {
        var dll = Dll("abc");
        Assert.True(PluginHashManifest.VerifyOrRecord(dll, HashFile));
        Assert.Equal(PluginStatus.Normal, PluginHashManifest.GetStatus(dll, HashFile));

        PluginHashManifest.SetEnabled(dll, HashFile, false);
        Assert.Equal(PluginStatus.Disabled, PluginHashManifest.GetStatus(dll, HashFile));
        Assert.True(PluginHashManifest.VerifyOrRecord(dll, HashFile), "停用不影响哈希校验（防掉包仍生效）");

        PluginHashManifest.SetEnabled(dll, HashFile, true);
        Assert.Equal(PluginStatus.Normal, PluginHashManifest.GetStatus(dll, HashFile));
    }

    [Fact]
    public void Remove_DeletesEntry()
    {
        var dll = Dll("def");
        PluginHashManifest.VerifyOrRecord(dll, HashFile);
        PluginHashManifest.Remove(dll, HashFile);
        Assert.Equal(PluginStatus.Unknown, PluginHashManifest.GetStatus(dll, HashFile));
    }

    [Fact]
    public void TamperedFile_StatusIsTampered()
    {
        var dll = Dll("original");
        PluginHashManifest.VerifyOrRecord(dll, HashFile);
        File.WriteAllBytes(dll, Encoding.UTF8.GetBytes("EVIL")); // 掉包
        Assert.Equal(PluginStatus.Tampered, PluginHashManifest.GetStatus(dll, HashFile));
    }

    [Fact]
    public void OldFlatFormat_HashMatch_MigratesAsNormal()
    {
        var dll = Dll("legacy-bytes");
        var sha = Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(dll)));
        File.WriteAllText(HashFile, $"{{ \"plugin.dll\": \"{sha}\" }}"); // 旧扁平格式 { dll: sha1 }
        Assert.Equal(PluginStatus.Normal, PluginHashManifest.GetStatus(dll, HashFile)); // 旧格式哈希匹配 → 视为已启用
    }

    [Fact]
    public void OldFlatFormat_HashMismatch_IsTampered()
    {
        var dll = Dll("legacy-bytes");
        File.WriteAllText(HashFile, "{ \"plugin.dll\": \"deadbeef\" }"); // 旧格式但哈希对不上
        Assert.Equal(PluginStatus.Tampered, PluginHashManifest.GetStatus(dll, HashFile)); // 保守拒绝：哈希不符当掉包
    }
}
