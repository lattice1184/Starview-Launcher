using Launcher.Core.Utils;

namespace Launcher.Core.Tests;

/// <summary>版本比较器：v 前缀忽略、数字感知、suffix 忽略</summary>
public class VersionUtilTests
{
    [Theory]
    [InlineData("v1.1.4", "1.1.2")]
    [InlineData("1.1.4", "1.1.2")]
    [InlineData("1.2.0", "1.1.9")]
    [InlineData("1.10.0", "1.9.9")]   // 数字感知：1.10 > 1.9（字符串序会错）
    [InlineData("v1.1.4-beta", "1.1.2")]  // suffix 不影响主版本序
    public void Compare_Newer_GreaterThanZero(string a, string b)
        => Assert.True(VersionUtil.Compare(a, b) > 0);

    [Theory]
    [InlineData("v1.1.4", "1.1.4")]
    [InlineData("1.1.4", "v1.1.4")]
    [InlineData("1.1.4", "1.1.4")]
    public void Compare_Equal_Zero(string a, string b)
        => Assert.Equal(0, VersionUtil.Compare(a, b));

    [Theory]
    [InlineData("1.1.2", "v1.1.4")]
    [InlineData("1.1.9", "1.2.0")]
    public void Compare_Older_LessThanZero(string a, string b)
        => Assert.True(VersionUtil.Compare(a, b) < 0);

    [Fact]
    public void Compare_NullOrEmpty_Safe()
    {
        Assert.Equal(0, VersionUtil.Compare(null, ""));
        Assert.True(VersionUtil.Compare("1.1.4", null) > 0);
        Assert.True(VersionUtil.Compare(null, "1.1.4") < 0);
    }
}
