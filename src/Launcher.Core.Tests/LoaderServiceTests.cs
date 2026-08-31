using System.Net;
using System.Net.Http;
using Launcher.Core.Download;
using Launcher.Core.Model.Loader;

namespace Launcher.Core.Tests;

/// <summary>加载器下载源：四家 meta 解析 / 计划构造 / Fabric 直装落盘 / 前置条件（离线 StubHttp）</summary>
public class LoaderServiceTests
{
    private const string FabricProfileJson = """
        {"id":"fabric-loader-0.16.13-1.21.1","inheritsFrom":"1.21.1","type":"release",
         "mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient",
         "libraries":[{"name":"net.fabricmc:fabric-loader:0.16.13",
                       "url":"https://maven.fabricmc.net/",
                       "downloads":{"artifact":{"url":"https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.16.13/fabric-loader-0.16.13.jar","size":5}}}]}
        """;

    private static LoaderService CreateService(Dictionary<string, string> routes, string gameDir,
        IEnumerable<string>? delayPaths = null)
    {
        var http = new HttpClient(new StubHandler(routes, delayPaths));
        var downloads = new DownloadService(http, gameDirectory: gameDir);
        // 临时缓存目录：profile json 缓存隔离（防测试间共享 AppData 缓存污染）
        var cache = Path.Combine(Path.GetTempPath(), $"lpc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cache);
        return new LoaderService(http, downloads, gameDir, loaderProfileCacheDir: cache,
            ecoCacheDir: Path.Combine(Path.GetTempPath(), "eco-test-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task FabricMeta_StableNewestFirst()
    {
        var routes = new Dictionary<string, string>
        {
            ["/v2/versions/loader/1.21.1"] = """
                [{"loader":{"version":"0.19.3","stable":true}},
                 {"loader":{"version":"0.16.13","stable":true}},
                 {"loader":{"version":"0.14.22","stable":false}}]
                """,
        };
        var svc = CreateService(routes, Path.GetTempPath());

        var versions = await svc.GetLoaderVersionsAsync(LoaderKind.Fabric, "1.21.1", CancellationToken.None);

        Assert.Equal(3, versions.Count);
        Assert.Equal("0.19.3", versions[0].Version);
        Assert.True(versions[0].IsStable);
        Assert.False(versions[2].IsStable);
    }

    [Fact]
    public async Task QuiltMeta_BetaDetected()
    {
        var routes = new Dictionary<string, string>
        {
            ["/v3/versions/loader/1.21.1"] = """
                [{"loader":{"version":"0.20.0-beta.9"}},{"loader":{"version":"0.19.1"}}]
                """,
        };
        var svc = CreateService(routes, Path.GetTempPath());

        var versions = await svc.GetLoaderVersionsAsync(LoaderKind.Quilt, "1.21.1", CancellationToken.None);

        Assert.Equal(2, versions.Count);
        Assert.False(versions[0].IsStable); // -beta.9
        Assert.True(versions[1].IsStable);  // 0.19.1
    }

    [Fact]
    public async Task ForgePromos_RecommendedFirst()
    {
        var routes = new Dictionary<string, string>
        {
            ["/net/minecraftforge/forge/promotions_slim.json"] =
                """{"promos":{"1.21.1-recommended":"52.1.0","1.21.1-latest":"52.1.16"}}""",
        };
        var svc = CreateService(routes, Path.GetTempPath());

        var versions = await svc.GetLoaderVersionsAsync(LoaderKind.Forge, "1.21.1", CancellationToken.None);

        Assert.Equal(2, versions.Count);
        Assert.Equal("52.1.0", versions[0].Version);
        Assert.True(versions[0].IsStable);
        Assert.Equal("52.1.16", versions[1].Version);
    }

    [Fact]
    public async Task NeoForgeMeta_PrefixFilteredAndNumericSorted()
    {
        var routes = new Dictionary<string, string>
        {
            ["/releases/net/neoforged/neoforge/maven-metadata.xml"] = """
                <metadata><versioning><versions>
                  <version>21.1.99</version>
                  <version>21.1.110</version>
                  <version>26.2.0.41-beta</version>
                </versions></versioning></metadata>
                """,
        };
        var svc = CreateService(routes, Path.GetTempPath());

        var versions = await svc.GetLoaderVersionsAsync(LoaderKind.NeoForge, "1.21.1", CancellationToken.None);

        Assert.Equal(2, versions.Count); // 26.x 不属于 21.1. 前缀
        Assert.Equal("21.1.110", versions[0].Version); // 数字比较 110 > 99
        Assert.Equal("21.1.99", versions[1].Version);
    }

    /// <summary>8-31 真竞速回归：镜像路径被延迟 400ms → 官方 0ms 先解析成功 → 竞速必须返回官方内容
    /// （旧串行 GetJsonFirstAsync 会先死等镜像超时再试官方；竞速让「快的源」赢，不是「列表第一」）。</summary>
    [Fact]
    public async Task FabricMeta_Race_FastOfficialWinsWhenMirrorDelayed()
    {
        var routes = new Dictionary<string, string>
        {
            ["/fabric-meta/v2/versions/loader/1.21.1"] = """[{"loader":{"version":"0.99.0","stable":true}}]""", // 镜像慢但有效
            ["/v2/versions/loader/1.21.1"] = """[{"loader":{"version":"0.19.3","stable":true}}]""",
        };
        var svc = CreateService(routes, Path.GetTempPath(), delayPaths: ["/fabric-meta/v2/versions/loader/1.21.1"]);

        var versions = await svc.GetLoaderVersionsAsync(LoaderKind.Fabric, "1.21.1", CancellationToken.None);

        Assert.Equal("0.19.3", versions[0].Version); // 快的官方胜（镜像延迟被弃用）
    }

    /// <summary>8-31 NeoForge 加 bmclapi /maven 镜像候选：官方路径延迟 400ms → 镜像 0ms 先解析成功 → 镜像胜。
    /// （镜像 404/坏 XML 时官方兜底——NeoForgeMeta_PrefixFilteredAndNumericSorted 已覆盖该回退路径）</summary>
    [Fact]
    public async Task NeoForgeMeta_Race_MirrorWinsWhenOfficialDelayed()
    {
        var routes = new Dictionary<string, string>
        {
            ["/releases/net/neoforged/neoforge/maven-metadata.xml"] =
                """<metadata><versioning><versions><version>21.1.99</version></versions></versioning></metadata>""",
            ["/maven/net/neoforged/neoforge/maven-metadata.xml"] =
                """<metadata><versioning><versions><version>21.1.110</version></versions></versioning></metadata>""",
        };
        var svc = CreateService(routes, Path.GetTempPath(), delayPaths: ["/releases/net/neoforged/neoforge/maven-metadata.xml"]);

        var versions = await svc.GetLoaderVersionsAsync(LoaderKind.NeoForge, "1.21.1", CancellationToken.None);

        Assert.Equal("21.1.110", versions[0].Version); // 快的 bmclapi 镜像胜
    }

    [Fact]
    public async Task CreatePlan_UrlsConstructedForAllKinds()
    {
        // 显式传 loaderVersion → 不触网，纯 URL 构造
        var svc = CreateService([], Path.GetTempPath());

        var fabric = await svc.CreatePlanAsync(LoaderKind.Fabric, "1.21.1", "0.16.13", CancellationToken.None);
        Assert.Equal("https://meta.fabricmc.net/v2/versions/loader/1.21.1/0.16.13/profile/json", fabric.ProfileJsonUrl);

        var quilt = await svc.CreatePlanAsync(LoaderKind.Quilt, "1.21.1", "0.20.0-beta.9", CancellationToken.None);
        Assert.Equal("https://meta.quiltmc.org/v3/versions/loader/1.21.1/0.20.0-beta.9/profile/json", quilt.ProfileJsonUrl);

        var forge = await svc.CreatePlanAsync(LoaderKind.Forge, "1.21.1", "52.1.0", CancellationToken.None);
        Assert.Equal("https://maven.minecraftforge.net/net/minecraftforge/forge/1.21.1-52.1.0/forge-1.21.1-52.1.0-installer.jar", forge.InstallerUrl);

        var neo = await svc.CreatePlanAsync(LoaderKind.NeoForge, "1.21.1", "21.1.110", CancellationToken.None);
        Assert.Equal("https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.110/neoforge-21.1.110-installer.jar", neo.InstallerUrl);
    }

    [Fact]
    public async Task FabricInstall_WritesProfileAndDownloadsChain()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"loader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(gameDir, "versions", "1.21.1"));
        try
        {
            // 父版本（原版已安装）：含 downloads.client
            File.WriteAllText(Path.Combine(gameDir, "versions", "1.21.1", "1.21.1.json"),
                """{"id":"1.21.1","mainClass":"net.minecraft.client.main.Main","libraries":[],"downloads":{"client":{"url":"https://piston/1.21.1/client.jar","size":5}}}""");

            var routes = new Dictionary<string, string>
            {
                ["/v2/versions/loader/1.21.1"] = """[{"loader":{"version":"0.16.13","stable":true}}]""",
                ["/v2/versions/loader/1.21.1/0.16.13/profile/json"] = FabricProfileJson,
            };
            var svc = CreateService(routes, gameDir);

            var plan = await svc.CreatePlanAsync(LoaderKind.Fabric, "1.21.1", "0.16.13", CancellationToken.None);
            await svc.InstallAsync(plan, (DownloadProgressHandler?)null, CancellationToken.None);

            // profile json 落盘（含继承关系）
            var id = "fabric-loader-0.16.13-1.21.1";
            var versionDir = Path.Combine(gameDir, "versions", id);
            Assert.True(File.Exists(Path.Combine(versionDir, $"{id}.json")));
            // 链解析后 client jar 落在子版本目录
            Assert.True(File.Exists(Path.Combine(versionDir, $"{id}.jar")), "client jar 应沿 inheritsFrom 链下载到子版本");
            // 加载器库落盘
            Assert.True(File.Exists(Path.Combine(gameDir, "libraries", "net", "fabricmc", "fabric-loader", "0.16.13", "fabric-loader-0.16.13.jar")));
        }
        finally { Directory.Delete(gameDir, true); }
    }

    [Fact]
    public async Task FabricInstall_MissingVanilla_Throws()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"loader-{Guid.NewGuid():N}");
        try
        {
            var routes = new Dictionary<string, string>
            {
                ["/v2/versions/loader/1.21.1"] = """[{"loader":{"version":"0.16.13","stable":true}}]""",
                ["/v2/versions/loader/1.21.1/0.16.13/profile/json"] = FabricProfileJson,
            };
            var svc = CreateService(routes, gameDir);

            var plan = await svc.CreatePlanAsync(LoaderKind.Fabric, "1.21.1", "0.16.13", CancellationToken.None);
            var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => svc.InstallAsync(plan, (DownloadProgressHandler?)null, CancellationToken.None));

            Assert.Contains("1.21.1", ex.Message);
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    /// <summary>AL29 H3/H6 补位：下载阶段静默跳过的库（json 漏配 url/downloads 的条目）安装后必须被校验抓到——
    /// 「安装完成」 != 「文件完整」。Fabric 直装路径与 VersionInstaller 路径（H6）同等保证。</summary>
    [Fact]
    public async Task FabricInstall_SilentlySkippedLibrary_ThrowsAfterInstall()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"loader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(gameDir, "versions", "1.21.1"));
        try
        {
            File.WriteAllText(Path.Combine(gameDir, "versions", "1.21.1", "1.21.1.json"),
                """{"id":"1.21.1","mainClass":"net.minecraft.client.main.Main","libraries":[],"downloads":{"client":{"url":"https://piston/1.21.1/client.jar","size":5}}}""");

            // 第二条库漏配 url/downloads → 下载阶段无任何动作（静默跳过）→ 安装后校验必须报缺文件
            const string profileJson = """
                {"id":"fabric-loader-0.16.13-1.21.1","inheritsFrom":"1.21.1","type":"release",
                 "mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient",
                 "libraries":[
                    {"name":"net.fabricmc:fabric-loader:0.16.13","url":"https://maven.fabricmc.net/",
                     "downloads":{"artifact":{"url":"https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.16.13/fabric-loader-0.16.13.jar","size":5}}},
                    {"name":"net.example:missing:1.0"}]}
                """;
            var routes = new Dictionary<string, string>
            {
                ["/v2/versions/loader/1.21.1"] = """[{"loader":{"version":"0.16.13","stable":true}}]""",
                ["/v2/versions/loader/1.21.1/0.16.13/profile/json"] = profileJson,
            };
            var svc = CreateService(routes, gameDir);

            var plan = await svc.CreatePlanAsync(LoaderKind.Fabric, "1.21.1", "0.16.13", CancellationToken.None);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.InstallAsync(plan, (DownloadProgressHandler?)null, CancellationToken.None));

            Assert.Contains("缺 1 个文件", ex.Message);
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    // ---------- AL29 真机回归：Forge 组路径（安装器 stub 注入，无需 java） ----------

    private const string VanillaJson21_10 = """
        {"id":"1.21.10","mainClass":"net.minecraft.client.main.Main","libraries":[],
         "downloads":{"client":{"url":"https://piston/1.21.10/client.jar","size":5}}}
        """;

    /// <summary>种子原版版本目录（json 无 jar——真机 22:38 预取残件同款）</summary>
    private static string SeedVanilla(string gameDir, string mcVersion)
    {
        var dir = Path.Combine(gameDir, "versions", mcVersion);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{mcVersion}.json"), VanillaJson21_10);
        return dir;
    }

    /// <summary>Forge 组路径：EnqueueGroup 驱动（与真实下载页同语义），HTTP 全走 StubHandler</summary>
    private static async Task<DownloadTask> RunGroupInstallAsync(string gameDir,
        Func<string, string[], Action<string>?, CancellationToken, Task<int>> installerProcess)
    {
        var http = new HttpClient(new StubHandler([]));
        var downloads = new DownloadService(http, gameDirectory: gameDir);
        var svc = new LoaderService(http, downloads, gameDir, installerProcess,
            ecoCacheDir: Path.Combine(Path.GetTempPath(), "eco-test-" + Guid.NewGuid().ToString("N")));
        var plan = new LoaderInstallPlan(LoaderKind.Forge, "1.21.10", "60.1.0", null,
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.21.10-60.1.0/forge-1.21.10-60.1.0-installer.jar",
            null, null); // 与 CreatePlanAsync 生产一致：Sha1/Size 均为 null
        var mgr = new DownloadManager(null);
        var task = mgr.EnqueueGroup($"下载 1.21.10 + Forge", (ctx, ct) => svc.InstallAsync(plan, ctx, ct));
        await task.Completion;
        // REVIEW-节流：等 State 终态稳定（SetState Post 与 Completion 的调度时序——防断言中间态）
        for (var i = 0; i < 200 && task.State is not (DownloadTaskState.Completed or DownloadTaskState.Failed or DownloadTaskState.Canceled); i++)
            await Task.Delay(10);
        return task;
    }

    /// <summary>AL29 真机场景 1（22:41 复现）：安装器退出 0 但什么都没写 → 安装后校验拦下：
    /// 任务 Failed、不误打安装标记（fix A：先校验后标记）、运行前已补 launcher_profiles.json（fix B）。</summary>
    [Fact]
    public async Task ForgeInstall_InstallerWroteNothing_GroupFailed_NoMark_ProfilesStub()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"loader-{Guid.NewGuid():N}");
        SeedVanilla(gameDir, "1.21.10");
        try
        {
            var profilesPresentAtRun = false;
            var task = await RunGroupInstallAsync(gameDir, (_, _, _, _) =>
            {
                profilesPresentAtRun = File.Exists(Path.Combine(gameDir, "launcher_profiles.json"));
                return Task.FromResult(0); // 安装器「成功」退出但什么都不写
            });

            Assert.True(profilesPresentAtRun, "运行安装器前应已补写 launcher_profiles.json");
            Assert.Equal(DownloadTaskState.Failed, task.State);
            Assert.Contains("缺 1 个文件", task.Error ?? "");
            Assert.False(File.Exists(Path.Combine(gameDir, "versions", "1.21.10", ".yanla-installed")),
                "校验失败不得打安装标记（先校验后标记）");
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    /// <summary>AL29 真机场景 2：安装器非零退出 → 必须如实报「安装器执行失败」，
    /// 不得被 FindNewestVersionDir+校验误报成「缺 N 个文件」掩盖根因（fix C：子任务失败显式传播）。</summary>
    [Fact]
    public async Task ForgeInstall_InstallerExitNonZero_RealErrorPropagated()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"loader-{Guid.NewGuid():N}");
        SeedVanilla(gameDir, "1.21.10");
        try
        {
            var task = await RunGroupInstallAsync(gameDir, (_, _, _, _) => Task.FromResult(5));

            Assert.Equal(DownloadTaskState.Failed, task.State);
            Assert.Contains("安装器执行失败（退出码 5）", task.Error ?? "");
            Assert.DoesNotContain("缺", task.Error ?? "");
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    /// <summary>launcher_profiles.json 已存在（可能是官方启动器真实配置）→ 不得覆盖。</summary>
    [Fact]
    public async Task ForgeInstall_LauncherProfilesExisting_NotOverwritten()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"loader-{Guid.NewGuid():N}");
        SeedVanilla(gameDir, "1.21.10");
        try
        {
            File.WriteAllText(Path.Combine(gameDir, "launcher_profiles.json"), "KEEP");
            var contentAtRun = "";
            var task = await RunGroupInstallAsync(gameDir, (_, _, _, _) =>
            {
                contentAtRun = File.ReadAllText(Path.Combine(gameDir, "launcher_profiles.json"));
                return Task.FromResult(0);
            });

            Assert.Equal("KEEP", contentAtRun);
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    /// <summary>成功路径回归（真机语义，Forge 1.21.10 实测）：安装器写出版本 json（继承原版）、
    /// client jar 落父版本目录、client classifier 库 url 为空（继承引用）→ 校验不得误报 → 组 Completed + 打标记。</summary>
    [Fact]
    public async Task ForgeInstall_Success_CompletesAndMarks()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"loader-{Guid.NewGuid():N}");
        var vanillaDir = SeedVanilla(gameDir, "1.21.10");
        try
        {
            var task = await RunGroupInstallAsync(gameDir, (_, _, _, _) =>
            {
                // 模拟真实 Forge 安装器：json 落 forge 目录，client jar 落父版本目录，classifier 库 url 空
                var forgeDir = Path.Combine(gameDir, "versions", "forge-1.21.10-60.1.0");
                Directory.CreateDirectory(forgeDir);
                File.WriteAllText(Path.Combine(forgeDir, "forge-1.21.10-60.1.0.json"),
                    """{"id":"forge-1.21.10-60.1.0","inheritsFrom":"1.21.10","mainClass":"net.minecraftforge.client.main.ForgeMain","libraries":[{"name":"net.minecraftforge:forge:1.21.10-60.1.0:client","downloads":{"artifact":{"path":"net/minecraftforge/forge/1.21.10-60.1.0/forge-1.21.10-60.1.0-client.jar","url":"","size":31989134}}}]}""");
                File.WriteAllText(Path.Combine(vanillaDir, "1.21.10.jar"), "12345"); // 父版本目录（真机实测落盘位置）
                return Task.FromResult(0);
            });

            Assert.Equal(DownloadTaskState.Completed, task.State);
            Assert.True(File.Exists(Path.Combine(gameDir, "versions", "forge-1.21.10-60.1.0", ".yanla-installed")),
                "校验通过后应打安装标记");
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    /// <summary>AL30 真机根因回归：修复路径（VersionInstaller.InstallAsync + EnqueueGroup，VersionBrowseViewModel.Repair 同语义）
    /// 对 forge 版本——client classifier 库 url 空（继承引用，无实体下载目标）→ pipeline 必须跳过，
    /// 不得建失败子任务（旧行为：子任务 Failed → 组任务 Failed + Error 被 Post 时序吞掉，真机 08-07 10:37
    /// 「修复 1.21.10-forge-60.1.0」Failed + Error=null 即此）。</summary>
    [Fact]
    public async Task RepairPath_UrlEmptyClassifier_Skipped_GroupCompletes()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"loader-{Guid.NewGuid():N}");
        SeedVanilla(gameDir, "1.21.10");
        try
        {
            var forgeId = "1.21.10-forge-60.1.0";
            var forgeDir = Path.Combine(gameDir, "versions", forgeId);
            Directory.CreateDirectory(forgeDir);
            File.WriteAllText(Path.Combine(forgeDir, $"{forgeId}.json"), """
                {"id":"1.21.10-forge-60.1.0","inheritsFrom":"1.21.10","mainClass":"net.minecraftforge.bootstrap.ForgeBootstrap",
                 "libraries":[
                   {"name":"net.minecraftforge:forge:1.21.10-60.1.0:universal",
                    "downloads":{"artifact":{"url":"https://maven.test/universal.jar","size":5}}},
                   {"name":"net.minecraftforge:forge:1.21.10-60.1.0:client",
                    "downloads":{"artifact":{"path":"net/minecraftforge/forge/1.21.10-60.1.0/forge-1.21.10-60.1.0-client.jar","url":"","size":31989134}}}]}
                """);

            var http = new HttpClient(new StubHandler([]));
            var downloads = new DownloadService(http, gameDirectory: gameDir);
            var installer = new VersionInstaller(http, downloads, gameDir);
            var version = await installer.GetOrFetchVersionJsonAsync(forgeId, null, CancellationToken.None);
            var mgr = new DownloadManager(null);
            var task = mgr.EnqueueGroup($"修复 {forgeId}", (ctx, ct) => installer.InstallAsync(version, ctx, ct));
            await task.Completion;

            Assert.Equal(DownloadTaskState.Completed, task.State);
            Assert.Null(task.Error);
            Assert.DoesNotContain(task.Children, c => c.State == DownloadTaskState.Failed);
            // 8-14 守卫语义：修复路径的目录是版本实际目录（PCL/官方扫描源同场景）——补文件≠本启动器安装，
            // 不得打标记（旧行为写 .yanla-installed → PCL 版本误标「本启动器」；自建目录安装打标记不受影响）
            Assert.False(File.Exists(Path.Combine(forgeDir, ".yanla-installed")),
                "修复路径非自建目录不得写安装标记");
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    /// <summary>按路径返回预设响应；未匹配路径返回 5 字节假文件内容（下载用）</summary>
    [Fact]
    public async Task FabricInstall_WithFabricApi_InstallsToVersionMods()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"loader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(gameDir, "versions", "1.21.1"));
        try
        {
            // 原版已安装（含 client jar 下载源）
            File.WriteAllText(Path.Combine(gameDir, "versions", "1.21.1", "1.21.1.json"),
                """{"id":"1.21.1","mainClass":"net.minecraft.client.main.Main","libraries":[],"downloads":{"client":{"url":"https://piston/1.21.1/client.jar","size":5}}}""");
            var routes = new Dictionary<string, string>
            {
                ["/v2/versions/loader/1.21.1"] = """[{"loader":{"version":"0.16.13","stable":true}}]""",
                ["/v2/versions/loader/1.21.1/0.16.13/profile/json"] = FabricProfileJson,
                ["/v2/project/fabric-api"] = """{"id":"P7dR8mSH","slug":"fabric-api","project_type":"mod","title":"Fabric API","description":"x","downloads":1,"follows":1,"date_created":"2024-01-01T00:00:00Z","date_modified":"2024-01-01T00:00:00Z"}""",
                ["/v2/project/P7dR8mSH/version"] = """[{"id":"v1","project_id":"P7dR8mSH","name":"Fabric API 0.92.1","version_number":"0.92.1+1.21.1","game_versions":["1.21.1"],"loaders":["fabric"],"files":[{"id":"f1","url":"https://cdn.modrinth.com/fabric-api-1.21.1.jar","filename":"fabric-api-1.21.1.jar","size":5,"primary":true}],"date_published":"2024-01-01T00:00:00Z"}]""",
                ["/fabric-api-1.21.1.jar"] = "12345",
            };
            var svc = CreateService(routes, gameDir);
            var plan = await svc.CreatePlanAsync(LoaderKind.Fabric, "1.21.1", "0.16.13", CancellationToken.None)
                with { InstallFabricApi = true };
            await svc.InstallAsync(plan, (DownloadProgressHandler?)null, CancellationToken.None);

            var id = "fabric-loader-0.16.13-1.21.1";
            // 加载器安装完成（标记存在）
            Assert.True(InstallMarker.IsMarked(gameDir, id), "加载器版本应已标记安装");
            // Fabric API 装进版本 mods 目录
            Assert.True(File.Exists(Path.Combine(gameDir, "versions", id, "mods", "fabric-api-1.21.1.jar")),
                "fabric-api jar 应下载到版本 mods 目录");
        }
        finally { Directory.Delete(gameDir, true); }
    }

    [Fact]
    public async Task FabricInstall_FabricApiMissing_InstallStillCompletes()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"loader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(gameDir, "versions", "1.21.1"));
        try
        {
            File.WriteAllText(Path.Combine(gameDir, "versions", "1.21.1", "1.21.1.json"),
                """{"id":"1.21.1","mainClass":"net.minecraft.client.main.Main","libraries":[],"downloads":{"client":{"url":"https://piston/1.21.1/client.jar","size":5}}}""");
            var routes = new Dictionary<string, string>
            {
                ["/v2/versions/loader/1.21.1"] = """[{"loader":{"version":"0.16.13","stable":true}}]""",
                ["/v2/versions/loader/1.21.1/0.16.13/profile/json"] = FabricProfileJson,
                // 无 fabric-api 路由 → fallback "12345" 解析失败 → 静默跳过（26.2 等新版场景）
            };
            var svc = CreateService(routes, gameDir);
            var plan = await svc.CreatePlanAsync(LoaderKind.Fabric, "1.21.1", "0.16.13", CancellationToken.None)
                with { InstallFabricApi = true };
            await svc.InstallAsync(plan, (DownloadProgressHandler?)null, CancellationToken.None);

            var id = "fabric-loader-0.16.13-1.21.1";
            Assert.True(InstallMarker.IsMarked(gameDir, id), "API 缺失不影响加载器安装完成");
            Assert.False(Directory.Exists(Path.Combine(gameDir, "versions", id, "mods")),
                "无 API 版本时 mods 目录不应出现");
        }
        finally { Directory.Delete(gameDir, true); }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _routes;
        private readonly HashSet<string>? _delayPaths;

        public StubHandler(Dictionary<string, string> routes, IEnumerable<string>? delayPaths = null)
        {
            _routes = routes;
            if (delayPaths is not null) _delayPaths = new HashSet<string>(delayPaths, StringComparer.Ordinal);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (_delayPaths?.Contains(path) == true) await Task.Delay(400, ct); // 竞速测试：慢候选 400ms
            var body = _routes.TryGetValue(path, out var json) ? json : "12345"; // 5 字节，匹配 size=5 校验
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8),
            };
        }
    }

    /// <summary>REVIEW-flake 根因回归：mtime 精确并列（密集写入同刻落盘，真机/测试均出现过）时
    /// 旧逻辑按枚举顺序选中父版本「1.21.10」→ 校验/标记打在原版目录 → forge 不显示已装。
    /// 修复后带 inheritsFrom 的安装器产出物必须胜出（tie-break）。</summary>
    [Fact]
    public async Task ForgeInstall_MtimeTie_InheritsFromWins()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"loader-{Guid.NewGuid():N}");
        var vanillaDir = SeedVanilla(gameDir, "1.21.10");
        try
        {
            var task = await RunGroupInstallAsync(gameDir, (_, _, _, _) =>
            {
                var forgeDir = Path.Combine(gameDir, "versions", "forge-1.21.10-60.1.0");
                Directory.CreateDirectory(forgeDir);
                File.WriteAllText(Path.Combine(forgeDir, "forge-1.21.10-60.1.0.json"),
                    """{"id":"forge-1.21.10-60.1.0","inheritsFrom":"1.21.10","mainClass":"net.minecraftforge.client.main.ForgeMain","libraries":[{"name":"net.minecraftforge:forge:1.21.10-60.1.0:client","downloads":{"artifact":{"path":"net/minecraftforge/forge/1.21.10-60.1.0/forge-1.21.10-60.1.0-client.jar","url":"","size":31989134}}}]}""");
                File.WriteAllText(Path.Combine(vanillaDir, "1.21.10.jar"), "12345");
                // 强制 mtime 精确并列：vanilla json 时间设为与 forge json 完全相同——
                // 旧逻辑稳定排序按枚举顺序取「1.21.10」（先创建排前）→ 标记打错目录
                var forgeJson = Path.Combine(gameDir, "versions", "forge-1.21.10-60.1.0", "forge-1.21.10-60.1.0.json");
                File.SetLastWriteTime(Path.Combine(vanillaDir, "1.21.10.json"), File.GetLastWriteTime(forgeJson));
                return Task.FromResult(0);
            });

            Assert.Equal(DownloadTaskState.Completed, task.State);
            Assert.True(File.Exists(Path.Combine(gameDir, "versions", "forge-1.21.10-60.1.0", ".yanla-installed")),
                "mtime 并列时安装标记必须打在安装器产出目录（inheritsFrom tie-break）");
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    /// <summary>8-19：GetVersionsAsync 对年份号（26.2）降级返回全量后，fabric-api 必须精确匹配 mcVersion 构建——
    /// 无对应构建（26.2 无发布/版本不匹配）→ 静默跳过，防装错版本进实例崩（fabric.mod.json 版本锁定）。</summary>
    [Fact]
    public async Task FabricInstall_FabricApiVersionMismatch_StillSkips()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"loader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(gameDir, "versions", "1.21.1"));
        try
        {
            File.WriteAllText(Path.Combine(gameDir, "versions", "1.21.1", "1.21.1.json"),
                """{"id":"1.21.1","mainClass":"net.minecraft.client.main.Main","libraries":[],"downloads":{"client":{"url":"https://piston/1.21.1/client.jar","size":5}}}""");
            var routes = new Dictionary<string, string>
            {
                ["/v2/versions/loader/1.21.1"] = """[{"loader":{"version":"0.16.13","stable":true}}]""",
                ["/v2/versions/loader/1.21.1/0.16.13/profile/json"] = FabricProfileJson,
                ["/v2/project/fabric-api"] = """{"id":"P7dR8mSH","slug":"fabric-api","project_type":"mod","title":"Fabric API","description":"x","downloads":1,"follows":1,"date_created":"2024-01-01T00:00:00Z","date_modified":"2024-01-01T00:00:00Z"}""",
                // 版本列表只有 1.20.1 构建（不含目标 1.21.1）——客户端过滤后为空 → 静默跳过
                ["/v2/project/P7dR8mSH/version"] = """[{"id":"v1","project_id":"P7dR8mSH","name":"Fabric API 0.92.0","version_number":"0.92.0+1.20.1","game_versions":["1.20.1"],"loaders":["fabric"],"files":[{"id":"f1","url":"https://cdn.modrinth.com/fabric-api-1.20.1.jar","filename":"fabric-api-1.20.1.jar","size":5,"primary":true}],"date_published":"2024-01-01T00:00:00Z"}]""",
            };
            var svc = CreateService(routes, gameDir);
            var plan = await svc.CreatePlanAsync(LoaderKind.Fabric, "1.21.1", "0.16.13", CancellationToken.None)
                with { InstallFabricApi = true };
            await svc.InstallAsync(plan, (DownloadProgressHandler?)null, CancellationToken.None);

            var id = "fabric-loader-0.16.13-1.21.1";
            Assert.True(InstallMarker.IsMarked(gameDir, id), "API 版本不匹配不影响加载器安装完成");
            Assert.False(Directory.Exists(Path.Combine(gameDir, "versions", id, "mods")),
                "版本不匹配时 mods 目录不应出现（防装错版本）");
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }
}
