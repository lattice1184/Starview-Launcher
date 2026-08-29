using System.Collections.Concurrent;
using System.Text.Json;

namespace Launcher.Core.Services;

/// <summary>
/// 8-24 模组中文名本地缓存：MC百科链解析出的 (slug → 中文名) 落盘复用，达成 Verse 式
/// 「英文（中文）」自动翻译显示 + 重复中文搜索零网络。键带前缀隔离来源：`mr:{slug}` / `cf:{slug}`。
/// 种子 = ModAliasTable 热门别名（约 43 条）；用户每次中文搜索持续喂养，有机增长。
/// </summary>
public static class ChineseNameCache
{
    private static readonly ConcurrentDictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string CachePath = Path.Combine(
        Launcher.Core.Utils.AppPaths.DataRoot, "cache", "chinese-names.json");
    private static readonly object SaveLock = new();
    private static Timer? _saveTimer;

    static ChineseNameCache()
    {
        Load();
        SeedFromAliasTable();
    }

    /// <summary>显示标题：命中缓存 → `标题（中文）`；标题已含中文（中文搜索链路的标题）或未命中 → 原样。
    /// cacheKey 形如 `mr:sodium` / `cf:sodium`（调用方按来源拼前缀）。</summary>
    public static string Apply(string cacheKey, string title)
    {
        if (string.IsNullOrEmpty(cacheKey) || string.IsNullOrEmpty(title)) return title;
        if (McmodSearchService.ContainsChinese(title)) return title; // 已是中文标题，不再叠后缀
        return Map.TryGetValue(cacheKey, out var zh) ? $"{title}（{zh}）" : title;
    }

    /// <summary>写入中文名（防抖保存）</summary>
    public static void Put(string cacheKey, string chineseName)
    {
        if (string.IsNullOrEmpty(cacheKey) || string.IsNullOrEmpty(chineseName)) return;
        if (Map.TryAdd(cacheKey, chineseName)) ScheduleSave();
    }

    /// <summary>测试隔离：清空（不落盘）</summary>
    internal static void Clear()
    {
        Map.Clear();
        lock (SaveLock) { _saveTimer?.Dispose(); _saveTimer = null; }
    }

    private static void SeedFromAliasTable()
    {
        foreach (var (zh, slugs) in ModAliasTable.AllEntries())
            foreach (var slug in slugs)
                if (Map.TryAdd($"mr:{slug}", zh)) { }
        Save(); // 种子一次性落盘
    }

    private static void Load()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var json = File.ReadAllText(CachePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict is null) return;
            foreach (var (k, v) in dict) Map[k] = v;
        }
        catch { /* 加载失败当空——下次搜索重养 */ }
    }

    private static void ScheduleSave()
    {
        lock (SaveLock)
        {
            _saveTimer ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(1000, Timeout.Infinite); // 防抖：1s 无新写入才落盘
        }
    }

    private static void Save()
    {
        try
        {
            lock (SaveLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                File.WriteAllText(CachePath, JsonSerializer.Serialize(Map.ToDictionary(kv => kv.Key, kv => kv.Value)));
            }
        }
        catch { /* 保存失败无妨——下次 Put 再试 */ }
    }
}
