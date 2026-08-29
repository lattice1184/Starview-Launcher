using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Launcher.Core.Utils;

/// <summary>
/// 本地机密保护（8-29 Linux 移植）：Windows = DPAPI 用户级加密（同一 Windows 账户才能解密，拷走文件也解不开）；
/// Linux/macOS = libsecret（Secret Service，经 secret-tool CLI，需 libsecret-tools 包）。
/// 存储格式带前缀标记；无前缀 = 旧版明文数据（读取原样返回，下次保存自动转加密）。
/// </summary>
public static class Secrets
{
    private static readonly ISecretStore Store = CreateStore();

    private static ISecretStore CreateStore()
    {
#if WINDOWS
        return new DpapiSecretStore();
#else
        return new SecretServiceStore();
#endif
    }

    /// <summary>加密为前缀 + base64；空串原样返回（不为空值存密文）。</summary>
    public static string Protect(string plain) => Store.Protect(plain);

    /// <summary>读取：带前缀 → 解密；无前缀 → 旧版明文迁移。解密失败（换账户/数据损坏）→ null。</summary>
    public static string? Read(string stored) => Store.Read(stored);
}

/// <summary>跨平台加解密存储抽象。</summary>
internal interface ISecretStore
{
    string Protect(string plain);
    string? Read(string stored);
}

#if WINDOWS
/// <summary>Windows：DPAPI 用户级加密（现有实现）。</summary>
internal sealed class DpapiSecretStore : ISecretStore
{
    private const string Prefix = "dpapi:";

    public string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain;
        var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(enc);
    }

    public string? Read(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored; // 旧版明文
        try
        {
            var enc = Convert.FromBase64String(stored[Prefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser));
        }
        catch { return null; }
    }
}
#else
/// <summary>
/// Linux/macOS：libsecret（Secret Service）。主密钥存系统 keyring（secret-tool CLI，需 libsecret-tools 包），
/// 值用 AES-GCM（随机 nonce）加密。secret-tool 不可用时降级为原值返回（不破坏数据，但等同明文存储——
/// 首次使用会因主密钥缺失而无法解密旧密文，提示安装 libsecret-tools）。
/// </summary>
internal sealed class SecretServiceStore : ISecretStore
{
    private const string Prefix = "sv:";
    private const string AttrApp = "application";
    private const string AttrKey = "starview";
    private const string SecretName = "master-key";

    private static readonly byte[]? MasterKey = LoadOrCreateMasterKey();

    public string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain;
        if (MasterKey is null) return plain; // libsecret 不可用降级
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var data = Encoding.UTF8.GetBytes(plain);
        var ct = new byte[data.Length];
        using (var aes = new AesGcm(MasterKey, tag.Length))
            aes.Encrypt(nonce, data, ct, tag);
        var payload = new byte[12 + tag.Length + ct.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, 12);
        ct.CopyTo(payload, 12 + tag.Length);
        return Prefix + Convert.ToBase64String(payload);
    }

    public string? Read(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored; // 旧版明文
        if (MasterKey is null) return null;
        try
        {
            var payload = Convert.FromBase64String(stored[Prefix.Length..]);
            if (payload.Length < 12 + 16) return null;
            var nonce = payload[..12];
            var tag = payload[12..28];
            var ct = payload[28..];
            using var aes = new AesGcm(MasterKey, tag.Length);
            var plain = new byte[ct.Length];
            aes.Decrypt(nonce, ct, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }

    private static byte[]? LoadOrCreateMasterKey()
    {
        var existing = SecretToolLookup();
        if (existing is { Length: 32 }) return existing;
        var key = RandomNumberGenerator.GetBytes(32);
        return SecretToolStore(key) ? key : null;
    }

    private static byte[]? SecretToolLookup()
    {
        try
        {
            var psi = new ProcessStartInfo("secret-tool", $"lookup {AttrApp} {AttrKey} {SecretName}")
            {
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            p.StandardInput.Close();
            var text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return string.IsNullOrEmpty(text) ? null : Convert.FromBase64String(text);
        }
        catch { return null; }
    }

    private static bool SecretToolStore(byte[] key)
    {
        try
        {
            var psi = new ProcessStartInfo("secret-tool", $"store --label=Starview {AttrApp} {AttrKey} {SecretName}")
            {
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardInput.Write(Convert.ToBase64String(key));
            p.StandardInput.Close();
            return p.WaitForExit(2000);
        }
        catch { return false; }
    }
}
#endif
