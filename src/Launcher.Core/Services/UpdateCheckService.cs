using System.Text.Json;
using Launcher.Core.Download;
using Launcher.Core.Utils;

namespace Launcher.Core.Services;

/// <summary>
/// 后台静默更新检查 + 下载（Part B）。
/// 启动后延迟检查 GitHub 最新 release → 匹配本平台资产 → 后台下载到临时目录；
/// 就绪状态落盘（{DataRoot}/update-state.json），重启后不重复下载、直接提示。
/// 不依赖 UI——App 层订阅结果弹「更新已就绪，重启生效」。
/// </summary>
public static class UpdateCheckService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>自动检查冷却：每次启动都打 GitHub API 浪费额度（未认证 60 次/小时按 IP）——6 小时内不重复自动检查</summary>
    private static readonly TimeSpan AutoCooldown = TimeSpan.FromHours(6);

    /// <summary>就绪状态落盘（同名文件幂等：dest 已完整存在时 DownloadService 按 size 跳过）</summary>
    private sealed record StateFile(string? ReadyTag, string? ReadyPath, DateTime? ReadyUtc, DateTime? LastCheckedUtc);

    public sealed record CheckResult(
        bool HasUpdate, bool WasSkipped, string? LatestTag, string? ReadyPath, string? AssetName, string? Error)
    {
        public static CheckResult Ready(string tag, string path)
            => new(true, false, tag, path, Path.GetFileName(path), null);
        public static CheckResult UpToDate(string tag)
            => new(false, false, tag, null, null, null);
        public static CheckResult Skipped()
            => new(false, true, null, null, null, null);
        public static CheckResult Failed(string error)
            => new(false, false, null, null, null, error);
    }

    /// <summary>测试注入：重定向状态文件路径（避免污染真实 %AppData%）</summary>
    internal static string? StateFileOverrideForTest;

    private static string StateFilePath()
        => StateFileOverrideForTest ?? Path.Combine(AppPaths.DataRoot, "update-state.json");

    /// <summary>检查并下载最新版。force=true 忽略冷却（设置页「检查更新」手动触发）</summary>
    public static async Task<CheckResult> CheckAsync(string currentVersion, bool force = false, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            // 1. 已就绪未消费 → 直接复用（重启后也提示，不必重下）
            var state = LoadState();
            if (state is not null && !string.IsNullOrWhiteSpace(state.ReadyPath) && File.Exists(state.ReadyPath))
                return CheckResult.Ready(state.ReadyTag ?? "", state.ReadyPath);

            // 2. 自动检查冷却（手动 force=true 无视）
            if (!force && state is { LastCheckedUtc: { } last } && DateTime.UtcNow - last < AutoCooldown)
                return CheckResult.Skipped();

            // 3. 查最新 release
            var release = await GitHubReleaseService.GetLatestAsync(ct);
            if (release is null)
                return CheckResult.Failed("检查更新失败（网络不通或 GitHub 限流）");

            // 4. 版本比较（当前已是最新 → 只记检查时间）
            if (VersionUtil.Compare(release.Tag, currentVersion) <= 0)
            {
                SaveState(new StateFile(null, null, null, DateTime.UtcNow));
                return CheckResult.UpToDate(release.Tag);
            }

            // 5. 匹配本平台资产
            var asset = GitHubReleaseService.MatchPlatformAsset(release);
            if (asset is null)
            {
                SaveState(new StateFile(null, null, null, DateTime.UtcNow));
                return CheckResult.Failed("最新版没有本平台安装包");
            }

            // 6. 后台下载到临时目录。必须显式用 ThirdPartyDlSourceResolver——
            // 默认 resolver 不含 GitHub 加速（DefaultDlSourceMapper 原样返回，github.com 官方直链国内被墙）。
            // 竞速候选：官方 + ghproxy.net/gh-proxy.com 镜像 + ghapi 换签名 CDN 直链，谁快用谁
            var dir = Path.Combine(Path.GetTempPath(), "starview-update");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, asset.Name);
            var dl = new DownloadService(http: null, resolver: new ThirdPartyDlSourceResolver(), options: null, gameDirectory: null);
            await dl.DownloadFileAsync(asset.BrowserDownloadUrl, dest, null,
                asset.Size > 0 ? asset.Size : null, null, ct);

            SaveState(new StateFile(release.Tag, dest, DateTime.UtcNow, DateTime.UtcNow));
            return CheckResult.Ready(release.Tag, dest);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return CheckResult.Failed("检查更新已取消");
        }
        catch (Exception ex)
        {
            return CheckResult.Failed($"检查更新失败：{ex.Message}");
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>消费就绪状态（更新安装完成后调用——避免重启后重复提示）</summary>
    public static void MarkConsumed()
    {
        try
        {
            var state = LoadState();
            if (state is null) return;
            SaveState(new StateFile(null, null, null, state.LastCheckedUtc));
        }
        catch { }
    }

    private static StateFile? LoadState()
    {
        try
        {
            var path = StateFilePath();
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<StateFile>(File.ReadAllText(path), JsonOpts);
        }
        catch { return null; }
    }

    private static void SaveState(StateFile state)
    {
        try
        {
            var path = StateFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOpts));
        }
        catch { }
    }
}
