using System.Security.Cryptography;
using System.Text;
using Launcher.Core.Diagnostics;

namespace Launcher.Core.Tests;

/// <summary>模组哈希清单（投毒检测）：记录 → 重算比对，篡改/删除检出，手动 mod 不拦。</summary>
public class ModHashManifestTests : IDisposable
{
    private readonly string _dir;
    public ModHashManifestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mhm-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Sha1Hex(string text)
        => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(text))); // 对齐 Sha1MatchesAsync 小写比对

    [Fact]
    public async Task Record_ThenVerify_Untampered_Passes()
    {
        File.WriteAllText(Path.Combine(_dir, "a.jar"), "content-a");
        ModHashManifest.Record(_dir, "a.jar", Sha1Hex("content-a"), null, "modrinth");
        var tampered = await ModHashManifest.VerifyAsync(_dir);
        Assert.Empty(tampered);
    }

    [Fact]
    public async Task TamperedFile_Detected()
    {
        var jar = Path.Combine(_dir, "b.jar");
        File.WriteAllText(jar, "original");
        ModHashManifest.Record(_dir, "b.jar", Sha1Hex("original"), null, "curseforge");
        File.WriteAllText(jar, "EVIL-PAYLOAD"); // 投毒替换（同名不同内容）
        var tampered = await ModHashManifest.VerifyAsync(_dir);
        Assert.Contains(tampered, t => t.Contains("b.jar"));
    }

    [Fact]
    public async Task DeletedFile_Detected()
    {
        var jar = Path.Combine(_dir, "c.jar");
        File.WriteAllText(jar, "content-c");
        ModHashManifest.Record(_dir, "c.jar", Sha1Hex("content-c"), null, "modrinth");
        File.Delete(jar);
        var tampered = await ModHashManifest.VerifyAsync(_dir);
        Assert.Contains(tampered, t => t.Contains("c.jar") && t.Contains("已删除"));
    }

    [Fact]
    public async Task ManualMod_NotInManifest_NotBlocked()
    {
        File.WriteAllText(Path.Combine(_dir, "manual.jar"), "user-added"); // 手动放，未记录
        var tampered = await ModHashManifest.VerifyAsync(_dir);
        Assert.Empty(tampered);
    }

    [Fact]
    public async Task Record_Overwrites_SameName_NewVersion()
    {
        var jar = Path.Combine(_dir, "d.jar");
        File.WriteAllText(jar, "v1");
        ModHashManifest.Record(_dir, "d.jar", Sha1Hex("v1"), null, "modrinth");
        File.WriteAllText(jar, "v2"); // 重装新版 → 记录更新
        ModHashManifest.Record(_dir, "d.jar", Sha1Hex("v2"), null, "modrinth");
        var tampered = await ModHashManifest.VerifyAsync(_dir);
        Assert.Empty(tampered);
    }
}
