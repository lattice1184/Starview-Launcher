namespace Launcher.App.Models;

/// <summary>第三方依赖清单项</summary>
public sealed record ThirdPartyLicense(string Name, string Version, string License);

/// <summary>
/// 第三方依赖与开源声明（关于页展示）。
/// 手写静态清单：发布后无 csproj 可读，升级依赖时同步更新这里。
/// </summary>
public static class ThirdPartyLicenses
{
    /// <summary>启动器自身与移植组件的开源声明</summary>
    public static readonly string[] ProjectNotices =
    [
        "Starview Launcher 依据 Apache-2.0 许可开源。",
        "PCL.Core 为 PCL Community 开源核心库，依据 Apache-2.0 许可（见 NOTICE）。",
        "联机模块 Terracotta 源自 BlockHelm-Launcher，依据 GPL-3.0 许可。",
        "联机隧道底层与独立联机方案 EasyTier 依据 LGPL-3.0 许可（源码：github.com/EasyTier/EasyTier）。",
        "界面图标来自 Lucide，依据 ISC 许可。",
    ];

    /// <summary>NuGet 依赖清单</summary>
    public static readonly ThirdPartyLicense[] Packages =
    [
        new("Avalonia", "12.1.1", "MIT"),
        new("Avalonia.Desktop", "12.1.1", "MIT"),
        new("Avalonia.Themes.Fluent", "12.1.1", "MIT"),
        new("Avalonia.Fonts.Inter", "12.1.1", "MIT"),
        new("CommunityToolkit.Mvvm", "8.4.2", "MIT"),
        new("SharpZipLib", "1.4.2", "MIT"),
        new("LiteDB", "5.0.21", "MIT"),
        new("fNbt", "0.6.4", "MIT"),
        new("YamlDotNet", "16.x", "MIT"),
        new("Sentry", "4.x", "MIT"),
        new("Polly", "8.x", "BSD-3-Clause"),
        new("Microsoft.Data.Sqlite", "8.x", "MIT"),
        new("System.Text.Json", "内置", "MIT"),
        new("Humanizer.Core.zh-CN", "2.x", "MIT"),
    ];
}
