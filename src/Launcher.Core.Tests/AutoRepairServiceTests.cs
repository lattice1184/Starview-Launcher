using Launcher.Core.Diagnostics;
using Launcher.Core.Model.Mojang;

namespace Launcher.Core.Tests;

/// <summary>AL10.2：下载后完整性校验——修复不得"虚假成功"，缺失如实报告</summary>
public class AutoRepairServiceTests
{
    private static VersionJson BuildVersion()
    {
        var lib = new LibraryJson("net.fabricmc:fabric-loader:0.19.3", null, null, null, null, null, null, null);
        return new VersionJson("1.21.11", "release", "net.minecraft.client.main.Main",
            null, null, null, null, [lib], null, null, null, null);
    }

    [Fact]
    public async Task VerifyFiles_ReportsAllMissing_ThenEmptyAfterFill()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"verify-{Guid.NewGuid():N}");
        var version = BuildVersion();

        // 全新目录：client jar + library 都缺
        var report = await AutoRepairService.VerifyFilesAsync(version, gameDir);
        Assert.Equal(2, report.Missing);
        Assert.False(report.IsComplete);
        Assert.Contains(report.MissingFiles, p => p.EndsWith($"{Path.DirectorySeparatorChar}1.21.11.jar"));
        Assert.Contains(report.MissingFiles, p => p.Contains("fabric-loader-0.19.3.jar"));

        // 补齐后完整
        Directory.CreateDirectory(Path.Combine(gameDir, "versions", "1.21.11"));
        File.WriteAllText(Path.Combine(gameDir, "versions", "1.21.11", "1.21.11.jar"), "x");
        Directory.CreateDirectory(Path.Combine(gameDir, "libraries", "net", "fabricmc", "fabric-loader", "0.19.3"));
        File.WriteAllText(Path.Combine(gameDir, "libraries", "net", "fabricmc", "fabric-loader", "0.19.3", "fabric-loader-0.19.3.jar"), "x");

        var filled = await AutoRepairService.VerifyFilesAsync(version, gameDir);
        Assert.True(filled.IsComplete);
        Assert.Equal(2, filled.Present);
        Assert.Equal(2, filled.TotalExpected);
        Assert.True(filled.TotalBytes > 0);
        Assert.Contains("文件完整", filled.SummaryText);
    }

    /// <summary>AL11：VerifyFiles 按 OS 规则过滤——linux-only natives 库不会下载，不应误报缺失</summary>
    [Fact]
    public async Task VerifyFiles_SkipsOtherOsLibraries()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"verify-{Guid.NewGuid():N}");
        var libs = new List<LibraryJson>
        {
            new("net.fabricmc:fabric-loader:0.19.3", null, null, null, null, null, null, null),
            new("org.lwjgl:lwjgl-glfw:3.2.2", null, null, null, null,
                [new RuleJson("allow", new RuleOsInfo("linux", null, null), null)], null, null),
        };
        var version = new VersionJson("1.21.11", "release", "net.minecraft.client.main.Main",
            null, null, null, null, libs, null, null, null, null);

        var report = await AutoRepairService.VerifyFilesAsync(version, gameDir);
        Assert.Equal(2, report.Missing); // client jar + fabric-loader；linux-only 库被过滤
        Assert.DoesNotContain(report.MissingFiles, p => p.Contains("lwjgl-glfw-3.2.2"));
    }

    /// <summary>8-14 natives（classifier）文件参与校验：natives jar 缺失应如实报不完整——
    /// 删了 natives jar「修复」却报已完整，会在启动解压时报错（BUGS.md:55-59）</summary>
    [Fact]
    public async Task VerifyFiles_MissingNativesJar_ReportsIncomplete()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"verify-native-{Guid.NewGuid():N}");
        var nativeLib = new LibraryJson(
            "org.lwjgl:lwjgl:3.3.1", null, null, null,
            new LibraryDownloads(
                new DownloadFileInfo("https://x/lwjgl.jar", "bb", 5), // 现实里 lwjgl:lwjgl 有 base artifact——补上贴合真实
                new Dictionary<string, DownloadFileInfo>
                {
                    ["natives-windows"] = new DownloadFileInfo("https://x/natives.jar", "aa", 5),
                }),
            null,
            new Dictionary<string, string> { ["windows"] = "natives-windows" },
            null);
        var version = new VersionJson("1.21.11", "release", "net.minecraft.client.main.Main",
            null, null, null, null, [nativeLib], null, null, null, null);

        // 目录全空：client jar + lwjgl artifact + lwjgl natives 都缺
        var report = await AutoRepairService.VerifyFilesAsync(version, gameDir);
        Assert.Equal(3, report.Missing);
        Assert.False(report.IsComplete);
        Assert.Contains(report.MissingFiles, p => p.Contains("lwjgl-3.3.1-natives-windows.jar"));

        // 补齐 natives 后完整
        Directory.CreateDirectory(Path.Combine(gameDir, "versions", "1.21.11"));
        File.WriteAllText(Path.Combine(gameDir, "versions", "1.21.11", "1.21.11.jar"), "x");
        Directory.CreateDirectory(Path.Combine(gameDir, "libraries", "org", "lwjgl", "lwjgl", "3.3.1"));
        File.WriteAllText(Path.Combine(gameDir, "libraries", "org", "lwjgl", "lwjgl", "3.3.1", "lwjgl-3.3.1.jar"), "x");
        File.WriteAllText(Path.Combine(gameDir, "libraries", "org", "lwjgl", "lwjgl", "3.3.1", "lwjgl-3.3.1-natives-windows.jar"), "x");

        var filled = await AutoRepairService.VerifyFilesAsync(version, gameDir);
        Assert.True(filled.IsComplete);
        Assert.Equal(3, filled.Present);
    }

    /// <summary>8-31 修老版本「缺文件」：classifiers-only natives 库（无 artifact、无 url、有 natives）——
    /// base jar 下载侧本就跳过（服务器无此文件），校验侧不得要求它存在；只校验 natives classifier jar</summary>
    [Fact]
    public async Task VerifyFiles_ClassifiersOnlyNatives_SkipsBaseJar()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"verify-classonly-{Guid.NewGuid():N}");
        var nativeLib = new LibraryJson(
            "net.java.jinput:jinput-platform:2.0.5", null, null, null,
            new LibraryDownloads(null, new Dictionary<string, DownloadFileInfo>
            {
                ["natives-windows"] = new DownloadFileInfo("https://x/jinput-native.jar", "cc", 5),
            }),
            null,
            new Dictionary<string, string> { ["windows"] = "natives-windows" },
            null);
        var version = new VersionJson("1.12.2", "release", "net.minecraft.client.main.Main",
            null, null, null, null, [nativeLib], null, null, null, null);

        // 全空：只缺 client jar + natives jar（base jar 不在清单里，不得误报）
        var report = await AutoRepairService.VerifyFilesAsync(version, gameDir);
        Assert.Equal(2, report.Missing);
        Assert.DoesNotContain(report.MissingFiles, p => p.Contains("jinput-platform-2.0.5.jar"));
        Assert.Contains(report.MissingFiles, p => p.Contains("jinput-platform-2.0.5-natives-windows.jar"));

        // 补齐 client + natives → 完整
        Directory.CreateDirectory(Path.Combine(gameDir, "versions", "1.12.2"));
        File.WriteAllText(Path.Combine(gameDir, "versions", "1.12.2", "1.12.2.jar"), "x");
        Directory.CreateDirectory(Path.Combine(gameDir, "libraries", "net", "java", "jinput", "jinput-platform", "2.0.5"));
        File.WriteAllText(Path.Combine(gameDir, "libraries", "net", "java", "jinput", "jinput-platform", "2.0.5", "jinput-platform-2.0.5-natives-windows.jar"), "x");

        var filled = await AutoRepairService.VerifyFilesAsync(version, gameDir);
        Assert.True(filled.IsComplete);
        Assert.Equal(2, filled.Present);
    }

    /// <summary>8-31 修老版本「缺文件」：twitch 形态 natives 带 ${arch} 占位符——展开后才能匹配到 classifier jar</summary>
    [Fact]
    public async Task VerifyFiles_ArchPlaceholderNatives_ResolvesClassifier()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"verify-arch-{Guid.NewGuid():N}");
        var twitchLib = new LibraryJson(
            "tv.twitch:twitch-platform:6.5", null, null, null,
            new LibraryDownloads(null, new Dictionary<string, DownloadFileInfo>
            {
                ["natives-windows-64"] = new DownloadFileInfo("https://x/twitch-native.jar", "dd", 5),
            }),
            null,
            new Dictionary<string, string> { ["windows"] = "natives-windows-${arch}" },
            null);
        var version = new VersionJson("1.8.9", "release", "net.minecraft.client.main.Main",
            null, null, null, null, [twitchLib], null, null, null, null);

        var report = await AutoRepairService.VerifyFilesAsync(version, gameDir);
        Assert.Equal(2, report.Missing); // client + 展开后的 natives-64 jar（base 跳过）
        Assert.DoesNotContain(report.MissingFiles, p => p.Contains("twitch-platform-6.5.jar"));
        Assert.Contains(report.MissingFiles, p => p.Contains("twitch-platform-6.5-natives-windows-64.jar"));
    }

    /// <summary>AL62 哈希质检：client jar 的 sha1 元数据 → 验证通过计数；内容不符 → 不通过</summary>
    [Fact]
    public async Task VerifyFiles_HashVerification_CountsMatches()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"verify-hash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(gameDir, "versions", "1.21.11"));
        var jarPath = Path.Combine(gameDir, "versions", "1.21.11", "1.21.11.jar");
        File.WriteAllText(jarPath, "hello hash");
        var goodSha1 = System.Security.Cryptography.SHA1.HashData("hello hash"u8.ToArray());
        var badSha1 = System.Security.Cryptography.SHA1.HashData("other content"u8.ToArray());
        // 用真实 JSON 反序列化构造带 sha1 的版本（等价于官方 version json）
        var json = $$"""
            {"id":"1.21.11","mainClass":"net.minecraft.client.main.Main",
             "downloads":{"client":{"sha1":"{{Convert.ToHexStringLower(goodSha1)}}","size":11,"url":"https://x"} } }
            """;
        var withSha1 = System.Text.Json.JsonSerializer.Deserialize<VersionJson>(json)!;

        var report = await AutoRepairService.VerifyFilesAsync(withSha1, gameDir, verifyHashes: true);
        Assert.True(report.IsComplete);
        Assert.Equal(1, report.VerifiedByHash); // 哈希匹配 → 通过

        var badJson = $$"""
            {"id":"1.21.11","mainClass":"net.minecraft.client.main.Main",
             "downloads":{"client":{"sha1":"{{Convert.ToHexStringLower(badSha1)}}","size":11,"url":"https://x"} } }
            """;
        var withBadSha1 = System.Text.Json.JsonSerializer.Deserialize<VersionJson>(badJson)!;
        var bad = await AutoRepairService.VerifyFilesAsync(withBadSha1, gameDir, verifyHashes: true);
        Assert.Equal(0, bad.VerifiedByHash); // 哈希不符 → 0 通过（存在性仍完整）
    }

    // ---- 8-29 溯源锁死：Fabric 26.x 冲突明细只在报错屏幕、不落盘 → 日志提取必空 ----
    // 这是已知限制的断言（不是 bug）：若未来有人以为日志提取可靠而删掉 jar 直读兜底，此测试会先红。
    // 真实案例：launch-20260829-085357.log 里只有 FormattedException 标题 + 堆栈（latest.log 同构），
    // 提取空 → 修复必须走 FindMissingDependencies（jar 直读 fabric.mod.json）兜底。

    [Fact]
    public void ExtractConflictingModIdsFromText_Fabric26StackOnly_ReturnsEmpty()
    {
        // 085357 实锤：Fabric 26.x 崩溃只在报错屏幕展示明细，日志里仅标题 + 堆栈
        var text = """
            net.fabricmc.loader.impl.FormattedException: Some of your mods are incompatible with the game or each other!
            	at net.fabricmc.loader.impl.FormattedException.ofLocalized(FormattedException.java:51)
            	at net.fabricmc.loader.impl.FabricLoaderImpl.load(FabricLoaderImpl.java:202)
            """;
        // 已知限制：提不出任何 id → 绝不能据此宣称「修复完成」；必须走 jar 直读兜底
        Assert.Empty(AutoRepairService.ExtractConflictingModIdsFromText(text));
    }

    [Fact]
    public void ExtractConflictingModIdsFromText_DetailTuple_ExtractsId()
    {
        // 明细行存在时（旧格式 / 有 '名' (id) 元组）能提出 id——证明提取只在「明细缺失」时不可靠
        var text = """
            Some of your mods are incompatible with the game or each other!
            A possible solution was found:
            - Install malilib 0.28.10-0.29.0
            More info:
            - Mod 'MiniHUD' (minihud) 0.39.9 requires malilib 0.28.10-0.29.0, which is not installed!
            """;
        var ids = AutoRepairService.ExtractConflictingModIdsFromText(text);
        Assert.Contains("minihud", ids);
    }

    [Fact]
    public void ExtractConflictingModIdsFromText_KeywordDiagnosticLine_ExtractsId()
    {
        // 8-26 补的新版 loader 诊断行（无引号括号）：关键字后第一个 token 即 mod id
        var text = "HARD_DEP_INCOMPATIBLE_PRESELECTED entityculling 1.7.3 {depends minecraft @ [1.21.x]}";
        var ids = AutoRepairService.ExtractConflictingModIdsFromText(text);
        Assert.Contains("entityculling", ids);
    }
}
