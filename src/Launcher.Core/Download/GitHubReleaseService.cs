using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Core.Download;

/// <summary>
/// GitHub releases 最新版检查（后台静默更新用）。
/// GET /repos/{o}/{r}/releases/latest → tag_name + assets 列表。
/// 复用 GitHubApiDirect 的认证头（EffectiveToken）与 403/429 退避模式；
/// 下载本身仍走 GitHubApiDirect 换链 + 镜像竞速，本服务只做"查版本、挑资产"。
/// </summary>
public static class GitHubReleaseService
{
    public const string Owner = "lattice1184";
    public const string Repo = "Starview-Launcher";

    /// <summary>检查用 HttpClient（internal 供测试注入 mock handler，同 GitHubApiDirect 模式）</summary>
    internal static HttpClient Http = HttpClientPool.Create();

    /// <summary>请求对象 JSON 键全小写，属性 PascalCase——必须忽略大小写</summary>
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>403/429 失败退避（限流期内不再空打 API，同 GitHubApiDirect）</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> FailureCache = new();

    /// <summary>清空失败缓存（仅测试用——静态缓存跨测试会污染断言）</summary>
    internal static void ClearCacheForTest() => FailureCache.Clear();

    /// <summary>最新版本信息（null = 请求失败/被限流/仓库无 release）</summary>
    public static async Task<LatestRelease?> GetLatestAsync(CancellationToken ct)
    {
        if (FailureCache.TryGetValue("latest", out var failUntil) && failUntil > DateTime.UtcNow)
            return null;

        var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Starview-Launcher/0.1");
        if (GitHubApiDirect.EffectiveToken() is { } token)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
                    FailureCache["latest"] = DateTime.UtcNow.AddMinutes(5);
                return null;
            }
            var release = JsonSerializer.Deserialize<ReleaseJson>(await resp.Content.ReadAsStringAsync(ct), JsonOpts);
            if (string.IsNullOrWhiteSpace(release?.TagName)) return null;
            return new LatestRelease(
                release.TagName,
                release.Assets?.Where(a => !string.IsNullOrWhiteSpace(a.Name))
                    .Select(a => new AssetInfo(a.Name!, a.BrowserDownloadUrl ?? "", a.Size))
                    .ToArray() ?? [],
                release.PublishedAt);
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

    /// <summary>匹配本平台可用的更新资产（null = 该 release 无本平台包）。Windows→exe，Linux/Mac→tar.gz</summary>
    public static AssetInfo? MatchPlatformAsset(LatestRelease? release)
    {
        if (OperatingSystem.IsWindows()) return MatchFor(release, "windows");
        if (OperatingSystem.IsLinux()) return MatchFor(release, "linux");
        if (OperatingSystem.IsMacOS()) return MatchFor(release, "macos");
        return null;
    }

    /// <summary>平台参数化匹配（internal 供测试覆盖三平台；archOverride 供测试模拟 Mac 两种架构）</summary>
    internal static AssetInfo? MatchFor(LatestRelease? release, string os, Architecture? archOverride = null)
    {
        if (release is null || release.Assets.Count == 0) return null;
        var arch = archOverride ?? RuntimeInformation.OSArchitecture;
        return os switch
        {
            "windows" => release.Assets.FirstOrDefault(a => string.Equals(a.Name, "Starview-Launcher.exe", StringComparison.OrdinalIgnoreCase)),
            "linux" => FindByPrefix(release.Assets, "starview-linux-x64-"),
            // 8-31 按本机架构匹配（Intel Mac 此前固定拿 arm64 包 → 打不开）。arm64 优先于 x64；兜底都有。
            "macos" => arch == Architecture.X64
                ? FindByPrefix(release.Assets, "starview-osx-x64-") ?? FindByPrefix(release.Assets, "starview-osx-arm64-")
                : FindByPrefix(release.Assets, "starview-osx-arm64-") ?? FindByPrefix(release.Assets, "starview-osx-x64-"),
            _ => null,
        };
    }

    private static AssetInfo? FindByPrefix(IReadOnlyList<AssetInfo> assets, string prefix)
        => assets.FirstOrDefault(a => a.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                      && a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                                      // 8-30 发布链同时出散文件包与 .app 变体——更新统一用散文件包（替换逻辑两种安装布局都适配）
                                      && !a.Name.EndsWith(".app.tar.gz", StringComparison.OrdinalIgnoreCase));

    public sealed record LatestRelease(string Tag, IReadOnlyList<AssetInfo> Assets, DateTime? PublishedAt);
    public sealed record AssetInfo(string Name, string BrowserDownloadUrl, long Size);

    // GitHub API 返回 snake_case（tag_name/browser_download_url），
    // PropertyNameCaseInsensitive 只忽略大小写不处理下划线——snake 键必须显式映射
    private sealed class ReleaseJson
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }
        public List<AssetJson>? Assets { get; set; }
        [JsonPropertyName("published_at")]
        public DateTime? PublishedAt { get; set; }
    }

    private sealed class AssetJson
    {
        public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
        public long Size { get; set; }
    }
}
