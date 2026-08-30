using Launcher.Core.Launch;
using Launcher.Core.Launch.Sandbox;

namespace Launcher.Core.Tests;

/// <summary>沙盒参数构造（离线：不真跑 bwrap/sandbox-exec/netsh）——
/// 三平台分派是运行时判断，Windows 上测不到 Linux/macOS 分支，故直接断言 internal 参数构造方法。</summary>
public class SandboxRunnerTests
{
    private static JavaArgumentsBuilder.LaunchProfile Profile() => new(
        JavaPath: "/usr/lib/jvm/java-21/bin/java",
        JvmArgs: new[] { "-Xmx2G", "-cp", "/home/user/.minecraft/libraries/a/b.jar" },
        GameArgs: new[] { "--server", "localhost" },
        WorkingDirectory: "/home/user/.minecraft",
        ClassPath: "",
        MainClass: "net.minecraft.client.main.Main",
        Log4jConfigPath: "",
        NativesDirectory: "/home/user/.minecraft/versions/1.21/natives",
        NativeJars: []);

    [Fact]
    public void Bwrap_StrictIsolation_UnsharesNetAndSamePathBindsGameDir()
    {
        var args = SandboxRunner.BuildBwrapArgs(Profile(), SandboxMode.StrictIsolation);

        // 断网开关
        Assert.Contains("--unshare-net", args);
        Assert.DoesNotContain("--share-net", args);
        // 同路径 bind（非 /game 改名）：游戏目录 + 绝对路径 classpath 容器内外一致
        Assert.Contains("--bind", args);
        var bindIdx = args.IndexOf("--bind");
        Assert.Equal("/home/user/.minecraft", args[bindIdx + 1]);
        Assert.Equal("/home/user/.minecraft", args[bindIdx + 2]);
        // 补齐 /dev /proc /tmp（朋友方案缺了会导致 JVM 必崩）
        Assert.Contains("--dev", args);
        Assert.Contains("--proc", args);
        Assert.Contains("--tmpfs", args);
        // java + 原参数在 bwrap 参数末尾（java 后：JvmArgs 3 + MainClass 1 + GameArgs 2 = 6 项 → java 是倒数第 7）
        Assert.Equal("/usr/lib/jvm/java-21/bin/java", args[^7]);
        Assert.Contains("-Xmx2G", args);
        Assert.Equal("net.minecraft.client.main.Main", args[^3]);
    }

    [Fact]
    public void Bwrap_Protected_SharesNet()
    {
        var args = SandboxRunner.BuildBwrapArgs(Profile(), SandboxMode.Protected);
        Assert.Contains("--share-net", args);
        Assert.DoesNotContain("--unshare-net", args);
    }

    [Fact]
    public void SandboxExec_Strict_BlocksAllNetwork()
    {
        var args = SandboxRunner.BuildSandboxExecArgs(Profile(), SandboxMode.StrictIsolation);
        var profile = args[args.IndexOf("-p") + 1];

        Assert.Contains("(deny network*)", profile);
        Assert.DoesNotContain("(allow network-outbound)", profile);
        // 只允许写游戏目录 + 系统临时目录（JVM 必需，权限管控合理性的核心）
        Assert.Contains("(deny file-write*)", profile);
        Assert.Contains("(allow file-write* (subpath \"/home/user/.minecraft\"))", profile);
        Assert.Contains("(allow file-write* (subpath \"/tmp\"))", profile);
    }

    [Fact]
    public void SandboxExec_Protected_AllowsOutboundOnly()
    {
        var args = SandboxRunner.BuildSandboxExecArgs(Profile(), SandboxMode.Protected);
        var profile = args[args.IndexOf("-p") + 1];

        Assert.Contains("(deny network*)", profile);
        Assert.Contains("(allow network-outbound)", profile);
    }

    [Fact]
    public void Manager_Disabled_AlwaysSupported()
    {
        Assert.True(SandboxManager.IsSandboxSupported(SandboxMode.Disabled, out _));
    }

    [Fact]
    public void Runner_Disabled_ReturnsNull()
    {
        var cmd = new SandboxRunner().Wrap(Profile(), SandboxMode.Disabled, out var degrade);
        Assert.Null(cmd);
        Assert.Null(degrade);
    }
}
