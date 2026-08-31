using Launcher.Core.Launch;

namespace Launcher.Core.Tests;

/// <summary>8-31 Java 自动补齐纯逻辑：组件选择 / mac bundle 扁平化 / 清单解析。
/// 下载委托 DownloadService（已单独测试），可执行位 Windows 上无意义不测。</summary>
public class JavaProvisioningServiceTests
{
    [Theory]
    [InlineData(25, "java-runtime-epsilon", 25)]
    [InlineData(21, "java-runtime-delta", 21)]
    [InlineData(17, "java-runtime-beta", 17)]
    [InlineData(16, "java-runtime-alpha", 16)]
    [InlineData(8, "jre-legacy", 8)] // 8 → 最近的 ≥8 是 jre-legacy（Java 8，旧版 MC）
    public void PickComponent_ChoosesClosestSatisfying(int required, string name, int major)
    {
        var (n, m) = JavaProvisioningService.PickComponent(required);
        Assert.Equal(name, n);
        Assert.Equal(major, m);
    }

    [Fact]
    public void PickComponent_AboveAll_FallsBackHighest()
    {
        var (n, m) = JavaProvisioningService.PickComponent(99); // 无 ≥99 组件 → 取最高 epsilon
        Assert.Equal("java-runtime-epsilon", n);
        Assert.Equal(25, m);
    }

    [Theory]
    [InlineData("jre.bundle/Contents/Home/bin/java")]
    [InlineData("jre.bundle/Contents/Home/bin/java.exe")]
    public void FlattenPath_StripsMacBundle(string raw)
    {
        var expected = Path.Combine("bin", Path.GetFileName(raw));
        Assert.Equal(expected, JavaProvisioningService.FlattenPath(raw));
    }

    [Theory]
    [InlineData("bin/java.exe")]
    [InlineData("lib/server/libjvm.dylib")]
    [InlineData("conf/security/java.security")]
    public void FlattenPath_KeepsNonBundlePath(string raw)
        => Assert.Equal(raw.Replace('/', Path.DirectorySeparatorChar), JavaProvisioningService.FlattenPath(raw));

    [Fact]
    public void ExtractManifestUrl_ParsesProductJson()
    {
        const string json = """
            {
              "mac-os-arm64": {
                "java-runtime-delta": [
                  { "manifest": { "url": "https://piston-meta.mojang.com/v1/packages/78b4178415da0ae9c738e65dd66ac57e4c8a4bcc/manifest.json" } }
                ]
              }
            }
            """;
        var url = JavaProvisioningService.ExtractManifestUrl(json, "mac-os-arm64", "java-runtime-delta");
        Assert.Equal("https://piston-meta.mojang.com/v1/packages/78b4178415da0ae9c738e65dd66ac57e4c8a4bcc/manifest.json", url);
    }

    [Fact]
    public void ParseFileList_SkipsDirs_Flattens_ReadsExecutable()
    {
        const string json = """
            {
              "files": {
                "jre.bundle": { "type": "directory" },
                "jre.bundle/Contents/Home/bin/java": {
                  "type": "file", "executable": true,
                  "downloads": { "raw": { "url": "https://piston-data.mojang.com/v1/objects/abc/java", "sha1": "aabbcc", "size": 12345 } }
                },
                "jre.bundle/Contents/Home/bin/jar": {
                  "type": "file",
                  "downloads": { "raw": { "url": "https://piston-data.mojang.com/v1/objects/def/jar", "size": 99 } }
                }
              }
            }
            """;
        var files = JavaProvisioningService.ParseFileList(json);

        Assert.Equal(2, files.Count); // 目录条目跳过
        var java = files[0];
        Assert.Equal(Path.Combine("bin", "java"), java.Path);
        Assert.Equal("https://piston-data.mojang.com/v1/objects/abc/java", java.Url);
        Assert.Equal("aabbcc", java.Sha1);
        Assert.Equal(12345, java.Size);
        Assert.True(java.Executable);

        var jar = files[1];
        Assert.Equal(Path.Combine("bin", "jar"), jar.Path);
        Assert.Null(jar.Sha1); // 无 sha1 字段 → null（下载不校验）
        Assert.False(jar.Executable);
    }
}
