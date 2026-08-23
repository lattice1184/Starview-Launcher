using System.IO.Compression;
using Launcher.Core.Diagnostics;
using Launcher.Core.Launch;

namespace Launcher.Core.Tests;

/// <summary>AL9 自修复引擎：规则分派（FixKind 归类）与 ExtractNatives 单测</summary>
public class DiagnosticsTests
{
    [Theory]
    [InlineData("Error: Could not find or load main class net.minecraft.client.main.Main", FixKind.Redownload)]
    [InlineData("java.lang.ClassNotFoundException: net.fabricmc.loader.impl.launch.knot.KnotClient", FixKind.Redownload)]
    [InlineData("java.lang.NoClassDefFoundError: cpw/mods/modlauncher/Launcher", FixKind.Redownload)]
    [InlineData("Error: Unable to access jarfile server.jar", FixKind.Redownload)]
    [InlineData("Failed to load main manifest attribute from a.jar", FixKind.Redownload)]
    [InlineData("no lwjgl64 in java.library.path", FixKind.ReExtractNatives)]
    [InlineData("java.lang.UnsatisfiedLinkError: Could not load library lwjgl.dll", FixKind.ReExtractNatives)]
    [InlineData("java.lang.OutOfMemoryError: Java heap space", FixKind.AdviceOnly)]
    [InlineData("java.lang.UnsupportedClassVersionError: 61.0 has been compiled by a more recent version", FixKind.AdviceOnly)]
    [InlineData("java.net.BindException: Address already in use", FixKind.AdviceOnly)]
    public void DiagnoseDetailed_ClassifiesFixKind(string logLine, FixKind expected)
    {
        var hits = LogDiagnostics.DiagnoseDetailed(logLine);

        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Fix == expected);
    }

    [Fact]
    public void DiagnoseDetailed_SamePatternReportedOnce()
    {
        var text = "Error: Could not find or load main class X\nError: Could not find or load main class Y";

        var hits = LogDiagnostics.DiagnoseDetailed(text);

        Assert.Single(hits);
        Assert.Equal(FixKind.Redownload, hits[0].Fix);
    }

    [Fact]
    public void DiagnoseDetailed_EmptyText_ReturnsEmpty()
    {
        Assert.Empty(LogDiagnostics.DiagnoseDetailed(""));
        Assert.Empty(LogDiagnostics.DiagnoseDetailed("   \n  "));
        Assert.Empty(LogDiagnostics.DiagnoseDetailed("正常日志，没有已知错误模式"));
    }

    [Fact]
    public void Diagnose_LegacyWrapper_KeepsOutputFormat()
    {
        var lines = LogDiagnostics.Diagnose("Error: Could not find or load main class X");

        Assert.Single(lines);
        Assert.StartsWith("▸ 匹配：", lines[0]);
        Assert.Contains("说明：", lines[0]);
    }

    [Fact]
    public void ExtractNatives_ExtractsDlls_AndClearFirstWipesResidual()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nat-{Guid.NewGuid():N}");
        var nativesDir = Path.Combine(dir, "natives");
        var jarPath = Path.Combine(dir, "lwjgl.jar");
        Directory.CreateDirectory(nativesDir);
        try
        {
            // 造一个含 dll（应提取）+ 其他文件（忽略）的假 natives jar
            using (var zip = ZipFile.Open(jarPath, ZipArchiveMode.Create))
            {
                using (var e1 = zip.CreateEntry("lib/lwjgl.dll").Open())
                using (var w1 = new StreamWriter(e1))
                    w1.Write("dll-data");
                using (var e2 = zip.CreateEntry("lib/README.txt").Open())
                using (var w2 = new StreamWriter(e2))
                    w2.Write("ignore");
            }
            // 残留文件：clearFirst 应清除
            File.WriteAllText(Path.Combine(nativesDir, "stale.dll"), "old");

            GameLaunchService.ExtractNatives([jarPath], nativesDir, clearFirst: true);

            Assert.True(File.Exists(Path.Combine(nativesDir, "lwjgl.dll")), "dll 应被解压到 natives 根目录");
            Assert.False(File.Exists(Path.Combine(nativesDir, "README.txt")), "非 dll 文件应被忽略");
            Assert.False(File.Exists(Path.Combine(nativesDir, "stale.dll")), "clearFirst 应清除残留");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------- AL43 退出码诊断 ----------

    [Fact]
    public void RealmsError_AdviceOnly()
    {
        var hits = LogDiagnostics.DiagnoseDetailed(
            "<log4j:Message><![CDATA[Couldn't connect to realms]]></log4j:Message>");

        Assert.Contains(hits, h => h.Fix == FixKind.AdviceOnly && h.Explanation.Contains("Realms"));
    }

    [Fact]
    public void DiagnoseExit_NegativeOne_AddsTerminatedHint()
    {
        var hits = LogDiagnostics.DiagnoseExit(-1, "Created: 1024x512x0 minecraft:textures/atlas/blocks.png-atlas");

        Assert.Contains(hits, h => h.Explanation.Contains("被强制终止"));
    }

    [Fact]
    public void DiagnoseExit_EmptyAndOtherCode_AddsFallback()
    {
        var hits = LogDiagnostics.DiagnoseExit(0, "正常日志，没有已知错误模式");

        Assert.Contains(hits, h => h.Explanation.Contains("未检测到已知崩溃模式"));
    }

    // ---------- 8-23 模组版本不匹配（Incompatible mods found）→ DisableConflictingMods ----------

    [Fact]
    public void DiagnoseDetailed_IncompatibleModsFound_ClassifiesDisableConflictingMods()
    {
        // 用户 26.1 实例真实报错：iris/sodium 是 1.21 版、malilib/tweakeroo 要求 26.2
        var log =
            "Reason: [HARD_DEP iris 1.7.3+mc1.21 {depends minecraft @ [1.21.x]}, HARD_DEP sodium 0.5.11+mc1.21 " +
            "{depends minecraft @ [1.21.x]}, HARD_DEP malilib 0.29.4 {depends minecraft @ [~26.2-]}, " +
            "NEG_HARD_DEP malilib 0.29.4 {breaks sodium @ [<0.9.0-]}, HARD_DEP tweakeroo 0.29.3 " +
            "{depends minecraft @ [~26.2-]}]\n" +
            "Incompatible mods found!\n" +
            "Some of your mods are incompatible with the game or each other!\n" +
            "将 模组 'Iris' (iris) 1.7.3+mc1.21 替换为与这一模组兼容的 任意版本";

        var hits = LogDiagnostics.DiagnoseDetailed(log);

        Assert.Contains(hits, h => h.Fix == FixKind.DisableConflictingMods);
    }

    [Fact]
    public void FixConflictingMods_DisablesMatchingJars()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"modrepair-{Guid.NewGuid():N}");
        var instanceId = "fabric-loader-0.19.3-26.1";
        var modsDir = Path.Combine(dir, "versions", instanceId, "mods");
        Directory.CreateDirectory(modsDir);
        try
        {
            // 造两个假模组 jar：iris（应被禁用）+ 一个无关 mod（应保留）
            CreateFakeFabricMod(Path.Combine(modsDir, "iris-1.7.3.jar"), "iris");
            CreateFakeFabricMod(Path.Combine(modsDir, "sodium-fabric-0.5.11.jar"), "sodium");
            CreateFakeFabricMod(Path.Combine(modsDir, "some-other-mod.jar"), "some_other");

            var result = AutoRepairService.FixConflictingMods(dir, instanceId,
                "Incompatible mods found!\n将 模组 'Iris' (iris) 1.7.3+mc1.21 替换为与这一模组兼容的 任意版本\n将 模组 'Sodium' (sodium) 0.5.11+mc1.21 替换为");

            Assert.Contains("iris", result);
            Assert.Contains("sodium", result);
            Assert.False(File.Exists(Path.Combine(modsDir, "iris-1.7.3.jar")), "iris.jar 应被重命名禁用");
            Assert.True(File.Exists(Path.Combine(modsDir, "iris-1.7.3.jar.disabled")), "iris.jar.disabled 应存在");
            Assert.False(File.Exists(Path.Combine(modsDir, "sodium-fabric-0.5.11.jar")), "sodium.jar 应被重命名禁用");
            Assert.True(File.Exists(Path.Combine(modsDir, "some-other-mod.jar")), "无关 mod 应保留");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void FixConflictingMods_NoConflicts_ReportsNoConflict()
    {
        var result = AutoRepairService.FixConflictingMods("", "x",
            "正常日志，没有 Incompatible mods found 字样");

        Assert.Contains("未识别到", result);
    }

    /// <summary>造一个含 fabric.mod.json 的最小假模组 jar</summary>
    private static void CreateFakeFabricMod(string path, string id)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var e = zip.CreateEntry("fabric.mod.json").Open())
        using (var w = new StreamWriter(e))
            w.Write("{\"schemaVersion\":1,\"id\":\"" + id + "\",\"version\":\"1.0.0\"}");
    }
}
