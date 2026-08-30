using System.Diagnostics;

namespace Launcher.Core.Launch.Sandbox;

/// <summary>沙盒能力检查 + runner 工厂（运行时平台分派，沿用仓库无抽象层惯例）。</summary>
public static class SandboxManager
{
    /// <summary>Windows：清理残留的 Starview 防火墙规则——启动器崩溃时「游戏退出删规则」没执行，
    /// 孤儿规则会持续挡 java 出站（权限管控合理性的兜底：不让规则越积越多）。启动器启动时调用一次；
    /// 非管理员删除失败静默跳过（残留规则反正不增不减，下次管理员启动再清）。</summary>
    public static void CleanupOrphanFirewallRules()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("netsh", "advfirewall firewall show rule name=all")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (proc is null) return;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                if (!t.StartsWith("规则名称:", StringComparison.Ordinal)
                    && !t.StartsWith("Rule Name:", StringComparison.OrdinalIgnoreCase))
                    continue;
                var name = t.Split(':', 2)[1].Trim();
                if (!name.StartsWith("StarviewBlock", StringComparison.Ordinal)) continue;
                try
                {
                    using var del = Process.Start(new ProcessStartInfo("netsh", $"advfirewall firewall delete rule name=\"{name}\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    del?.WaitForExit(5000);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>检查指定模式在当前环境是否可用。Disabled 恒可用。</summary>
    public static bool IsSandboxSupported(SandboxMode mode, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (mode == SandboxMode.Disabled) return true;

        if (OperatingSystem.IsLinux())
        {
            if (!File.Exists("/usr/bin/bwrap"))
            {
                errorMessage = "当前系统未安装 bubblewrap(bwrap)，沙盒模式不可用（Debian/Ubuntu: sudo apt install bubblewrap）";
                return false;
            }
            return true;
        }
        if (OperatingSystem.IsMacOS())
        {
            // sandbox-exec 为 macOS 内置，无需外部依赖
            return true;
        }
        if (OperatingSystem.IsWindows())
        {
            // 严格隔离走 netsh 防火墙规则（需管理员）；保护模式为普通启动
            return true;
        }
        errorMessage = "当前操作系统暂不支持沙盒功能";
        return false;
    }

    public static ISandboxRunner GetRunner() => new SandboxRunner();
}
