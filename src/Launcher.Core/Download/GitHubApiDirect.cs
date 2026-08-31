using System.Net;
using System.Text.Json;

namespace Launcher.Core.Download;

/// <summary>
/// GitHub 官方直链换链（黑科技 A，08-10 实测）：github.com 主域国内被墙（21s 超时），
/// 但 api.github.com（200）与 release-assets.githubusercontent.com（网络通）可达。
/// 通过 API 两步换链拿到 release 资产的签名直链——全程不碰 github.com、不依赖第三方镜像：
///   1. GET /repos/{o}/{r}/releases/tags/{tag} → assets 按文件名匹配 asset id
///   2. GET /repos/{o}/{r}/releases/assets/{id} + Accept: octet-stream → 302 到签名 CDN URL
///      （HttpClient 自动跟随，response.RequestMessage.RequestUri 即签名直链）
/// 签名 URL 约 1 小时过期——403 时外层换源重试会重新 Resolve → 重新换链。
/// 实测签名直链国内仅 64KB/s（慢），定位是"镜像全挂时的官方兜底源"，参与竞速不拖累其他源。
/// </summary>
public static class GitHubApiDirect
{
    /// <summary>ghapi 占位 URL 前缀（ThirdPartyDlSourceResolver 生成；DownloadService 下载前换链）</summary>
    public const string Scheme = "ghapi:";

    /// <summary>换链用 HttpClient（internal 供测试注入 mock handler）</summary>
    internal static HttpClient Http = HttpClientPool.Create();

    /// <summary>测试注入用（null = 动态读设置；"" = 显式未认证）</summary>
    internal static string? TokenOverride;

    /// <summary>8-13 当前生效 token（每次现读设置——改 token 即时生效；空 = 未认证默认模式）</summary>
    internal static string? EffectiveToken()
    {
        if (TokenOverride is not null) return TokenOverride.Length == 0 ? null : TokenOverride;
        var t = Launcher.Core.Utils.LauncherSettings.Current.GitHubApiToken;
        return string.IsNullOrWhiteSpace(t) ? null : t.Trim();
    }

    /// <summary>清空签名/失败缓存（仅测试用——静态缓存跨测试会污染同 URL 断言）</summary>
    internal static void ClearCacheForTest()
    {
        Cache.Clear();
        FailureCache.Clear();
    }

    /// <summary>GitHub API 返回键全小写（assets/name/id），属性名 PascalCase——必须忽略大小写</summary>
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>签名 URL 内存缓存（换链 API 有速率限制；签名本身也够 30 分钟）</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry> Cache = new();

    private sealed record CacheEntry(string Url, DateTime Expires);

    /// <summary>8-13 失败退避：换链失败（限流 403/429 等）也缓存——否则每轮重试 Resolve 都再打 API，
    /// 重试风暴耗尽未认证 60 次/小时额度（真机 8-13 候选从 6 源变 3 源即此）</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> FailureCache = new();

    /// <summary>
    /// 解析 ghapi 占位 URL：ghapi:{owner}/{repo}/{tag}/{name} → 签名直链（null = 换链失败/不支持）
    /// </summary>
    public static async Task<string?> GetSignedUrlAsync(string ghapiUrl, CancellationToken ct)
    {
        if (!ghapiUrl.StartsWith(Scheme)) return null;
        var parts = ghapiUrl[Scheme.Length..].Split('/', 4);
        if (parts.Length < 4 || parts.Any(string.IsNullOrEmpty)) return null;
        var (owner, repo, tag, name) = (parts[0], parts[1], parts[2], parts[3]);

        if (Cache.TryGetValue(ghapiUrl, out var hit) && hit.Expires > DateTime.UtcNow)
            return hit.Url;
        // 8-13 失败退避：限流期内直接放弃，不再打 API
        if (FailureCache.TryGetValue(ghapiUrl, out var failUntil) && failUntil > DateTime.UtcNow)
            return null;

        try
        {
            // 1. tags → assets 列表 → 按文件名匹配 asset id
            var tagsUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}";
            using var tagsReq = new HttpRequestMessage(HttpMethod.Get, tagsUrl);
            ApplyAuth(tagsReq);
            using var tagsResp = await Http.SendAsync(tagsReq, ct);
            if (!tagsResp.IsSuccessStatusCode)
            {
                if (IsRateLimited(tagsResp)) MarkFailure(ghapiUrl);
                return null;
            }
            var release = JsonSerializer.Deserialize<ReleaseJson>(await tagsResp.Content.ReadAsStringAsync(ct), JsonOpts);
            var asset = release?.Assets?.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (asset is null) return null;

            // 2. assets → octet-stream → 302 签名直链（HttpClient 自动跟随；RequestMessage.RequestUri 是最终 URL）
            var assetUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/assets/{asset.Id}";
            using var req = new HttpRequestMessage(HttpMethod.Get, assetUrl);
            req.Headers.Accept.ParseAdd("application/octet-stream");
            req.Headers.UserAgent.ParseAdd("Starview-Launcher/0.1");
            ApplyAuth(req);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                if (IsRateLimited(resp)) MarkFailure(ghapiUrl);
                return null;
            }
            var signed = resp.RequestMessage?.RequestUri?.ToString();
            if (string.IsNullOrEmpty(signed)) return null;

            Cache[ghapiUrl] = new CacheEntry(signed, DateTime.UtcNow.AddMinutes(30));
            return signed;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>8-13 限流判定：403/429（GitHub 未认证 60 次/小时按 IP；配 token 5000 次/小时）</summary>
    private static bool IsRateLimited(HttpResponseMessage resp)
        => resp.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests;

    /// <summary>8-13 附加认证头：配置了 GitHub token 才带（未配置 = 普通用户未认证模式）</summary>
    private static void ApplyAuth(HttpRequestMessage req)
    {
        if (EffectiveToken() is { } token)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>8-13 记录失败退避（5 分钟——限流窗口远大于此，等额度自然恢复前不再空打 API）</summary>
    private static void MarkFailure(string ghapiUrl) => FailureCache[ghapiUrl] = DateTime.UtcNow.AddMinutes(5);

    private sealed class ReleaseJson
    {
        public List<AssetJson>? Assets { get; set; }
    }

    private sealed class AssetJson
    {
        public long Id { get; set; }
        public string? Name { get; set; }
    }
}
