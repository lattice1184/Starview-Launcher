using System.IO.Compression;
using System.Threading;
using Launcher.Core.Diagnostics;

namespace Launcher.Core.Tests;

/// <summary>
/// 启动前模组兼容检查（8-26）：版本范围匹配 + 启动前禁用。
/// 关键：旧 MC 系 mod（[1.21.x]）装进 26.1.x 游戏 → 判不兼容；同系 → 兼容；解析不出/通配 → 不误报。
/// </summary>
public class ModCompatibilityCheckerTests
{
    private static int[] Game(string v)
        => ModCompatibilityChecker.ParseGameVersion(v) is { } g ? [.. g] : [];

    // ---- RangeAllows：声明范围是否允许游戏版本 ----

    [Theory]
    [InlineData("[1.21.x]", "1.21.4", true)]   // 同系兼容
    [InlineData("[1.21.x]", "1.21.11", true)]
    [InlineData("[1.21.x]", "26.1.2", false)]  // 旧 mod 装新版游戏 → 不兼容（entityculling 场景）
    [InlineData("1.21.x", "1.21.4", true)]
    [InlineData("1.21.x", "26.1.2", false)]
    [InlineData("1.21.4", "1.21.9", true)]     // 保守：patch 差异不误报
    [InlineData("1.21.4", "26.1.2", false)]
    [InlineData("1.21", "26.1.2", false)]
    [InlineData("*", "26.1.2", true)]
    [InlineData(">=1.20", "26.1.2", true)]     // 只设下界 → 新版游戏允许
    [InlineData(">=1.20 <1.22", "26.1.2", false)] // 设了 <2.0 上界 → 老版系不兼容新版
    [InlineData(">=1.20 <1.22", "1.21.4", true)]
    [InlineData(">=26.1 <26.2", "26.1.2", true)]
    [InlineData("1.20.1,1.21.x", "1.21.4", true)] // 数组合并成 OR 列表
    [InlineData("1.20.1,1.21.x", "26.1.2", false)]
    [InlineData("(1.20, 1.22]", "1.21.4", true)]
    public void RangeAllows_GameVersion(string declared, string game, bool expected)
        => Assert.Equal(expected, ModCompatibilityChecker.RangeAllows(declared, Game(game)));

    [Fact]
    public void RangeAllows_UnparseableDeclared_IsAllowed_NoFalsePositive()
    {
        // 声明含无法解析内容（如快照号 / 未知标记）→ 保守允许，不误报
        Assert.True(ModCompatibilityChecker.RangeAllows("25w06a", Game("26.1.2")));
        Assert.True(ModCompatibilityChecker.RangeAllows(">=1.2x", Game("26.1.2")));
    }

    [Fact]
    public void ParseGameVersion_SnapshotReturnsNull()
        => Assert.Null(ModCompatibilityChecker.ParseGameVersion("25w06a"));

    [Fact]
    public void ParseGameVersion_LoaderInstanceId_ReturnsNull()
        // 8-26 回归：version.Name 是实例 id（fabric-loader-0.19.3-26.1.2）不是游戏版本——解析必须 null，
        // 预检靠 McVersion/inheritsFrom 拿真游戏版本（ResolveCheckGameVersion 兜底链），不能再把 loader 名喂给检查器
        => Assert.Null(ModCompatibilityChecker.ParseGameVersion("fabric-loader-0.19.3-26.1.2"));

    // ---- 启动前禁用（Part 1 端到端）：扫 mods 目录 → 只禁用明显不兼容的 jar ----

    private static string NewModsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"modcheck-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFabricModJar(string jarPath, string id, string depends)
        => WriteFabricModJarDepends(jarPath, id, $"{{\"minecraft\":\"{depends}\"}}");

    /// <summary>写带任意 depends 对象的 fabric mod jar（8-29 缺失前置检测用）：dependsJson 是整个 depends 的 JSON 对象文本。</summary>
    private static void WriteFabricModJarDepends(string jarPath, string id, string dependsJson)
    {
        // ZipArchiveMode.Create 用 FileMode.CreateNew——重写已存在文件会抛 IOException，先删再写
        if (File.Exists(jarPath)) File.Delete(jarPath);
        using var zip = ZipFile.Open(jarPath, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("fabric.mod.json");
        using var w = new StreamWriter(entry.Open());
        w.Write($"{{\"schemaVersion\":1,\"id\":\"{id}\",\"version\":\"1.0.0\",\"depends\":{dependsJson}}}");
    }

    [Fact]
    public void FindIncompatible_FlagsOldMcMod_KeepsCompatibleOne()
    {
        var dir = NewModsDir();
        WriteFabricModJar(Path.Combine(dir, "entityculling.jar"), "entityculling", "[1.21.x]");
        WriteFabricModJar(Path.Combine(dir, "fabric-api.jar"), "fabric-api", ">=26.1 <26.2");

        var bad = ModCompatibilityChecker.FindIncompatible(dir, "26.1.2");

        var item = Assert.Single(bad);
        Assert.Equal("entityculling", item.Id);
        Assert.Equal("entityculling.jar", item.FileName);
        Assert.Equal("[1.21.x]", item.DeclaredRange);
        Assert.Equal("26.1.2", item.GameVersion);
    }

    [Fact]
    public void DisableIncompatible_RenamesJarToDisabled_AndIsIdempotent()
    {
        var dir = NewModsDir();
        var jar = Path.Combine(dir, "entityculling.jar");
        WriteFabricModJar(jar, "entityculling", "[1.21.x]");

        var disabled = ModCompatibilityChecker.DisableIncompatible(dir, "26.1.2");

        Assert.Single(disabled);
        Assert.False(File.Exists(jar));
        Assert.True(File.Exists(jar + ".disabled"));
        // 再跑一次：.disabled 不再被匹配（幂等）
        Assert.Empty(ModCompatibilityChecker.DisableIncompatible(dir, "26.1.2"));
    }

    [Fact]
    public void DisableIncompatible_CompatibleMods_TouchNothing()
    {
        var dir = NewModsDir();
        WriteFabricModJar(Path.Combine(dir, "fabric-api.jar"), "fabric-api", ">=26.1 <26.2");

        Assert.Empty(ModCompatibilityChecker.DisableIncompatible(dir, "26.1.2"));
        Assert.True(File.Exists(Path.Combine(dir, "fabric-api.jar")));
    }

    [Fact]
    public void FindIncompatible_NonFabricOrMissingDir_ReturnsEmpty()
    {
        // 非 Fabric jar（无 fabric.mod.json）→ 不参与
        var dir = NewModsDir();
        File.WriteAllText(Path.Combine(dir, "forge-mod.jar"), "not a zip");
        Assert.Empty(ModCompatibilityChecker.FindIncompatible(dir, "26.1.2"));

        // 目录不存在 → 空
        Assert.Empty(ModCompatibilityChecker.FindIncompatible(Path.Combine(dir, "nope"), "26.1.2"));

        // 游戏版本是快照号 → 解析不出 → 跳过检查，不误报
        WriteFabricModJar(Path.Combine(dir, "entityculling.jar"), "entityculling", "[1.21.x]");
        Assert.Empty(ModCompatibilityChecker.FindIncompatible(dir, "25w06a"));
    }

    // ---- 8-27 并行扫描 + 进度回调 + 会话缓存（启动预检加速） ----

    [Fact]
    public void FindIncompatible_ProgressCallback_ReportsEveryJar_EndsWithTotal()
    {
        var dir = NewModsDir();
        WriteFabricModJar(Path.Combine(dir, "a.jar"), "a", "[1.21.x]");
        WriteFabricModJar(Path.Combine(dir, "b.jar"), "b", ">=26.1 <26.2");

        var sync = new object();
        var calls = new List<(int Done, int Total)>();
        ModCompatibilityChecker.FindIncompatible(dir, "26.1.2", (d, t) => { lock (sync) calls.Add((d, t)); });

        Assert.Equal(2, calls.Count);
        Assert.Contains(calls, c => c.Done == 2 && c.Total == 2); // 最后一个 = (总数, 总数)
        Assert.All(calls, c => Assert.Equal(2, c.Total));
    }

    [Fact]
    public void FindIncompatible_CacheHit_SkipsRescan()
    {
        var dir = NewModsDir();
        WriteFabricModJar(Path.Combine(dir, "entityculling.jar"), "entityculling", "[1.21.x]");

        Assert.Single(ModCompatibilityChecker.FindIncompatible(dir, "26.1.2")); // 首次扫描入缓存

        // 目录未变 → 命中缓存，进度回调不被调用（不重扫 zip）
        var sync = new object();
        var progressCalls = 0;
        var second = ModCompatibilityChecker.FindIncompatible(dir, "26.1.2", (_, _) => { lock (sync) progressCalls++; });
        Assert.Single(second);
        Assert.Equal(0, progressCalls);
    }

    [Fact]
    public void FindIncompatible_ModsChanged_RevalidatesCache()
    {
        var dir = NewModsDir();
        var jar = Path.Combine(dir, "m.jar");
        WriteFabricModJar(jar, "m", "[1.21.x]");
        Assert.Single(ModCompatibilityChecker.FindIncompatible(dir, "26.1.2"));

        // 同路径重写为兼容版（内容长度变化 → 指纹变化 → 缓存失效重扫）
        WriteFabricModJar(jar, "m", ">=26.1 <26.2");
        Assert.Empty(ModCompatibilityChecker.FindIncompatible(dir, "26.1.2"));
    }

    [Fact]
    public void FindIncompatible_Canceled_ThrowsOperationCanceled()
    {
        var dir = NewModsDir();
        WriteFabricModJar(Path.Combine(dir, "a.jar"), "a", "[1.21.x]");
        WriteFabricModJar(Path.Combine(dir, "b.jar"), "b", "[1.21.x]");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // 预取消 → Parallel.ForEach / 循环内 ThrowIfCancellationRequested 抛 OCE
        Assert.Throws<OperationCanceledException>(() =>
            ModCompatibilityChecker.FindIncompatible(dir, "26.1.2", ct: cts.Token));
    }

    // ---- 8-29 缺失前置检测（minihud 缺 malilib 实锤场景）：依赖方缺 mod 前置 → 报缺失 ----

    [Fact]
    public void FindMissingDependencies_MiniHudWithoutMalilib_ReportsMissing()
    {
        // 8-29 实锤场景：MiniHUD 0.39.9 需要 malilib 0.28.10-0.29.0，目录没有 malilib →
        // 预检此前只查 depends.minecraft（minihud 声明 26.x 匹配）→「无冲突」却崩到 Fabric 报错页
        var dir = NewModsDir();
        WriteFabricModJarDepends(Path.Combine(dir, "minihud.jar"), "minihud",
            "{\"minecraft\":\"[26.1.x]\",\"malilib\":\"0.28.10-0.29.0\"}");

        var missing = ModCompatibilityChecker.FindMissingDependencies(dir);

        var item = Assert.Single(missing);
        Assert.Equal("minihud", item.DependentModId);
        Assert.Equal("minihud.jar", item.DependentFileName);
        Assert.Equal("malilib", item.DepId);
        Assert.Equal("0.28.10-0.29.0", item.RequiredRange);
    }

    [Fact]
    public void FindMissingDependencies_DepInstalled_NoReport()
    {
        var dir = NewModsDir();
        WriteFabricModJarDepends(Path.Combine(dir, "minihud.jar"), "minihud",
            "{\"minecraft\":\"[26.1.x]\",\"malilib\":\"0.28.10-0.29.0\"}");
        WriteFabricModJar(Path.Combine(dir, "malilib.jar"), "malilib", ">=26.1 <26.2");

        Assert.Empty(ModCompatibilityChecker.FindMissingDependencies(dir));
    }

    [Fact]
    public void FindMissingDependencies_FabricApiBundled_NoFalsePositive()
    {
        // 装了 fabric-api 时，它捆绑的 fabric-api-base 等模块不算缺失（fabric-api 单独 jar 提供全部模块）
        var dir = NewModsDir();
        WriteFabricModJar(Path.Combine(dir, "fabric-api.jar"), "fabric-api", ">=26.1 <26.2");
        WriteFabricModJarDepends(Path.Combine(dir, "some-mod.jar"), "some-mod",
            "{\"minecraft\":\"[26.1.x]\",\"fabric-api-base\":\"*\"}");

        Assert.Empty(ModCompatibilityChecker.FindMissingDependencies(dir));
    }

    [Fact]
    public void FindMissingDependencies_StandaloneLanguageDep_StillReports()
    {
        // fabric-language-kotlin 是独立 jar，fabric-api 捆绑不覆盖——缺它必须报
        var dir = NewModsDir();
        WriteFabricModJar(Path.Combine(dir, "fabric-api.jar"), "fabric-api", ">=26.1 <26.2");
        WriteFabricModJarDepends(Path.Combine(dir, "kotlin-mod.jar"), "kotlin-mod",
            "{\"minecraft\":\"[26.1.x]\",\"fabric-language-kotlin\":\">=1.10\"}");

        var item = Assert.Single(ModCompatibilityChecker.FindMissingDependencies(dir));
        Assert.Equal("fabric-language-kotlin", item.DepId);
    }

    [Fact]
    public void FindMissingDependencies_DisabledDep_CountsMissing()
    {
        // .disabled 的前置没被加载 → 依赖它的启用 mod 仍然缺前置，必须报（否则又是「无冲突」崩掉）
        var dir = NewModsDir();
        WriteFabricModJarDepends(Path.Combine(dir, "minihud.jar"), "minihud",
            "{\"minecraft\":\"[26.1.x]\",\"malilib\":\"0.28.10-0.29.0\"}");
        WriteFabricModJar(Path.Combine(dir, "malilib.jar.disabled"), "malilib", ">=26.1 <26.2");

        var item = Assert.Single(ModCompatibilityChecker.FindMissingDependencies(dir));
        Assert.Equal("malilib", item.DepId);
    }

    [Fact]
    public void FindMissingDependencies_DisabledDependent_NotScanned()
    {
        // 依赖方自己被禁用 → 不参与（它的前置缺不缺无所谓，没加载就不崩）
        var dir = NewModsDir();
        WriteFabricModJarDepends(Path.Combine(dir, "minihud.jar.disabled"), "minihud",
            "{\"minecraft\":\"[26.1.x]\",\"malilib\":\"0.28.10-0.29.0\"}");

        Assert.Empty(ModCompatibilityChecker.FindMissingDependencies(dir));
    }

    [Fact]
    public void FindMissingDependencies_LoaderKeys_NeverReported()
    {
        // minecraft / fabricloader / java 是加载器内置，永不报缺失
        var dir = NewModsDir();
        WriteFabricModJarDepends(Path.Combine(dir, "m.jar"), "m",
            "{\"minecraft\":\"[26.1.x]\",\"fabricloader\":\">=0.19\",\"java\":\">=21\"}");

        Assert.Empty(ModCompatibilityChecker.FindMissingDependencies(dir));
    }

    [Fact]
    public void FindMissingDependencies_EmptyOrMissingDir_ReturnsEmpty()
    {
        var dir = NewModsDir();
        Assert.Empty(ModCompatibilityChecker.FindMissingDependencies(dir));
        Assert.Empty(ModCompatibilityChecker.FindMissingDependencies(Path.Combine(dir, "nope")));
    }
}
