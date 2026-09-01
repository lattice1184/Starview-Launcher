using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Launcher.Core.Multiplayer;

/// <summary>
/// 陶瓦联机模块的下载 / 校验 / 安装（锁版本 0.4.2，不做 latest 探测——比 BHL 更简，SHA256 必校验）。
/// 双源候选：Gitee 优先 → GitHub 备选。安装到 %AppData%\Launcher\tools\terracotta\{version}\。
/// 移植自 BlockHelm-Launcher（GPL-3.0）的 TerracottaProvisioningService。
/// </summary>
public sealed class TerracottaProvisioningService
{
    /// <summary>锁定的陶瓦版本</summary>
    public const string LockedVersion = "0.4.2";

    /// <summary>已知 SHA256（{version}/{arch}/{os}）——资产校验用（8-29 实测下载 Linux x86_64 计算写入）</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownDigests = new Dictionary<string, string>
    {
        ["0.4.2/x86_64/windows"] = "07ebe139e3ca5f74576e58b1a96efe59abdfbe148d3f1a49bfdca8b6f70745f0",
        ["0.4.2/arm64/windows"] = "acfab0a87a02dedc6dab7c05303186c8907f56f815548b693fb3324358da7d14",
        ["0.4.2/x86_64/linux"] = "675c4fd6c74d49ed8165151ba2be5b6582e0af20fb6d912074543c2484b1e10a",
        // macOS：pending = 安全拒装（digest 待 Mac/代理实测下载回填）
        ["0.4.2/arm64/macos"] = "13de7f9ce8733971b23493fabbe7e16d480f1e0d16a6265b4861f5a01bbecb60", // 9-1 直连实测
        ["0.4.2/x86_64/macos"] = "16306157d89423ce79fa901cdb75a6386ec1a9b1bd43a5d47c2c47cf01a16b86", // 9-1 直连实测
    };

    /// <summary>平台键：windows / macos / linux（macOS 值须与上游 /meta 的 target_os 一致，可能 darwin——待实测；
    /// 若上游确无 macOS 构建则 Mac 隐藏联机入口）</summary>
    public static string OsKey => OperatingSystem.IsMacOS() ? "macos"
        : OperatingSystem.IsWindows() ? "windows" : "linux";

    public static string AssetName(string version, string arch) => $"terracotta-{version}-{OsKey}-{arch}-pkg.tar.gz";

    /// <summary>资产内主文件原始名（用于识别并统一改名）。Windows 包内为 terracotta-{ver}-windows-{arch}.exe；
    /// Linux 包内无扩展名。8-29 修复：平台化时曾漏掉 Windows 的 .exe，导致重命名匹配不上 → 安装被拒。</summary>
    public static string ExeFileName(string version, string arch) => OperatingSystem.IsWindows()
        ? $"terracotta-{version}-{OsKey}-{arch}.exe"
        : $"terracotta-{version}-{OsKey}-{arch}";

    /// <summary>安装后的统一可执行名（Windows 带 .exe，Linux 无扩展名）</summary>
    public static string InstalledExeName => OperatingSystem.IsWindows() ? "terracotta.exe" : "terracotta";

    /// <summary>Gitee 资产 URL（国内快，优先）</summary>
    public static string GiteeAssetUrl(string version, string arch)
        => $"https://gitee.com/burningtnt/Terracotta/releases/download/v{version}/{AssetName(version, arch)}";

    /// <summary>GitHub 资产 URL（备选）</summary>
    public static string GitHubAssetUrl(string version, string arch)
        => $"https://github.com/burningtnt/Terracotta/releases/download/v{version}/{AssetName(version, arch)}";

    /// <summary>安装根：%AppData%\Launcher\tools\terracotta</summary>
    public static string ModuleRoot => Path.Combine(
        Launcher.Core.Utils.AppPaths.DataRoot, "tools", "terracotta");

    /// <summary>manifest 文件名（自研命名，区别于 BHL 的 .blockhelm-module.json）</summary>
    public const string ManifestName = ".terracotta-module.json";

    /// <summary>陶瓦守护进程互斥锁文件（%TEMP%/tmp 下，与 HMCL 握手协议一致；Lobby 读、Repair 删共用）</summary>
    public static string LockPath => Path.Combine(Path.GetTempPath(), "terracotta", "terracotta.lock");

    private const long MaxArchiveBytes = 64 * 1024 * 1024;
    private const int BufferSize = 81920;

    private static readonly SemaphoreSlim InstallLock = new(1, 1);

    private readonly HttpClient _http;

    public TerracottaProvisioningService(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) });
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Starview-Launcher/1.0");
    }

    /// <summary>当前架构名（x86_64 / arm64）</summary>
    public static string Arch =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x86_64",
        };

    /// <summary>已装模块（校验通过的最高版本），无则 null</summary>
    public TerracottaModule? TryGetAvailable()
    {
        if (!Directory.Exists(ModuleRoot))
        {
            MultiplayerLog.Log($"模块扫描: 根目录不存在 {ModuleRoot}");
            return null;
        }
        var versionDirs = Directory.GetDirectories(ModuleRoot);
        MultiplayerLog.Log($"模块扫描: {versionDirs.Length} 个版本目录");
        TerracottaModule? best = null;
        foreach (var dir in versionDirs)
        {
            var moduleDir = Path.Combine(dir, $"terracotta-{OsKey}-{Arch}");
            var module = ValidateInstallation(moduleDir);
            if (module is null)
            {
                MultiplayerLog.Log($"模块扫描: {Path.GetFileName(dir)} 校验未通过");
                continue;
            }
            MultiplayerLog.Log($"模块扫描: {Path.GetFileName(dir)} 校验通过 v{module.Version}");
            if (best is null || CompareVersions(module.Version, best.Version) > 0) best = module;
        }
        if (best is null) MultiplayerLog.Log("模块扫描: 无可用模块");
        return best;
    }

    /// <summary>重装模块（AL44 一键修复）：清掉现有版本目录 → 走 EnsureAvailableAsync 重新下载安装。</summary>
    public async Task<TerracottaModule> ReinstallAsync(
        IProgress<TerracottaProvisionProgress>? progress = null, CancellationToken ct = default)
    {
        if (Directory.Exists(ModuleRoot))
        {
            try { Directory.Delete(ModuleRoot, recursive: true); } catch { /* 删不掉则装到空壳上 */ }
        }
        return await EnsureAvailableAsync(progress, ct);
    }

    /// <summary>确保模块可用：已装且校验通过直接返回；否则下载安装。并发串行（SemaphoreSlim）。</summary>
    public async Task<TerracottaModule> EnsureAvailableAsync(
        IProgress<TerracottaProvisionProgress>? progress = null, CancellationToken ct = default)
    {
        await InstallLock.WaitAsync(ct);
        try
        {
            if (TryGetAvailable() is { } installed) return installed;

            var version = LockedVersion;
            var arch = Arch;
            // 8-30 修：pending 未过滤会当真哈希比对（Mac 联机下载报"SHA256 不匹配：期望 pending"）——对齐 EasyTier 拒装语义
            var expectedSha = KnownDigests.TryGetValue($"{version}/{arch}/{OsKey}", out var s) && s != "pending" ? s : null;

            var candidates = new[] { GiteeAssetUrl(version, arch), GitHubAssetUrl(version, arch) };
            string? lastError = null;
            foreach (var url in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return await DownloadAndInstallAsync(version, arch, url, expectedSha, progress, ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }
            throw new MultiplayerLobbyException(
                MultiplayerLobbyFailure.BackendUnavailable,
                $"陶瓦模块下载失败：{lastError ?? "未知错误"}（已尝试 Gitee 与 GitHub）");
        }
        finally
        {
            InstallLock.Release();
        }
    }

    // ---------- 下载 + 安装 ----------

    private async Task<TerracottaModule> DownloadAndInstallAsync(
        string version, string arch, string url, string? expectedSha,
        IProgress<TerracottaProvisionProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new TerracottaProvisionProgress("terracotta-download", 0));
        var tmp = Path.Combine(Path.GetTempPath(), $"terracotta-{Guid.NewGuid():N}.tar.gz");
        try
        {
            using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                if (total <= 0 || total > MaxArchiveBytes)
                    throw new InvalidDataException($"归档大小异常：{total} 字节");
                await using var fs = File.Create(tmp);
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[BufferSize];
                long read = 0;
                while (true)
                {
                    var n = await stream.ReadAsync(buffer, ct);
                    if (n == 0) break;
                    read += n;
                    if (read > MaxArchiveBytes)
                        throw new InvalidDataException("归档超过 64MB 上限");
                    await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                    var percent = total > 0 ? (int)Math.Clamp(read * 90 / total, 0, 90) : 0;
                    progress?.Report(new TerracottaProvisionProgress("terracotta-download", percent));
                }
            }

            // SHA256 必校验（锁版后不存在无 digest 分支）
            if (expectedSha is null)
                throw new InvalidDataException($"缺少 {version}/{arch} 的已知 SHA256");
            var actualSha = await Sha256HexAsync(tmp, ct);
            if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"SHA256 不匹配：期望 {expectedSha}，实际 {actualSha}（下载可能被篡改）");

            progress?.Report(new TerracottaProvisionProgress("terracotta-extract", 92));
            return await InstallFromArchiveAsync(version, arch, tmp, expectedSha, progress, ct);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* 清理失败无所谓 */ }
        }
    }

    /// <summary>解压（预检：仅 2 个文件、扁平名、≤64MB）→ staging → manifest → 原子发布（旧目录挪 backup，失败回滚）</summary>
    private static async Task<TerracottaModule> InstallFromArchiveAsync(
        string version, string arch, string archivePath, string archiveSha,
        IProgress<TerracottaProvisionProgress>? progress, CancellationToken ct)
    {
        var installRoot = Path.Combine(ModuleRoot, version);
        var targetDir = Path.Combine(installRoot, $"terracotta-{OsKey}-{arch}");
        var staging = targetDir + ".staging-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(staging);
        try
        {
            var files = new Dictionary<string, (long Size, string Sha)>();
            await using (var gz = new GZipStream(File.OpenRead(archivePath), CompressionMode.Decompress))
            using (var tar = new TarReader(gz))
            {
                while (await tar.NextAsync(ct) is { } entry)
                {
                    if (entry.Kind is not (TarEntryKind.RegularFile or TarEntryKind.V7RegularFile)) continue;
                    var name = entry.Name;
                    // 扁平名：含目录分隔符或 . / .. 直接拒
                    if (name.Contains('/') || name is "." or "..")
                        throw new InvalidDataException($"归档含非法路径：{name}");
                    var realName = name == ExeFileName(version, arch) ? InstalledExeName : name;
                    if (files.ContainsKey(realName))
                        throw new InvalidDataException($"归档含重复文件：{realName}");
                    if (entry.Size <= 0 || entry.Size > MaxArchiveBytes)
                        throw new InvalidDataException($"归档项大小异常：{name} = {entry.Size} 字节");
                    var outPath = Path.Combine(staging, realName);
                    await using (var fs = File.Create(outPath))
                        await tar.CopyToAsync(fs, BufferSize, ct);
                    files[realName] = (entry.Size, await Sha256HexAsync(outPath, ct));
                }
            }
            // 文件集：Windows = terracotta.exe + VCRUNTIME140.DLL；Linux = terracotta（无 VC 运行库）
            if (OperatingSystem.IsWindows())
            {
                if (files.Count != 2 || !files.ContainsKey("terracotta.exe") || !files.ContainsKey("VCRUNTIME140.DLL"))
                    throw new InvalidDataException($"归档文件不齐：{string.Join(", ", files.Keys)}");
            }
            else if (files.Count != 1 || !files.ContainsKey("terracotta"))
            {
                throw new InvalidDataException($"归档文件不齐：{string.Join(", ", files.Keys)}");
            }

            // manifest（先写 tmp 再 Move 覆盖，防半写）
            var manifestPath = Path.Combine(staging, ManifestName);
            var manifest = new ModuleManifest
            {
                Version = version,
                Architecture = arch,
                ArchiveSha256 = archiveSha,
                PublisherDigestVerified = true,
                Files = files.ToDictionary(kv => kv.Key, kv => new ModuleFileInfo(kv.Value.Size, kv.Value.Sha)),
            };
            var manifestTmp = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(manifestTmp, JsonSerializer.Serialize(manifest), ct);
            File.Move(manifestTmp, manifestPath);

            // 原子发布：旧目录挪 backup，失败回滚
            var backup = targetDir + ".backup-" + Guid.NewGuid().ToString("N")[..8];
            if (Directory.Exists(targetDir)) Directory.Move(targetDir, backup);
            try
            {
                Directory.Move(staging, targetDir);
            }
            catch
            {
                if (Directory.Exists(backup)) Directory.Move(backup, targetDir); // 回滚
                throw;
            }
            if (Directory.Exists(backup))
            {
                try { Directory.Delete(backup, true); } catch { /* 备份删除失败无碍 */ }
            }
            progress?.Report(new TerracottaProvisionProgress("terracotta-ready", 100));
            return ValidateInstallation(targetDir)
                ?? throw new InvalidDataException("安装后校验失败");
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    /// <summary>校验已装目录：manifest 存在、arch 匹配、目录名 == manifest.Version、2 文件 Size+SHA256 全匹配</summary>
    private static TerracottaModule? ValidateInstallation(string dir)
    {
        try
        {
            var manifestPath = Path.Combine(dir, ManifestName);
            if (!File.Exists(manifestPath)) return null;
            var manifest = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(manifestPath));
            if (manifest is null || manifest.Architecture != Arch) return null;
            var dirName = Path.GetFileName(dir);
            if (!dirName.StartsWith($"terracotta-{OsKey}-") || !dirName.EndsWith(manifest.Architecture)) return null;
            if (manifest.Files.Count != (OperatingSystem.IsWindows() ? 2 : 1)
                || !manifest.Files.ContainsKey(InstalledExeName)
                || (OperatingSystem.IsWindows() && !manifest.Files.ContainsKey("VCRUNTIME140.DLL")))
                return null;
            foreach (var (name, info) in manifest.Files)
            {
                var path = Path.Combine(dir, name);
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length != info.Size) return null;
                if (!string.Equals(Sha256Hex(path), info.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
            }
            return new TerracottaModule(manifest.Version, manifest.Architecture, dir, Path.Combine(dir, InstalledExeName));
        }
        catch (Exception ex)
        {
            MultiplayerLog.Log($"模块校验失败 {dir}: {ex.Message}");
            return null;
        }
    }

    private static int CompareVersions(string a, string b)
    {
        var ap = ParseVersion(a.Split('-')[0]);
        var bp = ParseVersion(b.Split('-')[0]);
        for (var i = 0; i < Math.Max(ap.Length, bp.Length); i++)
        {
            var x = i < ap.Length ? ap[i] : 0;
            var y = i < bp.Length ? bp[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static int[] ParseVersion(string s)
    {
        var parts = s.Split('.');
        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            result[i] = int.TryParse(parts[i], out var n) ? n : 0;
        return result;
    }

    private static async Task<string> Sha256HexAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexStringLower(hash);
    }

    private static string Sha256Hex(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    // ---------- manifest 模型 ----------

    private sealed class ModuleManifest
    {
        public string Version { get; set; } = "";
        public string Architecture { get; set; } = "";
        public string ArchiveSha256 { get; set; } = "";
        public bool PublisherDigestVerified { get; set; }
        public Dictionary<string, ModuleFileInfo> Files { get; set; } = new();
    }

    private sealed record ModuleFileInfo(long Size, string Sha256);
}

/// <summary>极简 tar 读取（只认 POSIX 头：name/size/typeflag，512 对齐；仅需 RegularFile 语义）</summary>
internal sealed class TarReader : IDisposable
{
    private readonly Stream _stream;
    private readonly byte[] _header = new byte[512];
    private readonly byte[] _buffer = new byte[81920];
    private TarEntry? _current;

    public TarReader(Stream stream) => _stream = stream;

    public async Task<TarEntry?> NextAsync(CancellationToken ct)
    {
        _current = null;
        var n = await ReadExactlyAsync(_stream, _header, 512, ct);
        if (n < 512) return null;
        if (_header.All(b => b == 0)) return null; // 结尾零块
        var name = ReadString(_header, 0, 100);
        var size = ParseOctal(_header, 124, 12);
        var type = _header[156] is 0 or (byte)'0' ? TarEntryKind.V7RegularFile : (char)_header[156] switch
        {
            '0' => TarEntryKind.RegularFile,
            _ => TarEntryKind.Other,
        };
        var entry = new TarEntry(name, size, type);
        _current = entry;
        return entry;
    }

    /// <summary>把当前项内容复制到目标流（读 size 字节 + 跳过 padding）</summary>
    public async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken ct)
    {
        if (_current is null) throw new InvalidOperationException("未读取 tar 项");
        long remaining = _current.Size;
        var padded = (_current.Size + 511) & ~511L;
        long padding = padded - remaining;
        while (remaining > 0)
        {
            var n = await _stream.ReadAsync(_buffer.AsMemory(0, (int)Math.Min(bufferSize, remaining)), ct);
            if (n == 0) throw new EndOfStreamException("tar 截断");
            await destination.WriteAsync(_buffer.AsMemory(0, n), ct);
            remaining -= n;
        }
        while (padding > 0)
        {
            var n = await _stream.ReadAsync(_buffer.AsMemory(0, (int)Math.Min(padding, _buffer.Length)), ct);
            if (n == 0) throw new EndOfStreamException("tar 截断");
            padding -= n;
        }
        _current = null;
    }

    public void Dispose() => _stream.Dispose();

    private static async Task<int> ReadExactlyAsync(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(total, count - total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static string ReadString(byte[] b, int offset, int length)
    {
        var end = offset + length;
        while (end > offset && b[end - 1] == 0) end--;
        return System.Text.Encoding.UTF8.GetString(b, offset, end - offset);
    }

    private static long ParseOctal(byte[] b, int offset, int length)
    {
        long value = 0;
        for (var i = offset; i < offset + length; i++)
        {
            var c = b[i];
            if (c is 0 or (byte)' ') break;
            if (c is (byte)'0' or (byte)'1' or (byte)'2' or (byte)'3'
                or (byte)'4' or (byte)'5' or (byte)'6' or (byte)'7')
                value = value * 8 + (c - '0');
        }
        return value;
    }
}

internal enum TarEntryKind
{
    RegularFile,
    V7RegularFile,
    Other,
}

internal sealed class TarEntry(string name, long size, TarEntryKind kind)
{
    public string Name { get; } = name;
    public long Size { get; } = size;
    public TarEntryKind Kind { get; } = kind;
}
