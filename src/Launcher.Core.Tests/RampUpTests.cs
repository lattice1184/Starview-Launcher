using System.Net;
using System.Net.Http;
using Launcher.Core.Download;

namespace Launcher.Core.Tests;

/// <summary>
/// 分片并发决策（AL60 ramp-up）+ 8-18 固定片大小：先探测单连接速度再定并发（限并发源自动降单连接，
/// 按连接限速源吃满并发）；片边界固定 256KB——探测只决定同时下几片，不再决定片边界（换源续进度核心）。
/// </summary>
public class RampUpTests
{
    private sealed class RangeHandler : HttpMessageHandler
    {
        public readonly List<string> Ranges = [];
        public TimeSpan Delay;
        private readonly object _lock = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            lock (_lock) Ranges.Add(range is null ? "full" : $"{range.From}-{range.To}");
            await Task.Delay(Delay, ct);
            long len = range is null || range.From is null || range.To is null
                ? 0 : range.To.Value - range.From.Value + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[len]) };
        }
    }

    private static DownloadService CreateService(HttpMessageHandler handler, int chunkCount = 8)
        => new(new HttpClient(handler), null, new DownloadOptions
        {
            MaxSourceAttempts = 1,
            ChunkCount = chunkCount,
            BackoffProvider = _ => TimeSpan.Zero,
        }, Path.GetTempPath(), (_, _) => Task.FromResult(true));

    [Fact]
    public void BuildDownloadRequest_ProgressiveThrottleCdn_ForcesHttp11()
    {
        // 8-24 渐进 CDN 强制 h1：openssl ALPN 实测 cdn-raw/cdn-alt/mcimirror 均协商 h2，h2 多路复用
        // 把分片折叠到同一条 TCP、共享 per-连接限速配额——强制 h1 让每分片独占 TCP。
        // 必须 RequestVersionExact：OrLower 会被 HttpClient「Version==1.1 && OrLower → 用 DefaultRequestVersion(h2)
        // 覆盖」规则改写，是 no-op（8-24 测试抓出）。
        var req = DownloadService.BuildDownloadRequest("https://cdn-raw.modrinth.com/a/b.jar");
        Assert.Equal(HttpVersion.Version11, req.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, req.VersionPolicy);
    }

    [Fact]
    public void BuildDownloadRequest_OtherHost_KeepsDefaultVersion()
    {
        // 非渐进域不强制（保持默认 1.1 + OrLower → 客户端会用 DefaultRequestVersion 协商版本）
        var req = DownloadService.BuildDownloadRequest("https://example.com/f.bin");
        Assert.Equal(HttpVersion.Version11, req.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, req.VersionPolicy);
    }

    [Theory]
    // 快源：探测 0.1s 拉完 1MB → ~10MB/s → 单连接（限并发源：分片只会触发限流）
    [InlineData(100, 1)]
    // 中速：1.5s 拉 1MB → ~667KB/s → 4 并发
    [InlineData(1500, 4)]
    // 慢源：2s 窗口截断（0 字节）→ 满并发 8（按连接限速源需要分片）
    [InlineData(5000, 8)]
    public async Task Probe_DecidesConcurrencyBySpeed(int delayMs, int expectedConcurrency)
    {
        var handler = new RangeHandler { Delay = TimeSpan.FromMilliseconds(delayMs) };
        var svc = CreateService(handler);
        var partDir = Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(partDir); // 生产路径由 DownloadChunkedAsync 创建；直接测探测函数需自建（探测写 probe.part）
        try
        {
            var concurrency = await svc.ProbeAndDecideConcurrencyAsync("https://example.com/f.bin", 3 * 1024 * 1024, partDir, CancellationToken.None,
                new DownloadService.ThrottleState()); // 8-22 探测也走共享节流（限速 0 时无副作用）
            Assert.Equal(expectedConcurrency, concurrency);
        }
        finally
        {
            try { Directory.Delete(partDir, true); } catch { }
        }
    }

    [Fact]
    public async Task FixedChunk_SizeDeterminesChunkCount_NotConcurrency()
    {
        // 8-18：3MB 文件固定片 1MB → 3 片（Range = 探测 1 + 正式 3）；并发只影响同时下几片
        var handler = new RangeHandler { Delay = TimeSpan.FromMilliseconds(100) };
        var svc = CreateService(handler);
        var dest = Path.Combine(Path.GetTempPath(), $"ramp3-{Guid.NewGuid():N}.bin");
        const long size = 3 * 1024 * 1024;
        try
        {
            await svc.DownloadFileAsync("https://example.com/f.bin", dest, null, size, _ => { }, CancellationToken.None);
            Assert.True(1 + 3 == handler.Ranges.Count,
                $"期望 4 个 Range（探测 1 + 3 片），实际 {handler.Ranges.Count}: {string.Join(", ", handler.Ranges)}");
            Assert.Equal(size, new FileInfo(dest).Length);
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Theory]
    // 8-24 快源大文件统一满并发（原保底 4→满）：探测已证快源（>800KB/s），16/8 并发是下载管理器常态
    [InlineData(100, 100L * 1024 * 1024, 8)]  // 快源 + 100MB → 满并发 8（第三方 ISO/Mojang 等）
    [InlineData(100, 8L * 1024 * 1024, 1)]    // 快源 + 恰 8MB → 1（≤8MB 非渐进小文件，限并发源不受影响）
    [InlineData(100, 8L * 1024 * 1024 + 1, 8)] // 快源 + 8MB+1 → 满并发 8
    public async Task Probe_FastSource_LargeFile_FloorsConcurrency(int delayMs, long totalSize, int expected)
    {
        var handler = new RangeHandler { Delay = TimeSpan.FromMilliseconds(delayMs) };
        var svc = CreateService(handler);
        var partDir = Path.Combine(Path.GetTempPath(), $"probe2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(partDir);
        try
        {
            var concurrency = await svc.ProbeAndDecideConcurrencyAsync("https://example.com/f.bin", totalSize, partDir, CancellationToken.None,
                new DownloadService.ThrottleState()); // 8-22 探测也走共享节流（限速 0 时无副作用）
            Assert.Equal(expected, concurrency);
        }
        finally
        {
            try { Directory.Delete(partDir, true); } catch { }
        }
    }

    [Theory]
    // 8-22 Fabric API 治本：渐进限速 CDN（Modrinth/GitHub）小文件（≤8MB）快源满并发——
    // 探测开局全速 → 旧逻辑给 1 连接 → CDN 按连接累积量掉到几十 KB/s 全程磨（升片守卫 8MB 对小文件永不触发）
    // 8-24 4→满并发（每连接限速，并发线性叠加；片数受 1MB 片边界约束，4-8MB 实际 4-8 片）
    [InlineData("https://cdn.modrinth.com/data/x/fabric-api.jar", 8)]
    [InlineData("https://cdn-raw.modrinth.com/data/x/f.jar", 8)]
    [InlineData("https://cdn-alt.modrinth.com/data/x/f.jar", 8)]
    [InlineData("https://mod.mcimirror.top/data/x/f.jar", 8)]
    [InlineData("https://github.com/user/repo/f.jar", 8)]
    // 普通域小文件快源保持 1（限并发源不受影响——回归保护）
    [InlineData("https://example.com/f.jar", 1)]
    public async Task Probe_FastSource_SmallFile_ThrottleCdnFloorsConcurrency(string url, int expected)
    {
        var handler = new RangeHandler { Delay = TimeSpan.FromMilliseconds(50) }; // 1MB/50ms ≈ 20MB/s 快源
        var svc = CreateService(handler);
        var partDir = Path.Combine(Path.GetTempPath(), $"probe3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(partDir);
        try
        {
            var concurrency = await svc.ProbeAndDecideConcurrencyAsync(url, 1536 * 1024, partDir, CancellationToken.None,
                new DownloadService.ThrottleState());
            Assert.Equal(expected, concurrency);
            // 8-22 域特征快速路径：渐进限速域小文件免探测——不发起任何请求（探测对它们纯浪费）；
            // 普通域仍走探测（1 次请求）
            Assert.Equal(url.Contains("modrinth") || url.Contains("github") || url.Contains("mcimirror") ? 0 : 1, handler.Ranges.Count);
        }
        finally
        {
            try { Directory.Delete(partDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SmallFile_NoProbe_ChunksByFixedSize()
    {
        // < 1MB：不探测（探测段≈整个文件无意义），并发满额；片数 = ceil(size/1MB) = 1
        var handler = new RangeHandler { Delay = TimeSpan.FromMilliseconds(100) };
        var svc = CreateService(handler);
        var dest = Path.Combine(Path.GetTempPath(), $"ramp2-{Guid.NewGuid():N}.bin");
        const long size = 500 * 1024;
        try
        {
            await svc.DownloadFileAsync("https://example.com/f.bin", dest, null, size, _ => { }, CancellationToken.None);
            Assert.Equal(1, handler.Ranges.Count);
            Assert.Equal(size, new FileInfo(dest).Length);
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Theory]
    // 8-24 渐进限速 CDN 大文件（>8MB）快源满并发起步：每连接独立被限速，起步满并发直接拉高总吞吐
    // （此前只给 4，升片阈值 300KB/s 又低于 1MB/s 平台期 → 永不升片 → 110MB 后卡 1MB/s）
    [InlineData("https://cdn-raw.modrinth.com/data/x/big.jar", 8)]
    [InlineData("https://cdn.modrinth.com/data/x/big.jar", 8)]
    [InlineData("https://mod.mcimirror.top/data/x/big.jar", 8)]
    // 8-24 普通域快源大文件也满并发（原保底 4→满）——探测已证快源，堆并发是常态
    [InlineData("https://example.com/big.bin", 8)]
    public async Task Probe_FastSource_LargeFile_ProgressiveThrottleUsesMax(string url, int expected)
    {
        var handler = new RangeHandler { Delay = TimeSpan.FromMilliseconds(50) }; // 1MB/50ms ≈ 20MB/s 快源
        var svc = CreateService(handler);
        var partDir = Path.Combine(Path.GetTempPath(), $"probe4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(partDir);
        try
        {
            var concurrency = await svc.ProbeAndDecideConcurrencyAsync(url, 9 * 1024 * 1024, partDir, CancellationToken.None,
                new DownloadService.ThrottleState());
            Assert.Equal(expected, concurrency);
        }
        finally
        {
            try { Directory.Delete(partDir, true); } catch { }
        }
    }
}
