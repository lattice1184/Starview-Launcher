using System.Security.Cryptography;

namespace Launcher.Core.Services;

/// <summary>
/// 旧版 KeyProxy 密文的读取（AL50 一次性迁移）：应用数据目录 keyproxy\key.bin 是
/// KeyProxy 时代 DPAPI 原始字节格式（非 Secrets 的 "dpapi:" base64 格式）。迁移完成后文件删除，
/// 本类不再有写入路径，仅保留读——缺失/损坏返回 null（视为未配置，用户重新填写）。
/// Linux/macOS 无旧 KeyProxy 密文（迁移前数据本就只存在于 Windows），直接返回 null。
/// </summary>
public static class LegacyKeyStore
{
    public static string DefaultFilePath { get; } = Path.Combine(
        Launcher.Core.Utils.AppPaths.DataRoot, "keyproxy", "key.bin");

    /// <summary>读取并解密旧代理密文；非 Windows / 文件缺失 / 换账户 / 损坏 → null</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string? ReadLegacyKey()
    {
        if (!OperatingSystem.IsWindows()) return null; // Linux/macOS 无旧 KeyProxy 密文
        try
        {
            if (!File.Exists(DefaultFilePath)) return null;
            var enc = File.ReadAllBytes(DefaultFilePath);
            return System.Text.Encoding.UTF8.GetString(
                ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return null; // 换账户/损坏：视为未配置
        }
    }
}
