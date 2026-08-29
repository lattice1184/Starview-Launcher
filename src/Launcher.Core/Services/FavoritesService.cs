using System.Text.Json;

namespace Launcher.Core.Services;

/// <summary>
/// 生态收藏（本地持久化）：projectId 集合存 AppData\Launcher\favorites.json。
/// 静态单例 + 变化通知（星标切换即时写盘，跨会话保持）。
/// </summary>
public static class FavoritesService
{
    private static readonly string PathFile = Path.Combine(
        Launcher.Core.Utils.AppPaths.DataRoot, "favorites.json");

    private static readonly HashSet<string> Ids = Load();

    /// <summary>收藏变化通知（卡片星标 / 筛选列表刷新用）</summary>
    public static event Action? Changed;

    public static bool IsFavorite(string projectId) => Ids.Contains(projectId);

    public static IReadOnlyList<string> All => Ids.ToList();

    public static void Toggle(string projectId)
    {
        if (!Ids.Remove(projectId)) Ids.Add(projectId);
        Save();
        Changed?.Invoke();
    }

    public static bool TryAdd(string projectId)
    {
        if (!Ids.Add(projectId)) return false;
        Save();
        Changed?.Invoke();
        return true;
    }

    private static HashSet<string> Load()
    {
        try
        {
            if (File.Exists(PathFile))
            {
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(PathFile));
                if (list is not null) return [.. list];
            }
        }
        catch { /* 坏数据回退空 */ }
        return [];
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathFile)!);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(Ids.ToList()));
        }
        catch { /* 保存失败不阻塞 */ }
    }
}
