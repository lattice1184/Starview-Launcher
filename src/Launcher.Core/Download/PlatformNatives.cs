using System.Runtime.InteropServices;

namespace Launcher.Core.Download;

/// <summary>
/// Mojang natives 分类器 key 解析（8-30 修：原写死 "windows"，Mac/Linux 装游戏下的是 Windows .dll natives，跑不起来）。
/// Windows→"windows"；macOS→优先 osx-arm64/osx-x86_64（按架构），回退 "osx"；Linux→"linux"。
/// </summary>
public static class PlatformNatives
{
    /// <summary>按平台取 natives 分类器 key；无匹配返回 null。
    /// 8-31 展开 ${arch} 占位符（老版本如 twitch-platform 的 "windows":"natives-windows-${arch}"）——
    /// 不展开则 classifier key 匹配不上，natives 永不下载/校验误报缺。</summary>
    public static string? ResolveKey(IReadOnlyDictionary<string, string> natives)
    {
        if (OperatingSystem.IsWindows()) return ExpandArch(natives.TryGetValue("windows", out var k) ? k : null);
        if (OperatingSystem.IsMacOS())
        {
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x86_64";
            if (natives.TryGetValue(arch, out var k)) return ExpandArch(k);
            return ExpandArch(natives.TryGetValue("osx", out k) ? k : null);
        }
        return ExpandArch(natives.TryGetValue("linux", out var kl) ? kl : null);
    }

    /// <summary>${arch} → "64"/"32"（x64/arm64 → 64，否则 32）</summary>
    private static string? ExpandArch(string? key)
    {
        if (key is null || !key.Contains("${arch}", StringComparison.Ordinal)) return key;
        var bits = RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64 ? "64" : "32";
        return key.Replace("${arch}", bits, StringComparison.Ordinal);
    }
}
