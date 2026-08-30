using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Launcher.Core.Multiplayer;

/// <summary>
/// EasyTier 联机模块的下载 / 校验 / 安装（锁版本 v2.6.4，SHA256 必校验；LGPL-3.0 进程外调用）。
/// 候选链：GitHub 直连 → 镜像（ghfast.top / ghproxy.net——镜像域名易变，任一可用即过）。
/// 安装到 %AppData%\Launcher\tools\easytier\{version}\。模式对齐 TerracottaProvisioningService。
/// </summary>
public sealed class EasyTierProvisioningService
{
    /// <summary>锁定的 EasyTier 版本</summary>
    public const string LockedVersion = "2.6.4";

    /// <summary>已知 SHA256（{version}/{arch}/{os}）——资产校验用（8-29 Linux 实测下载后计算写入；
    /// macOS 仍 pending = 安全拒装，待 Mac/代理实测下载回填）</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownDigests = new Dictionary<string, string>
    {
        ["2.6.4/x86_64/windows"] = "27af91e270e554709b048bd32327fefd2dfce5062ae1e8701af7550c6f525f84",
        ["2.6.4/x86_64/linux"] = "pending",
        ["2.6.4/aarch64/macos"] = "pending",
        ["2.6.4/x86_64/macos"] = "pending",
    };

    /// <summary>平台键：windows / macos / linux（macOS 包命名需上游实测；若确无 macOS 构建则 Mac 隐藏联机入口）</summary>
    public static string OsKey => OperatingSystem.IsMacOS() ? "macos"
        : OperatingSystem.IsWindows() ? "windows" : "linux";

    /// <summary>EasyTier 上游 arch 键：Apple Silicon → aarch64，x64 → x86_64</summary>
    public static string ArchKey => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";

    /// <summary>资产名：easytier-{os}-{arch}-v{version}.zip（windows/linux 现有命名不变）</summary>
    public static string AssetName(string version) => $"easytier-{OsKey}-{ArchKey}-v{version}.zip";

    /// <summary>包内可执行名（Windows 带 .exe，Linux 无扩展名）</summary>
    public static string CoreExeName => OperatingSystem.IsWindows() ? "easytier-core.exe" : "easytier-core";
    public static string CliExeName => OperatingSystem.IsWindows() ? "easytier-cli.exe" : "easytier-cli";

    /// <summary>GitHub 资产 URL（官方源）</summary>
    public static string GitHubAssetUrl(string version)
        => $"https://github.com/EasyTier/EasyTier/releases/download/v{version}/{AssetName(version)}";

    /// <summary>镜像候选（国内加速；域名易变——候选链依序尝试）</summary>
    public static string[] MirrorUrls(string version) =>
    [
        $"https://ghfast.top/https://github.com/EasyTier/EasyTier/releases/download/v{version}/{AssetName(version)}",
        $"https://ghproxy.net/https://github.com/EasyTier/EasyTier/releases/download/v{version}/{AssetName(version)}",
    ];

    /// <summary>安装根：应用数据目录 tools\easytier（Linux: ~/.local/share/starview/tools/easytier）</summary>
    public static string ModuleRoot => Path.Combine(
        Launcher.Core.Utils.AppPaths.ToolsDir, "easytier");

    private const long MaxArchiveBytes = 128 * 1024 * 1024;
    private const int BufferSize = 81920;

    private static readonly SemaphoreSlim InstallLock = new(1, 1);

    private readonly HttpClient _http;

    public EasyTierProvisioningService(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) });
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("YanKa-Launcher/1.0");
    }

    /// <summary>已装模块（easytier-core + easytier-cli 都在则可用），无则 null</summary>
    public bool TryGetAvailable(out string moduleDir)
    {
        moduleDir = Path.Combine(ModuleRoot, LockedVersion);
        return File.Exists(Path.Combine(moduleDir, CoreExeName))
            && File.Exists(Path.Combine(moduleDir, CliExeName));
    }

    /// <summary>重装（一键修复）：清版本目录 → 重新下载安装</summary>
    public async Task<string> ReinstallAsync(CancellationToken ct = default)
    {
        var dir = Path.Combine(ModuleRoot, LockedVersion);
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
        return await EnsureAvailableAsync(ct);
    }

    /// <summary>确保模块可用：已装直接返回；否则下载安装（校验 SHA256 + 解压）。并发串行。</summary>
    public async Task<string> EnsureAvailableAsync(CancellationToken ct = default)
    {
        await InstallLock.WaitAsync(ct);
        try
        {
            if (TryGetAvailable(out var installed)) return installed;

            var version = LockedVersion;
            var expectedSha = KnownDigests.TryGetValue($"{version}/{ArchKey}/{OsKey}", out var s) && s != "pending" ? s : null;

            var candidates = new List<string> { GitHubAssetUrl(version) };
            candidates.AddRange(MirrorUrls(version));
            string? lastError = null;
            foreach (var url in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return await DownloadAndInstallAsync(version, url, expectedSha, ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }
            throw new MultiplayerLobbyException(
                MultiplayerLobbyFailure.BackendUnavailable,
                $"EasyTier 模块下载失败：{lastError ?? "未知错误"}（已尝试官方源与镜像）");
        }
        finally
        {
            InstallLock.Release();
        }
    }

    private async Task<string> DownloadAndInstallAsync(string version, string url, string? expectedSha, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"easytier-{Guid.NewGuid():N}.zip");
        try
        {
            // 流式落盘 + SHA256 边下边算（大文件不占内存）
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;
            await using (var fs = File.Create(temp))
            {
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                var sha = SHA256.Create();
                var buffer = new byte[BufferSize];
                long read = 0;
                while (true)
                {
                    var n = await src.ReadAsync(buffer, ct);
                    if (n == 0) break;
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    read += n;
                    if (read > MaxArchiveBytes)
                        throw new InvalidDataException($"EasyTier 包超限（>{MaxArchiveBytes / 1024 / 1024}MB）");
                    fs.Write(buffer, 0, n);
                }
                sha.TransformFinalBlock([], 0, 0);
                // 8-20 堵口子（对齐 TerracottaProvisioningService）：锁版本必须有已知哈希——缺失即拒绝安装
                // （原 null → 静默不校验：将来改 LockedVersion 忘了补哈希表 = 下载二进制裸奔直接执行）
                if (expectedSha is null)
                    throw new InvalidDataException($"缺少 {version}/x86_64 的已知 SHA256，拒绝安装");
                var actual = Convert.ToHexString(sha.Hash!);
                if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"EasyTier 包校验失败（期望 {expectedSha[..8]}… 实际 {actual[..8]}…）");
                MultiplayerLog.Log($"EasyTier 下载完成：{read / 1024 / 1024}MB（sha {expectedSha[..8]}）");
            }

            // 解压到版本目录（zip 内两个可执行：core + cli）
            var moduleDir = Path.Combine(ModuleRoot, version);
            Directory.CreateDirectory(moduleDir);
            ZipFile.ExtractToDirectory(temp, moduleDir, overwriteFiles: true);
            if (!TryGetAvailable(out _))
                throw new InvalidDataException($"EasyTier 包内容不完整（缺 {CoreExeName} / {CliExeName}）");
            return moduleDir;
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }
}
