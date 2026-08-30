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

    /// <summary>Linux/macOS：解压到 staging → 延迟脚本原子替换安装目录 → 重启（失败回滚 + 日志）</summary>
    private static async Task<string?> StartUnixAsync(string readyPath, CancellationToken ct)
    {
        var root = InstallRoot();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return "找不到启动器安装目录";

        var staging = Path.Combine(Path.GetTempPath(), $"starview-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            await Files.ExtractFileAsync(readyPath, staging, null, ct);
            // 完整性预检：staging 必须有可执行体（顶层散文件 Launcher.App 或 .app bundle 内），
            // 否则继续会 rm 旧版却拷入空目录 → 打不开。预检不过直接报错，不碰安装目录。
            if (!HasExecutable(staging))
                return "更新包内容为空或缺少可执行文件";

            var logPath = Path.Combine(Launcher.Core.Utils.AppPaths.LogsDir, "update-install.log");
            var script = Path.Combine(Path.GetTempPath(), $"starview-apply-{Guid.NewGuid():N}.sh");
            File.WriteAllText(script, BuildApplyScript(root, staging, readyPath, logPath));
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

    /// <summary>staging 是否含可执行体：顶层散文件 Launcher.App，或 Starview.app bundle 内 Launcher.App</summary>
    private static bool HasExecutable(string staging)
    {
        return File.Exists(Path.Combine(staging, "Launcher.App"))
            || File.Exists(Path.Combine(staging, "Starview.app", "Contents", "MacOS", "Launcher.App"));
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

    internal static string BuildApplyScript(string root, string staging, string readyPath, string logPath)
    {
        // 8-31 原子替换重写：set -e 任一失败即中止；mv 整体备份 → cp 全新目录 → 验证 → 回滚。
        // 不再「先 rm 后 cp」——任何失败旧版仍完好可打开，杜绝「更新后目录空/打不开」。
        // 全部路径单引号包裹（空格/中文安全）；脚本输出落 update-install.log 供排查。
        return string.Join('\n',
        [
            "#!/bin/sh",
            "set -e",
            "mkdir -p \"$(dirname '" + logPath + "')\"",
            "exec >> '" + logPath + "' 2>&1",
            "echo \"=== update install $(date) ===\"",
            "echo \"root='" + root + "' staging='" + staging + "' ready='" + readyPath + "'\"",
            "sleep 3   # 等旧进程完全退出（避免新旧双开 / 复制中途被杀）",
            // 1) staging 完整性（双保险：防解压不全就开动 → rm 旧版后拷入空目录）
            "[ -s '" + staging + "/Launcher.App' ] || [ -s '" + staging + "/Starview.app/Contents/MacOS/Launcher.App' ] || { echo 'FAIL: staging missing executable'; exit 1; }",
            "[ -f '" + staging + "/Launcher.App.runtimeconfig.json' ] || { echo 'FAIL: staging missing runtimeconfig'; exit 1; }",
            // 2) 原子替换：旧目录整体移走备份 → 拷入全新新版（同设备 mv，父目录需可写）
            "BACKUP='" + root + ".old-$$'",
            "mv '" + root + "' \"$BACKUP\" || { echo 'FAIL: cannot move old dir (parent read-only?)'; exit 1; }",
            "if ! cp -R '" + staging + "' '" + root + "'; then",
            "    rm -rf '" + root + "'; mv \"$BACKUP\" '" + root + "'; echo 'FAIL: copy failed, rolled back'; exit 1;",
            "fi",
            "chmod +x '" + root + "/Launcher.App' '" + root + "/start.sh' 2>/dev/null || true",
            // 3) 新目录验证（可执行体在才认）；失败回滚 → 旧版完好
            "NEWAPP='" + root + "/Launcher.App'",
            "[ -x \"$NEWAPP\" ] || NEWAPP='" + root + "/Starview.app/Contents/MacOS/Launcher.App'",
            "if [ ! -x \"$NEWAPP\" ]; then",
            "    rm -rf '" + root + "'; mv \"$BACKUP\" '" + root + "'; echo 'FAIL: invalid new install, rolled back'; exit 1;",
            "fi",
            // 4) 成功：清备份 / staging / 下载包（失败路径保留 readyPath 供下次重试）
            "rm -rf \"$BACKUP\"",
            "rm -rf '" + staging + "'",
            "rm -f '" + readyPath + "'",
            // 5) 重启（散文件 Launcher.App；.app bundle 布局走 Contents/MacOS）
            "cd '" + root + "'",
            "nohup \"$NEWAPP\" >/dev/null 2>&1 &",
            "echo \"OK restarted: $NEWAPP\"",
            "exit 0",
        ]);
    }
}
