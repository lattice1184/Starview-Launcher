using System.Text.Json;

namespace Launcher.Core.Account;

/// <summary>
/// 8-13 clientId 远程下发（「藏」的最高层，PCL 同款）：登录前从配置服务器拉 clientId →
/// 本地 DPAPI 加密缓存 → 拉不到用缓存 → 缓存没有用内置兜底。三层全挂仍能登录（内置值）。
/// 远程 URL 为空 = 跳过远程（未配置服务器时零开销）。
/// </summary>
public static class ClientIdRemote
{
    /// <summary>远程配置 URL（占位——注册 Cloudflare Worker 后替换；空 = 不拉远程）</summary>
    internal const string RemoteUrl = "";

    private static string CachePath => Path.Combine(
        Launcher.Core.Utils.AppPaths.DataRoot, "client-id.cache");

    /// <summary>解析并写入当前进程的生效值：设置手动值 > 远程/缓存 > 内置兜底。登录/刷新前调用。</summary>
    public static async Task ResolveAsync(HttpClient http, CancellationToken ct)
    {
        var manual = Launcher.Core.Utils.LauncherSettings.Current.MicrosoftClientId;
        if (!string.IsNullOrWhiteSpace(manual))
        {
            MicrosoftAuth.SetResolvedClientId(manual.Trim());
            return;
        }
        var cached = ReadCache();
        if (!string.IsNullOrWhiteSpace(cached))
        {
            MicrosoftAuth.SetResolvedClientId(cached);
            return;
        }
        if (RemoteUrl.Length == 0) return; // 未配远程 → 内置兜底
        try
        {
            using var resp = await http.GetAsync(RemoteUrl, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var id = doc.RootElement.TryGetProperty("clientId", out var el) ? el.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id))
            {
                WriteCache(id);
                MicrosoftAuth.SetResolvedClientId(id);
            }
        }
        catch { /* 远程失败静默——走缓存/内置兜底 */ }
    }

    private static string? ReadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            return Launcher.Core.Utils.Secrets.Read(File.ReadAllText(CachePath));
        }
        catch { return null; }
    }

    private static void WriteCache(string id)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, Launcher.Core.Utils.Secrets.Protect(id));
        }
        catch { /* 缓存写失败不阻塞登录 */ }
    }
}
