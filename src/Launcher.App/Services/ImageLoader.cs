using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace Launcher.App.Services;

/// <summary>
/// 图片异步加载器：内存缓存 + 并发去重 + 降采样 + 磁盘缓存（AL65——切 tab/翻页不重下图标）。
/// 磁盘缓存 %LocalAppData%\Launcher\imgcache\{sha256(url)}；下载并发门 4（不抢主下载连接）；
/// 8s 超时（图标小，15s 太宽）。
/// </summary>
public static class ImageLoader
{
    // 8-26 图标不显示修复：cdn-alt.modrinth.com 国内 2-3s（坏时 >8s 超时→空白）——超时放宽到 12s 减少失败
    private static readonly HttpClient Http = Launcher.Core.Download.HttpClientPool.Create(TimeSpan.FromSeconds(12));
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> Cache = new();
    private static readonly SemaphoreSlim Gate = new(4);
    private static readonly string CacheDir = Path.Combine(
        Launcher.Core.Utils.AppPaths.CacheRoot, "imgcache");
    /// <summary>内存位图缓存上限（8-22 内存瘦身：旧实现只增不减——翻页/切 tab 攒几十上百张
    /// 位图常驻（每张 96px 解码 ≈36KB，300 张 = 10MB+）。超限整体清空：磁盘缓存（imgcache）
    /// 兜底重新解码（毫秒级），无泄漏无失效。按「近似 LRU」——字典无序，整体清最简。
    /// 8-26 内存真减：128→64（≈2.3MB 上限→1.1MB；磁盘兜底秒级重解，翻页体验无感）。</summary>
    private const int CacheMaxEntries = 64;

    /// <summary>内存位图缓存张数（--mem-profile 诊断用，只读）</summary>
    internal static int CacheCount => Cache.Count;

    public static Task LoadAsync(string? url, Action<Bitmap?> onLoaded, CancellationToken ct = default)
        => LoadAsync(url, onLoaded, 96, ct);

    /// <summary>按目标宽度解码（大图降采样节省内存）</summary>
    public static async Task LoadAsync(string? url, Action<Bitmap?> onLoaded, int decodeWidth, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(url))
        {
            onLoaded(null);
            return;
        }
        // 8-18 失败重试窗：失败缓存只锁 60s——启动早期网络未就绪的失败不能永久锁死头像/图标
        if (_failedAt.TryGetValue(url, out var f) && Environment.TickCount64 - f < FailRetryMs)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => onLoaded(null));
            return;
        }
        try
        {
            TrimIfNeeded(); // 8-22 超限整体清空（磁盘缓存兜底）
            var task = Cache.GetOrAdd(url, static u => DownloadAsync(u, 96));
            var bitmap = decodeWidth <= 96
                ? await task
                : await DownloadAsync(url, decodeWidth); // 大图单独解码（不污染小图缓存）
            // 8-22 回调封送 UI 线程：解码/下载在后台，直接回调 = 线程池触发绑定更新——
            // 搜索后 20 张图标同时完成 → Avalonia UI 线程排队风暴（「搜索时明显变卡」主因）
            Avalonia.Threading.Dispatcher.UIThread.Post(() => onLoaded(bitmap));
        }
        catch
        {
            // 失败也缓存 null：切 tab 反复重建视图时不再重复请求坏图（秒切换的关键）
            // 8-18 修正：null 缓存改为失败时间戳 + 60s 重试窗（原逻辑永久锁死——启动早期失败永不恢复）
            _failedAt[url] = Environment.TickCount64;
            Cache.TryRemove(url, out _);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => onLoaded(null));
        }
    }

    /// <summary>失败重试窗（毫秒，8-18）：失败后此窗内直接返回 null 不再请求，窗外重新尝试。
    /// 8-26 60s→20s：cdn-alt 间歇慢导致图标偶发空白，缩短重试窗让图标尽快重新加载</summary>
    private const long FailRetryMs = 20_000;

    /// <summary>url → 最近失败时间戳（8-18 替代永久 null 缓存）</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _failedAt = new();

    /// <summary>容量裁剪：超过上限整体清空（近似 LRU 的最简实现——位图重新解码有磁盘缓存兜底）。
    /// 8-19 内存瘦身：失败时间戳同步剪枝——窗外的条目已无意义（下次失败会重新覆盖），只增不减长期累积</summary>
    private static void TrimIfNeeded()
    {
        var cutoff = Environment.TickCount64 - FailRetryMs;
        foreach (var (url, ts) in _failedAt.Where(kv => kv.Value < cutoff))
            _failedAt.TryRemove(url, out _);
        if (Cache.Count <= CacheMaxEntries) return;
        foreach (var key in Cache.Keys)
            Cache.TryRemove(key, out _);
    }

    /// <summary>磁盘缓存清理（8-19 启动 fire-and-forget）：删除超过 30 天未访问的缓存文件——
    /// imgcache 里的图标 URL 随版本更新会永久失效（旧项目页/已删模组），磁盘只增不减</summary>
    public static void CleanupDiskCache(TimeSpan? maxAge = null)
    {
        maxAge ??= TimeSpan.FromDays(30);
        try
        {
            if (!Directory.Exists(CacheDir)) return;
            var cutoff = DateTime.UtcNow - maxAge.Value;
            foreach (var file in Directory.EnumerateFiles(CacheDir))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    private static async Task<Bitmap?> DownloadAsync(string url, int decodeWidth)
    {
        var path = CachePath(url);
        // 磁盘缓存命中：本地直接解码（无网络）
        if (File.Exists(path))
        {
            await using var fs = File.OpenRead(path);
            return Bitmap.DecodeToWidth(fs, decodeWidth);
        }
        // 下载并发门：最多 4 个图片请求同时进行
        await Gate.WaitAsync();
        try
        {
            // 双重检查（并发下其他线程可能已写入）
            if (File.Exists(path))
            {
                await using var fs = File.OpenRead(path);
                return Bitmap.DecodeToWidth(fs, decodeWidth);
            }
            // 8-26 图标镜像回退：cdn.modrinth.com 国内 307 到 cdn-alt（间歇超时/新连接挂，SESSION 实测），
            // 文件下载有 5 候选竞速镜像、图标此前直连单域名裸奔 → 成片空白。逐个候选限时重试，
            // 全部失败才抛（此时 LoadAsync 才写 20s 失败锁——不再单主源失败就锁死整批）。
            byte[]? bytes = null;
            foreach (var candidate in CandidateUrls(url))
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // 每候选限时，坏主源不拖满 12s
                    using var resp = await Http.GetAsync(candidate, cts.Token);
                    resp.EnsureSuccessStatusCode();
                    bytes = await resp.Content.ReadAsByteArrayAsync();
                    break;
                }
                catch { /* 该候选失败 → 换下一个镜像 */ }
            }
            if (bytes is null) throw new InvalidOperationException($"图标所有候选源均失败：{url}");
            try { Directory.CreateDirectory(CacheDir); await File.WriteAllBytesAsync(path, bytes); } catch { }
            using var ms = new MemoryStream(bytes);
            return Bitmap.DecodeToWidth(ms, decodeWidth);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>图标候选源（8-26）：主域名优先；cdn.modrinth.com 失败时按 host 换三个镜像
    /// （cdn-alt/cdn-raw/mcimirror 探针实测都能 200 服务图标缩略图）。非 Modrinth 域名只回主源。
    /// 磁盘缓存键按原 URL，镜像成功也落原键——不重复缓存。</summary>
    private static IEnumerable<string> CandidateUrls(string url)
    {
        const string primary = "https://cdn.modrinth.com/";
        yield return url;
        if (url.StartsWith(primary, StringComparison.OrdinalIgnoreCase))
        {
            var rest = url[primary.Length..];
            yield return "https://cdn-alt.modrinth.com/" + rest;
            yield return "https://cdn-raw.modrinth.com/" + rest;
            yield return "https://mod.mcimirror.top/" + rest;
        }
    }

    private static string CachePath(string url)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(CacheDir, key);
    }
}
