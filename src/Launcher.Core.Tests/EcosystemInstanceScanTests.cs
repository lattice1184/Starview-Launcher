using Launcher.App.Services;
using Launcher.App.ViewModels;
using Launcher.Core.Launch;

namespace Launcher.Core.Tests;

/// <summary>
/// 8-27 回归：生态页（MOD 下载页）实例下拉漏传 McVersion → fabric 实例 ResolvedGameVersion 空
/// → 详情页不按游戏版本过滤 → 自动匹配从全量选最新（「模组显示 1.21」实锤）。
/// 覆盖 VersionScan.Inspect 读 inheritsFrom + BuildInstanceVM 构造链路（与 loader 版本号 0.19.3/0.19.4 无关）。
/// </summary>
public class EcosystemInstanceScanTests
{
    private const string FabricId = "fabric-loader-0.19.4-26.1.2";

    /// <summary>搭临时游戏目录：versions/{id}/{id}.json（fabric 实例：inheritsFrom + mainClass）</summary>
    private static string CreateTempGameDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "starview-test-" + Guid.NewGuid().ToString("N"));
        var vdir = Path.Combine(root, "versions", FabricId);
        Directory.CreateDirectory(vdir);
        File.WriteAllText(Path.Combine(vdir, $"{FabricId}.json"),
            $$"""
            {
              "id": "{{FabricId}}",
              "inheritsFrom": "26.1.2",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "releaseTime": "2026-08-01T00:00:00Z",
              "libraries": []
            }
            """);
        return root;
    }

    [Fact]
    public void VersionScan_Inspect_FabricInstance_ReturnsMcVersionFromInheritsFrom()
    {
        var dir = CreateTempGameDir();
        try
        {
            var (loader, mc) = VersionScan.Inspect(dir, FabricId);
            Assert.Equal("fabric", loader);
            Assert.Equal("26.1.2", mc); // 读 version.json 的 inheritsFrom，不是猜实例名
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>修复后：BuildInstanceVM 把 McVersion 填进 VersionInstanceVM → ResolvedGameVersion 正确</summary>
    [Fact]
    public void BuildInstanceVM_FabricInstance_ResolvesGameVersion26_1_2()
    {
        var dir = CreateTempGameDir();
        try
        {
            var vm = EcosystemViewModel.BuildInstanceVM(FabricId, dir);
            Assert.Equal("fabric", vm.LoaderBadge);
            Assert.Equal("26.1.2", vm.McVersion);
            Assert.Equal("26.1.2", vm.ResolvedGameVersion); // 详情页按它过滤 → 自动匹配到 26.1.2 构建而非 1.21
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>对照（bug 语义）：旧构造只给 loader 不给 McVersion → fabric 实例 ResolvedGameVersion 空串
    /// → 详情页 effGameVersion=null → 不按版本过滤 → SelectBestVersion 全量选最新（选到 1.21）。</summary>
    [Fact]
    public void LegacyConstruction_WithoutMcVersion_ResolvedGameVersionEmpty()
    {
        var dir = CreateTempGameDir();
        try
        {
            var vm = new VersionInstanceVM(FabricId, "", dir, LoaderDetector.Detect(dir, FabricId) ?? "");
            Assert.Equal("", vm.ResolvedGameVersion);
        }
        finally { Directory.Delete(dir, true); }
    }
}
