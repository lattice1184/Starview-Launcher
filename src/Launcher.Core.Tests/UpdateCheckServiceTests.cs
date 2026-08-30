using System.Net;
using System.Net.Http;
using Launcher.Core.Download;
using Launcher.Core.Services;
using Launcher.Core.Utils;

namespace Launcher.Core.Tests;

/// <summary>后台静默更新检查：就绪状态复用、冷却跳过、force 忽略冷却、版本比较</summary>
public class UpdateCheckServiceTests
{
    private readonly string _stateFile = Path.Combine(Path.GetTempPath(), $"update-state-test-{Guid.NewGuid():N}.json");

    private sealed class StubHandler(Func<HttpResponseMessage> factory) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(factory());
        }
    }

    private static void Use(StubHandler handler)
    {
        GitHubReleaseService.ClearCacheForTest();
        GitHubApiDirect.TokenOverride = null;
        GitHubReleaseService.Http = new HttpClient(handler);
    }

    private async Task<string?> RunAsync(string currentVersion, bool force, CancellationToken ct = default)
    {
        UpdateCheckService.StateFileOverrideForTest = _stateFile;
        try
        {
            var r = await UpdateCheckService.CheckAsync(currentVersion, force, ct);
            return r.HasUpdate ? "update" : r.WasSkipped ? "skipped" : r.Error is null ? "uptodate" : "failed";
        }
        finally
        {
            UpdateCheckService.StateFileOverrideForTest = null;
        }
    }

    private void WriteState(string? readyTag, string? readyPath, DateTime? lastChecked)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        File.WriteAllText(_stateFile,
            $$"""{"readyTag":{{Js(readyTag)}},"readyPath":{{Js(readyPath)}},"lastCheckedUtc":{{Js(lastChecked?.ToString("o"))}}}""");
    }

    private static string Js(string? s)
        => s is null ? "null" : $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    [Fact]
    public async Task ReadyState_ReusesDownloadedPackage_NoApiCall()
    {
        var readyFile = Path.Combine(Path.GetTempPath(), $"starview-ready-{Guid.NewGuid():N}.exe");
        File.WriteAllText(readyFile, "x");
        try
        {
            WriteState("v1.1.4", readyFile, DateTime.UtcNow.AddHours(-1));
            var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
            Use(handler);

            var r = await RunAsync("1.1.3", false);

            Assert.Equal("update", r);
            Assert.Equal(0, handler.Calls); // 已就绪 → 不打 API
        }
        finally { File.Delete(readyFile); }
    }

    [Fact]
    public async Task ReadyFileMissing_DoesNotReuse()
    {
        WriteState("v1.1.4", Path.Combine(Path.GetTempPath(), "starview-does-not-exist.exe"), null);
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"tag_name":"v1.1.4","assets":[]}""", System.Text.Encoding.UTF8, "application/json"),
        });
        Use(handler);

        var r = await RunAsync("1.1.3", false);

        // ready 文件不存在 → 走正常检查；发现 v1.1.4 > 1.1.3 但资产为空 → 无本平台包 → Failed
        Assert.Equal("failed", r);
        Assert.True(handler.Calls >= 1);
    }

    [Fact]
    public async Task AutoCooldown_SkipsWithinSixHours()
    {
        WriteState(null, null, DateTime.UtcNow);
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        Use(handler);

        var r = await RunAsync("1.1.3", false);

        Assert.Equal("skipped", r);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Force_IgnoresCooldown()
    {
        WriteState(null, null, DateTime.UtcNow);
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"tag_name":"v1.1.4","assets":[]}""", System.Text.Encoding.UTF8, "application/json"),
        });
        Use(handler);

        var r = await RunAsync("1.1.4", true); // 手动检查：忽略冷却，当前版本=最新 tag → up to date

        Assert.Equal("uptodate", r);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task UpToDate_NoUpdate()
    {
        WriteState(null, null, DateTime.UtcNow.AddHours(-7)); // 冷却外
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"tag_name":"v1.1.4","assets":[]}""", System.Text.Encoding.UTF8, "application/json"),
        });
        Use(handler);

        var r = await RunAsync("1.1.4", false);

        Assert.Equal("uptodate", r);
        Assert.Equal(1, handler.Calls);
    }
}
