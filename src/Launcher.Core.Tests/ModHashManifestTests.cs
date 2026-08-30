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
        var result = await ModHashManifest.VerifyAsync(_dir);
        Assert.Empty(result.Tampered);
    }

    [Fact]
    public async Task TamperedFile_Detected()
    {
        var jar = Path.Combine(_dir, "b.jar");
        File.WriteAllText(jar, "original");
        ModHashManifest.Record(_dir, "b.jar", Sha1Hex("original"), null, "curseforge");
        File.WriteAllText(jar, "EVIL-PAYLOAD"); // 投毒替换（同名不同内容）
        var result = await ModHashManifest.VerifyAsync(_dir);
        Assert.Contains(result.Tampered, t => t.Contains("b.jar"));
    }

    [Fact]
    public async Task DeletedFile_Detected()
    {
        var jar = Path.Combine(_dir, "c.jar");
        File.WriteAllText(jar, "content-c");
        ModHashManifest.Record(_dir, "c.jar", Sha1Hex("content-c"), null, "modrinth");
        File.Delete(jar);
        var result = await ModHashManifest.VerifyAsync(_dir);
        Assert.Contains(result.Tampered, t => t.Contains("c.jar") && t.Contains("已删除"));
    }

    [Fact]
    public async Task ManualMod_NotInManifest_ReportedUntracked()
    {
        // 8-31 C：手动放/未校验的 jar 现在进 Untracked（App 层据此弹窗引导严格隔离），不再静默不拦
        File.WriteAllText(Path.Combine(_dir, "manual.jar"), "user-added");
        var result = await ModHashManifest.VerifyAsync(_dir);
        Assert.Empty(result.Tampered);
        Assert.Contains(result.Untracked, u => u == "manual.jar");
    }

    [Fact]
    public async Task Untracked_Detected_OnlyForManifestMissing()
    {
        // 清单内的 a.jar 不应误报 Untracked；手动塞的 manual.jar 应进 Untracked
        File.WriteAllText(Path.Combine(_dir, "a.jar"), "content-a");
        ModHashManifest.Record(_dir, "a.jar", Sha1Hex("content-a"), null, "modrinth");
        File.WriteAllText(Path.Combine(_dir, "manual.jar"), "user-added");
        var result = await ModHashManifest.VerifyAsync(_dir);
        Assert.DoesNotContain(result.Untracked, u => u == "a.jar");
        Assert.Contains(result.Untracked, u => u == "manual.jar");
    }

    [Fact]
    public async Task Record_Overwrites_SameName_NewVersion()
    {
        var jar = Path.Combine(_dir, "d.jar");
        File.WriteAllText(jar, "v1");
        ModHashManifest.Record(_dir, "d.jar", Sha1Hex("v1"), null, "modrinth");
        File.WriteAllText(jar, "v2"); // 重装新版 → 记录更新
        ModHashManifest.Record(_dir, "d.jar", Sha1Hex("v2"), null, "modrinth");
        var result = await ModHashManifest.VerifyAsync(_dir);
        Assert.Empty(result.Tampered);
    }

    [Fact]
    public async Task Record_NoOfficialSha1_AutoComputesLocalBaseline()
    {
        // 8-31 自动信任：Modrinth/CF 部分版本 API 不给 sha1 → 回退本机实际文件计算基线，
        // 启动器自己装的 mod 不再标"未校验"，且未被误标 Untracked
        File.WriteAllText(Path.Combine(_dir, "e.jar"), "launcher-downloaded");
        ModHashManifest.Record(_dir, "e.jar", null, null, "modrinth"); // 官方 sha1 缺失
        var result = await ModHashManifest.VerifyAsync(_dir);
        Assert.Empty(result.Tampered);
        Assert.DoesNotContain(result.Untracked, u => u == "e.jar");
    }

    [Fact]
    public async Task Record_NoOfficialSha1_AutoBaseline_StillDetectsTamper()
    {
        // 自动计算的基线同样能抓"同名不同内容"投毒
        var jar = Path.Combine(_dir, "f.jar");
        File.WriteAllText(jar, "original");
        ModHashManifest.Record(_dir, "f.jar", null, null, "curseforge");
        File.WriteAllText(jar, "EVIL-PAYLOAD");
        var result = await ModHashManifest.VerifyAsync(_dir);
        Assert.Contains(result.Tampered, t => t.Contains("f.jar"));
    }

    [Fact]
    public void Record_FileMissing_NoOfficialSha1_DoesNotRecord()
    {
        // 文件不在且无官方 sha1 → 无法取基线，不记录（不误建空条目/清单）
        ModHashManifest.Record(_dir, "ghost.jar", null, null, "modrinth");
        var manifest = Path.Combine(_dir, ".starview-mods.json");
        Assert.True(!File.Exists(manifest) || !File.ReadAllText(manifest).Contains("ghost.jar"));
    }
}
