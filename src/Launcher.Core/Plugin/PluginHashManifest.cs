using System.Security.Cryptography;
using System.Text.Json;

namespace Launcher.Core.Plugin;

/// <summary>插件登记状态（8-31 升级：从纯哈希升级为"哈希 + 启停 + 导入时间"注册表）。</summary>
public enum PluginStatus
{
    /// <summary>未登记（首次见，Load 时记录基准）</summary>
    Unknown,
    /// <summary>哈希一致且启用</summary>
    Normal,
    /// <summary>用户停用</summary>
    Disabled,
    /// <summary>哈希不一致（被掉包/投毒）</summary>
    Tampered,
}

/// <summary>插件单条登记记录。</summary>
public sealed record PluginEntry(string Sha1, bool Enabled, DateTime ImportedAt)
{
    /// <summary>从旧扁平格式（dll→sha1 字符串）迁移</summary>
    public static PluginEntry FromFlat(string sha1) => new(sha1, true, DateTime.Now);
}

/// <summary>
/// 插件登记（8-31 MVP 防投毒）：plugins/.starview-plugins.json 记录每个插件 dll 的 SHA1 + 启停状态。
/// 首次加载记录基准；之后重算比对——不一致 = 文件被掉包/投毒 → 拒绝加载。
/// 8-31 升级：enabled/importedAt 进注册表（停用不删除文件，Load 跳过）；兼容旧扁平格式。
/// 与 mod 哈希投毒检测（ModHashManifest）同思路，插件独立文件。独立类便于单测（不依赖 AppPaths）。
/// </summary>
public static class PluginHashManifest
{
    // Save 与 Load 必须同命名策略：统一 camelCase（"sha1"/"enabled"/"importedAt"）
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    /// <summary>校验 + 记录：首次见返回 true（记录基准后加载）；二次比对一致 true，不一致（掉包）false。</summary>
    public static bool VerifyOrRecord(string dllPath, string hashFile)
    {
        try
        {
            var name = System.IO.Path.GetFileName(dllPath);
            var sha1 = ComputeSha1(dllPath);
            var entries = Load(hashFile);
            if (!entries.TryGetValue(name, out var entry))
            {
                entries[name] = new PluginEntry(sha1, true, DateTime.Now); // 首次：记录基准 + 默认启用
                Save(hashFile, entries);
                return true;
            }
            return entry.Sha1 == sha1;
        }
        catch { return false; } // 读失败保守拒绝（不加载未确认的插件）
    }

    /// <summary>查询插件状态（不写文件）。</summary>
    public static PluginStatus GetStatus(string dllPath, string hashFile)
    {
        try
        {
            var name = System.IO.Path.GetFileName(dllPath);
            var entries = Load(hashFile);
            if (!entries.TryGetValue(name, out var entry)) return PluginStatus.Unknown;
            if (entry.Sha1 != ComputeSha1(dllPath)) return PluginStatus.Tampered;
            return entry.Enabled ? PluginStatus.Normal : PluginStatus.Disabled;
        }
        catch { return PluginStatus.Tampered; } // 读失败保守视为异常
    }

    /// <summary>设启停状态（不影响哈希）。未登记条目按登记处理。</summary>
    public static void SetEnabled(string dllPath, string hashFile, bool enabled)
    {
        try
        {
            var name = System.IO.Path.GetFileName(dllPath);
            var sha1 = ComputeSha1(dllPath);
            var entries = Load(hashFile);
            entries[name] = entries.TryGetValue(name, out var old)
                ? new PluginEntry(old.Sha1, enabled, old.ImportedAt)
                : new PluginEntry(sha1, enabled, DateTime.Now);
            Save(hashFile, entries);
        }
        catch { /* 写失败不阻断（下次以盘上为准） */ }
    }

    /// <summary>移除登记（删除插件时同步清条目）。</summary>
    public static void Remove(string dllPath, string hashFile)
    {
        try
        {
            var name = System.IO.Path.GetFileName(dllPath);
            var entries = Load(hashFile);
            if (entries.Remove(name)) Save(hashFile, entries);
        }
        catch { }
    }

    private static string ComputeSha1(string dllPath) => Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(dllPath)));

    private static Dictionary<string, PluginEntry> Load(string hashFile)
    {
        try
        {
            if (!File.Exists(hashFile)) return new Dictionary<string, PluginEntry>();
            using var doc = JsonDocument.Parse(File.ReadAllText(hashFile));
            var result = new Dictionary<string, PluginEntry>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var v = prop.Value;
                // 旧扁平格式 { dll: "sha1" } → 迁移为 enabled=true 条目
                if (v.ValueKind == JsonValueKind.String)
                {
                    result[prop.Name] = PluginEntry.FromFlat(v.GetString()!);
                    continue;
                }
                if (v.TryGetProperty("sha1", out var sha))
                    result[prop.Name] = new PluginEntry(sha.GetString() ?? "",
                        v.TryGetProperty("enabled", out var en) ? en.GetBoolean() : true,
                        v.TryGetProperty("importedAt", out var dt) && DateTime.TryParse(dt.GetString(), out var t) ? t : DateTime.Now);
            }
            return result;
        }
        catch { return new Dictionary<string, PluginEntry>(); }
    }

    private static void Save(string hashFile, Dictionary<string, PluginEntry> entries)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(hashFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(hashFile, JsonSerializer.Serialize(entries, JsonOptions));
        }
        catch { /* 记录失败不阻断（下次仍首次记录） */ }
    }
}
