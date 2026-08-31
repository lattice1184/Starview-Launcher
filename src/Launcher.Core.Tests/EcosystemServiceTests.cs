using System.Net;
using Launcher.Core.Model.Modrinth;
using Launcher.Core.Services;
using Launcher.Core.Utils;

namespace Launcher.Core.Tests;

/// <summary>生态服务静态工具离线测试（不依赖网络）</summary>
public class EcosystemServiceTests
{
    /// <summary>8-31 关镜像跑 GetVersionsAsync：测试 stub 只覆盖官方路径，真机 ModrinthMirrorEnabled 默认开
    /// 会让服务先查 mcimirror 镜像（未 stub → 404）再回退官方 = 2 次请求破 Assert.Single。测试与真机设置解耦。</summary>
    private static async Task<T> WithMirrorOffAsync<T>(Func<Task<T>> body)
    {
        var s = LauncherSettings.Current;
        var original = s.ModrinthMirrorEnabled;
        s.ModrinthMirrorEnabled = false;
        try { return await body(); }
        finally { s.ModrinthMirrorEnabled = original; }
    }

    // ---------- BuildFacets ----------

    [Fact]
    public void BuildFacets_TypeOnly()
    {
        var facets = EcosystemService.BuildFacets(ProjectType.Mod, null, null);
        Assert.Equal("[[\"project_type:mod\"]]", facets);
    }

    [Theory]
    [InlineData(ProjectType.Modpack, "modpack")]
    [InlineData(ProjectType.Resourcepack, "resourcepack")]
    [InlineData(ProjectType.Shader, "shader")]
    [InlineData(ProjectType.Datapack, "datapack")] // 8-26 修：曾落入 "mod" → 数据包页搜出模组
    public void FacetName_MapsCorrectly(ProjectType type, string expected)
        => Assert.Equal(expected, EcosystemService.FacetName(type));

    [Fact]
    public void BuildFacets_TypeVersionLoader()
    {
        var facets = EcosystemService.BuildFacets(ProjectType.Mod, "1.21.1", "fabric");
        Assert.Equal("[[\"project_type:mod\"],[\"versions:1.21.1\"],[\"categories:fabric\"]]", facets);
    }

    [Fact]
    public void BuildFacets_TypeVersionLoaderCategory()
    {
        var facets = EcosystemService.BuildFacets(ProjectType.Mod, "1.21.1", "fabric", "optimization");
        Assert.Equal("[[\"project_type:mod\"],[\"versions:1.21.1\"],[\"categories:fabric\"],[\"categories:optimization\"]]", facets);
    }

    [Fact]
    public void BuildFacets_CategoryOnly()
    {
        var facets = EcosystemService.BuildFacets(ProjectType.Mod, null, null, "utility");
        Assert.Equal("[[\"project_type:mod\"],[\"categories:utility\"]]", facets);
    }

    [Fact]
    public void BuildFacets_LoaderAndCategoryForceLowercase()
    {
        // UI 传 "Fabric"/"NeoForge" 大写 → facets 必须小写（Modrinth API 要求，否则 0 结果）
        var facets = EcosystemService.BuildFacets(ProjectType.Mod, null, "Fabric", "OPTIMIZATION");
        Assert.Equal("[[\"project_type:mod\"],[\"categories:fabric\"],[\"categories:optimization\"]]", facets);
    }

    // ---------- TryParseGameVersion ----------

    [Theory]
    [InlineData("1.21.1", "1.21.1")]
    [InlineData("1.21.1-Fabric", "1.21.1")]
    [InlineData("1.20.4", "1.20.4")]
    public void TryParseGameVersion_Succeeds(string instanceId, string expected)
    {
        Assert.True(EcosystemService.TryParseGameVersion(instanceId, out var version));
        Assert.Equal(expected, version);
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("26.2-Fabric 0.19.3")]
    [InlineData("")]
    public void TryParseGameVersion_Fails(string instanceId)
    {
        // "26.2" 解析为版本号，但 "26.2-Fabric 0.19.3" 前缀是 26.2 —— 视为版本
        if (instanceId == "26.2-Fabric 0.19.3")
        {
            Assert.True(EcosystemService.TryParseGameVersion(instanceId, out _));
            return;
        }
        Assert.False(EcosystemService.TryParseGameVersion(instanceId, out _));
    }

    // ---------- ResolveGameVersion（8-26：McVersion 优先——fabric-loader-… 实例名也正确）----------

    [Theory]
    [InlineData("26.1.2", "fabric-loader-0.19.3-26.1.2", "26.1.2")] // fabric 26.x：McVersion 优先（修复核心）
    [InlineData("1.21.4", "fabric-loader-0.19.3-1.21.4", "1.21.4")]  // fabric 1.x
    [InlineData("", "1.21.1-Fabric", "1.21.1")]                      // 回退实例名开头
    [InlineData("", "26.1.2", "26.1.2")]                             // 原生版
    [InlineData("", "fabric-loader-0.19.3-26.1.2", "")]              // 名不解析且无 McVersion → 空（不瞎猜）
    [InlineData("", "foo", "")]                                      // 自定义名
    public void ResolveGameVersion_McVersionFirst(string mcVersion, string instanceName, string expected)
        => Assert.Equal(expected, EcosystemService.ResolveGameVersion(mcVersion, instanceName));

    // ---------- GuessLoader ----------

    [Theory]
    [InlineData("1.21.1-Fabric", "fabric")]
    [InlineData("1.20.1-forge", "forge")]
    [InlineData("neoforge-1.21", "neoforge")]
    [InlineData("quilt-1.19", "quilt")]
    [InlineData("iris-1.21.1", "iris")]
    [InlineData("optifine-1.20", "optifine")]
    [InlineData("1.21.1", null)]
    public void GuessLoader_Detects(string instanceId, string? expected)
        => Assert.Equal(expected, EcosystemService.GuessLoader(instanceId));

    // ---------- ResolveSubDir / ResolveInstallPath ----------

    [Theory]
    [InlineData(ProjectType.Mod, "mods")]
    [InlineData(ProjectType.Resourcepack, "resourcepacks")]
    [InlineData(ProjectType.Shader, "shaderpacks")]
    [InlineData(ProjectType.Modpack, null)]
    public void ResolveSubDir_Maps(ProjectType type, string? expected)
        => Assert.Equal(expected, EcosystemService.ResolveSubDir(type));

    [Fact]
    public void ResolveInstallPath_InstanceDirectories()
    {
        // 8-19 第二批：落点跟随版本隔离设置（默认隔离开）——不再用目录存在性猜
        var temp = Path.Combine(Path.GetTempPath(), "yanla-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "versions", "1.21.1"));
        var prev = LauncherSettings.Current.VersionIsolation;
        try
        {
            LauncherSettings.Current.VersionIsolation = true;
            Assert.Equal(Path.Combine(temp, "versions", "1.21.1", "mods"),
                EcosystemService.ResolveInstallPath(temp, "1.21.1", ProjectType.Mod));
            Assert.Equal(Path.Combine(temp, "versions", "1.21.1", "shaderpacks"),
                EcosystemService.ResolveInstallPath(temp, "1.21.1", ProjectType.Shader));
            // 实例目录缺失时也装实例目录（隔离开恒实例，落点目录被创建）
            Assert.Equal(Path.Combine(temp, "versions", "1.20.1", "mods"),
                EcosystemService.ResolveInstallPath(temp, "1.20.1", ProjectType.Mod));
            Assert.True(Directory.Exists(Path.Combine(temp, "versions", "1.20.1")));
            Assert.Equal(Path.Combine(temp, "downloads", "modpacks"),
                EcosystemService.ResolveInstallPath(temp, "any", ProjectType.Modpack));

            // 隔离关：目录存在与否都装共享目录（游戏 game_directory=根，只读根 mods）
            LauncherSettings.Current.VersionIsolation = false;
            Assert.Equal(Path.Combine(temp, "mods"),
                EcosystemService.ResolveInstallPath(temp, "1.21.1", ProjectType.Mod));
            Assert.Equal(Path.Combine(temp, "shaderpacks"),
                EcosystemService.ResolveInstallPath(temp, "1.20.1", ProjectType.Shader));
        }
        finally
        {
            LauncherSettings.Current.VersionIsolation = prev;
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    // ---------- SelectBestVersion ----------

    private static ModrinthVersion MakeVersion(string id, DateTime published, bool featured = false, bool hasFile = true,
        string? type = null)
        => new(id, "p", $"v{id}", id, null, null,
            hasFile ? [new ModrinthVersionFile(id, "u", $"{id}.jar", 1, false, null)] : null,
            null, null, 0, type, featured, published);

    [Fact]
    public void SelectBestVersion_FeaturedFirst()
    {
        var versions = new[]
        {
            MakeVersion("old", DateTime.UtcNow.AddDays(-10)),
            MakeVersion("featured", DateTime.UtcNow.AddDays(-5), featured: true),
            MakeVersion("new", DateTime.UtcNow),
        };
        var best = EcosystemService.SelectBestVersion(versions);
        Assert.Equal("featured", best!.Id);
    }

    [Fact]
    public void SelectBestVersion_NewestWhenNoFeatured()
    {
        var versions = new[]
        {
            MakeVersion("old", DateTime.UtcNow.AddDays(-10)),
            MakeVersion("new", DateTime.UtcNow),
        };
        Assert.Equal("new", EcosystemService.SelectBestVersion(versions)!.Id);
    }

    [Fact]
    public void SelectBestVersion_FiltersNoFileVersions()
    {
        var versions = new[]
        {
            MakeVersion("nofile", DateTime.UtcNow, hasFile: false),
            MakeVersion("withfile", DateTime.UtcNow.AddDays(-1)),
        };
        Assert.Equal("withfile", EcosystemService.SelectBestVersion(versions)!.Id);
    }

    [Fact]
    public void SelectBestVersion_EmptyReturnsNull()
        => Assert.Null(EcosystemService.SelectBestVersion([]));

    [Fact]
    public void SelectBestVersion_ReleasePreferred_OverNewerBeta()
    {
        // 8-13 根因回归：26.2 的 beta 预发布日期最新（快照总在后）——正式版必须赢
        var versions = new[]
        {
            MakeVersion("release-old", DateTime.UtcNow.AddDays(-30), type: "release"),
            MakeVersion("beta-new", DateTime.UtcNow, type: "beta"),
        };
        Assert.Equal("release-old", EcosystemService.SelectBestVersion(versions)!.Id);
    }

    [Fact]
    public void SelectBestVersion_BetaOverAlpha()
    {
        // 无正式版时：beta 优先于 alpha（快照间也有稳定度排序）
        var versions = new[]
        {
            MakeVersion("alpha-new", DateTime.UtcNow, type: "alpha"),
            MakeVersion("beta-old", DateTime.UtcNow.AddDays(-10), type: "beta"),
        };
        Assert.Equal("beta-old", EcosystemService.SelectBestVersion(versions)!.Id);
    }

    // ---------- PickPrimaryFile ----------

    [Fact]
    public void PickPrimaryFile_PrimaryFirst()
    {
        var files = new List<ModrinthVersionFile>
        {
            new("a", "u", "a.jar", 1, false, null),
            new("b", "u", "b.jar", 1, true, null),
        };
        Assert.Equal("b", EcosystemService.PickPrimaryFile(files)!.Id);
    }

    [Fact]
    public void PickPrimaryFile_FirstWhenNoPrimary()
    {
        var files = new List<ModrinthVersionFile>
        {
            new("a", "u", "a.jar", 1, false, null),
            new("c", "u", "c.jar", 1, false, null),
        };
        Assert.Equal("a", EcosystemService.PickPrimaryFile(files)!.Id);
    }

    [Fact]
    public void PickPrimaryFile_NullOrEmptyReturnsNull()
    {
        Assert.Null(EcosystemService.PickPrimaryFile(null));
        Assert.Null(EcosystemService.PickPrimaryFile([]));
    }

    // ---------- 中文搜索重排（A 修复） ----------

    [Theory]
    [InlineData("自定义", true)]
    [InlineData("自定义联机", true)]
    [InlineData("custom", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsChineseQuery_DetectsCjk(string? q, bool expected)
        => Assert.Equal(expected, EcosystemService.IsChineseQuery(q));

    [Fact]
    public void MatchScore_TitleBeatsDescriptionBeatsNone()
    {
        Assert.Equal(3, EcosystemService.MatchScore("自定义联机 Mod", "描述", "自定义"));
        Assert.Equal(2, EcosystemService.MatchScore("普通模组", "支持自定义配置", "自定义"));
        Assert.Equal(0, EcosystemService.MatchScore("普通模组", "普通描述", "自定义"));
        // 大小写不敏感（英文词也适用）
        Assert.Equal(3, EcosystemService.MatchScore("Custom Recipes", "x", "custom"));
    }

    [Fact]
    public void ReorderMatches_ChineseQuery_PutsTitleMatchesFirst()
    {
        // 模拟 Modrinth 中文搜索：字幕高亮因描述含「自定义」排第一，标题匹配的沉在后面
        var items = new List<(string Title, string Desc)>
        {
            ("字幕高亮", "为不同字幕添加样式以区分，同时提供对单个字幕的自定义"),
            ("BLZYの自定义进度", "Replaces the vanilla advancement screen"),
            ("马夫鱼的弩", "The original Crossbow is enhanced"),
        };
        var reordered = EcosystemService.ReorderMatches(items, "自定义", x => x.Title, x => x.Desc);

        Assert.Equal("BLZYの自定义进度", reordered[0].Title); // 标题匹配升顶
        Assert.Equal("字幕高亮", reordered[1].Title);          // 描述匹配次之
        Assert.Equal("马夫鱼的弩", reordered[2].Title);        // 无匹配沉底
    }

    [Fact]
    public void ReorderMatches_SameScoreKeepsSourceOrder()
    {
        var items = new List<(string Title, string Desc)>
        {
            ("甲", "都含自定义A"),
            ("乙", "都含自定义B"),
            ("丙", "都含自定义C"),
        };
        var reordered = EcosystemService.ReorderMatches(items, "自定义", x => x.Title, x => x.Desc);

        Assert.Equal(["甲", "乙", "丙"], reordered.Select(x => x.Title));
    }

    // ---------- 游戏版本语义比较（8-12：26.2 这类 YY.M 新格式混排） ----------

    [Fact]
    public void CompareGameVersions_YyM_NewerThanPatch()
    {
        // 2026 新格式：26.2 > 1.21.6（字符串序 26.2 < 1.21.6 会判反——语义序必须对）
        Assert.True(EcosystemService.CompareGameVersions("26.2", "1.21.6") > 0);
        Assert.True(EcosystemService.CompareGameVersions("1.21.6", "26.2") < 0);
    }

    [Fact]
    public void CompareGameVersions_PatchNumeric()
    {
        // 补丁号数字比较：1.21.10 > 1.21.6（字符串序 "1.21.10" < "1.21.6"）
        Assert.True(EcosystemService.CompareGameVersions("1.21.10", "1.21.6") > 0);
        Assert.True(EcosystemService.CompareGameVersions("1.21.6", "1.21.10") < 0);
    }

    [Fact]
    public void CompareGameVersions_Equal()
    {
        Assert.Equal(0, EcosystemService.CompareGameVersions("1.21.1", "1.21.1"));
    }

    [Fact]
    public void FilterGameVersionOptions_ReleaseOnly_SemanticDesc()
    {
        // 26.2(release)/1.21.10(release)/1.21.6(release) 进；1.15.2(release 低于下限)与
        // 25w46a(snapshot) 不进；结果语义降序 26.2 排最上
        var entries = new[]
        {
            new VersionManifestService.GameVersionEntry("25w46a", "snapshot", false, default, null, ""),
            new VersionManifestService.GameVersionEntry("1.15.2", "release", false, default, null, ""),
            new VersionManifestService.GameVersionEntry("1.21.6", "release", false, default, null, ""),
            new VersionManifestService.GameVersionEntry("26.2", "release", false, default, null, ""),
            new VersionManifestService.GameVersionEntry("1.21.10", "release", false, default, null, ""),
        };
        var result = VersionManifestService.FilterGameVersionOptions(entries);
        Assert.Equal(["26.2", "1.21.10", "1.21.6"], result);
    }

    // ---------- 8-19 补 2：GetVersionsAsync 年份号空结果降级（26.2 Modrinth versions API 不认） ----------

    /// <summary>按 PathAndQuery 路由 JSON（仿 CfStubHandler；404 会触发 GetJsonAsync 重试，测试全部路由避免计数干扰）</summary>
    private sealed class RouteHandler : HttpMessageHandler
    {
        public readonly List<string> Urls = [];
        private readonly Dictionary<string, string> _routes = [];

        public void Route(string pathAndQuery, string json) => _routes[pathAndQuery] = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(_routes.TryGetValue(request.RequestUri!.PathAndQuery, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private const string VersionsJson = """
        [{"id":"v1","project_id":"abc","name":"v1.0","version_number":"v1.0","game_versions":["1.21.1"],
        "loaders":["fabric"],"files":[{"id":"f1","url":"https://cdn.example.com/f.jar","filename":"f.jar","size":5,"primary":true}],
        "date_published":"2024-01-01T00:00:00Z"}]
        """;

    [Fact]
    public async Task GetVersionsAsync_YearFormat_QueriesFullListOnce_LoaderPreserved()
    {
        // 8-22 改：年份号直接全量一次（旧实现先查空再降级 = 2 次串行）——安装链卡半分钟的主因
        var handler = new RouteHandler();
        handler.Route("/v2/project/abc/version?loaders=%5B%22fabric%22%5D", VersionsJson);
        var svc = new EcosystemService(new HttpClient(handler), cacheDir: Path.Combine(Path.GetTempPath(), "eco-test-" + Guid.NewGuid().ToString("N")));

        var list = await WithMirrorOffAsync(() => svc.GetVersionsAsync("abc", "26.2", "fabric"));

        Assert.NotEmpty(list);
        Assert.Single(handler.Urls);                         // 恰 1 次全量
        Assert.DoesNotContain("game_versions", handler.Urls[0]);
        Assert.Contains("loaders", handler.Urls[0]);         // loader 保留
    }

    [Fact]
    public async Task GetVersionsAsync_YearFormat_EmptyFullList_ReturnsEmptyOnce()
    {
        var handler = new RouteHandler();
        handler.Route("/v2/project/abc/version", "[]");
        var svc = new EcosystemService(new HttpClient(handler), cacheDir: Path.Combine(Path.GetTempPath(), "eco-test-" + Guid.NewGuid().ToString("N")));

        var list = await WithMirrorOffAsync(() => svc.GetVersionsAsync("abc", "26.2"));

        Assert.Empty(list);
        Assert.Single(handler.Urls);                         // 全量也空 → 1 次返回，防循环
    }

    [Fact]
    public async Task GetVersionsAsync_TraditionalEmpty_NoFallback()
    {
        var handler = new RouteHandler();
        handler.Route("/v2/project/abc/version?game_versions=%5B%221.21.1%22%5D", "[]");
        var svc = new EcosystemService(new HttpClient(handler), cacheDir: Path.Combine(Path.GetTempPath(), "eco-test-" + Guid.NewGuid().ToString("N")));

        var list = await WithMirrorOffAsync(() => svc.GetVersionsAsync("abc", "1.21.1"));

        Assert.Empty(list);             // 传统版本空 = 真实语义（mod 不支持 1.21.1）——不降级
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task GetVersionsAsync_TraditionalNonEmpty_NoFallback()
    {
        var handler = new RouteHandler();
        handler.Route("/v2/project/abc/version?game_versions=%5B%221.21.1%22%5D", VersionsJson);
        var svc = new EcosystemService(new HttpClient(handler), cacheDir: Path.Combine(Path.GetTempPath(), "eco-test-" + Guid.NewGuid().ToString("N")));

        var list = await WithMirrorOffAsync(() => svc.GetVersionsAsync("abc", "1.21.1"));

        Assert.NotEmpty(list);
        Assert.Single(handler.Urls);    // 传统版本正常路径不变
    }
}
