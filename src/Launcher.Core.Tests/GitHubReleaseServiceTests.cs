using System.Net;
using System.Net.Http;
using Launcher.Core.Download;

namespace Launcher.Core.Tests;

/// <summary>GitHub releases/latest 检查：解析、平台资产匹配、403/429 退避</summary>
public class GitHubReleaseServiceTests
{
    private const string LatestJson = """
        {
          "tag_name": "v1.1.4",
          "published_at": "2026-08-29T12:00:00Z",
          "assets": [
            {"name": "Starview-Launcher.exe", "browser_download_url": "https://github.com/x.exe", "size": 12345678},
            {"name": "starview-linux-x64-20260830.tar.gz", "browser_download_url": "https://github.com/x-linux.tar.gz", "size": 87654321},
            {"name": "starview-osx-arm64-20260830.tar.gz", "browser_download_url": "https://github.com/x-osx-arm64.tar.gz", "size": 555},
            {"name": "starview-osx-x64-20260830.tar.gz", "browser_download_url": "https://github.com/x-osx-x64.tar.gz", "size": 666}
          ]
        }
        """;

    private sealed class StubHandler(Func<HttpResponseMessage> factory) : HttpMessageHandler
    {
        public int Calls;
        public string? LastAuth;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastAuth = request.Headers.Authorization?.ToString();
            return Task.FromResult(factory());
        }
    }

    private static void Use(StubHandler handler)
    {
        GitHubReleaseService.ClearCacheForTest();
        GitHubApiDirect.TokenOverride = null;
        GitHubReleaseService.Http = new HttpClient(handler);
    }

    [Fact]
    public async Task GetLatest_ParsesTagAndAssets()
    {
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(LatestJson, System.Text.Encoding.UTF8, "application/json"),
        });
        Use(handler);

        var latest = await GitHubReleaseService.GetLatestAsync(CancellationToken.None);

        Assert.NotNull(latest);
        Assert.Equal("v1.1.4", latest!.Tag);
        Assert.NotNull(latest.PublishedAt);
        Assert.Equal(4, latest.Assets.Count);
        Assert.Contains(latest.Assets, a => a.Name == "Starview-Launcher.exe" && a.Size == 12345678);
    }

    [Fact]
    public async Task GetLatest_NonSuccess_ReturnsNull()
    {
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound));
        Use(handler);

        var latest = await GitHubReleaseService.GetLatestAsync(CancellationToken.None);

        Assert.Null(latest);
    }

    [Fact]
    public async Task GetLatest_RateLimited_BacksOff()
    {
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.Forbidden));
        Use(handler);

        Assert.Null(await GitHubReleaseService.GetLatestAsync(CancellationToken.None));
        // 限流期内再次检查：不再打 API（退避）
        Assert.Null(await GitHubReleaseService.GetLatestAsync(CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void Match_PlatformSpecificAssets()
    {
        var latest = new GitHubReleaseService.LatestRelease("v1.1.4",
            [
                new("Starview-Launcher.exe", "https://x.exe", 1),
                new("starview-linux-x64-20260830.tar.gz", "https://x-linux.tar.gz", 2),
                new("starview-osx-arm64-20260830.tar.gz", "https://x-osx-arm64.tar.gz", 3),
                new("starview-osx-x64-20260830.tar.gz", "https://x-osx-x64.tar.gz", 4),
            ], null);

        Assert.Equal("Starview-Launcher.exe", GitHubReleaseService.MatchFor(latest, "windows")!.Name);
        Assert.Equal("starview-linux-x64-20260830.tar.gz", GitHubReleaseService.MatchFor(latest, "linux")!.Name);
        // macos 优先 arm64
        Assert.Equal("starview-osx-arm64-20260830.tar.gz", GitHubReleaseService.MatchFor(latest, "macos")!.Name);
    }

    [Fact]
    public void Match_PrefersPlainTarGz_OverAppBundleVariant()
    {
        // 8-30 发布链同时出散文件包与 .app 变体——更新统一用散文件包
        var latest = new GitHubReleaseService.LatestRelease("v1.1.4",
            [
                new("starview-osx-arm64-20260830.app.tar.gz", "https://x.app.tar.gz", 3),
                new("starview-osx-arm64-20260830.tar.gz", "https://x.tar.gz", 4),
            ], null);

        Assert.Equal("starview-osx-arm64-20260830.tar.gz", GitHubReleaseService.MatchFor(latest, "macos")!.Name);
    }

    [Fact]
    public void Match_MacosFallsBackToX64()
    {
        var latest = new GitHubReleaseService.LatestRelease("v1.1.4",
            [new("starview-osx-x64-20260830.tar.gz", "https://x-osx-x64.tar.gz", 4)], null);

        Assert.Equal("starview-osx-x64-20260830.tar.gz", GitHubReleaseService.MatchFor(latest, "macos")!.Name);
    }

    [Fact]
    public void Match_NoAssetForPlatform_ReturnsNull()
    {
        var latest = new GitHubReleaseService.LatestRelease("v1.1.4",
            [new("starview-linux-x64-20260830.tar.gz", "https://x-linux.tar.gz", 2)], null);

        Assert.Null(GitHubReleaseService.MatchFor(latest, "windows"));
        Assert.Null(GitHubReleaseService.MatchFor(null, "windows"));
    }
}
