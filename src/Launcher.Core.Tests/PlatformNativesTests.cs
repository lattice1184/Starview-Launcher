using Launcher.Core.Download;

namespace Launcher.Core.Tests;

/// <summary>8-31 natives 分类器 key：${arch} 占位符展开（老版本 twitch-platform 的
/// "windows":"natives-windows-${arch}"——不展开则 classifier 匹配不上，natives 永不下载/校验误报缺）。
/// 用全平台分支字典 → 测试与运行 OS 无关。</summary>
public class PlatformNativesTests
{
    [Fact]
    public void ResolveKey_ExpandsArchPlaceholder()
    {
        var natives = new Dictionary<string, string>
        {
            ["windows"] = "natives-windows-${arch}",
            ["linux"] = "natives-linux-${arch}",
            ["osx"] = "natives-osx-${arch}",
        };
        var key = PlatformNatives.ResolveKey(natives);
        Assert.NotNull(key);
        Assert.DoesNotContain("${arch}", key!);
        Assert.StartsWith("natives-", key);
    }

    [Fact]
    public void ResolveKey_NoPlaceholder_Unchanged()
    {
        var natives = new Dictionary<string, string>
        {
            ["windows"] = "natives-windows",
            ["linux"] = "natives-linux",
            ["osx"] = "natives-osx",
        };
        var key = PlatformNatives.ResolveKey(natives);
        Assert.NotNull(key);
        Assert.DoesNotContain("${arch}", key!);
        Assert.Contains(key, natives.Values);
    }
}
