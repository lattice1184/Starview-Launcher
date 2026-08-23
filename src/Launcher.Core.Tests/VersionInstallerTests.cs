using System.Net;
using Launcher.Core.Download;
using Launcher.Core.Model.Mojang;
using Launcher.Core.Utils;

namespace Launcher.Core.Tests;

/// <summary>AL29 H6：安装后完整性校验——下载完成必须 == 文件完整，不得「虚假成功」</summary>
public class VersionInstallerTests
{
    private static VersionJson BuildVersion(params LibraryJson[] libs)
        => new("1.21.11", "release", "net.minecraft.client.main.Main",
            null, null, null, null, [.. libs],
            new DownloadsInfo(new DownloadFileInfo("http://test/c/client.jar", null, 5), null, null),
            null, null, null);

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("12345"), // 5 字节，匹配 size=5
            });
    }

    private static (VersionInstaller installer, string gameDir) MakeInstaller()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"vinst-{Guid.NewGuid():N}");
        var http = new HttpClient(new StubHandler());
        // 与 MirrorFallbackTests 同款 stub 链：stub 处理器 + 单候选解析器（任意 host 都按官方源）
        // + 零退避 + 跳过真实网络预检——否则 stub 主机 DNS 失败会走真实镜像网络（13 秒重试才报错）。
        // 注意：必须注入 DownloadService，VersionInstaller 自建的会带真实 HttpClient + 真实网络检查器。
        var resolver = new ResolvingDlSourceMapper(new DefaultDlSourceMapper(), new BmclapiDlSourceMapper());
        var downloads = new DownloadService(http, resolver, new DownloadOptions
        {
            MaxSourceAttempts = 2,
            BackoffProvider = _ => TimeSpan.Zero,
        }, gameDir, (_, _) => Task.FromResult(true));
        return (new VersionInstaller(gameDirectory: gameDir, downloads: downloads), gameDir);
    }

    [Fact]
    public async Task Install_CompleteFiles_Succeeds_AndMarks()
    {
        var (installer, gameDir) = MakeInstaller();
        var lib = new LibraryJson("net.x:ok:1.0", null, null, null,
            new LibraryDownloads(new DownloadFileInfo("http://test/libs/ok.jar", null, 5), null), null, null, null);
        var version = BuildVersion(lib);

        await installer.InstallAsync(version, (DownloadProgressHandler?)null, CancellationToken.None);

        // client jar + 库都落地，不抛（8-14 守卫：临时目录非自建 → 不打标记——自建目录打标记由真机验证）
        Assert.True(File.Exists(Path.Combine(gameDir, "versions", "1.21.11", "1.21.11.jar")));
        Assert.True(File.Exists(Path.Combine(gameDir, "libraries", "net", "x", "ok", "1.0", "ok-1.0.jar")));
        Assert.False(File.Exists(Path.Combine(gameDir, "versions", "1.21.11", ".yanla-installed")),
            "非自建目录（PCL/官方扫描源同语义）不得写安装标记");
    }

    [Fact]
    public async Task Install_SkippedLibrary_ThrowsAfterDownload()
    {
        var (installer, gameDir) = MakeInstaller();
        var ok = new LibraryJson("net.x:ok:1.0", null, null, null,
            new LibraryDownloads(new DownloadFileInfo("http://test/libs/ok.jar", null, 5), null), null, null, null);
        // 无 url 无 downloads 的库：下载器合法跳过（AL10.2 曾发生的 url 形式库问题场景）
        var missing = new LibraryJson("net.x:missing:1.0", null, null, null, null, null, null, null);
        var version = BuildVersion(ok, missing);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => installer.InstallAsync(version, (DownloadProgressHandler?)null, CancellationToken.None));

        Assert.Contains("缺 1 个文件", ex.Message);
        Assert.Contains("missing-1.0.jar", ex.Message);
    }

    [Fact]
    public async Task Install_Failure_CleansClientJar_AndSkipsMark()
    {
        // 事务化安装：下载失败 → 清理本次新建的 client jar + 不打完整安装标记——
        // 半装态消失后「已安装」判定（json+jar）恢复诚实，不再"显示已安装→启动才报缺文件"
        var gameDir = Path.Combine(Path.GetTempPath(), $"vinst-{Guid.NewGuid():N}");
        try
        {
            var resolver = new ResolvingDlSourceMapper(new DefaultDlSourceMapper(), new BmclapiDlSourceMapper());
            var downloads = new DownloadService(new HttpClient(new FailHandler()), resolver, new DownloadOptions
            {
                MaxSourceAttempts = 2,
                BackoffProvider = _ => TimeSpan.Zero,
            }, gameDir, (_, _) => Task.FromResult(true));
            var installer = new VersionInstaller(gameDirectory: gameDir, downloads: downloads);

            await Assert.ThrowsAnyAsync<Exception>(
                () => installer.InstallAsync(BuildVersion(), (DownloadProgressHandler?)null, CancellationToken.None));

            var vdir = Path.Combine(gameDir, "versions", "1.21.11");
            Assert.False(File.Exists(Path.Combine(vdir, "1.21.11.jar")), "失败后 client jar 必须被清理");
            Assert.False(File.Exists(Path.Combine(vdir, ".yanla-installed")), "失败不得带完整安装标记");
        }
        finally
        {
            if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true);
        }
    }

    /// <summary>全 404——下载必然失败（换源也 404）</summary>
    private sealed class FailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    // ---------- AL41 删除完整性：清理预取残留的父版本 ----------

    private static void WriteVersion(string gameDir, string id, string? inheritsFrom,
        bool withJar = false, bool withMarker = false, bool prefetched = false)
    {
        var dir = Path.Combine(gameDir, "versions", id);
        Directory.CreateDirectory(dir);
        var json = $"{{\"id\":\"{id}\",{(inheritsFrom is null ? "" : $"\"inheritsFrom\":\"{inheritsFrom}\",")}\"mainClass\":\"x\"}}";
        File.WriteAllText(Path.Combine(dir, $"{id}.json"), json);
        if (withJar) File.WriteAllBytes(Path.Combine(dir, $"{id}.jar"), [1, 2, 3]);
        if (withMarker) InstallMarker.Mark(gameDir, id);
        if (prefetched) InstallMarker.MarkPrefetched(gameDir, id);
    }

    [Fact]
    public void DeleteLoader_CleansOrphanPrefetchedParent()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"vinst-{Guid.NewGuid():N}");
        try
        {
            WriteVersion(gameDir, "fabric-loader-0.19.3-1.21.10", "1.21.10");
            WriteVersion(gameDir, "1.21.10", null, prefetched: true); // 预取残留（.prefetched 标记）

            VersionInstaller.CleanupOrphanParents(gameDir, "fabric-loader-0.19.3-1.21.10");

            Assert.False(Directory.Exists(Path.Combine(gameDir, "versions", "1.21.10"))); // 残留被清
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    [Fact]
    public void DeleteLoader_KeepsUnmarkedResidue()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"vinst-{Guid.NewGuid():N}");
        try
        {
            WriteVersion(gameDir, "fabric-loader-0.19.3-1.21.10", "1.21.10");
            WriteVersion(gameDir, "1.21.10", null); // 无标记残件（下载中断）——需保留可修

            VersionInstaller.CleanupOrphanParents(gameDir, "fabric-loader-0.19.3-1.21.10");

            Assert.True(Directory.Exists(Path.Combine(gameDir, "versions", "1.21.10"))); // 残件不碰
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    [Fact]
    public void DeleteLoader_KeepsInstalledParent()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"vinst-{Guid.NewGuid():N}");
        try
        {
            WriteVersion(gameDir, "fabric-loader-0.19.3-1.21.10", "1.21.10");
            WriteVersion(gameDir, "1.21.10", null, withJar: true, withMarker: true); // 正式安装

            VersionInstaller.CleanupOrphanParents(gameDir, "fabric-loader-0.19.3-1.21.10");

            Assert.True(Directory.Exists(Path.Combine(gameDir, "versions", "1.21.10"))); // 正式安装保留
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    [Fact]
    public void DeleteLoader_KeepsSharedParent()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"vinst-{Guid.NewGuid():N}");
        try
        {
            WriteVersion(gameDir, "fabric-loader-0.19.3-1.21.10", "1.21.10");
            WriteVersion(gameDir, "fabric-loader-0.19.4-1.21.10", "1.21.10"); // 另一版本共享同一原版
            WriteVersion(gameDir, "1.21.10", null, prefetched: true);

            VersionInstaller.CleanupOrphanParents(gameDir, "fabric-loader-0.19.3-1.21.10");

            Assert.True(Directory.Exists(Path.Combine(gameDir, "versions", "1.21.10"))); // 被引用不删
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    [Fact]
    public async Task GetOrFetchVersionJson_NonOwnDir_NoMarker()
    {
        // 8-14 误标根因守卫：临时目录不是自建目录（PCL/官方等扫描源同语义）——任何标记都不写
        // （旧行为对任意目录打 .prefetched → 修复路径把标记写进 PCL 目录 → 版本页误标「本启动器」）
        var gameDir = Path.Combine(Path.GetTempPath(), $"vinst-{Guid.NewGuid():N}");
        try
        {
            var http = new HttpClient(new JsonHandler());
            var installer = new VersionInstaller(http, new DownloadService(http, gameDirectory: gameDir), gameDir);

            _ = await installer.GetOrFetchVersionJsonAsync("1.21.10", "http://test/mc.json", CancellationToken.None);

            Assert.False(InstallMarker.IsPrefetched(gameDir, "1.21.10"), "非自建目录不得写 .prefetched");
            Assert.False(InstallMarker.IsMarked(gameDir, "1.21.10"), "非自建目录不得写 .yanla-installed");
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    [Fact]
    public async Task GetOrFetchVersionJson_InstalledVersion_NotMarkedPrefetched()
    {
        // 真机 08-09 根因回归：已正式安装（.yanla-installed）的版本被自动修复流程读 json 时误打 .prefetched
        var gameDir = Path.Combine(Path.GetTempPath(), $"vinst-{Guid.NewGuid():N}");
        try
        {
            WriteVersion(gameDir, "26.2", null, withJar: true, withMarker: true);
            var installer = new VersionInstaller(new HttpClient(new JsonHandler()),
                new DownloadService(gameDirectory: gameDir), gameDir);

            _ = await installer.GetOrFetchVersionJsonAsync("26.2", "http://test/mc.json", CancellationToken.None);

            Assert.False(InstallMarker.IsPrefetched(gameDir, "26.2"), "已安装版本不得被打预取标记");
            Assert.True(InstallMarker.IsMarked(gameDir, "26.2"), "正式安装标记保持");
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    [Fact]
    public void DeleteLoader_KeepsDoubleMarkedParent()
    {
        // 兜底口径回归：旧 bug 产生的「已装+.prefetched」双标记父版本——删加载器版本时绝不误删
        var gameDir = Path.Combine(Path.GetTempPath(), $"vinst-{Guid.NewGuid():N}");
        try
        {
            WriteVersion(gameDir, "fabric-loader-0.19.3-1.21.10", "1.21.10");
            WriteVersion(gameDir, "1.21.10", null, withMarker: true, prefetched: true); // 双标记

            VersionInstaller.CleanupOrphanParents(gameDir, "fabric-loader-0.19.3-1.21.10");

            Assert.True(Directory.Exists(Path.Combine(gameDir, "versions", "1.21.10")), "双标记已装版本不得被清理");
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    // ---------- 8-23 启动清理守卫（CleanupOrphanPrefetches / CleanupParentsAfterDelete）----------

    [Fact]
    public void CleanupOrphanPrefetches_KeepsDoubleMarkedDir()
    {
        // 8-23 守卫：双标记（.yanla-installed+.prefetched）且无任何引用的正式安装版本——启动清理绝不删
        var gameDir = Path.Combine(Path.GetTempPath(), $"vinst-{Guid.NewGuid():N}");
        try
        {
            WriteVersion(gameDir, "26.2", null, withMarker: true, prefetched: true); // 双标记，无引用

            VersionInstaller.CleanupOrphanPrefetches(gameDir);

            Assert.True(Directory.Exists(Path.Combine(gameDir, "versions", "26.2")), "双标记正式安装版本不得被启动清理删除");
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    [Fact]
    public void CleanupOrphanPrefetches_DeletesUnreferencedPrefetched()
    {
        // 原语义回归：仅 .prefetched、无引用 → 删（残留清理仍需工作）
        var gameDir = Path.Combine(Path.GetTempPath(), $"vinst-{Guid.NewGuid():N}");
        try
        {
            WriteVersion(gameDir, "1.21.10", null, prefetched: true); // 仅预取，无引用

            VersionInstaller.CleanupOrphanPrefetches(gameDir);

            Assert.False(Directory.Exists(Path.Combine(gameDir, "versions", "1.21.10")), "孤立预取残留应被清理");
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    [Fact]
    public void CleanupOrphanPrefetches_KeepsReferencedPrefetched()
    {
        // 原语义回归：.prefetched 且被另一版本 inheritsFrom 引用 → 保留
        var gameDir = Path.Combine(Path.GetTempPath(), $"vinst-{Guid.NewGuid():N}");
        try
        {
            WriteVersion(gameDir, "fabric-loader-0.19.3-1.21.10", "1.21.10");
            WriteVersion(gameDir, "1.21.10", null, prefetched: true);

            VersionInstaller.CleanupOrphanPrefetches(gameDir);

            Assert.True(Directory.Exists(Path.Combine(gameDir, "versions", "1.21.10")), "仍被引用的预取父版本应保留");
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    [Fact]
    public void CleanupParentsAfterDelete_KeepsDoubleMarkedParent()
    {
        // 8-23 守卫对齐 CleanupOrphanPrefetches：删加载器连带清理时，双标记父版本绝不删
        var gameDir = Path.Combine(Path.GetTempPath(), $"vinst-{Guid.NewGuid():N}");
        try
        {
            WriteVersion(gameDir, "1.21.10", null, withMarker: true, prefetched: true); // 双标记父版本

            VersionInstaller.CleanupParentsAfterDelete(gameDir, ["1.21.10"]);

            Assert.True(Directory.Exists(Path.Combine(gameDir, "versions", "1.21.10")), "双标记父版本不得被连带清理删除");
        }
        finally { if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true); }
    }

    /// <summary>返回合法版本 json（GetOrFetch 解析用）</summary>
    private sealed class JsonHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"1.21.10","mainClass":"x"}"""),
            });
    }
}
