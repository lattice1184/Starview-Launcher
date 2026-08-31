using System.Text.Json;
using Launcher.Core.Launch;
using Launcher.Core.Model.Mojang;

namespace Launcher.Core.Tests;

/// <summary>AL29 H5：启动前完整性校验——缺失库在启动前报错（FileNotFoundException），不再 JVM 启动后崩溃</summary>
public class GameLaunchServiceTests
{
    private static VersionJson BuildVersion(int javaMajor = 99)
    {
        var lib = new LibraryJson("net.x:missing:1.0", null, null, null, null, null, null, null);
        return new VersionJson("1.21.11", "release", "net.minecraft.client.main.Main",
            null, null, null, null, [lib], null, new JavaVersionInfo(javaMajor, null), null, null);
    }

    private static string SetupGameDir(VersionJson version, bool createLibrary)
    {
        var gameDir = Path.Combine(Path.GetTempPath(), $"glaunch-{Guid.NewGuid():N}");
        var vdir = Path.Combine(gameDir, "versions", version.Id);
        Directory.CreateDirectory(vdir);
        File.WriteAllText(Path.Combine(vdir, $"{version.Id}.json"),
            JsonSerializer.Serialize(version, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(vdir, $"{version.Id}.jar"), "dummy-jar");
        if (createLibrary)
        {
            var libPath = Path.Combine(gameDir, "libraries", "net", "x", "missing", "1.0", "missing-1.0.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(libPath)!);
            File.WriteAllText(libPath, "dummy-lib");
        }
        return gameDir;
    }

    [Fact]
    public async Task Launch_MissingLibrary_ThrowsFileNotFoundException_BeforeJava()
    {
        var version = BuildVersion();
        var gameDir = SetupGameDir(version, createLibrary: false);

        var svc = new GameLaunchService();
        // javaMajor=99 不会命中本机 Java——若校验门失效，错误将是「需要 Java 99」而非 FileNotFoundException
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            svc.LaunchAsync(version.Id, gameDir, "u", "uuid", "token", 2048,
                null, javaPathOverride: null, onLog: _ => { }));

        Assert.Contains("文件不完整", ex.Message);
        Assert.Contains("missing-1.0.jar", ex.Message); // 首例缺失文件点名
    }

    [Fact]
    public async Task Launch_CompleteFiles_PassesVerification()
    {
        var version = BuildVersion();
        var gameDir = SetupGameDir(version, createLibrary: true);

        var svc = new GameLaunchService();
        // 8-31 文件齐 → 校验通过 → 走到 Java 选择（99 必无）→ 自动补齐路径。
        // 测试置 DisableForTests 让补齐直接抛清晰异常（不走真下载 100MB 进 AppData）
        JavaProvisioningService.DisableForTests = true;
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.LaunchAsync(version.Id, gameDir, "u", "uuid", "token", 2048,
                    null, javaPathOverride: null, onLog: _ => { }));

            Assert.Contains("Java 99", ex.Message);
        }
        finally { JavaProvisioningService.DisableForTests = false; }
    }
}
