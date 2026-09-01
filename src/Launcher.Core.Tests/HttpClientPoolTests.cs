using System.Net;
using Launcher.Core.Download;

namespace Launcher.Core.Tests;

/// <summary>共享连接池（AL45）：参数断言 + 复用语义</summary>
public class HttpClientPoolTests
{
    [Fact]
    public void SharedHandler_HasTunedParameters()
    {
        // 连接参数定调：慢源快速判死 + 连接热保持 + HTTP/2 多路复用
        Assert.Equal(TimeSpan.FromSeconds(5), HttpClientPool.SharedHandler.ConnectTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), HttpClientPool.SharedHandler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromMinutes(2), HttpClientPool.SharedHandler.PooledConnectionIdleTimeout);
        Assert.True(HttpClientPool.SharedHandler.EnableMultipleHttp2Connections);
    }

    [Fact]
    public void SharedClient_PrefersHttp2_WithFallback()
    {
        // HTTP/2 优先，服务器不支持自动降级 HTTP/1.1（bmclapi 等旧源）
        Assert.Equal(HttpVersion.Version20, HttpClientPool.Shared.DefaultRequestVersion);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, HttpClientPool.Shared.DefaultVersionPolicy);
    }

    [Fact]
    public void SharedHandler_Singleton()
    {
        // 单例语义：任何引用都是同一实例（连接池唯一）
        Assert.Same(HttpClientPool.SharedHandler, HttpClientPool.SharedHandler);
        Assert.Same(HttpClientPool.Shared, HttpClientPool.Shared);
    }

    [Fact]
    public void SharedClient_UserAgent_BrowserCompatible()
    {
        // 8-18 UA 浏览器格式：ghproxy.net 对非浏览器 UA 返回 403（镜像候选实际不可用）；
        // 浏览器前缀 + 保留本启动器标识（CurseForge 要求 UA 含联系信息）
        var ua = HttpClientPool.Shared.DefaultRequestHeaders.UserAgent.ToString();
        Assert.Contains("Mozilla", ua);
        Assert.Contains("Starview", ua);
    }

    [Fact]
    public void RebuildShared_RecreatesInstances_WithoutDisposingOld()
    {
        // 8-31 崩 134 根因回归：旧实现 RebuildShared 对旧 handler 盲 30s 定时 Dispose——
        // 仍持有旧 client 的下载任务（DownloadService 构造时捕获 Shared）→ ObjectDisposedException。
        // 新实现只置空静态引用交给 GC，不主动销毁：重建本身不抛、新实例可用。
        var oldClient = HttpClientPool.Shared;
        var oldHandler = HttpClientPool.SharedHandler;
        Assert.NotNull(oldClient);
        Assert.NotNull(oldHandler);

        HttpClientPool.RebuildShared();

        // 重建后拿到全新实例（连接池带新配置）
        var newClient = HttpClientPool.Shared;
        var newHandler = HttpClientPool.SharedHandler;
        Assert.NotSame(oldClient, newClient);
        Assert.NotSame(oldHandler, newHandler);
        // 新实例可用（不抛 ObjectDisposedException）
        Assert.NotNull(newClient.DefaultRequestHeaders.UserAgent.ToString());
    }
}
