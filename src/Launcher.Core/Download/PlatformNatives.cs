using System.Runtime.InteropServices;

namespace Launcher.Core.Download;

/// <summary>
/// Mojang natives 分类器 key 解析（8-30 修：原写死 "windows"，Mac/Linux 装游戏下的是 Windows .dll natives，跑不起来）。
/// Windows→"windows"；macOS→优先 osx-arm64/osx-x86_64（按架构），回退 "osx"；Linux→"linux"。
/// </summary>
public static class PlatformNatives
{
    /// <summary>按平台取 natives 分类器 key；无匹配返回 null。</summary>
    public static string? ResolveKey(IReadOnlyDictionary<string, string> natives)
    {
        if (OperatingSystem.IsWindows()) return natives.TryGetValue("windows", out var k) ? k : null;
        if (OperatingSystem.IsMacOS())
        {
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x86_64";
            if (natives.TryGetValue(arch, out var k)) return k;
            return natives.TryGetValue("osx", out k) ? k : null;
        }
        return natives.TryGetValue("linux", out var kl) ? kl : null;
    }
}
