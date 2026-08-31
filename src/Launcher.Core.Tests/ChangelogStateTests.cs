using Launcher.Core.Utils;

namespace Launcher.Core.Tests;

/// <summary>8-31 更新后弹窗：首装不弹、升级弹一次、展示后不再弹；GroupsAfter 过滤正确</summary>
public class ChangelogStateTests
{
    private readonly string _stateFile = Path.Combine(Path.GetTempPath(), $"changelog-state-{Guid.NewGuid():N}.json");

    private void Use() => ChangelogState.StateFileOverrideForTest = _stateFile;

    [Fact]
    public void FirstRun_NoState_DoesNotShow()
    {
        Use();
        try
        {
            Assert.False(ChangelogState.ShouldShow("1.1.9")); // 首装不弹
            ChangelogState.SetSeen("1.1.9");
            Assert.False(ChangelogState.ShouldShow("1.1.9")); // 已记录 → 不弹
        }
        finally { ChangelogState.StateFileOverrideForTest = null; }
    }

    [Fact]
    public void Upgrade_ShowsOnce_ThenConsumed()
    {
        Use();
        try
        {
            ChangelogState.SetSeen("1.1.8");
            Assert.True(ChangelogState.ShouldShow("1.1.9")); // 升级 → 弹
            ChangelogState.SetSeen("1.1.9");
            Assert.False(ChangelogState.ShouldShow("1.1.9")); // 已展示 → 不再弹
        }
        finally { ChangelogState.StateFileOverrideForTest = null; }
    }

    [Fact]
    public void SameVersion_DoesNotShow()
    {
        Use();
        try
        {
            ChangelogState.SetSeen("1.1.9");
            Assert.False(ChangelogState.ShouldShow("1.1.9"));
        }
        finally { ChangelogState.StateFileOverrideForTest = null; }
    }

    [Fact]
    public void GroupsAfter_ReturnsNewerNamedGroupsOnly()
    {
        var g = ChangelogCatalog.GroupsAfter("1.1.7");
        Assert.Equal(4, g.Count); // v1.1.11 + v1.1.10 + v1.1.9 + v1.1.8（最新在前）
        Assert.Equal("v1.1.11", g[0].Version);
        Assert.Equal("v1.1.10", g[1].Version);
        Assert.DoesNotContain(g, x => x.Version == ChangelogCatalog.HistoricalVersion); // 历史组不进弹窗
        Assert.All(g, x => Assert.NotEmpty(x.Items));
    }

    [Fact]
    public void GroupsAfter_Empty_ForNullOrLatest()
    {
        Assert.Empty(ChangelogCatalog.GroupsAfter(null));    // 首装：无可弹
        Assert.Empty(ChangelogCatalog.GroupsAfter("1.1.11")); // 已是最新：无更新
    }
}
