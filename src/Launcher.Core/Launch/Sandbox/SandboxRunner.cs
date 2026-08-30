using System.Diagnostics;
using System.Text;

namespace Launcher.Core.Launch.Sandbox;

/// <summary>
/// 三平台沙盒实现（C# 直构命令，无内嵌 helper 二进制）：
/// Linux → bwrap；macOS → sandbox-exec；Windows → netsh 防火墙规则（严格断网）。
/// 关键设计：游戏目录**同路径 bind**（非 /game 改名）——JavaArgumentsBuilder 生成的
/// 绝对路径 classpath/natives 参数在容器内继续有效，LaunchProfile 无需改动。
/// </summary>
public sealed class SandboxRunner : ISandboxRunner
{
    public SandboxCommand? Wrap(JavaArgumentsBuilder.LaunchProfile profile, SandboxMode mode, out string? degradeReason)
    {
        degradeReason = null;
        if (mode == SandboxMode.Disabled) return null;

        if (OperatingSystem.IsLinux()) return WrapLinux(profile, mode);
        if (OperatingSystem.IsMacOS()) return WrapMac(profile, mode, out degradeReason);
        if (OperatingSystem.IsWindows()) return WrapWindows(profile, mode, out degradeReason);

        degradeReason = "当前操作系统不支持沙盒，已按普通启动";
        return null;
    }

    /// <summary>拼接 java + 原参数到 bwrap/sandbox-exec 参数末尾</summary>
    private static void AppendJavaArgs(List<string> dst, JavaArgumentsBuilder.LaunchProfile p)
    {
        dst.Add(p.JavaPath);
        dst.AddRange(p.JvmArgs);
        dst.Add(p.MainClass);
        dst.AddRange(p.GameArgs);
    }

    // ---------- Linux：bwrap ----------

    private static SandboxCommand WrapLinux(JavaArgumentsBuilder.LaunchProfile profile, SandboxMode mode)
        => new("/usr/bin/bwrap", BuildBwrapArgs(profile, mode), null);

    /// <summary>bwrap 参数构造（internal 供单测直接断言——三平台分派是运行时判断，Windows 上跑测试走不到 Linux 分支）</summary>
    internal static List<string> BuildBwrapArgs(JavaArgumentsBuilder.LaunchProfile profile, SandboxMode mode)
    {
        var gameDir = profile.WorkingDirectory;
        var args = new List<string>();
        // 系统目录只读 + 运行时必需伪文件系统（朋友方案缺 /dev /proc /tmp，JVM 必崩，这里补齐）
        args.AddRange(new[]
        {
            "--ro-bind", "/usr", "/usr",
            "--ro-bind", "/lib", "/lib",
            "--ro-bind", "/lib64", "/lib64",
            "--ro-bind", "/etc", "/etc",
            "--ro-bind", "/sys", "/sys",
            "--dev", "/dev",
            "--proc", "/proc",
            "--tmpfs", "/tmp",
        });
        // 整个用户主目录只读（防游戏写主目录其他东西），随后游戏目录单独可写（后者覆盖前者）
        args.AddRange(new[] { "--ro-bind", "/home", "/home" });
        // java 可执行若不在系统目录（如 /opt、自建目录），只读 bind 其所在目录
        var javaDir = Path.GetDirectoryName(profile.JavaPath);
        if (javaDir is not null && javaDir.StartsWith('/') &&
            !javaDir.StartsWith("/usr") && !javaDir.StartsWith("/lib"))
            args.AddRange(new[] { "--ro-bind", javaDir, javaDir });
        // 游戏目录同路径可写 bind + chdir（容器内路径与宿主一致，classpath 绝对路径继续有效）
        args.AddRange(new[] { "--bind", gameDir, gameDir, "--chdir", gameDir, "--unshare-all" });
        args.Add(mode == SandboxMode.StrictIsolation ? "--unshare-net" : "--share-net");

        AppendJavaArgs(args, profile);
        return args;
    }

    // ---------- macOS：sandbox-exec（Seatbelt） ----------

    private static SandboxCommand WrapMac(JavaArgumentsBuilder.LaunchProfile profile, SandboxMode mode, out string? degradeReason)
    {
        degradeReason = null;
        // 8-30 诚实标注：sandbox-exec 的 network-outbound 限制在部分 macOS 版本实际不生效，
        // 严格断网为尽力而为（真实按进程断网需 pf/防火墙 root 权限）
        if (mode == SandboxMode.StrictIsolation)
            degradeReason = "macOS 严格断网受 sandbox-exec 能力限制，可能无法完全阻断联网（文件隔离已生效）";
        return new SandboxCommand("/usr/bin/sandbox-exec", BuildSandboxExecArgs(profile, mode), null);
    }

    /// <summary>sandbox-exec 参数构造（internal 供单测直接断言）</summary>
    internal static List<string> BuildSandboxExecArgs(JavaArgumentsBuilder.LaunchProfile profile, SandboxMode mode)
    {
        var sb = new StringBuilder();
        sb.Append("(version 1)\n(allow default)\n");
        // 文件：全读，只允许写 游戏目录 + 系统临时目录（JVM/minecraft 依赖 /tmp /var/folders 写临时文件，
        // 8-30 朋友反馈"权限管控要合理"——不放开这两处 JVM 直接崩，沙盒就不可用）
        sb.Append("(deny file-write*)\n");
        sb.Append("(allow file-write* (subpath \"").Append(profile.WorkingDirectory.Replace("\"", "\\\"")).Append("\"))\n");
        sb.Append("(allow file-write* (subpath \"/tmp\"))\n");
        sb.Append("(allow file-write* (subpath \"/var/folders\"))\n");
        // 网络：严格模式全禁；保护模式允许出站（禁入站）
        sb.Append("(deny network*)\n");
        if (mode == SandboxMode.Protected) sb.Append("(allow network-outbound)\n");

        var args = new List<string> { "-p", sb.ToString() };
        AppendJavaArgs(args, profile);
        return args;
    }

    // ---------- Windows：临时防火墙出站规则（严格断网） ----------

    private static SandboxCommand? WrapWindows(JavaArgumentsBuilder.LaunchProfile profile, SandboxMode mode, out string? degradeReason)
    {
        degradeReason = null;
        // Windows 无轻量文件沙盒：保护模式 = 普通启动（UI 已标注），严格模式 = 真断网
        if (mode != SandboxMode.StrictIsolation) return null;

        var ruleName = "StarviewBlock" + Guid.NewGuid().ToString("N")[..8];
        var javaPath = profile.JavaPath;
        var result = RunNetsh($"advfirewall firewall add rule name=\"{ruleName}\" dir=out program=\"{javaPath}\" action=block profile=any");
        if (result.ExitCode != 0)
        {
            degradeReason = "严格断网需要管理员权限，本次已降级为保护模式（可联网）。可右键「以管理员身份运行」启动器开启严格隔离";
            return null;
        }

        // cleaned 放闭包局部变量（非实例字段）：runner 被缓存复用时多个启动实例各自独立，不会串
        var cleaned = 0;
        var cleanup = new Action(() =>
        {
            if (Interlocked.Exchange(ref cleaned, 1) == 0)
                RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
        });
        return new SandboxCommand(profile.JavaPath, OriginalArgs(profile), cleanup);
    }

    /// <summary>Windows 防火墙包装不改变命令本身，返回原参数（仅挂清理动作）</summary>
    private static List<string> OriginalArgs(JavaArgumentsBuilder.LaunchProfile p)
    {
        var args = new List<string>();
        args.AddRange(p.JvmArgs);
        args.Add(p.MainClass);
        args.AddRange(p.GameArgs);
        return args;
    }

    private static ProcessResult RunNetsh(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return new ProcessResult(-1);
            var err = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return new ProcessResult(proc.ExitCode, err);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, ex.Message);
        }
    }

    private sealed record ProcessResult(int ExitCode, string? Error = null);
}
