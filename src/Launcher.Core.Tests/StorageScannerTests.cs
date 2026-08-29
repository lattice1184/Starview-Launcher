using Launcher.Core.Utils;

namespace Launcher.Core.Tests;

/// <summary>存储扫描与清理离线测试（临时目录隔离，不碰真实 AppData）</summary>
public class StorageScannerTests : IDisposable
{
    private readonly string _gameDir;
    private readonly string _appData;
    private readonly string _tempRoot;

    public StorageScannerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "yanla-storage-test-" + Guid.NewGuid().ToString("N"));
        _gameDir = Path.Combine(_tempRoot, "minecraft");
        _appData = Path.Combine(_tempRoot, "appdata", "Launcher");
        Directory.CreateDirectory(Path.Combine(_gameDir, "versions"));
        Directory.CreateDirectory(Path.Combine(_gameDir, "libraries"));
        Directory.CreateDirectory(Path.Combine(_gameDir, "assets"));
        Directory.CreateDirectory(Path.Combine(_gameDir, "saves"));
        Directory.CreateDirectory(Path.Combine(_gameDir, "logs"));
        Directory.CreateDirectory(Path.Combine(_gameDir, "crash-reports"));
        Directory.CreateDirectory(Path.Combine(_gameDir, "backups"));
        Directory.CreateDirectory(Path.Combine(_gameDir, "downloads", "modpacks"));
        Directory.CreateDirectory(Path.Combine(_gameDir, "versions", "1.21.1", ".parts"));
        Directory.CreateDirectory(_appData);
        Directory.CreateDirectory(Path.Combine(_appData, "cache"));
        Directory.CreateDirectory(Path.Combine(_appData, "logs"));

        File.WriteAllBytes(Path.Combine(_gameDir, "versions", "1.21.1", "1.21.1.jar"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_gameDir, "libraries", "lib.dll"), new byte[50]);
        File.WriteAllBytes(Path.Combine(_gameDir, "assets", "obj.bin"), new byte[30]);
        File.WriteAllBytes(Path.Combine(_gameDir, "logs", "latest.log"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_gameDir, "backups", "1.21.1-test.zip"), new byte[200]);
        File.WriteAllBytes(Path.Combine(_gameDir, "downloads", "modpacks", "a.mrpack"), new byte[40]);
        File.WriteAllBytes(Path.Combine(_gameDir, "versions", "1.21.1", ".parts", "chunk0"), new byte[300]);
        File.WriteAllBytes(Path.Combine(_appData, "cache", "v.json"), new byte[20]);
        File.WriteAllBytes(Path.Combine(_appData, "logs", "launch.log"), new byte[5]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, true); } catch { /* 文件锁残留忽略 */ }
    }

    [Fact]
    public void Scan_GroupSizes_And_CanDeleteFlags()
    {
        var groups = StorageScanner.Scan(_gameDir, _appData).ToDictionary(g => g.Key);

        // 游戏文件：180B，全不可删
        var game = groups["game"];
        Assert.Equal(180, game.TotalBytes);
        Assert.All(game.Items, i => Assert.False(i.CanDelete));

        // 下载缓存：parts 300 + cache 20 + modpacks 40 = 360B，全可删
        var dl = groups["downloads"];
        Assert.Equal(360, dl.TotalBytes);
        Assert.All(dl.Items, i => Assert.True(i.CanDelete));

        // 日志：gameDir logs 10 + appData logs 5 = 15B（crash-reports 空）
        Assert.Equal(15, groups["logs"].TotalBytes);

        // 备份：200B
        Assert.Equal(200, groups["backups"].TotalBytes);
    }

    [Fact]
    public void DeleteGroup_RemovesAndReturnsBytes()
    {
        var groups = StorageScanner.Scan(_gameDir, _appData).ToDictionary(g => g.Key);

        var freed = StorageScanner.DeleteGroup(groups["downloads"]);
        Assert.Equal(360, freed);
        Assert.False(Directory.Exists(Path.Combine(_gameDir, "versions", "1.21.1", ".parts")));
        Assert.False(Directory.Exists(Path.Combine(_appData, "cache")));

        freed = StorageScanner.DeleteGroup(groups["logs"]);
        Assert.Equal(15, freed);
        Assert.False(Directory.Exists(Path.Combine(_gameDir, "logs")));
    }

    [Fact]
    public void DeleteGroup_LockedFile_SkipsWithoutCrash()
    {
        // 锁住 parts 目录里的文件 → 整个 parts 目录删不掉，freed 不计
        var locked = Path.Combine(_gameDir, "versions", "1.21.1", ".parts", "chunk0");
        using var fs = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var groups = StorageScanner.Scan(_gameDir, _appData).ToDictionary(g => g.Key);
        var freed = StorageScanner.DeleteGroup(groups["downloads"]);

        Assert.Equal(60, freed); // 只有 cache + modpacks 删成
        Assert.True(Directory.Exists(Path.Combine(_gameDir, "versions", "1.21.1", ".parts")));
    }

    [Fact]
    public void FormatSize_RendersUnits()
    {
        Assert.Equal("512 B", StorageScanner.FormatSize(512));
        Assert.Equal("1.5 KB", StorageScanner.FormatSize(1536));
        Assert.Equal("2.0 MB", StorageScanner.FormatSize(2 * 1024 * 1024));
        Assert.Equal("1.0 GB", StorageScanner.FormatSize(1024L * 1024 * 1024));
    }
}
