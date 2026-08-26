using Launcher.Core.Model.Modrinth;
using Launcher.Core.Services;

namespace Launcher.Core.Tests;

/// <summary>
/// 自动匹配版本过滤（8-26）：26.x 年份号 API 剥掉 game_versions 过滤后，客户端 FilterByGameVersion 兜底——
/// 防止「自动匹配」装到声明旧 MC 系（[1.21.x]）的版本进 26.1.2 游戏（entityculling 实锤）。
/// </summary>
public class EcosystemVersionFilterTests
{
    private static ModrinthVersion V(string versionNumber, params string[] gameVersions)
        => new("v-" + versionNumber, "proj", versionNumber, versionNumber,
            gameVersions.Length > 0 ? gameVersions.ToList() : null,
            ["fabric"],
            [new ModrinthVersionFile("f1", "https://x/" + versionNumber, $"{versionNumber}.jar", 100, true, null)],
            [], null, 1, "release", true, DateTime.UnixEpoch);

    [Fact]
    public void Filter_KeepsExactAndPrefixAndWildcard_ForYearVersion()
    {
        var list = new List<ModrinthVersion>
        {
            V("a", "26.1.2"),      // 精确
            V("b", "26.1"),        // 前缀声明（覆盖 26.1.2）
            V("c", "26.1.x"),      // 通配
            V("d", "1.21.1", "1.21.4"), // 旧 MC 系 → 剔除
        };

        var kept = EcosystemService.FilterByGameVersion(list, "26.1.2");

        Assert.Equal(["a", "b", "c"], kept.Select(v => v.VersionNumber));
    }

    [Fact]
    public void Filter_EntitycullingCase_Drops_121x_For_2612()
    {
        // 用户 26.1.2 实锤：entityculling 1.7.3 声明 [1.21.x]，被 SelectBestVersion 无脑选最新 → 装崩
        var oldMc = V("1.7.3", "1.21.1", "1.21.4");
        var right = V("1.7.4", "26.1.2");

        var kept = EcosystemService.FilterByGameVersion(new[] { oldMc, right }, "26.1.2");

        Assert.Single(kept);
        Assert.Equal("1.7.4", kept[0].VersionNumber);
    }

    [Fact]
    public void Filter_EmptyOrNoMatch_ReturnsEmpty_NoSilentFallback()
    {
        Assert.Empty(EcosystemService.FilterByGameVersion([], "26.1.2"));
        Assert.Empty(EcosystemService.FilterByGameVersion([V("x", "1.21.4")], "26.1.2"));
        Assert.Empty(EcosystemService.FilterByGameVersion([V("y")], "26.1.2")); // GameVersions null → 不能验证 → 不自动选
    }

    [Fact]
    public void Filter_Classic_1xGame_Works()
    {
        var list = new List<ModrinthVersion> { V("a", "1.21.4"), V("b", "1.21.1") };
        Assert.Single(EcosystemService.FilterByGameVersion(list, "1.21.4"));
    }
}
