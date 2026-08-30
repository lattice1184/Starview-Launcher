using System.Net;
using System.Security.Cryptography;
using Launcher.Core.Download;

namespace Launcher.Core.Tests;

/// <summary>
/// 分片并发下载测试：本地 HttpListener 模拟支持 Range 的服务器，
/// 验证 10MB 文件分片下载后与源数据完全一致 + 幂等。
/// </summary>
public class DownloadServiceTests : IAsyncLifetime
{
    private const int Port = 18345;
    private HttpListener? _listener;
    private byte[] _payload = [];
    private byte[] _smallPayload = [];
    private byte[] _slowPayload = [];

    public Task InitializeAsync()
    {
        _payload = new byte[10 * 1024 * 1024];
        _smallPayload = new byte[64 * 1024];
        _slowPayload = new byte[1024 * 1024];
        Random.Shared.NextBytes(_payload);
        Random.Shared.NextBytes(_smallPayload);
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
        _ = ServeAsync();
        return Task.CompletedTask;
    }

    /// <summary>按 URL 路径路由到对应数据：/small.bin → 64KB，/slow.bin → 1MB（慢速），其余 → 10MB</summary>
    private byte[] PayloadFor(HttpListenerContext ctx)
        => ctx.Request.Url?.AbsolutePath switch
        {
            "/small.bin" => _smallPayload,
            "/slow.bin" => _slowPayload,
            _ => _payload,
        };

    public Task DisposeAsync()
    {
        _listener?.Stop();
        _listener?.Close();
        return Task.CompletedTask;
    }

    private async Task ServeAsync()
    {
        while (_listener!.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            // 并发处理请求 + 异常隔离（客户端断开不拖垮循环）
            _ = Task.Run(async () =>
            {
                try
                {
                    var data = PayloadFor(ctx);
                    var range = ctx.Request.Headers["Range"];
                    if (range is not null && range.StartsWith("bytes="))
                    {
                        var spec = range["bytes=".Length..];
                        var parts = spec.Split('-');
                        var start = long.Parse(parts[0]);
                        var end = parts.Length > 1 && parts[1].Length > 0
                            ? long.Parse(parts[1])
                            : data.Length - 1;
                        ctx.Response.StatusCode = 206;
                        ctx.Response.AddHeader("Content-Range", $"bytes {start}-{end}/{data.Length}");
                        ctx.Response.ContentLength64 = end - start + 1;
                        // 慢速端点：分块写+延迟，把单片下载时长拉过节流窗口（测分片进度节流上报）
                        if (ctx.Request.Url?.AbsolutePath == "/slow.bin")
                        {
                            var segLen = (int)(end - start + 1);
                            for (var offset = 0; offset < segLen; offset += 64 * 1024)
                            {
                                var n = Math.Min(64 * 1024, segLen - offset);
                                await ctx.Response.OutputStream.WriteAsync(data.AsMemory((int)start + offset, n));
                                // 130ms×4 块 = 单片 ~520ms ≈ 2 个节流窗口（250ms）：保证片内稳定存在
                                // 中间上报（80ms×4=320ms 只有 1.28 窗口，全局抢占偶发吞光 → flaky >4 断言）
                                await Task.Delay(130);
                            }
                        }
                        else
                        {
                            await ctx.Response.OutputStream.WriteAsync(data.AsMemory((int)start, (int)(end - start + 1)));
                        }
                    }
                    else if (ctx.Request.HttpMethod == "HEAD")
                    {
                        // HEAD：只返回长度，绝不能写 body（HttpListener 写 HEAD body 会挂起）
                        ctx.Response.ContentLength64 = data.Length;
                    }
                    else
                    {
                        ctx.Response.ContentLength64 = data.Length;
                        await ctx.Response.OutputStream.WriteAsync(data);
                    }
                }
                catch { /* 客户端断开 */ }
                finally
                {
                    try { ctx.Response.Close(); } catch { }
                }
            });
        }
    }

    private string TempPath() => Path.Combine(Path.GetTempPath(), $"dl-{Guid.NewGuid():N}.bin");

    [Fact]
    public async Task ChunkedDownload_ProducesIdenticalFile()
    {
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(_payload));
        var dest = TempPath();
        try
        {
            var svc = new DownloadService();
            await svc.DownloadFileAsync($"http://localhost:{Port}/big.bin", dest, sha1, _payload.Length);
            var actual = await File.ReadAllBytesAsync(dest);
            Assert.Equal(_payload, actual);
        }
        finally { File.Delete(dest); }
    }

    [Fact]
    public async Task ChunkedDownload_SecondCall_IsIdempotent()
    {
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(_payload));
        var dest = TempPath();
        try
        {
            var svc = new DownloadService();
            await svc.DownloadFileAsync($"http://localhost:{Port}/big.bin", dest, sha1, _payload.Length);
            var first = await File.ReadAllBytesAsync(dest);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await svc.DownloadFileAsync($"http://localhost:{Port}/big.bin", dest, sha1, _payload.Length);
            sw.Stop();

            var second = await File.ReadAllBytesAsync(dest);
            Assert.Equal(first, second);
            Assert.True(sw.ElapsedMilliseconds < 500, $"幂等跳过应 <500ms，实际 {sw.ElapsedMilliseconds}ms");
        }
        finally { File.Delete(dest); }
    }

    [Fact]
    public async Task ChunkedDownload_Progress_ReportsMoreThanChunkCount()
    {
        // 修 B 回归：分片下载旧实现每片完成才报一次进度（4 片 = 4 次回调）→ 大文件
        // 速度/剩余时间文字每片周期才刷新（观感「延迟显示」）。慢速流（每 64KB 延迟 80ms，
        // 单片 > 250ms 节流窗口）应产生完成回调之外的中间上报：回调数必须多于分片数。
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(_slowPayload));
        var dest = TempPath();
        try
        {
            var reports = new List<(long done, long total)>();
            var svc = new DownloadService(null, null, new DownloadOptions
            {
                ChunkCount = 4,
                BufferSize = 80 * 1024,
            }, null);
            await svc.DownloadFileAsync($"http://localhost:{Port}/slow.bin", dest, sha1, _slowPayload.Length,
                p =>
                {
                    lock (reports) reports.Add((p.FileBytesDone, p.FileTotalBytes));
                });

            lock (reports)
            {
                Assert.True(reports.Count > 4,
                    $"进度回调应多于分片数（4），实际 {reports.Count}——每片完成才报一次的粒度未修");
                Assert.Equal(_slowPayload.Length, reports[^1].done);
                for (var i = 1; i < reports.Count; i++)
                    Assert.True(reports[i].done >= reports[i - 1].done,
                        $"进度应单调递增：{reports[i - 1].done} → {reports[i].done}");
            }

            var actual = await File.ReadAllBytesAsync(dest);
            Assert.Equal(_slowPayload, actual);
        }
        finally { File.Delete(dest); }
    }

    [Fact]
    public async Task Progress_Overall_StaysBelow100_UntilTaskCompletes()
    {
        // 字节读完 ≠ 任务完成：分片合并 + SHA1 校验 + 落盘还有成本。
        // 进度提前报 100 → UI「进度条满但还挂着下载中/排队等待」观感卡死
        // （BHL 对照：100% 即完成）。修复：底层进度封顶 99，100 只由任务完成时给出。
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(_payload));
        var dest = TempPath();
        try
        {
            var peaks = new List<double>();
            var svc = new DownloadService();
            await svc.DownloadFileAsync($"http://localhost:{Port}/big.bin", dest, sha1, _payload.Length,
                p => { lock (peaks) peaks.Add(p.OverallPercent); });

            lock (peaks)
            {
                Assert.NotEmpty(peaks);
                Assert.All(peaks, v => Assert.True(v < 100, $"完成前进度不应到 100，实际 {v}"));
            }
        }
        finally { File.Delete(dest); }
    }

    [Fact]
    public async Task SmallFile_SingleConnection_Works()
    {
        var small = _smallPayload;
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(small));
        var dest = TempPath();
        try
        {
            var svc = new DownloadService();
            await svc.DownloadFileAsync($"http://localhost:{Port}/small.bin", dest, sha1, small.Length);
            var actual = await File.ReadAllBytesAsync(dest);
            Assert.Equal(small, actual);
        }
        finally { File.Delete(dest); }
    }

    [Fact]
    public async Task UnknownSize_ExpectedSizeZero_UsesContentLength_ForProgress()
    {
        // 8-30 修「整合包无单位/进度」：mrpack 无 size 字段 → expectedSize=0（非 null）——
        // 旧 `expectedSize ?? ContentLength` 在 0 时取 0 吞掉响应头，total 恒 0 → UI "-/-" 无进度。
        // 修复后 expectedSize<=0 回落 Content-Length，progress 报真实总量。
        var small = _smallPayload;
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(small));
        var dest = TempPath();
        try
        {
            long? seenTotal = null;
            var svc = new DownloadService();
            await svc.DownloadFileAsync($"http://localhost:{Port}/small.bin", dest, sha1, expectedSize: 0,
                p => { if (p.FileTotalBytes > 0) seenTotal = p.FileTotalBytes; });
            Assert.Equal(small.Length, seenTotal);
        }
        finally { File.Delete(dest); }
    }
}
