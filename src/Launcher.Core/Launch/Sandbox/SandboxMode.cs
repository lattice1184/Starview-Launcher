namespace Launcher.Core.Launch.Sandbox;

/// <summary>沙盒启动模式。语义沿用 8-30 朋友方案（Disabled/Protected/StrictIsolation）。</summary>
public enum SandboxMode
{
    /// <summary>关闭沙盒，普通启动（行为与以往完全一致）</summary>
    Disabled,
    /// <summary>保护模式：可联网，仅限制文件访问（Windows 无轻量文件沙盒，等同普通启动，UI 已标注）</summary>
    Protected,
    /// <summary>严格隔离：断网 + 文件隔离（Windows 端断网需管理员权限，非管理员自动降级）</summary>
    StrictIsolation,
}
