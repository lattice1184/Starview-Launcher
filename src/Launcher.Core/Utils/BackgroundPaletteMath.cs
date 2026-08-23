using System.Globalization;

namespace Launcher.Core.Utils;

/// <summary>32 位 RGBA 纯值（A 在首位，与 #AARRGGBB 一致；Core 无 UI 依赖）</summary>
public sealed record Rgba32(byte A, byte R, byte G, byte B);

/// <summary>
/// 背景色派生整套表面色（亮/暗二态翻转）：背景浅色 → 深色文字系 + 白卡片；背景暗色 → 现状暗主题。
/// 判据 IsLight = A ≥ 128 且相对亮度 &gt; 0.30（低 alpha 一律按暗——半透明透出的是暗色 acrylic 基底，
/// 混合结果必比背景色暗；阈值与 AccentColorMath.DeriveOnAccent 一致）。纯字节运算、无 Avalonia 依赖。
/// </summary>
public static class BackgroundPaletteMath
{
    /// <summary>背景色默认值（8-23 加深：0xB8=72% → 0xE6=90%——白色应用透不过半透明窗口，白字可读）</summary>
    public const string DefaultBackground = "#E61D222C";

    /// <summary>校验并解析背景色：#RRGGBB（alpha=FF）或 #AARRGGBB；非法返回 null</summary>
    public static Rgba32? TryParse(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#') return null;
        if (hex.Length != 7 && hex.Length != 9) return null;
        for (var i = 1; i < hex.Length; i++)
            if (!Uri.IsHexDigit(hex[i])) return null;
        return hex.Length == 9
            ? new Rgba32(
                byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber),
                byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber),
                byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber),
                byte.Parse(hex.AsSpan(7, 2), NumberStyles.HexNumber))
            : new Rgba32(0xFF,
                byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber),
                byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber),
                byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber));
    }

    /// <summary>该背景是否按「亮主题」翻转（浅色文字转深色、卡片转白）</summary>
    public static bool IsLightBackground(Rgba32 bg)
        => bg.A >= 128 && AccentColorMath.RelativeLuminance(new Rgb24(bg.R, bg.G, bg.B)) > 0.30;

    /// <summary>由背景色派生整套表面色</summary>
    public static BackgroundPalette Derive(Rgba32 bg) => IsLightBackground(bg) ? LightPalette : DarkPalette;

    /// <summary>亮主题整套表面色（设计基准：深字白卡，TextPrimary/TextSecondary 对 BgRaised 对比度 ≥ 4.5）</summary>
    public static readonly BackgroundPalette LightPalette = new(
        IsLight: true,
        TextPrimary: new Rgb24(0x1A, 0x1F, 0x2B),
        TextSecondary: new Rgb24(0x5A, 0x64, 0x74),
        TextDim: new Rgb24(0x7A, 0x84, 0x94),
        BgBase: new Rgb24(0xF2, 0xF4, 0xF8),
        BgSurface: new Rgb24(0xE8, 0xEB, 0xF0),
        BgRaised: new Rgb24(0xFF, 0xFF, 0xFF),
        BgHover: new Rgb24(0xE2, 0xE6, 0xEE),
        BgActive: new Rgb24(0xD8, 0xDE, 0xE8),
        BorderColor: new Rgb24(0xC9, 0xD0, 0xDB));

    /// <summary>暗主题整套表面色（= App.axaml 现状设计令牌，翻转后可还原）</summary>
    public static readonly BackgroundPalette DarkPalette = new(
        IsLight: false,
        TextPrimary: new Rgb24(0xE8, 0xEA, 0xF0),
        TextSecondary: new Rgb24(0x8A, 0x93, 0xA6),
        TextDim: new Rgb24(0x6F, 0x7B, 0x90),
        BgBase: new Rgb24(0x14, 0x18, 0x1F),
        BgSurface: new Rgb24(0x1D, 0x22, 0x2C),
        BgRaised: new Rgb24(0x24, 0x2A, 0x36),
        BgHover: new Rgb24(0x2C, 0x35, 0x44),
        BgActive: new Rgb24(0x2A, 0x32, 0x40),
        BorderColor: new Rgb24(0x2F, 0x37, 0x45));
}

/// <summary>背景色派生的整套表面色（IsLight 决定是亮主题还是暗主题）</summary>
public sealed record BackgroundPalette(
    bool IsLight,
    Rgb24 TextPrimary,
    Rgb24 TextSecondary,
    Rgb24 TextDim,
    Rgb24 BgBase,
    Rgb24 BgSurface,
    Rgb24 BgRaised,
    Rgb24 BgHover,
    Rgb24 BgActive,
    Rgb24 BorderColor);
