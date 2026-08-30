namespace Launcher.Core.Launch.Sandbox;

/// <summary>沙盒包装结果：替换后的启动命令 + 游戏退出时的清理动作（如删防火墙规则）。</summary>
public sealed record SandboxCommand(string FileName, IReadOnlyList<string> Arguments, Action? Cleanup);

/// <summary>
/// 沙盒启动器：把普通 Java 启动命令包装为受隔离的命令。
/// 8-30 采用 C# 直构命令（Linux bwrap / macOS sandbox-exec / Windows 防火墙），
/// 不引入内嵌 C helper 二进制——消除不可审计黑盒，三平台可落地。
/// </summary>
public interface ISandboxRunner
{
    /// <summary>
    /// 包装启动命令。
    /// 返回 null = 本次不沙盒（降级为普通启动），degradeReason 给用户可见原因（可能为 null）。
    /// 返回非 null = 用 SandboxCommand 替换原命令；Cleanup 非空时由调用方挂到进程 Exited。
    /// </summary>
    SandboxCommand? Wrap(JavaArgumentsBuilder.LaunchProfile profile, SandboxMode mode, out string? degradeReason);
}
