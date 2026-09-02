using System.Net;
using System.Net.Http;
using Launcher.Core.Download;

namespace Launcher.Core.Tests;

/// <summary>下载中源健康监测（AL61）：持续低速（默认 30s &lt; 100KB/s）→ 判源死抛 SlowSourceException → 外层换路</summary>
public class SlowSourceTests
{
    private sealed class SlowStream : Stream
    {
        private readonly int _chunk;
        private readonly int _delayMs;
        private readonly long _total;
        private readonly long _slowAfter; // 8-22：超过此字节后切换慢速（测「末尾剩一片不判死」）；默认 0 = 一开始就慢
        private long _sent;

        public SlowStream(long total, int chunk, int delayMs, long slowAfter = 0)
        {
            _total = total;
            _chunk = chunk;
            _delayMs = delayMs;
            _slowAfter = slowAfter;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _total;
        public override long Position { get => _sent; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_sent >= _total) return 0;
            if (_sent >= _slowAfter) await Task.Delay(_delayMs, cancellationToken);
            var n = (int)Math.Min(_chunk, _total - _sent);
            _sent += n;
            return n;
        }
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        private readonly long _total;
        private readonly int _chunk;
        private readonly int _delayMs;

        public SlowHandler(long total, int chunk, int delayMs, long slowAfter = 0)
        {
            _total = total;
            _chunk = chunk;
            _delayMs = delayMs;
            _slowAfter = slowAfter;
        }
        private readonly long _slowAfter;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new SlowStream(_total, _chunk, _delayMs, _slowAfter)),
            });
        }
    }

    private static DownloadService CreateService(TimeSpan? interval = null, long threshold = 1024 * 1024,
        long probeMs = 100, int samples = 2)
    {
        return new DownloadService(new HttpClient(new SlowHandler(5 * 1024 * 1024, 16 * 1024, 200)),
            null, new DownloadOptions
            {
                MaxSourceAttempts = 1,
                RaceEliminateInterval = interval ?? TimeSpan.FromSeconds(1),
                SlowSpeedBps = threshold,   // 阈值 1MB/s > 慢流 80KB/s → 判死
                SlowProbeMs = probeMs,
                SlowSamples = samples,
                BackoffProvider = _ => TimeSpan.Zero,
            }, Path.GetTempPath(), (_, _) => Task.FromResult(true));
    }

    [Fact]
    public async Task SlowSource_SmallFile_WaitsForCompletion()
    {
        // 8-22 语义变更：<1MB 小文件——剩余恒 < 一片大小（1MB）→ 不再判死（弃尾清零净亏），
        // 等慢流下完（500KB @ 80KB/s ≈ 6s）；流长度必须与期望一致（CreateService 的 5MB 流会
        // 读超期望 → 片长度异常 → 回退单连接双倍耗时——旧测试靠判死掩盖了这点）
        var handler = new SlowHandler(500 * 1024, 16 * 1024, 200);
        var svc = new DownloadService(new HttpClient(handler), null, new DownloadOptions
        {
            MaxSourceAttempts = 1,
            RaceEliminateInterval = TimeSpan.FromSeconds(1),
            SlowSpeedBps = 1024 * 1024,
            SlowProbeMs = 100,
            SlowSamples = 2,
            BackoffProvider = _ => TimeSpan.Zero,
        }, Path.GetTempPath(), (_, _) => Task.FromResult(true));
        var dest = Path.Combine(Path.GetTempPath(), $"slow1-{Guid.NewGuid():N}.bin");
        try
        {
            await svc.DownloadFileAsync("https://example.com/f.bin", dest, null, 500 * 1024, _ => { }, CancellationToken.None);
            Assert.Equal(500 * 1024, new FileInfo(dest).Length);
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Fact]
    public async Task SlowSource_TailRemainder_WaitsForLastChunk()
    {
        // 8-22 末尾守卫：20MB 前 19.2MB 快 + 最后 800KB 慢（< 一片 1MB）——
        // 剩余不足一片时不判死，等最后一片下完下载成功；
        // 无守卫时旧逻辑会判死换路清零（真机 8-12 PowerToys 271MB 最后 1MB 判死弃 99.6%）
        var handler = new SlowHandler(20 * 1024 * 1024, 16 * 1024, 200,
            slowAfter: 20 * 1024 * 1024 - 800 * 1024);
        var svc = new DownloadService(new HttpClient(handler), null, new DownloadOptions
        {
            MaxSourceAttempts = 1,
            RaceEliminateInterval = TimeSpan.FromSeconds(1),
            SlowSpeedBps = 1024 * 1024,   // 阈值 1MB/s > 尾部 80KB/s → 旧逻辑必判死
            SlowProbeMs = 100,
            SlowSamples = 2,
            BackoffProvider = _ => TimeSpan.Zero,
        }, Path.GetTempPath(), (_, _) => Task.FromResult(true));
        var dest = Path.Combine(Path.GetTempPath(), $"slow3-{Guid.NewGuid():N}.bin");
        try
        {
            await svc.DownloadFileAsync("https://example.com/f.bin", dest, null, 20 * 1024 * 1024, _ => { }, CancellationToken.None);
            Assert.Equal(20 * 1024 * 1024, new FileInfo(dest).Length);
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Fact]
    public async Task SlowSource_Chunked_AbortsWithoutFallback()
    {
        // 分片（5MB > 探测阈值）：8 片总吞吐 640KB/s < 阈值 1MB/s → 判死直接抛（不回退单连接再等 30s）
        var svc = CreateService();
        var dest = Path.Combine(Path.GetTempPath(), $"slow2-{Guid.NewGuid():N}.bin");
        try
        {
            await Assert.ThrowsAsync<SlowSourceException>(() =>
                svc.DownloadFileAsync("https://example.com/f.bin", dest, null, 5 * 1024 * 1024, _ => { }, CancellationToken.None));
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Fact]
    public async Task FastSource_NotAborted()
    {
        // 快流回归：无 Delay 的 mock——下载成功不判死
        var handler = new FastHandler();
        var svc = new DownloadService(new HttpClient(handler), null, new DownloadOptions
        {
            MaxSourceAttempts = 1,
            SlowSpeedBps = 1024 * 1024,
            SlowProbeMs = 50,
            SlowSamples = 2,
            BackoffProvider = _ => TimeSpan.Zero,
        }, Path.GetTempPath(), (_, _) => Task.FromResult(true));
        var dest = Path.Combine(Path.GetTempPath(), $"fast-{Guid.NewGuid():N}.bin");
        try
        {
            await svc.DownloadFileAsync("https://example.com/f.bin", dest, null, 5 * 1024 * 1024, _ => { }, CancellationToken.None);
            Assert.Equal(5 * 1024 * 1024, new FileInfo(dest).Length);
        }
        finally
        {
            File.Delete(dest);
        }
    }

    private sealed class FastHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[5 * 1024 * 1024]),
            });
    }

    /// <summary>9-2 修：分片"并发到顶仍慢→立即判死"分支漏查 allowSlowDeath——单候选（无镜像）会被误杀
    /// 白白报错而非硬啃到底。谓词纯逻辑断言：allowSlowDeath=false 永不判死；=true 且条件满足才判死。</summary>
    [Fact]
    public void ShouldForceSlowAbort_RespectsAllowSlowDeath()
    {
        // 条件全满足：speedIdx>=3、均速<阈值、并发到顶、剩余≥一片
        const bool atMax = true;
        const long threshold = 1024 * 1024;
        const long remaining = 20 * 1024 * 1024;
        const long chunkSize = 1024 * 1024;

        // 单候选（allowSlowDeath=false）→ 永不判死（判死本意是换源，单源没得换）
        Assert.False(Launcher.Core.Download.DownloadService.ShouldForceSlowAbort(
            allowSlowDeath: false, speedIdx: 5, avg: 500_000, slowThreshold: threshold, atMax, remaining, chunkSize));
        // 多源（allowSlowDeath=true）→ 条件满足即判死
        Assert.True(Launcher.Core.Download.DownloadService.ShouldForceSlowAbort(
            allowSlowDeath: true, speedIdx: 5, avg: 500_000, slowThreshold: threshold, atMax, remaining, chunkSize));
        // 快（均速≥阈值）→ 不判死
        Assert.False(Launcher.Core.Download.DownloadService.ShouldForceSlowAbort(
            allowSlowDeath: true, speedIdx: 5, avg: 2_000_000, slowThreshold: threshold, atMax, remaining, chunkSize));
        // 采样不足 3 次 → 不判死
        Assert.False(Launcher.Core.Download.DownloadService.ShouldForceSlowAbort(
            allowSlowDeath: true, speedIdx: 2, avg: 500_000, slowThreshold: threshold, atMax, remaining, chunkSize));
        // 剩余不足一片 → 不判死（弃尾清零净亏，等最后一片下完）
        Assert.False(Launcher.Core.Download.DownloadService.ShouldForceSlowAbort(
            allowSlowDeath: true, speedIdx: 5, avg: 500_000, slowThreshold: threshold, atMax, remaining: chunkSize - 1, chunkSize));
    }
}
