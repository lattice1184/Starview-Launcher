using System.Net;

namespace Launcher.Core.Download;

/// <summary>
/// 共享 HTTP 连接池（AL45 下载提速 P0+P1）：
/// 旧实现每次 new SocketsHttpHandler → 连接池永不复用，每个文件一次 TCP+TLS 握手
/// （50 个库文件 = 50 次握手，同 host 每次省 50-200ms）。
/// 连接池在 handler 上；HttpClient 是轻量无状态包装，各服务 new HttpClient(SharedHandler) 即复用连接。
/// HTTP/2：CDN 为 HTTPS 走 ALPN 协商 h2；bmclapi 等旧服务器经 RequestVersionOrLower 自动降级 HTTP/1.1。
/// </summary>
public static class HttpClientPool
{
    private static readonly object Gate = new();
    private static SocketsHttpHandler? _handler;
    private static HttpClient? _client;

    /// <summary>共享 handler：连接池 + 连接参数 + HTTP/2 多路复用。
    /// 8-20 代理支持：构造时读 LauncherSettings.ProxyAddress——配置后全局走代理（加速器场景）；
    /// 设置变更后 RebuildShared 重建（惰性：下次访问 Shared 才建新 handler）</summary>
    public static SocketsHttpHandler SharedHandler
    {
        get { lock (Gate) { return _handler ??= CreateHandler(); } }
    }

    /// <summary>共享 client（DownloadService.CreateClient 用；默认请求版本 HTTP/2，服务器不支持自动降级）</summary>
    public static HttpClient Shared
    {
        get { lock (Gate) { return _client ??= CreateShared(); } }
    }

    private static SocketsHttpHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),     // AL32：慢源 5s 判死（原 15s 直连卡 TCP/TLS）
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),   // 防陈旧连接/DNS 变更
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2), // 突发下载期保持连接热
            EnableMultipleHttp2Connections = true,         // HTTP/2 同 host 多连接
        };
        // 8-20 代理支持：host:port（加速器本地端口）→ 全局走代理；留空直连。仅 Http 代理（socks 加速器填 http 端口）
        var proxy = Launcher.Core.Utils.LauncherSettings.Current.ProxyAddress;
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new System.Net.WebProxy(proxy.Contains("://") ? proxy : "http://" + proxy);
            handler.UseProxy = true;
        }
        return handler;
    }

    private static HttpClient CreateShared() => CreateSharedClient();

    /// <summary>8-20 代理设置变更后重建共享连接池（设置页保存时调用）：
    /// 下次访问 Shared/SharedHandler 惰性建新 handler（带新代理）。
    /// 8-31 修：不再延迟 30s 销毁旧 handler——固定延时是盲猜、不等 in-flight 任务结束，
    /// 大文件/多文件下载剩余时间 &gt;30s 即命中 ObjectDisposedException（SocketsHttpHandler）——
    /// 朋友 Mac 实测：下载 JRE 期间改设置触发重建 → 下载崩 → 运行时残缺 → 游戏启动即崩 134。
    /// 旧 handler 交给 GC 回收（持有它的 DownloadService 实例释放后即可回收）；
    /// 空闲连接本身有 2min IdleTimeout 会自动关闭，不 Dispose 只是回收稍慢，远好于崩下载。</summary>
    public static void RebuildShared()
    {
        lock (Gate)
        {
            _handler = null;
            _client = null;
        }
    }

    /// <summary>8-19 修复：创建共享 handler 的 HttpClient，disposeHandler:false——
    /// 默认 new HttpClient(handler) 在 Dispose 时会连同共享连接池一起销毁（实机：LittleSkin 登录后
    /// 后续请求报 Cannot access a disposed object），所有新建 client 必须走这里</summary>
    public static HttpClient CreateSharedClient(TimeSpan? timeout = null)
    {
        var client = new HttpClient(SharedHandler, disposeHandler: false)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(15),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>创建带 15s 请求超时的 HttpClient（生态 API 用——默认 100s 超时会让慢源拖死整页）</summary>
    public static HttpClient Create(TimeSpan? timeout = null) => CreateSharedClient(timeout);

    /// <summary>
    /// 8-18 浏览器格式 UA：ghproxy.net 实测对非浏览器 UA（Starview/0.1）返回 403——
    /// 镜像候选实际不可用，大文件只剩 gh-proxy.com 一个镜像。带浏览器前缀 + 保留本启动器标识
    /// （CurseForge 要求 UA 含联系信息）。全仓无 UA 读取/校验逻辑，改动低风险。
    /// </summary>
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Starview/0.1";
}
