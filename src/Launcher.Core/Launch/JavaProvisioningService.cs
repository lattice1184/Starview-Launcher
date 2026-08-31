using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Launcher.Core.Download;

namespace Launcher.Core.Launch;

/// <summary>
/// 8-31 Java 自动补齐：启动时本机无匹配 Java（尤其 macOS Apple Silicon 出厂不带，也蹭不到官方 runtime 缓存）→
/// 自动下载 Mojang 官方 java-runtime（按架构选 mac-os-arm64/mac-os/windows-x64/linux），
/// 扁平化落到 JavaSelector 扫描的 mcRoot/runtime/{组件}/bin/java，一次补齐后续即用。
/// 缺 Java 的根因此前是「探测失败 → 无补齐」整环不存在（Windows 用户蹭 %AppData%\.minecraft\runtime 缓存才能用）。
/// </summary>
public static class JavaProvisioningService
{
    /// <summary>Mojang java-runtime 产品清单 hash（2026-08-31 实测 200；Mojang 更新后此 hash 404 需跟进）</summary>
    private const string RuntimeProductHash = "2ec0cc96c44e5a76b9c8b7c39df7210883d12871";

    private const string ProductUrl =
        $"https://piston-meta.mojang.com/v1/products/java-runtime/{RuntimeProductHash}/all.json";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>清单里的一个文件：目标相对路径（已扁平化）、下载信息、可执行标记</summary>
    internal sealed record RuntimeFile(string Path, string Url, string? Sha1, long Size, bool Executable);

    /// <summary>测试开关：置 true 时缺 Java 直接抛错，不走真下载（避免单测把 100MB 运行时拉进真机 AppData）</summary>
    internal static bool DisableForTests;

    /// <summary>
    /// 确保存在满足 requiredMajor 的 Java；缺失则自动下载官方运行时，返回 java 可执行文件路径。
    /// 失败抛清晰异常（提示手动指定路径 / 装 JDK）。
    /// </summary>
    public static async Task<string> EnsureJavaAsync(int requiredMajor, Action<string>? onStage, CancellationToken ct)
    {
        // 0. 先再探测：已补齐/新装/设置过路径 → 零下载直接返回
        var existing = JavaSelector.Pick(requiredMajor);
        if (existing is not null && existing != "java" && File.Exists(existing)) return existing;
        if (DisableForTests)
            throw new InvalidOperationException(
                $"需要 Java {requiredMajor}，但本机未找到匹配版本（自动补齐在测试环境禁用）");

        // 1. 选组件（epsilon=25 / delta=21 / beta=17 / alpha=16）
        var component = PickComponent(requiredMajor);
        var archKey = OsManifestKey();

        onStage?.Invoke(
            $"未检测到 Java {requiredMajor}，正在自动下载官方运行时（{archKey} / {component.Name}，约 100MB，仅首次）");

        // 2. 产品清单 → 该 OS 该组件的文件清单
        var files = await FetchFileListAsync(archKey, component.Name, ct);

        // 3. 落到 mcRoot/runtime/{组件}/（扁平化 jre.bundle/Contents/Home 前缀 → bin/java 被 JavaSelector 扫到）
        var destRoot = Path.Combine(JavaSelector.MinecraftRoot(), "runtime", component.Name);
        Directory.CreateDirectory(destRoot);
        await DownloadFilesAsync(files, destRoot, onStage, ct);

        // 4. 校验并返回
        var javaPath = Path.Combine(destRoot, "bin", JavaSelector.JavaExe);
        if (!File.Exists(javaPath))
            throw new InvalidOperationException($"自动下载 Java 完成但未找到可执行文件：{javaPath}");
        onStage?.Invoke("Java 运行时就绪");
        return javaPath;
    }

    /// <summary>选组件：major ≥ required 最近者；都不满足取最高（epsilon）</summary>
    internal static (string Name, int Major) PickComponent(int requiredMajor)
    {
        var runtimes = JavaSelector.Runtimes;
        var best = runtimes.Where(r => r.Major >= requiredMajor).OrderBy(r => r.Major).FirstOrDefault();
        if (best.Name is not null) return best;
        return runtimes.OrderByDescending(r => r.Major).First();
    }

    /// <summary>Mojang 产品清单的 OS 键（mac-os-arm64 / mac-os / windows-x64 / linux），按运行架构</summary>
    internal static string OsManifestKey() => OperatingSystem.IsMacOS()
        ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "mac-os-arm64" : "mac-os")
        : OperatingSystem.IsWindows() ? "windows-x64" : "linux";

    /// <summary>扁平化清单路径：去掉 mac 的 jre.bundle/Contents/Home/ 前缀（win/linux 原样），分隔符归一</summary>
    internal static string FlattenPath(string path)
    {
        const string bundlePrefix = "jre.bundle/Contents/Home/";
        var rel = path.StartsWith(bundlePrefix, StringComparison.Ordinal) ? path[bundlePrefix.Length..] : path;
        return rel.Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>拉取该 OS 该组件的文件清单（all.json → 组件 manifest.url → 文件列表）</summary>
    internal static async Task<List<RuntimeFile>> FetchFileListAsync(string osKey, string component, CancellationToken ct)
    {
        using var productResp = await _http.GetAsync(ProductUrl, ct);
        productResp.EnsureSuccessStatusCode();
        var manifestUrl = ExtractManifestUrl(await productResp.Content.ReadAsStringAsync(ct), osKey, component);

        using var fileResp = await _http.GetAsync(manifestUrl, ct);
        fileResp.EnsureSuccessStatusCode();
        return ParseFileList(await fileResp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>产品清单（all.json 文本）→ 该 OS 该组件的 manifest URL（可单测）</summary>
    internal static string ExtractManifestUrl(string productJsonText, string osKey, string component)
    {
        using var json = JsonDocument.Parse(productJsonText);
        return json.RootElement
            .GetProperty(osKey).GetProperty(component)[0].GetProperty("manifest").GetProperty("url").GetString()
            ?? throw new InvalidDataException($"java-runtime 产品清单缺少 {osKey}/{component}");
    }

    /// <summary>组件文件清单 → 待下载文件列表（目录跳过；路径扁平化；raw 下载信息；可执行标记）</summary>
    internal static List<RuntimeFile> ParseFileList(string filesJsonText)
    {
        using var json = JsonDocument.Parse(filesJsonText);
        var files = new List<RuntimeFile>();
        foreach (var kv in json.RootElement.GetProperty("files").EnumerateObject())
        {
            var e = kv.Value;
            if (e.GetProperty("type").GetString() != "file") continue; // 目录跳过
            var raw = e.GetProperty("downloads").GetProperty("raw");
            files.Add(new RuntimeFile(
                Path: FlattenPath(kv.Name),
                Url: raw.GetProperty("url").GetString() ?? throw new InvalidDataException($"清单缺 url: {kv.Name}"),
                Sha1: raw.TryGetProperty("sha1", out var s) ? s.GetString() : null,
                Size: raw.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
                Executable: e.TryGetProperty("executable", out var ex) && ex.GetBoolean()));
        }
        return files;
    }

    /// <summary>并行下载（8 并发，分批推进实时报进度）；单文件失败记入列表，全部结束统一抛错</summary>
    private static async Task DownloadFilesAsync(List<RuntimeFile> files, string destRoot,
        Action<string>? onStage, CancellationToken ct)
    {
        long total = files.Sum(f => f.Size);
        long done = 0;
        var failures = new List<string>();
        var downloads = new DownloadService();
        foreach (var batch in files.Chunk(8))
        {
            ct.ThrowIfCancellationRequested();
            await Task.WhenAll(batch.Select(f => DownloadOneAsync(f, destRoot, failures, downloads, ct)));
            done += batch.Sum(f => f.Size);
            if (onStage is not null && total > 0)
                onStage($"下载 Java 运行时 {Math.Min(100, 100 * done / total)}%");
        }
        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"Java 运行时下载失败 {failures.Count}/{files.Count} 个文件（首例：{failures[0]}）");
    }

    private static async Task DownloadOneAsync(RuntimeFile f, string destRoot, List<string> failures,
        DownloadService downloads, CancellationToken ct)
    {
        try
        {
            var dest = Path.Combine(destRoot, f.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // 已存在且 sha1 对上 → 跳过（断点续装；二次启动不再重下）
            if (f.Sha1 is not null && File.Exists(dest) && Sha1Matches(dest, f.Sha1)) return;
            await downloads.DownloadFileAsync(f.Url, dest, f.Sha1, f.Size, null, ct);
            if (f.Executable) TrySetExecutable(dest);
        }
        catch (Exception ex)
        {
            lock (failures) failures.Add($"{f.Path}: {ex.Message}");
        }
    }

    private static bool Sha1Matches(string path, string expected)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var sha = SHA1.Create();
            return Convert.ToHexString(sha.ComputeHash(fs)).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Mac/Linux 可执行位（java 需要 +x；Windows 跳过）</summary>
    private static void TrySetExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch { /* 可执行位失败尽力而为 */ }
    }
}
