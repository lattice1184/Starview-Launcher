using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Core.Multiplayer;

namespace Launcher.Core.Tests;

/// <summary>
/// 陶瓦模块下载/校验/安装测试。与 Lobby 测试同 collection（串行）：
/// 安装写真实 ModuleRoot（%AppData%\Launcher\tools\terracotta），且要临时动 KnownDigests——
/// 必须独占，防与其他测试的安装目录互相干扰。
/// </summary>
[Collection("terracotta")]
public class TerracottaProvisioningServiceTests : IDisposable
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        private readonly Dictionary<string, (int Status, byte[] Body)> _routes = [];

        public void Route(string hostPath, int status, byte[] body) => _routes[hostPath] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = $"{request.RequestUri!.Host}{request.RequestUri.AbsolutePath}";
            lock (Requests) Requests.Add(key);
            return Task.FromResult(_routes.TryGetValue(key, out var r)
                ? r.Status == 200
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(r.Body) }
                    : new HttpResponseMessage((HttpStatusCode)r.Status)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private readonly string _root = TerracottaProvisioningService.ModuleRoot;
    private readonly string? _backup; // 测试期间把真实安装目录挪走，避免被真实安装干扰（或污染真实安装）
    private readonly bool _moved;

    public TerracottaProvisioningServiceTests()
    {
        if (Directory.Exists(_root))
        {
            _backup = _root + ".test-bak-" + Guid.NewGuid().ToString("N")[..8];
            Directory.Move(_root, _backup);
            _moved = true;
        }
    }

    public void Dispose()
    {
        if (_moved)
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true); // 清掉测试安装的
            if (_backup is not null && Directory.Exists(_backup)) Directory.Move(_backup, _root);
        }
        else if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, true); } catch { }
        }
    }

    // ---------- 纯函数 ----------

    [Fact]
    public void AssetTemplates_MatchLockedVersion()
    {
        Assert.Equal("terracotta-0.4.2-windows-x86_64-pkg.tar.gz",
            TerracottaProvisioningService.AssetName("0.4.2", "x86_64"));
        Assert.Equal("terracotta-0.4.2-windows-arm64.exe",
            TerracottaProvisioningService.ExeFileName("0.4.2", "arm64"));
        Assert.Equal("https://gitee.com/burningtnt/Terracotta/releases/download/v0.4.2/terracotta-0.4.2-windows-arm64-pkg.tar.gz",
            TerracottaProvisioningService.GiteeAssetUrl("0.4.2", "arm64"));
        Assert.Equal("https://github.com/burningtnt/Terracotta/releases/download/v0.4.2/terracotta-0.4.2-windows-x86_64-pkg.tar.gz",
            TerracottaProvisioningService.GitHubAssetUrl("0.4.2", "x86_64"));
        Assert.Equal("x86_64", TerracottaProvisioningService.Arch); // 测试机器 x64
        Assert.Contains(TerracottaProvisioningService.KnownDigests.Keys, k => k.StartsWith("0.4.2/")); // 锁版本 digest 存在
    }

    [Fact]
    public void TryGetAvailable_NoModuleRoot_ReturnsNull()
    {
        // fixture 已把 ModuleRoot 挪走 → 目录不存在
        Assert.Null(new TerracottaProvisioningService().TryGetAvailable());
    }

    [Fact]
    public void TryGetAvailable_ValidInstall_ReturnsModule()
    {
        InstallFakeModule("9.9.9", out var moduleDir);
        var module = new TerracottaProvisioningService().TryGetAvailable();
        Assert.NotNull(module);
        Assert.Equal("9.9.9", module!.Version);
        Assert.Equal(TerracottaProvisioningService.Arch, module.Architecture);
        Assert.Equal(Path.Combine(moduleDir, "terracotta.exe"), module.ExePath);
    }

    [Fact]
    public void TryGetAvailable_TamperedFile_ReturnsNull()
    {
        InstallFakeModule("9.9.8", out var moduleDir);
        File.WriteAllText(Path.Combine(moduleDir, "terracotta.exe"), "tampered"); // 改内容 → SHA 不匹配
        Assert.Null(new TerracottaProvisioningService().TryGetAvailable());
    }

    // ---------- 下载安装 ----------

    [Fact]
    public async Task EnsureAvailableAsync_BothSourcesFail_ThrowsUnavailable()
    {
        var handler = new StubHandler();
        handler.Route("gitee.com/burningtnt/Terracotta/releases/download/v0.4.2/terracotta-0.4.2-windows-x86_64-pkg.tar.gz", 500, []);
        handler.Route("github.com/burningtnt/Terracotta/releases/download/v0.4.2/terracotta-0.4.2-windows-x86_64-pkg.tar.gz", 500, []);
        var svc = new TerracottaProvisioningService(handler);

        var ex = await Assert.ThrowsAsync<MultiplayerLobbyException>(
            () => svc.EnsureAvailableAsync());

        Assert.Equal(MultiplayerLobbyFailure.BackendUnavailable, ex.Failure);
        Assert.Contains("Gitee", ex.Message);
        Assert.Contains("GitHub", ex.Message);
        Assert.Null(svc.TryGetAvailable());
    }

    [Fact]
    public async Task EnsureAvailableAsync_ShaMismatch_RejectsInstall()
    {
        var handler = new StubHandler();
        // 合法 tar.gz 结构但内容是伪造的 → SHA256 与 KnownDigests 不符 → 拒绝（SHA 失败会换源，两个源都给假包）
        var fake = BuildTarGz("terracotta-0.4.2-windows-x86_64.exe", "fake-exe"u8.ToArray(), "fake-vcr"u8.ToArray());
        handler.Route("gitee.com/burningtnt/Terracotta/releases/download/v0.4.2/terracotta-0.4.2-windows-x86_64-pkg.tar.gz", 200, fake);
        handler.Route("github.com/burningtnt/Terracotta/releases/download/v0.4.2/terracotta-0.4.2-windows-x86_64-pkg.tar.gz", 200, fake);
        var svc = new TerracottaProvisioningService(handler);

        var ex = await Assert.ThrowsAsync<MultiplayerLobbyException>(
            () => svc.EnsureAvailableAsync());

        Assert.Equal(MultiplayerLobbyFailure.BackendUnavailable, ex.Failure);
        Assert.Contains("SHA256", ex.Message);
        Assert.Contains(handler.Requests, r => r.Contains("github.com")); // 确实换了 GitHub 兜底
        Assert.Null(svc.TryGetAvailable());
    }

    [Fact]
    public async Task EnsureAvailableAsync_CorruptArchive_Rejects()
    {
        var handler = new StubHandler();
        handler.Route("gitee.com/burningtnt/Terracotta/releases/download/v0.4.2/terracotta-0.4.2-windows-x86_64-pkg.tar.gz", 200,
            "not-a-gzip"u8.ToArray());
        var svc = new TerracottaProvisioningService(handler);

        var ex = await Assert.ThrowsAsync<MultiplayerLobbyException>(
            () => svc.EnsureAvailableAsync());

        Assert.Equal(MultiplayerLobbyFailure.BackendUnavailable, ex.Failure);
        Assert.Null(svc.TryGetAvailable());
    }

    [Fact]
    public async Task EnsureAvailableAsync_GiteeFails_GitHubSucceeds()
    {
        var handler = new StubHandler();
        handler.Route("gitee.com/burningtnt/Terracotta/releases/download/v0.4.2/terracotta-0.4.2-windows-x86_64-pkg.tar.gz", 500, []);
        var pkg = BuildTarGz("terracotta-0.4.2-windows-x86_64.exe", "exe"u8.ToArray(), "vcr"u8.ToArray());
        handler.Route("github.com/burningtnt/Terracotta/releases/download/v0.4.2/terracotta-0.4.2-windows-x86_64-pkg.tar.gz",
            200, pkg);
        var svc = new TerracottaProvisioningService(handler);
        using var _ = SwapDigest(pkg);

        var module = await svc.EnsureAvailableAsync();

        Assert.NotNull(module);
        Assert.Equal("0.4.2", module.Version);
        Assert.Contains(handler.Requests, r => r.Contains("github.com")); // Gitee 失败后确实换了 GitHub
        Assert.Equal("exe", await File.ReadAllTextAsync(Path.Combine(module.Directory, "terracotta.exe")));
    }

    [Fact]
    public async Task EnsureAvailableAsync_Installed_SecondCallNoDownload()
    {
        var handler = new StubHandler();
        var pkg = BuildTarGz("terracotta-0.4.2-windows-x86_64.exe", "exe"u8.ToArray(), "vcr"u8.ToArray());
        handler.Route("gitee.com/burningtnt/Terracotta/releases/download/v0.4.2/terracotta-0.4.2-windows-x86_64-pkg.tar.gz", 200, pkg);
        var svc = new TerracottaProvisioningService(handler);
        using var _ = SwapDigest(pkg);

        var first = await svc.EnsureAvailableAsync();
        var downloads = handler.Requests.Count(r => r.Contains("download"));
        var second = await svc.EnsureAvailableAsync();

        Assert.Equal(first.Version, second.Version); // 已装直接返回（重新扫描，非同一实例但内容一致）
        Assert.Equal(1, downloads); // 第二次不再下载
    }

    /// <summary>把 KnownDigests 的 0.4.2/{arch}/{os} 换成测试包的 SHA，结束后恢复（IReadOnlyDictionary 的底层就是 Dictionary）</summary>
    private static IDisposable SwapDigest(byte[] package)
    {
        var key = $"0.4.2/{TerracottaProvisioningService.Arch}/{TerracottaProvisioningService.OsKey}";
        var dict = (Dictionary<string, string>)TerracottaProvisioningService.KnownDigests;
        var original = dict[key];
        var sha = Convert.ToHexStringLower(SHA256.HashData(package));
        dict[key] = sha;
        return new DisposeAction(() => dict[key] = original);
    }

    private sealed class DisposeAction(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    /// <summary>在 ModuleRoot 下装一个「假模块」（2 文件 + manifest，SHA 真实计算）</summary>
    private void InstallFakeModule(string version, out string moduleDir)
    {
        var arch = TerracottaProvisioningService.Arch;
        moduleDir = Path.Combine(_root, version, $"terracotta-windows-{arch}");
        Directory.CreateDirectory(moduleDir);
        File.WriteAllBytes(Path.Combine(moduleDir, "terracotta.exe"), "exe"u8.ToArray());
        File.WriteAllBytes(Path.Combine(moduleDir, "VCRUNTIME140.DLL"), "vcr"u8.ToArray());
        var manifest = new
        {
            Version = version,
            Architecture = arch,
            ArchiveSha256 = "00",
            PublisherDigestVerified = true,
            Files = new Dictionary<string, object>
            {
                ["terracotta.exe"] = new { Size = 3, Sha256 = ShaOf(moduleDir, "terracotta.exe") },
                ["VCRUNTIME140.DLL"] = new { Size = 3, Sha256 = ShaOf(moduleDir, "VCRUNTIME140.DLL") },
            },
        };
        File.WriteAllText(Path.Combine(moduleDir, ".terracotta-module.json"),
            JsonSerializer.Serialize(manifest));
    }

    private static string ShaOf(string dir, string name)
        => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, name))));

    // ---------- tar.gz 构造（标准 ustar 头；生产 TarReader 只认 name/size/typeflag，写标准头更保险） ----------

    private static byte[] BuildTarGz(string exeName, byte[] exeBytes, byte[] vcrBytes)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            WriteTarEntry(gz, exeName, exeBytes);
            WriteTarEntry(gz, "VCRUNTIME140.DLL", vcrBytes);
            gz.Write(new byte[1024]); // 结尾零块 ×2
        }
        return ms.ToArray();
    }

    private static void WriteTarEntry(Stream s, string name, byte[] content)
    {
        var header = new byte[512];
        var nameBytes = Encoding.UTF8.GetBytes(name);
        Array.Copy(nameBytes, 0, header, 0, Math.Min(nameBytes.Length, 100));
        WriteOctal(header, 100, 7, 0x1ED);    // mode 0644
        WriteOctal(header, 108, 7, 0);        // uid
        WriteOctal(header, 116, 7, 0);        // gid
        WriteOctal(header, 124, 11, content.Length); // size
        WriteOctal(header, 136, 11, 0);       // mtime
        header[156] = (byte)'0';              // typeflag regular
        Array.Copy("ustar\0"u8.ToArray(), 0, header, 257, 6);
        header[263] = (byte)'0';
        header[264] = (byte)'0';
        for (var i = 148; i < 156; i++) header[i] = (byte)' '; // checksum 先填空格
        var checksum = header.Sum(b => b);
        var text = Convert.ToString(checksum, 8);
        for (var i = 0; i < text.Length; i++) header[148 + i] = (byte)text[i];
        header[148 + text.Length] = 0;
        s.Write(header);
        s.Write(content);
        var pad = (512 - content.Length % 512) % 512;
        if (pad > 0) s.Write(new byte[pad]);
    }

    private static void WriteOctal(byte[] header, int offset, int length, long value)
    {
        var text = Convert.ToString(value, 8);
        var start = offset + length - text.Length - 1; // 右对齐 + 结尾 null
        // 前导 '0' 填充：GNU tar 数字字段 = 前导零的八进制 + NUL 结尾（解析器遇 NUL/空格即停）
        Array.Fill(header, (byte)'0', offset, start - offset);
        for (var i = 0; i < text.Length; i++) header[start + i] = (byte)text[i];
        header[start + text.Length] = 0;
    }

    /// <summary>9-1 修联机 Mac 拒装：macOS 哈希实测回填——pending 会触发「缺少已知 SHA256，拒绝安装」。
    /// 锁死不回退（有人清回 pending 则 Mac 联机再次拒装）。</summary>
    [Fact]
    public void KnownDigests_MacNotPending_SoInstallable()
    {
        Assert.NotEqual("pending", TerracottaProvisioningService.KnownDigests["0.4.2/arm64/macos"]);
        Assert.NotEqual("pending", TerracottaProvisioningService.KnownDigests["0.4.2/x86_64/macos"]);
        Assert.NotEqual("pending", Launcher.Core.Multiplayer.EasyTierProvisioningService.KnownDigests["2.6.4/aarch64/macos"]);
        Assert.NotEqual("pending", Launcher.Core.Multiplayer.EasyTierProvisioningService.KnownDigests["2.6.4/x86_64/macos"]);
    }
}
