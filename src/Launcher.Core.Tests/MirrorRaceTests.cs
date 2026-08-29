using System.Diagnostics;
using System.Net;
using System.Net.Http;
using Launcher.Core.Download;
using Launcher.Core.Utils;

namespace Launcher.Core.Tests;

/// <summary>并行竞速（AL32 秒接）：同文件多源同时发起先到先得；慢源被取消；全失败按轮数退避换轮</summary>
public class MirrorRaceTests
{
    /// <summary>可控延迟 stub：未路由 host 返回默认字节（基础 10ms 保证 when-any 顺序可测）</summary>
    private sealed class DelayedStubHandler : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        private readonly object _lock = new();
        private readonly Dictionary<string, (int DelayMs, int Status, byte[] Body)> _routes = [];

        public void Route(string hostPath, int delayMs, int status, byte[] body)
            => _routes[hostPath] = (delayMs, status, body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = $"{request.RequestUri!.Host}{request.RequestUri.AbsolutePath}";
            lock (_lock) Requests.Add($"{request.Method} {key}");
            await Task.Delay(10, ct);
            if (_routes.TryGetValue(key, out var route))
            {
                if (route.DelayMs > 0) await Task.Delay(route.DelayMs, ct);
                return route.Status == 200
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(route.Body) }
                    : new HttpResponseMessage((HttpStatusCode)route.Status);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("12345"u8.ToArray()) };
        }
    }

    private static DownloadService CreateService(DelayedStubHandler handler)
    {
        var http = new HttpClient(handler);
        // 官方源：any host；镜像源：bmclapi2.bangbang93.com
        var resolver = new ResolvingDlSourceMapper(new DefaultDlSourceMapper(), new BmclapiDlSourceMapper());
        return new DownloadService(http, resolver, new DownloadOptions
        {
            MaxSourceAttempts = 2,
            BackoffProvider = _ => TimeSpan.Zero,
        }, Path.GetTempPath(), (_, _) => Task.FromResult(true)); // 跳过真实网络预检
    }

    [Fact]
    public async Task FastSourceWins_SlowSourceCancelled()
    {
        var handler = new DelayedStubHandler();
        // 官方源 600ms 才返回好字节；镜像 20ms 返回——竞速应 ~30ms 内完成并取镜像字节
        handler.Route("resources.download.minecraft.net/ab/abcdef", 600, 200, "MIRROR!"u8.ToArray());
        handler.Route("bmclapi2.bangbang93.com/ab/abcdef", 20, 200, "12345"u8.ToArray());
        var svc = CreateService(handler);
        var dest = Path.Combine(Path.GetTempPath(), $"race-{Guid.NewGuid():N}.jar");
        try
        {
            var sw = Stopwatch.StartNew();
            await svc.DownloadFileAsync("https://resources.download.minecraft.net/ab/abcdef", dest, null, 5,
                null, CancellationToken.None);
            sw.Stop();

            Assert.Equal("12345", await File.ReadAllTextAsync(dest)); // 镜像字节胜出
            Assert.True(sw.ElapsedMilliseconds < 400,
                $"并行竞速应 ~30ms 完成，实际 {sw.ElapsedMilliseconds}ms（串行需 ~620ms）");
            // 慢源（官方）只发过一次请求——赢家确定后整轮结束，不重试慢源
            Assert.Equal(1, handler.Requests.Count(r => r.Contains("resources.download.minecraft.net")));
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public async Task SlowSourceWrongBytes_ValidSourceStillWins()
    {
        // 官方源快但字节错（校验失败）→ 不得选它；镜像慢但字节对 → 等它完成
        var handler = new DelayedStubHandler();
        handler.Route("libraries.minecraft.net/org/a/1.0/a-1.0.jar", 10, 200, "WRONG!!"u8.ToArray());
        handler.Route("bmclapi2.bangbang93.com/maven/org/a/1.0/a-1.0.jar", 150, 200, "12345"u8.ToArray());
        var svc = CreateService(handler);
        var dest = Path.Combine(Path.GetTempPath(), $"race-{Guid.NewGuid():N}.jar");
        try
        {
            await svc.DownloadFileAsync("https://libraries.minecraft.net/org/a/1.0/a-1.0.jar", dest, null, 5,
                null, CancellationToken.None);

            Assert.Equal("12345", await File.ReadAllTextAsync(dest)); // 校验合法的字节胜出
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public async Task BothFail_ParallelRequestsPerRound_RespectsAttempts()
    {
        var handler = new DelayedStubHandler();
        handler.Route("resources.download.minecraft.net/ab/abcdef", 10, 500, []);
        handler.Route("bmclapi2.bangbang93.com/ab/abcdef", 10, 500, []);
        var svc = CreateService(handler); // MaxSourceAttempts=2 → 2 轮 × 每轮 2 源并行 = 4 请求
        var dest = Path.Combine(Path.GetTempPath(), $"race-{Guid.NewGuid():N}.jar");
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                svc.DownloadFileAsync("https://resources.download.minecraft.net/ab/abcdef", dest, null, 5,
                    null, CancellationToken.None));

            Assert.Equal(4, handler.Requests.Count); // 2 轮 × 2 源（并行不改变总请求数）
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    // ---- 8-29 RaceTextAsync：meta 查询（版本 json / loader profile）多源竞速 ----

    [Fact]
    public async Task RaceText_FastCandidateWins()
    {
        // 官方 600ms、镜像 20ms → 取镜像内容（72s 空档根因：此前裸单源拉官方）
        var handler = new DelayedStubHandler();
        handler.Route("piston-meta.mojang.com/v1/packages/abc/26.1.2.json", 600, 200, "OFFICIAL"u8.ToArray());
        handler.Route("bmclapi2.bangbang93.com/version/26.1.2/json", 20, 200, "MIRROR"u8.ToArray());
        var svc = CreateService(handler);

        var text = await svc.RaceTextAsync(
            ["https://piston-meta.mojang.com/v1/packages/abc/26.1.2.json",
             "https://bmclapi2.bangbang93.com/version/26.1.2/json"], CancellationToken.None);

        Assert.Equal("MIRROR", text); // 快源赢，不被慢官方拖住
    }

    [Fact]
    public async Task RaceText_FirstFails_FallsToNext()
    {
        var handler = new DelayedStubHandler();
        handler.Route("meta.fabricmc.net/v2/versions/loader/26.1.2/0.19.5/profile/json", 10, 500, []);
        handler.Route("bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/26.1.2/0.19.5/profile/json", 10, 200, "PROFILE"u8.ToArray());
        var svc = CreateService(handler);

        var text = await svc.RaceTextAsync(
            ["https://meta.fabricmc.net/v2/versions/loader/26.1.2/0.19.5/profile/json",
             "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/26.1.2/0.19.5/profile/json"], CancellationToken.None);

        Assert.Equal("PROFILE", text); // 首个 500 → 等下一个候选
    }

    [Fact]
    public async Task RaceText_SingleCandidate_DirectPassThrough()
    {
        var handler = new DelayedStubHandler();
        handler.Route("only.example.com/a.json", 10, 200, "ONLY"u8.ToArray());
        var svc = CreateService(handler);

        var text = await svc.RaceTextAsync(["https://only.example.com/a.json"], CancellationToken.None);
        Assert.Equal("ONLY", text); // 单候选直通，零竞速开销
    }
}
