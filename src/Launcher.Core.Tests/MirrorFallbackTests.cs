using System.Net;
using System.Net.Http;
using Launcher.Core.Download;
using Launcher.Core.Utils;

namespace Launcher.Core.Tests;

/// <summary>镜像回退：官方失败→镜像成功 / 官方坏字节→镜像好字节 / 双失败按次数 / 不可映射 URL 单候选</summary>
public class MirrorFallbackTests
{
    /// <summary>按 host+path 返回状态/内容；跟踪请求序列（并发竞速下多个源并行打请求——List.Add 非线程安全，加锁防丢条目）</summary>
    private sealed class HostStubHandler : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        private readonly object _lock = new();
        private readonly Dictionary<string, (int Status, byte[] Body)> _routes = [];
        private readonly byte[] _defaultBody = "12345"u8.ToArray();

        public void RouteBytes(string hostPath, int status, byte[] body) => _routes[hostPath] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = $"{request.RequestUri!.Host}{request.RequestUri.AbsolutePath}";
            lock (_lock) Requests.Add($"{request.Method} {key}");
            if (_routes.TryGetValue(key, out var route))
            {
                return Task.FromResult(route.Status == 200
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(route.Body) }
                    : new HttpResponseMessage((HttpStatusCode)route.Status));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_defaultBody) });
        }
    }

    private static DownloadService CreateService(HostStubHandler handler, DownloadOptions? options = null)
    {
        var http = new HttpClient(handler);
        // 官方源：any host；镜像源：bmclapi2.bangbang93.com
        var resolver = new ResolvingDlSourceMapper(new DefaultDlSourceMapper(), new BmclapiDlSourceMapper());
        return new DownloadService(http, resolver, options ?? new DownloadOptions
        {
            MaxSourceAttempts = 2,
            BackoffProvider = _ => TimeSpan.Zero,
        }, Path.GetTempPath(),
        (_, _) => Task.FromResult(true)); // 跳过真实网络预检——全走 stub（测试不依赖外网）
    }

    [Fact]
    public async Task OfficialFails_MirrorSucceeds()
    {
        var handler = new HostStubHandler();
        handler.RouteBytes("resources.download.minecraft.net/ab/abcdef", 500, []);
        handler.RouteBytes("bmclapi2.bangbang93.com/ab/abcdef", 200, "12345"u8.ToArray());
        var svc = CreateService(handler);
        var dest = Path.Combine(Path.GetTempPath(), $"mirror-{Guid.NewGuid():N}.jar");
        try
        {
            var url = "https://resources.download.minecraft.net/ab/abcdef";
            await svc.DownloadFileAsync(url, dest, null, 5, null, CancellationToken.None);

            Assert.True(File.Exists(dest));
            Assert.Equal(5, new FileInfo(dest).Length);
            Assert.Contains(handler.Requests, r => r.Contains("bmclapi2.bangbang93.com")); // 镜像被请求
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public async Task PistonDataClientJar_HasMirrorCandidate()
    {
        // 8-26 回归锁定：client.jar 曾漏加 piston-data 镜像 → 版本安装里唯一候选(1) 单直连文件，
        // Mojang 波动时无兜底（19:40 实测 26.7s）。官方失败 → 镜像必须接手。
        var handler = new HostStubHandler();
        handler.RouteBytes("piston-data.mojang.com/v1/objects/abcdef/client.jar", 500, []);
        handler.RouteBytes("bmclapi2.bangbang93.com/v1/objects/abcdef/client.jar", 200, "12345"u8.ToArray());
        var svc = CreateService(handler);
        var dest = Path.Combine(Path.GetTempPath(), $"pistonmirror-{Guid.NewGuid():N}.jar");
        try
        {
            var url = "https://piston-data.mojang.com/v1/objects/abcdef/client.jar";
            await svc.DownloadFileAsync(url, dest, null, 5, null, CancellationToken.None);

            Assert.True(File.Exists(dest));
            Assert.Equal(5, new FileInfo(dest).Length);
            Assert.Contains(handler.Requests, r => r.Contains("bmclapi2.bangbang93.com/v1/objects/abcdef"));
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public async Task OfficialWrongBytes_MirrorCorrectBytes_Wins()
    {
        var handler = new HostStubHandler();
        handler.RouteBytes("libraries.minecraft.net/org/a/1.0/a-1.0.jar", 200, "WRONG!!"u8.ToArray());
        handler.RouteBytes("bmclapi2.bangbang93.com/maven/org/a/1.0/a-1.0.jar", 200, "12345"u8.ToArray());
        var svc = CreateService(handler);
        var dest = Path.Combine(Path.GetTempPath(), $"mirror-{Guid.NewGuid():N}.jar");
        try
        {
            // 官方 URL 无法映射镜像（libraries.minecraft.net 可映射到 /maven）→ 校验失败后换镜像
            var url = "https://libraries.minecraft.net/org/a/1.0/a-1.0.jar";
            await svc.DownloadFileAsync(url, dest, null, 5, null, CancellationToken.None);

            Assert.True(File.Exists(dest));
            Assert.Equal(5, new FileInfo(dest).Length); // 镜像的好字节胜出
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public async Task BothSourcesFail_ThrowsAfterAttempts()
    {
        var handler = new HostStubHandler();
        handler.RouteBytes("resources.download.minecraft.net/ab/abcdef", 500, []);
        handler.RouteBytes("bmclapi2.bangbang93.com/ab/abcdef", 500, []);
        var svc = CreateService(handler); // MaxSourceAttempts=2 → 每轮 2 源 → 4 次请求
        var dest = Path.Combine(Path.GetTempPath(), $"mirror-{Guid.NewGuid():N}.jar");
        try
        {
            var url = "https://resources.download.minecraft.net/ab/abcdef";
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                svc.DownloadFileAsync(url, dest, null, 5, null, CancellationToken.None));

            Assert.True(handler.Requests.Count == 4,
                $"requests({handler.Requests.Count}): {string.Join(" | ", handler.Requests)}"); // 2 轮 × 2 源
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public async Task UnmappableUrl_SingleCandidateNoDuplicates()
    {
        var handler = new HostStubHandler();
        handler.RouteBytes("custom.example.com/x.jar", 200, "12345"u8.ToArray());
        var svc = CreateService(handler);
        var dest = Path.Combine(Path.GetTempPath(), $"mirror-{Guid.NewGuid():N}.jar");
        try
        {
            var url = "https://custom.example.com/x.jar";
            await svc.DownloadFileAsync(url, dest, null, 5, null, CancellationToken.None);

            // 不可映射 → 每轮只有官方一个候选；成功即止 → 只请求一次
            Assert.Single(handler.Requests);
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public async Task ForgeMaven_OfficialSlow_MirrorWins()
    {
        // 8-14 修复：maven.minecraftforge.net 无镜像 → 单候选官方直连 37-81KB/s 判死失败
        // （整合包 Forge 1.20.1-47.4.0 实机 45s 报错）。修复后应映射到 BMCLAPI /maven——
        // 官方假死（500），镜像 200 秒成功。
        var handler = new HostStubHandler();
        handler.RouteBytes("maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.4.0/forge-1.20.1-47.4.0-installer.jar", 500, []);
        handler.RouteBytes("bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/1.20.1-47.4.0/forge-1.20.1-47.4.0-installer.jar", 200, "forge"u8.ToArray());
        var svc = CreateService(handler);
        var dest = Path.Combine(Path.GetTempPath(), $"forge-{Guid.NewGuid():N}.jar");
        try
        {
            var url = "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.4.0/forge-1.20.1-47.4.0-installer.jar";
            await svc.DownloadFileAsync(url, dest, null, 5, null, CancellationToken.None);

            Assert.Equal("forge"u8.ToArray(), File.ReadAllBytes(dest));
            Assert.Contains(handler.Requests, r => r.Contains("bmclapi2.bangbang93.com/maven/net/minecraftforge"));
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public async Task NetworkUnreachable_AfterRetries_ReportsClearly()
    {
        var handler = new HostStubHandler();
        handler.RouteBytes("resources.download.minecraft.net/ab/abcdef", 500, []);
        handler.RouteBytes("bmclapi2.bangbang93.com/ab/abcdef", 500, []);
        var http = new HttpClient(handler);
        var resolver = new ResolvingDlSourceMapper(new DefaultDlSourceMapper(), new BmclapiDlSourceMapper());
        // 注入网络检查：报告不可达
        var svc = new DownloadService(http, resolver, new DownloadOptions
        {
            MaxSourceAttempts = 2,
            BackoffProvider = _ => TimeSpan.Zero,
        }, Path.GetTempPath(), (hosts, ct) => Task.FromResult(false));
        var dest = Path.Combine(Path.GetTempPath(), $"mirror-{Guid.NewGuid():N}.jar");
        try
        {
            var url = "https://resources.download.minecraft.net/ab/abcdef";
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.DownloadFileAsync(url, dest, null, 5, null, CancellationToken.None));

            Assert.Contains("网络不可达", ex.Message);
            Assert.Contains("resources.download.minecraft.net", ex.Message);
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public async Task MirrorOnly_OnlyMirrorCandidate()
    {
        var handler = new HostStubHandler();
        handler.RouteBytes("bmclapi2.bangbang93.com/ab/abcdef", 200, "12345"u8.ToArray());
        var http = new HttpClient(handler);
        var resolver = new ResolvingDlSourceMapper(new DefaultDlSourceMapper(), new BmclapiDlSourceMapper());
        var svc = new DownloadService(http, resolver, new DownloadOptions
        {
            DownloadSource = DownloadSourcePreference.MirrorOnly,
            MaxSourceAttempts = 2,
            BackoffProvider = _ => TimeSpan.Zero,
        }, Path.GetTempPath());
        var dest = Path.Combine(Path.GetTempPath(), $"mirror-{Guid.NewGuid():N}.jar");
        try
        {
            var url = "https://resources.download.minecraft.net/ab/abcdef";
            await svc.DownloadFileAsync(url, dest, null, 5, null, CancellationToken.None);

            // 仅镜像 → 只请求镜像，官方不出现
            Assert.All(handler.Requests, r => Assert.DoesNotContain("resources.download.minecraft.net", r));
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public async Task MirrorFirst_MirrorWins_WhenOfficialFails()
    {
        var handler = new HostStubHandler();
        // 官方 500 失败；镜像 200 好字节——MirrorFirst 下镜像必须胜出（字节可验证）
        handler.RouteBytes("resources.download.minecraft.net/ab/abcdef", 500, []);
        handler.RouteBytes("bmclapi2.bangbang93.com/ab/abcdef", 200, "12345"u8.ToArray());
        var http = new HttpClient(handler);
        var resolver = new ResolvingDlSourceMapper(new DefaultDlSourceMapper(), new BmclapiDlSourceMapper());
        var svc = new DownloadService(http, resolver, new DownloadOptions
        {
            DownloadSource = DownloadSourcePreference.MirrorFirst,
            MaxSourceAttempts = 2,
            BackoffProvider = _ => TimeSpan.Zero,
        }, Path.GetTempPath());
        var dest = Path.Combine(Path.GetTempPath(), $"mirror-{Guid.NewGuid():N}.jar");
        try
        {
            var url = "https://resources.download.minecraft.net/ab/abcdef";
            await svc.DownloadFileAsync(url, dest, null, 5, null, CancellationToken.None);

            // 竞速语义：官方并行发起可能被取消在请求前（请求可不出现）——只断言结果与镜像被请求
            Assert.True(File.Exists(dest));
            Assert.Equal("12345", await File.ReadAllTextAsync(dest));
            Assert.Contains(handler.Requests, r => r.Contains("bmclapi2.bangbang93.com"));
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }
}
