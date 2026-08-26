using System.Net;
using System.Net.Http;
using Launcher.Core.Download;

namespace Launcher.Core.Tests;

/// <summary>
/// 8-19 片大小自适应：小文件（&lt;64MB）恒 1MB（现有行为零变化）；大文件片 = totalSize/64
/// （目标 64 片——8 并发 × 8 波 = 0.8s RTT 上界）；上限 4MB（零字节失败重下粒度上限）。
/// 片大小入口一次定、永不变化——边界固定 → 换源续传复用语义保留。
/// </summary>
public class ChunkSizeTests
{
    private const long MB = 1024 * 1024;

    [Fact]
    public void Below64MB_Uses1MBFloor()
    {
        Assert.Equal(MB, DownloadService.ChunkSizeFor(500 * 1024));   // 小文件 1 片
        Assert.Equal(MB, DownloadService.ChunkSizeFor(10 * MB));
        Assert.Equal(MB, DownloadService.ChunkSizeFor(64 * MB - 1));  // 边界下沿
    }

    [Fact]
    public void At64MB_Exactly1MB()
    {
        // 64MB/64 = 1MB 恰在下限——不 clamp 也 1MB（边界精确值）
        Assert.Equal(MB, DownloadService.ChunkSizeFor(64 * MB));
    }

    [Fact]
    public void MidRange_Targets64Chunks()
    {
        // 非 2 幂片大小——抓「凑整/硬编码」回归
        var cs100 = DownloadService.ChunkSizeFor(100 * MB);
        Assert.Equal(1_638_400L, cs100);
        Assert.Equal(64, (int)Math.Ceiling(100.0 * MB / cs100));      // 恰 64 片

        var cs166 = DownloadService.ChunkSizeFor(166 * MB);           // HTTP Toolkit 实战尺寸
        Assert.Equal(2_719_744L, cs166);
        Assert.Equal(64, (int)Math.Ceiling(166.0 * MB / cs166));
    }

    [Fact]
    public void At256MB_Exactly4MB()
    {
        Assert.Equal(4 * MB, DownloadService.ChunkSizeFor(256 * MB));
    }

    [Fact]
    public void Above256MB_CappedAt4MB()
    {
        // 1GB → 4MB 上限 → 256 片（8 并发 32 波 = 3.2s RTT，可接受）
        Assert.Equal(4 * MB, DownloadService.ChunkSizeFor(MB * 1024));
        Assert.Equal(256, (int)Math.Ceiling(1024.0 * MB / (4 * MB)));
    }

    [Fact]
    public void AlwaysWithinBounds_SelfConsistent()
    {
        // 256KB..8GB 扫描：片大小恒 ∈ [1MB, 4MB]，片数自洽（≥1）
        for (long size = 256 * 1024; size <= 8L * MB * 1024; size *= 2)
        {
            var cs = DownloadService.ChunkSizeFor(size);
            Assert.InRange(cs, MB, 4 * MB);
            Assert.True((int)Math.Ceiling((double)size / cs) >= 1);
        }
    }

    // ---------- 8-25 渐进限速 CDN 细片（模组提速） ----------

    [Fact]
    public void ProgressiveCdn_SmallFiles_Use256KBFloor()
    {
        // 慢源（cdn-raw/cdn-alt ~100KB/s）细片换并发：1.5MB 模组 1MB 片=2 连接（200KB/s），
        // 256KB 片=6 连接（600KB/s）；普通源不受影响仍 1MB。
        Assert.Equal(256 * 1024, DownloadService.ChunkSizeFor(500 * 1024, progressiveThrottleCdn: true));
        Assert.Equal(256 * 1024, DownloadService.ChunkSizeFor(10 * MB, progressiveThrottleCdn: true));
        Assert.Equal(256 * 1024, DownloadService.ChunkSizeFor(16 * MB - 1, progressiveThrottleCdn: true));
        Assert.Equal(6, (int)Math.Ceiling(1.5 * MB / (256 * 1024)));   // 1.5MB → 6 片
        // 普通源（GitHub 等快源）保持 1MB 下限——8-18 RTT 教训不动
        Assert.Equal(MB, DownloadService.ChunkSizeFor(500 * 1024));
        Assert.Equal(MB, DownloadService.ChunkSizeFor(10 * MB));
    }

    [Fact]
    public void ProgressiveCdn_At64MB_SameAsNormal()
    {
        // 64MB/64 = 1MB 恰在下限以上——大文件渐进/普通源一致（细片只服务小文件并发）
        Assert.Equal(MB, DownloadService.ChunkSizeFor(64 * MB, progressiveThrottleCdn: true));
        Assert.Equal(MB, DownloadService.ChunkSizeFor(64 * MB));
    }

    [Fact]
    public void ProgressiveCdn_MidRange_StillMoreParallel()
    {
        // 16-64MB 渐进源：totalSize/64 落 256KB..1MB 区间——比普通源 1MB 下限更细、并发更高
        var cs32 = DownloadService.ChunkSizeFor(32 * MB, progressiveThrottleCdn: true);
        Assert.InRange(cs32, 256 * 1024, MB);
        Assert.Equal(64, (int)Math.Ceiling(32.0 * MB / cs32));
    }

    // ---------- 集成：自适应边界端到端 ----------

    private sealed class RangeHandler : HttpMessageHandler
    {
        public readonly List<(long Start, long End)> Ranges = [];
        private readonly object _lock = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            if (range is null || range.From is null || range.To is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            var (start, end) = (range.From.Value, range.To.Value);
            lock (_lock) Ranges.Add((start, end));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(new byte[end - start + 1]),
            });
        }
    }

    [Fact]
    public async Task AdaptiveBoundaries_NotPowerOfTwo()
    {
        // 100MB → 1,638,400 字节/片（非 2 幂）→ 64 片；所有 Range start 对齐该边界
        var handler = new RangeHandler();
        var svc = new DownloadService(new HttpClient(handler), null, new DownloadOptions
        {
            MaxSourceAttempts = 1,
            BackoffProvider = _ => TimeSpan.Zero,
        }, Path.GetTempPath(), (_, _) => Task.FromResult(true));
        var dest = Path.Combine(Path.GetTempPath(), $"csz-{Guid.NewGuid():N}.bin");
        const long size = 100 * 1024 * 1024;
        try
        {
            await svc.DownloadFileAsync("https://example.com/f.bin", dest, null, size, _ => { }, CancellationToken.None);

            Assert.Equal(size, new FileInfo(dest).Length);
            var starts = handler.Ranges.Select(r => r.Start).Where(s => s != 0).ToList();
            Assert.Equal(63, starts.Count);                              // 探测(start=0) + 64 片（片 0 与探测同 start）
            Assert.All(starts, s => Assert.Equal(0L, s % 1_638_400L));  // 边界 1,638,400 对齐
        }
        finally
        {
            File.Delete(dest);
            try { Directory.Delete(dest + ".parts", true); } catch { }
        }
    }
}
