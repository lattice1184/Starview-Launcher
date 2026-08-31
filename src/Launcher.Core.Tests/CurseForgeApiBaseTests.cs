using System.Net;
using Launcher.Core.Ecosystem;
using Launcher.Core.Model.Modrinth;
using Launcher.Core.Services;
using Launcher.Core.Utils;

namespace Launcher.Core.Tests;

/// <summary>8-16 批次 52：CF API 地址覆盖（设置项 → 请求走代理；显式注入优先）</summary>
public class CurseForgeApiBaseTests
{
    private sealed class UrlCaptureHandler : HttpMessageHandler
    {
        public List<string> Uris { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri?.ToString() ?? "");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[],"pagination":{}}"""),
            });
        }
    }

    private static CurseForgeService CreateService(UrlCaptureHandler handler, string? apiBase = null)
        // 8-31 注入测试 key：依赖真机 settings 的 CF key（未配置 → IsEnabled=false 搜索返回空 → 断言空）——
        // 测试与真机配置解耦，不随 settings.json 状态波动
        => new("test-key", new HttpClient(handler), null, null, apiBase);

    [Fact]
    public async Task SettingOverride_RoutesToProxy()
    {
        var handler = new UrlCaptureHandler();
        var svc = CreateService(handler); // 不注入 apiBase → 走设置
        var original = LauncherSettings.Current.CurseForgeApiBase;
        LauncherSettings.Current.CurseForgeApiBase = "https://cf-api.example.com/v1";
        try
        {
            var page = await svc.SearchAsync(ProjectType.Mod, "jei", null, CurseForgeService.SortIndex.Relevance, 20, 0, CancellationToken.None);
            Assert.NotNull(page);
            var url = handler.Uris[0];
            Assert.StartsWith("https://cf-api.example.com/v1/", url);
            Assert.DoesNotContain("api.curseforge.com", url);
        }
        finally { LauncherSettings.Current.CurseForgeApiBase = original; }
    }

    [Fact]
    public async Task SettingEmpty_UsesOfficial()
    {
        var handler = new UrlCaptureHandler();
        var svc = CreateService(handler);
        var original = LauncherSettings.Current.CurseForgeApiBase;
        LauncherSettings.Current.CurseForgeApiBase = "";
        try
        {
            await svc.SearchAsync(ProjectType.Mod, "jei", null, CurseForgeService.SortIndex.Relevance, 20, 0, CancellationToken.None);
            Assert.StartsWith("https://api.curseforge.com/v1/", handler.Uris[0]);
        }
        finally { LauncherSettings.Current.CurseForgeApiBase = original; }
    }

    [Fact]
    public async Task InjectedApiBase_TakesPriorityOverSetting()
    {
        var handler = new UrlCaptureHandler();
        var svc = CreateService(handler, apiBase: "https://injected.local/v1"); // 显式注入
        var original = LauncherSettings.Current.CurseForgeApiBase;
        LauncherSettings.Current.CurseForgeApiBase = "https://setting.local/v1";
        try
        {
            await svc.SearchAsync(ProjectType.Mod, "jei", null, CurseForgeService.SortIndex.Relevance, 20, 0, CancellationToken.None);
            Assert.StartsWith("https://injected.local/v1/", handler.Uris[0]); // 注入压过设置
        }
        finally { LauncherSettings.Current.CurseForgeApiBase = original; }
    }
}
