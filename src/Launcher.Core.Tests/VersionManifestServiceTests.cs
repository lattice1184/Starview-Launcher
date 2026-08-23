using System.Net;
using System.Net.Http;
using Launcher.Core.Services;

namespace Launcher.Core.Tests;

/// <summary>AL29 C1：已安装 = json && jar——预取 json 残件不得谎报已装（下载成功但启动报缺失根因）+ AL33 清单多源</summary>
public class VersionManifestServiceTests
{
    /// <summary>按 host+path 路由状态/内容的清单 stub（串行候选链，无并发——List.Add 无需锁）</summary>
    private sealed class ManifestStubHandler : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        private readonly Dictionary<string, (int Status, string Body)> _routes = [];
        private const string DefaultBody = "{\"versions\":[]}";

        public void Route(string hostPath, int status, string body) => _routes[hostPath] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = $"{request.RequestUri!.Host}{request.RequestUri.AbsolutePath}";
            Requests.Add($"{request.Method} {key}");
            if (_routes.TryGetValue(key, out var r))
            {
                return Task.FromResult(r.Status == 200
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(r.Body) }
                    : new HttpResponseMessage((HttpStatusCode)r.Status));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(DefaultBody),
            });
        }
    }

    [Fact]
    public async Task FetchManifest_OfficialFails_MirrorSucceeds()
    {
        var handler = new ManifestStubHandler();
        const string mirrorJson = "{\"versions\":[{\"id\":\"mirror-version\",\"type\":\"release\"}]}";
        handler.Route("piston-meta.mojang.com/mc/game/version_manifest_v2.json", 500, "");
        handler.Route("bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json", 200, mirrorJson);
        var http = new HttpClient(handler);

        var json = await VersionManifestService.FetchManifestJsonAsync(http, CancellationToken.None);

        Assert.Contains("mirror-version", json); // 官方 500 → 镜像清单胜出
        Assert.Contains(handler.Requests, r => r.Contains("bmclapi2.bangbang93.com"));
    }

    [Fact]
    public async Task FetchManifest_OfficialOk_MirrorNotRequested()
    {
        var handler = new ManifestStubHandler();
        const string officialJson = "{\"versions\":[{\"id\":\"official-version\",\"type\":\"release\"}]}";
        handler.Route("piston-meta.mojang.com/mc/game/version_manifest_v2.json", 200, officialJson);
        handler.Route("bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json", 500, "");
        var http = new HttpClient(handler);

        var json = await VersionManifestService.FetchManifestJsonAsync(http, CancellationToken.None);

        Assert.Contains("official-version", json);
        Assert.Single(handler.Requests); // 官方成功即止，不请求镜像
    }

    [Fact]
    public async Task FetchManifest_AllFail_Throws()
    {
        var handler = new ManifestStubHandler();
        handler.Route("piston-meta.mojang.com/mc/game/version_manifest_v2.json", 500, "");
        handler.Route("bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json", 500, "");
        var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            VersionManifestService.FetchManifestJsonAsync(http, CancellationToken.None));

        Assert.Contains("均不可用", ex.Message);
        Assert.Equal(2, handler.Requests.Count); // 两个源都试过
    }

    private static (string gameDir, string id) MakeVersionDir(bool json, bool jar)
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"inst-{Guid.NewGuid():N}");
        var dir = Path.Combine(gameDir, "versions", "1.21.11");
        Directory.CreateDirectory(dir);
        if (json) File.WriteAllText(Path.Combine(dir, "1.21.11.json"), "{}");
        if (jar) File.WriteAllText(Path.Combine(dir, "1.21.11.jar"), "x");
        return (gameDir, "1.21.11");
    }

    [Fact]
    public void IsInstalled_JsonOnly_False()
    {
        var (g, id) = MakeVersionDir(json: true, jar: false);
        Assert.False(VersionManifestService.IsInstalled(g, id));
    }

    [Fact]
    public void IsInstalled_JsonAndJar_True()
    {
        var (g, id) = MakeVersionDir(json: true, jar: true);
        Assert.True(VersionManifestService.IsInstalled(g, id));
    }

    [Fact]
    public void IsInstalled_LoaderChild_True()
    {
        // fabric 完整安装后 client jar 沿链落子版本目录——不得误伤加载器版本
        var gameDir = Path.Combine(Path.GetTempPath(), $"inst-{Guid.NewGuid():N}");
        var dir = Path.Combine(gameDir, "versions", "fabric-loader-0.19.3-1.21.11");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "fabric-loader-0.19.3-1.21.11.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "fabric-loader-0.19.3-1.21.11.jar"), "x");
        Assert.True(VersionManifestService.IsInstalled(gameDir, "fabric-loader-0.19.3-1.21.11"));
    }

    [Fact]
    public void IsInstalled_MissingDir_False()
    {
        var (g, id) = MakeVersionDir(json: false, jar: false);
        Assert.False(VersionManifestService.IsInstalled(g, id));
    }

    // ---------- ScanUsableInstances（8-14：已装集合改三路 jar 判定——26.2 父版本 jar 落加载器子目录时
    // 不得漏标：版本页侧栏「已安装」集合与行徽章必须同口径；真机复现：下载 26.2+fabric 后侧栏 26.2 不亮） ----------

    [Fact]
    public void ScanUsableInstances_VanillaParentWithLoaderChild_Usable()
    {
        // 26.2 原版 json-only（jar 在 fabric 子版本目录）→ 必须计入可用实例
        var root = Path.Combine(Path.GetTempPath(), $"usable-{Guid.NewGuid():N}");
        var vanilla = Path.Combine(root, "versions", "26.2");
        Directory.CreateDirectory(vanilla);
        File.WriteAllText(Path.Combine(vanilla, "26.2.json"), "{}");
        var fab = Path.Combine(root, "versions", "fabric-loader-0.19.3-26.2");
        Directory.CreateDirectory(fab);
        File.WriteAllText(Path.Combine(fab, "fabric-loader-0.19.3-26.2.json"), "{\"inheritsFrom\":\"26.2\"}");
        File.WriteAllText(Path.Combine(fab, "fabric-loader-0.19.3-26.2.jar"), "x");

        var result = VersionManifestService.ScanUsableInstances([root], cleanForeignMarkers: false);

        Assert.True(result.ContainsKey("26.2"), "原版父版本经子版本 jar 应判可用");
        Assert.True(result.ContainsKey("fabric-loader-0.19.3-26.2"), "加载器版本应判可用");
    }

    [Fact]
    public void ScanUsableInstances_JsonOnlyLone_Excluded()
    {
        // 只有 json、无自身 jar、无引用子版本 → 预取残件，不得计入
        var root = Path.Combine(Path.GetTempPath(), $"usable-{Guid.NewGuid():N}");
        var dir = Path.Combine(root, "versions", "lone-26.2");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "lone-26.2.json"), "{}");

        var result = VersionManifestService.ScanUsableInstances([root], cleanForeignMarkers: false);

        Assert.DoesNotContain("lone-26.2", result.Keys);
    }

    [Fact]
    public void ScanUsableInstances_OwnJar_Usable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"usable-{Guid.NewGuid():N}");
        var dir = Path.Combine(root, "versions", "26.2");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "26.2.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "26.2.jar"), "x");

        var result = VersionManifestService.ScanUsableInstances([root], cleanForeignMarkers: false);

        Assert.True(result.ContainsKey("26.2"));
    }

    // ---------- IsInstanceTarget（8-12：实例 = MOD 安装目标，json-only——26.2 父版本 jar 落加载器子目录） ----------

    [Fact]
    public void IsInstanceTarget_JsonOnly_True()
    {
        // 26.2 场景：Fabric 父版本——versions/26.2/ 只有 json 无 jar（jar 沿 inheritsFrom 落加载器子目录）
        var gameDir = Path.Combine(Path.GetTempPath(), $"inst-{Guid.NewGuid():N}");
        var dir = Path.Combine(gameDir, "versions", "26.2");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "26.2.json"), "{}");
        Assert.True(VersionManifestService.IsInstanceTarget(gameDir, "26.2"));
        Assert.False(VersionManifestService.IsInstalled(gameDir, "26.2")); // 权威口径仍双文件
    }

    [Fact]
    public void IsInstanceTarget_PrefetchOnly_False()
    {
        // 预取残留（.prefetched 未正式安装）不算实例——半成品目录不进模组安装目标
        var gameDir = Path.Combine(Path.GetTempPath(), $"inst-{Guid.NewGuid():N}");
        var dir = Path.Combine(gameDir, "versions", "1.21.11");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "1.21.11.json"), "{}");
        Launcher.Core.Download.InstallMarker.MarkPrefetched(gameDir, "1.21.11");
        Assert.False(VersionManifestService.IsInstanceTarget(gameDir, "1.21.11"));
    }

    [Fact]
    public void IsInstanceTarget_PrefetchButMarked_True()
    {
        // .prefetched + .yanla-installed 双标记残留：正式安装过 → 兜底显示（对齐 ShouldShowInPage）
        var gameDir = Path.Combine(Path.GetTempPath(), $"inst-{Guid.NewGuid():N}");
        var dir = Path.Combine(gameDir, "versions", "1.21.11");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "1.21.11.json"), "{}");
        Launcher.Core.Download.InstallMarker.MarkPrefetched(gameDir, "1.21.11");
        Launcher.Core.Download.InstallMarker.Mark(gameDir, "1.21.11");
        Assert.True(VersionManifestService.IsInstanceTarget(gameDir, "1.21.11"));
    }

    [Fact]
    public void IsInstanceTarget_MissingDir_False()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"inst-{Guid.NewGuid():N}");
        Assert.False(VersionManifestService.IsInstanceTarget(gameDir, "ghost"));
    }

    // ---------- 8-23 主页版本消失修复：CollectInstalledCandidates（manifest 失败磁盘兜底）----------

    private static VersionManifestService.GameVersionEntry Entry(string id, bool installed = true, string dir = "")
        => new(id, "release", installed, DateTime.MinValue, null, dir);

    [Fact]
    public void CollectInstalledCandidates_EmptyManifest_StillReturnsDiskScan()
    {
        // 核心回归：manifest 拉取失败（网络/镜像全挂）传入空集 → 磁盘扫描结果仍兜底返回
        var gameDir = Path.Combine(Path.GetTempPath(), $"inst-{Guid.NewGuid():N}");
        try
        {
            var dir = Path.Combine(gameDir, "versions", "1.21.11");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "1.21.11.json"), "{}");
            File.WriteAllBytes(Path.Combine(dir, "1.21.11.jar"), [1, 2, 3]);

            var candidates = VersionManifestService.CollectInstalledCandidates(
                Array.Empty<VersionManifestService.GameVersionEntry>(), [gameDir], cleanForeignMarkers: false);

            Assert.Contains(candidates, c => c.Id == "1.21.11");
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    [Fact]
    public void CollectInstalledCandidates_MergesAndDedups()
    {
        // manifest 条目与磁盘扫描命中同名版本 → 只出现一次
        var gameDir = Path.Combine(Path.GetTempPath(), $"inst-{Guid.NewGuid():N}");
        try
        {
            var dir = Path.Combine(gameDir, "versions", "1.21.11");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "1.21.11.json"), "{}");
            File.WriteAllBytes(Path.Combine(dir, "1.21.11.jar"), [1, 2, 3]);
            var entries = new[] { Entry("1.21.11", installed: true, dir: gameDir) };

            var candidates = VersionManifestService.CollectInstalledCandidates(entries, [gameDir], cleanForeignMarkers: false);

            Assert.Single(candidates);
            Assert.Equal("1.21.11", candidates[0].Id);
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    [Fact]
    public void CollectInstalledCandidates_FiltersPrefetchedHidden()
    {
        // .prefetched 未正式安装 → 排除；.prefetched+.yanla-installed 双标记 → 包含（对齐 ShouldShowInPage）
        var gameDir = Path.Combine(Path.GetTempPath(), $"inst-{Guid.NewGuid():N}");
        try
        {
            var prefetchedDir = Path.Combine(gameDir, "versions", "26.2");
            Directory.CreateDirectory(prefetchedDir);
            File.WriteAllText(Path.Combine(prefetchedDir, "26.2.json"), "{}");
            File.WriteAllBytes(Path.Combine(prefetchedDir, "26.2.jar"), [1, 2, 3]);
            Launcher.Core.Download.InstallMarker.MarkPrefetched(gameDir, "26.2"); // 仅预取 → 应排除

            var doubleMarkedDir = Path.Combine(gameDir, "versions", "1.21.10");
            Directory.CreateDirectory(doubleMarkedDir);
            File.WriteAllText(Path.Combine(doubleMarkedDir, "1.21.10.json"), "{}");
            File.WriteAllBytes(Path.Combine(doubleMarkedDir, "1.21.10.jar"), [1, 2, 3]);
            Launcher.Core.Download.InstallMarker.MarkPrefetched(gameDir, "1.21.10");
            Launcher.Core.Download.InstallMarker.Mark(gameDir, "1.21.10"); // 双标记 → 应包含

            var candidates = VersionManifestService.CollectInstalledCandidates(
                Array.Empty<VersionManifestService.GameVersionEntry>(), [gameDir], cleanForeignMarkers: false);

            Assert.DoesNotContain(candidates, c => c.Id == "26.2");
            Assert.Contains(candidates, c => c.Id == "1.21.10");
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }
}
