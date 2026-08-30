using System.Diagnostics;
using Launcher.Core.Launch.Sandbox;
using Launcher.Core.Utils;

namespace Launcher.Core.Launch;

/// <summary>
/// 游戏进程管理：启动 JVM + 实时日志管道 + 退出检测。
/// </summary>
public sealed class LaunchProcess
{
    public sealed record LaunchResult(Process Process, string ExitStatusFilePath);

    /// <summary>启动命令行描述（AL8 日志增强：launch-*.log 首行记录，崩溃时根因一眼可见）。
    /// 按 ArgumentList 语义拼接——含空格/引号的参数用双引号包裹转义，日志可直接复现。</summary>
    public static string DescribeCommandLine(JavaArgumentsBuilder.LaunchProfile profile)
        => DescribeCommandLine(profile.JavaPath,
            profile.JvmArgs.Append(profile.MainClass).Concat(profile.GameArgs));

    /// <summary>通用重载（服务端等无 LaunchProfile 的场景）</summary>
    public static string DescribeCommandLine(string exe, IEnumerable<string> args)
        => string.Join(' ', new[] { exe }.Concat(RedactTokens(args)).Select(QuoteArg));

    /// <summary>8-13 日志脱敏：launch-*.log 的「启动命令」行打码 token——
    /// 正版启动会写真实 accessToken（--auth_access_token/--auth_session/--accessToken），
    /// 拷走日志即可冒名进号。--x 与 --x= 两种形态都覆盖；值替换为 ***，参数名保留（根因诊断不受影响）。</summary>
    internal static IEnumerable<string> RedactTokens(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            var eq = a.IndexOf('=');
            var name = eq >= 0 ? a[..eq] : a;
            if (name is "--auth_access_token" or "--auth_session" or "--accessToken" or "--authAccessToken")
            {
                if (eq >= 0) list[i] = name + "=***";
                else if (i + 1 < list.Count) list[i + 1] = "***";
            }
        }
        return list;
    }

    private static string QuoteArg(string arg)
        => arg.Contains(' ') || arg.Contains('"')
            ? "\"" + arg.Replace("\"", "\\\"") + "\""
            : arg;

    /// <summary>启动游戏进程。日志行通过 onLog 回调实时输出。
    /// 8-30 sandboxMode：非 Disabled 时用沙盒包装命令（Linux bwrap / macOS sandbox-exec / Windows 防火墙），
    /// 包装失败自动降级普通启动并 onLog 提示原因。</summary>
    public static LaunchResult Start(
        JavaArgumentsBuilder.LaunchProfile profile,
        Action<string>? onLog = null, CancellationToken ct = default,
        GamePriority priority = GamePriority.Normal,
        SandboxMode sandboxMode = SandboxMode.Disabled)
    {
        var psi = new ProcessStartInfo
        {
            FileName = profile.JavaPath,
            WorkingDirectory = profile.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        // 沙盒包装：替换 FileName + 参数（Windows 防火墙模式命令不变仅挂清理），失败降级普通启动
        SandboxCommand? cmd = null;
        if (sandboxMode != SandboxMode.Disabled)
        {
            cmd = SandboxManager.GetRunner().Wrap(profile, sandboxMode, out var degrade);
            if (degrade is not null) onLog?.Invoke($"§ 沙盒降级：{degrade}");
            if (cmd is not null)
            {
                psi.FileName = cmd.FileName;
                foreach (var a in cmd.Arguments) psi.ArgumentList.Add(a);
            }
        }
        if (cmd is null)
        {
            // JvmArgs 已含 -cp + classpath（JavaArgumentsBuilder 统一末尾追加）；这里补主类与游戏参数
            foreach (var arg in profile.JvmArgs) psi.ArgumentList.Add(arg);
            psi.ArgumentList.Add(profile.MainClass);
            foreach (var arg in profile.GameArgs) psi.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLog?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLog?.Invoke(e.Data); };

        process.Start();
        if (cmd is not null)
            onLog?.Invoke("§ 启动命令（沙盒）：" + DescribeCommandLine(cmd.FileName, cmd.Arguments));
        if (cmd?.Cleanup is not null)
        {
            // 游戏退出清理（如删防火墙规则）：EnableRaisingEvents 才能收到 Exited；异常吞掉不影响退出检测
            var cleanup = cmd.Cleanup;
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => { try { cleanup(); } catch { } };
        }
        ApplyPriority(process, priority, onLog);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 1.17+ 的 exitStatus 文件：不预写 0（避免掩盖崩溃）；游戏正常退出时自己写入
        var exitFile = Path.Combine(profile.WorkingDirectory, "exitStatus");
        try { if (File.Exists(exitFile)) File.Delete(exitFile); } catch { }

        return new LaunchResult(process, exitFile);
    }

    /// <summary>读取退出状态（-1 = 崩溃/文件缺失）</summary>
    public static int ReadExitStatus(string path)
    {
        try { return int.Parse(File.ReadAllText(path)); }
        catch { return -1; }
    }

    /// <summary>
    /// 综合退出码：进程 ExitCode 非 0（JVM 崩溃/OOM/被杀）优先；
    /// ExitCode==0 时读 exitStatus 文件补充——本项目裸 Java 启动不写该文件（官方启动器包装器才写），
    /// 文件缺失即正常退出返回 0（修复"主界面退出游戏被误报异常退出(-1)"）；文件存在非 0 才视为异常。
    /// </summary>
    public static int GetExitCode(LaunchResult result)
    {
        try
        {
            if (result.Process.HasExited && result.Process.ExitCode != 0)
                return result.Process.ExitCode;
        }
        catch { }
        var fileCode = ReadExitStatus(result.ExitStatusFilePath);
        return fileCode > 0 ? fileCode : 0; // 缺失/解析失败/为 0 → 正常退出
    }

    /// <summary>GamePriority → Windows 进程优先级类</summary>
    public static ProcessPriorityClass ToPriorityClass(GamePriority p) => p switch
    {
        GamePriority.BelowNormal => ProcessPriorityClass.BelowNormal,
        GamePriority.AboveNormal => ProcessPriorityClass.AboveNormal,
        GamePriority.High => ProcessPriorityClass.High,
        GamePriority.RealTime => ProcessPriorityClass.RealTime,
        _ => ProcessPriorityClass.Normal,
    };

    /// <summary>启动后设置进程优先级（Normal 跳过零开销；失败仅记日志——非管理员设 RealTime 可能无权限）</summary>
    private static void ApplyPriority(Process process, GamePriority priority, Action<string>? onLog)
    {
        if (priority == GamePriority.Normal) return;
        try { process.PriorityClass = ToPriorityClass(priority); }
        catch (Exception ex) { onLog?.Invoke($"§ 设置进程优先级失败：{ex.Message}"); }
    }
}
