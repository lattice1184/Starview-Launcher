using Launcher.Core.Utils;

namespace Launcher.Core.Tests;

/// <summary>背景色派生数学离线测试（不依赖 UI）</summary>
public class BackgroundPaletteMathTests
{
    // ---------- TryParse ----------

    [Theory]
    [InlineData("#B81D222C", 0xB8, 0x1D, 0x22, 0x2C)] // 9 位 AARRGGBB
    [InlineData("#2DD4BF", 0xFF, 0x2D, 0xD4, 0xBF)]   // 7 位 RRGGBB → alpha FF
    [InlineData("#b81d222c", 0xB8, 0x1D, 0x22, 0x2C)] // 小写接受
    public void TryParse_Accepts(string hex, byte a, byte r, byte g, byte b)
    {
        var c = BackgroundPaletteMath.TryParse(hex);
        Assert.NotNull(c);
        Assert.Equal(new Rgba32(a, r, g, b), c);
    }

    [Theory]
    [InlineData("B81D222C")]  // 缺 #
    [InlineData("#123")]      // 短
    [InlineData("#12345678A")]// 长
    [InlineData("#GGGGGG")]   // 非 hex
    [InlineData("")]          // 空
    [InlineData(null)]        // null
    public void TryParse_Rejects(string? hex)
        => Assert.Null(BackgroundPaletteMath.TryParse(hex));

    // ---------- 亮暗翻转 ----------

    [Fact]
    public void Derive_Default_IsDark() // 默认背景 = 旧版硬编码值，保持暗主题
    {
        var p = BackgroundPaletteMath.Derive(BackgroundPaletteMath.TryParse(BackgroundPaletteMath.DefaultBackground)!);
        Assert.False(p.IsLight);
        Assert.Equal(BackgroundPaletteMath.DarkPalette, p);
    }

    [Fact]
    public void Derive_DeepenedDefault_StillDark() // 8-23 加深到 0xE6(90%) 后不误翻亮
    {
        var p = BackgroundPaletteMath.Derive(BackgroundPaletteMath.TryParse("#E61D222C")!);
        Assert.False(p.IsLight);
        Assert.Equal(BackgroundPaletteMath.DarkPalette, p);
    }

    [Fact]
    public void Derive_LightColor_FlipsToLightTheme() // 浅色背景 → 亮主题
    {
        var p = BackgroundPaletteMath.Derive(BackgroundPaletteMath.TryParse("#F0F0F0")!);
        Assert.True(p.IsLight);
        Assert.Equal(BackgroundPaletteMath.LightPalette, p);
    }

    [Fact]
    public void Derive_LowAlpha_LightColor_StaysDark() // 低 alpha 按暗（透暗 acrylic 基底）
    {
        var p = BackgroundPaletteMath.Derive(BackgroundPaletteMath.TryParse("#33F0F0F0")!);
        Assert.False(p.IsLight);
    }

    [Fact]
    public void Derive_HighAlpha_LightColor_IsLight() // 高 alpha 浅色 → 亮
    {
        var p = BackgroundPaletteMath.Derive(BackgroundPaletteMath.TryParse("#FFF0F0F0")!);
        Assert.True(p.IsLight);
    }

    [Fact]
    public void Derive_MidLuminance_StaysDark() // 亮度 ≤0.30（如 #7A7A7A）不翻转
    {
        var p = BackgroundPaletteMath.Derive(BackgroundPaletteMath.TryParse("#7A7A7A")!);
        Assert.False(p.IsLight);
    }

    // ---------- 亮主题对比度（WCAG ≥ 4.5 正文级） ----------

    [Fact]
    public void LightPalette_TextContrast_PassesWcag()
    {
        var p = BackgroundPaletteMath.LightPalette;
        Assert.True(AccentColorMath.ContrastRatio(p.TextPrimary, p.BgRaised) >= 4.5, "TextPrimary on BgRaised");
        Assert.True(AccentColorMath.ContrastRatio(p.TextSecondary, p.BgRaised) >= 4.5, "TextSecondary on BgRaised");
        Assert.True(AccentColorMath.ContrastRatio(p.TextPrimary, p.BgSurface) >= 4.5, "TextPrimary on BgSurface");
    }
}
