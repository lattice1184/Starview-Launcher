using System.Diagnostics;
using PCL.Core.App;
using PCL.Core.IO;

namespace Launcher.Core.Update;

/// <summary>
/// 应用更新安装（Part C）。
/// Windows：复用 PCL UpdateService 链路（update {pid} {target} {source} true——新实例等旧进程退出后
/// FileSystemWatcher 替换单文件并重启）。
/// Linux/macOS：tar.gz 散文件包 → 解压 staging → 延迟 shell 脚本覆盖安装目录（sleep 后旧进程已退出，
/// POSIX 允许删除运行中的可执行/dylib），完成后重启。
/// </summary>
public static class UpdateInstaller
{
    /// <summary>启动更新流程（成功返回 null；错误返回可展示文案）。返回后当前进程应尽快退出——替换由子进程/延迟脚本接管</summary>
    public static async Task<string?> StartAsync(string readyPath, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(readyPath)) return "更新文件不存在，请重新检查更新";
            return OperatingSystem.IsWindows()
                ? StartWindows(readyPath)
                : await StartUnixAsync(readyPath, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "更新已取消";
        }
        catch (Exception ex)
        {
            return $"更新失败：{ex.Message}";
        }
    }

    /// <summary>Windows：单文件 exe 自替换（复用 PCL UpdateService 的 update 参数协议）</summary>
    private static string? StartWindows(string readyPath)
    {
        var target = Basics.ExecutablePath;
        if (!Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return "当前程序不是 exe，无法用 Windows 更新方式替换";
        var psi = new ProcessStartInfo(target)
        {
            UseShellExecute = false,
            Arguments = $"update {Environment.ProcessId} \"{target}\" \"{readyPath}\" true",
        };
        Process.Start(psi);
        return null;
    }

    /// <summary>Linux/macOS：解压到 staging → 延迟脚本覆盖安装目录 → 重启</summary>
    private static async Task<string?> StartUnixAsync(string readyPath, CancellationToken ct)
    {
        var root = InstallRoot();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return "找不到启动器安装目录";

        var staging = Path.Combine(Path.GetTempPath(), $"starview-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            await Files.ExtractFileAsync(readyPath, staging, null, ct);
            if (!Directory.EnumerateFileSystemEntries(staging).Any())
                return "更新包内容为空";

            var script = Path.Combine(Path.GetTempPath(), $"starview-apply-{Guid.NewGuid():N}.sh");
            File.WriteAllText(script, BuildApplyScript(root, staging, readyPath));
            var psi = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                Arguments = $"\"{script}\"",
            };
            Process.Start(psi);
            return null;
        }
        finally
        {
            // staging 由脚本在替换完成后清理；异常路径（脚本未启动）也尽力清理
            try { if (!Directory.EnumerateFileSystemEntries(staging).Any()) Directory.Delete(staging, true); } catch { }
        }
    }

    /// <summary>
    /// 安装目录：Linux = 可执行目录；macOS = .app bundle 根（Contents/MacOS 上溯两级）或可执行目录（散文件安装）。
    /// 覆盖式更新：删除旧 Launcher.App + 动态库，从 staging 复制新文件——两种安装布局都适配。
    /// </summary>
    private static string? InstallRoot()
    {
        var dir = Basics.ExecutableDirectory;
        if (!OperatingSystem.IsMacOS()) return dir;
        var contents = Path.GetDirectoryName(dir);
        var bundle = contents is null ? null : Path.GetDirectoryName(contents);
        return bundle is not null && Path.GetExtension(bundle).Equals(".app", StringComparison.OrdinalIgnoreCase)
            ? bundle
            : dir;
    }

    private static string BuildApplyScript(string root, string staging, string readyPath)
    {
        return string.Join('\n',
        [
            "#!/bin/sh",
            "sleep 3   # 等旧进程完全退出，避免复制中途被杀",
            $"cd '{root}'",
            // 覆盖式更新：删旧可执行/库/脚本，保留用户可能放置的无关文件；新包移除的库也一并清掉
            "rm -f Launcher.App start.sh Starview.desktop 启动.command lib*.so lib*.dylib",
            $"cp -R '{staging}'/. .",
            // 删下载包 + staging（失败则下次检查重新下载）
            $"rm -rf '{staging}'",
            $"rm -f '{readyPath}'",
            "chmod +x Launcher.App start.sh 2>/dev/null",
            "nohup ./Launcher.App >/dev/null 2>&1 &",
            "exit 0",
        ]);
    }
}
