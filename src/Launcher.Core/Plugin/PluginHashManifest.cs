using System.Security.Cryptography;
using System.Text.Json;

namespace Launcher.Core.Plugin;

/// <summary>
/// 插件哈希登记（8-31 MVP 防投毒）：plugins/.starview-plugins.json 记录每个插件 dll 的 SHA1。
/// 首次加载记录基准；之后重算比对——不一致 = 文件被掉包/投毒 → 拒绝加载。
/// 与 mod 哈希投毒检测（ModHashManifest）同思路，插件独立文件。独立类便于单测（不依赖 AppPaths）。
/// </summary>
public static class PluginHashManifest
{
    /// <summary>校验 + 记录：首次见返回 true（记录基准后加载）；二次比对一致 true，不一致（掉包）false。</summary>
    public static bool VerifyOrRecord(string dllPath, string hashFile)
    {
        try
        {
            var name = System.IO.Path.GetFileName(dllPath);
            var sha1 = Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(dllPath)));
            var entries = Load(hashFile);
            if (!entries.TryGetValue(name, out var recorded))
            {
                entries[name] = sha1; // 首次：记录基准哈希
                Save(hashFile, entries);
                return true;
            }
            return recorded == sha1;
        }
        catch { return false; } // 读失败保守拒绝（不加载未确认的插件）
    }

    private static Dictionary<string, string> Load(string hashFile)
    {
        try
        {
            if (!File.Exists(hashFile)) return new Dictionary<string, string>();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(hashFile)) ?? new();
        }
        catch { return new Dictionary<string, string>(); }
    }

    private static void Save(string hashFile, Dictionary<string, string> entries)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(hashFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(hashFile, JsonSerializer.Serialize(entries));
        }
        catch { /* 记录失败不阻断（下次仍首次记录） */ }
    }
}
