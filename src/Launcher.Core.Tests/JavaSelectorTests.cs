using System.Text.Json;
using Launcher.Core.Launch;
using Launcher.Core.Model.Mojang;

namespace Launcher.Core.Tests;

/// <summary>AL10.2：Java 选型纯逻辑——版本要求是最低 Java，选 ≥ 要求且最接近的</summary>
public class JavaSelectorTests
{
    private static JavaSelector.JavaInstall J(int major, string name) => new(name, major);

    [Fact]
    public void BestMatch_SelectsClosestAtOrAboveRequirement()
    {
        var list = new[] { J(17, "a"), J(21, "b"), J(25, "c") };
        Assert.Equal("c", JavaSelector.BestMatch(list, 25)); // 精确匹配
        Assert.Equal("b", JavaSelector.BestMatch(list, 21)); // 精确匹配
        Assert.Equal("a", JavaSelector.BestMatch(list, 8));  // 需求低 → 最低可用（向后兼容）
        Assert.Null(JavaSelector.BestMatch(list, 30));       // 本机无满足版本
    }

    [Fact]
    public void BestMatch_NoRequirement_TakesHighest()
        => Assert.Equal("c", JavaSelector.BestMatch([J(17, "a"), J(21, "b"), J(25, "c")], null));

    [Fact]
    public void BestMatch_Empty_FallsBackToJava()
        => Assert.Equal("java", JavaSelector.BestMatch(Array.Empty<JavaSelector.JavaInstall>(), 21));

    // ---------- ParseJavaHomeVLine：macOS java_home -V 输出解析（8-31 补 Mac JDK 探测） ----------

    [Fact]
    public void ParseJavaHomeVLine_RealLine_ExtractsHomeAndMajor()
    {
        var hit = JavaSelector.ParseJavaHomeVLine(
            "    21.0.5 (arm64) \"Eclipse Temurin\" - \"21.0.5+11\" /Library/Java/JavaVirtualMachines/temurin-21.jdk/Contents/Home");
        Assert.NotNull(hit);
        Assert.Equal(21, hit.Value.Major);
        Assert.Equal("/Library/Java/JavaVirtualMachines/temurin-21.jdk/Contents/Home", hit.Value.Home);
    }

    [Theory]
    [InlineData("Matching Java Virtual Machines (3):")]
    [InlineData("")]
    [InlineData("  ")]
    public void ParseJavaHomeVLine_SkipsHeaderAndEmpty(string line)
        => Assert.Null(JavaSelector.ParseJavaHomeVLine(line));

    // ---------- ResolveRequiredMajor：版本所需 Java 大版本（自身 → 继承链 → 版本号推断） ----------

    private static VersionJson ParseVersion(string json) => JsonSerializer.Deserialize<VersionJson>(json)!;

    [Fact]
    public void ResolveRequiredMajor_OwnJavaVersionWins()
    {
        var v = ParseVersion("""{"id":"26.2","javaVersion":{"majorVersion":25}}""");
        Assert.Equal(25, JavaSelector.ResolveRequiredMajor(v, _ => null));
    }

    [Fact]
    public void ResolveRequiredMajor_InheritsFromParent()
    {
        // fabric-loader profile 无 javaVersion，继承原版 26.2（Java 25）——开服 Java 崩溃根因用例
        var v = ParseVersion("""{"id":"fabric-loader-0.19.3-26.2","inheritsFrom":"26.2"}""");
        var parent = ParseVersion("""{"id":"26.2","javaVersion":{"majorVersion":25}}""");
        Assert.Equal(25, JavaSelector.ResolveRequiredMajor(v, id => id == "26.2" ? parent : null));
    }

    [Fact]
    public void ResolveRequiredMajor_DeepChain()
    {
        var v = ParseVersion("""{"id":"pack","inheritsFrom":"loader"}""");
        var mid = ParseVersion("""{"id":"loader","inheritsFrom":"26.2"}""");
        var parent = ParseVersion("""{"id":"26.2","javaVersion":{"majorVersion":25}}""");
        Assert.Equal(25, JavaSelector.ResolveRequiredMajor(v, id => id switch
        {
            "loader" => mid,
            "26.2" => parent,
            _ => null,
        }));
    }

    [Fact]
    public void ResolveRequiredMajor_NoChain_FallsBackToVersionInference()
    {
        Assert.Equal(8, JavaSelector.ResolveRequiredMajor(ParseVersion("""{"id":"1.8.9"}"""), _ => null));
        Assert.Equal(17, JavaSelector.ResolveRequiredMajor(ParseVersion("""{"id":"1.21.1"}"""), _ => null));
    }
}
