using Launcher.Core.Utils;

namespace Launcher.Core.Tests;

/// <summary>启动器设置：默认值 / 读写 / 坏 JSON 回退</summary>
public class LauncherSettingsTests
{
    [Fact]
    public void Defaults_VersionIsolationOn()
    {
        var s = new LauncherSettings();
        Assert.True(s.VersionIsolation);
        Assert.Null(s.GameDirectory);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            var s = new LauncherSettings { GameDirectory = @"C:\Users\test\YanKa Launcher\.minecraft", VersionIsolation = false };
            s.Save(path);

            var loaded = LauncherSettings.Load(path);
            Assert.Equal(@"C:\Users\test\YanKa Launcher\.minecraft", loaded.GameDirectory);
            Assert.False(loaded.VersionIsolation);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_BrokenJson_FallsBackToDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not valid json !!!");
            var loaded = LauncherSettings.Load(path);
            Assert.True(loaded.VersionIsolation);
            Assert.Null(loaded.GameDirectory);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_MissingFile_Defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        var loaded = LauncherSettings.Load(path);
        Assert.NotNull(loaded);
    }

    [Fact]
    public void Defaults_LaunchFields()
    {
        var s = new LauncherSettings();
        Assert.Equal(-2, s.MemoryMb); // AL16：默认自动分配（按可用内存留余量）
        Assert.Equal(2048, s.ServerMemoryMb); // 服务器内存独立默认 2048
        Assert.Null(s.JavaPath);
        Assert.Null(s.ExtraJvmArgs);
        Assert.True(s.AutoChineseEnabled);
        Assert.True(s.EcoFollowInstance); // 8-19：默认跟随实例（老用户无感）
        Assert.Equal(DownloadSourcePreference.MirrorFirst, s.DownloadSource); // 8-18：默认镜像优先
        Assert.Equal(0, s.MaxConcurrentDownloads);
    }

    [Fact]
    public void SaveAndLoad_LaunchFields_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            var s = new LauncherSettings
            {
                MemoryMb = 8192,
                ServerMemoryMb = 4096,
                JavaPath = @"C:\Program Files\Java\jdk-21in\java.exe",
                ExtraJvmArgs = "-Dxxx=1 -Xss2m",
                AutoChineseEnabled = false,
                EcoFollowInstance = false,
                DownloadSource = DownloadSourcePreference.MirrorOnly,
                MaxConcurrentDownloads = 12,
            };
            s.Save(path);

            var loaded = LauncherSettings.Load(path);
            Assert.Equal(8192, loaded.MemoryMb);
            Assert.Equal(4096, loaded.ServerMemoryMb);
            Assert.Equal(@"C:\Program Files\Java\jdk-21in\java.exe", loaded.JavaPath);
            Assert.Equal("-Dxxx=1 -Xss2m", loaded.ExtraJvmArgs);
            Assert.False(loaded.AutoChineseEnabled);
            Assert.False(loaded.EcoFollowInstance);
            Assert.Equal(DownloadSourcePreference.MirrorOnly, loaded.DownloadSource);
            Assert.Equal(12, loaded.MaxConcurrentDownloads);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Defaults_GamePriority_Normal()
    {
        var s = new LauncherSettings();
        Assert.Equal(GamePriority.Normal, s.GamePriority);
    }

    [Fact]
    public void SaveAndLoad_GamePriority_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            var s = new LauncherSettings { GamePriority = GamePriority.High };
            s.Save(path);

            var loaded = LauncherSettings.Load(path);
            Assert.Equal(GamePriority.High, loaded.GamePriority);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Defaults_DownloadTierFields()
    {
        var s = new LauncherSettings();
        Assert.Equal(DownloadTier.Medium, s.DownloadTier); // 默认中档（分片/库并发 16）
        Assert.Equal(0, s.ChunkCount);
        Assert.Equal(0, s.BufferSize);
        Assert.Equal("", s.CurseForgeApiKey);
        Assert.Equal("", s.ThirdPartyDownloadDir);
    }

    [Fact]
    public void Defaults_BackgroundImagePath_Null()
    {
        // 主题系统：默认无背景（null/空 → 亚克力纯色）
        var s = new LauncherSettings();
        Assert.Null(s.BackgroundImagePath);
    }

    [Fact]
    public void SaveAndLoad_BackgroundImagePath_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            var s = new LauncherSettings { BackgroundImagePath = @"D:\壁纸\晚霞.png" };
            s.Save(path);

            var loaded = LauncherSettings.Load(path);
            Assert.Equal(@"D:\壁纸\晚霞.png", loaded.BackgroundImagePath);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveAndLoad_DownloadTierFields_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            var s = new LauncherSettings
            {
                DownloadTier = DownloadTier.High,
                ChunkCount = 12,
                BufferSize = 163840,
                CurseForgeApiKey = "cf-key-abc",
                ThirdPartyDownloadDir = @"D:\Downloads\mods",
            };
            s.Save(path);

            var loaded = LauncherSettings.Load(path);
            Assert.Equal(DownloadTier.High, loaded.DownloadTier);
            Assert.Equal(12, loaded.ChunkCount);
            Assert.Equal(163840, loaded.BufferSize);
            Assert.Equal("cf-key-abc", loaded.CurseForgeApiKey);
            Assert.Equal(@"D:\Downloads\mods", loaded.ThirdPartyDownloadDir);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_OldJsonWithWindowOpacity_DefaultsToBlur()
    {
        // 8-23 滑块改两档单选：旧 settings.json 只有 WindowOpacity double、新字段 Opacity 缺失
        // → 反序列化成功、Opacity 回退默认 Blur（墓碑字段兼容，旧值不再被 UI 使用）
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"WindowOpacity\": 0.7, \"VersionIsolation\": true}");
            var loaded = LauncherSettings.Load(path);
            Assert.Equal(OpacityMode.Blur, loaded.Opacity);
            Assert.Equal(0.7, loaded.WindowOpacity); // 墓碑字段读出旧值但已弃用
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveAndLoad_OpacityMode_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            var s = new LauncherSettings { Opacity = OpacityMode.Solid };
            s.Save(path);

            var loaded = LauncherSettings.Load(path);
            Assert.Equal(OpacityMode.Solid, loaded.Opacity);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

}
