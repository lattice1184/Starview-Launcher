using Launcher.Core.Launch;

namespace Launcher.Core.Account;

/// <summary>
/// 8-19 LittleSkin 皮肤本地同步（登录/皮肤库共用）：下载角色皮肤
/// （yggdrasil 纹理路径 → 降级 /textures/{hash}）→ 校验尺寸 → 写 %AppData%\Launcher\skins\{name}.png。
/// SkinPack 注入条件 = 本地文件存在——OAuth 登录后不同步则游戏内是默认 Steve/Alex（旧邮箱流程有、重构丢失的回归）。
/// </summary>
public static class LittleSkinSkinSync
{
    /// <summary>本地皮肤路径（与 HomeViewModel.LocalSkinPath 同规则）</summary>
    public static string LocalSkinPath(string playerName) => Path.Combine(
        Launcher.Core.Utils.AppPaths.DataRoot, "skins", $"{playerName}.png");

    /// <summary>下载皮肤写本地；返回是否成功（下载失败/尺寸不合规 → false，调用方决定兜底）。
    /// 8-19 修死 URL：/skin/{name}.png 实测 404（非 LittleSkin 纹理路径）——
    /// 正确来源是 yggdrasil profile 的 textures URL（/textures/{hash}），uuid 可查，hash 直连</summary>
    public static async Task<bool> DownloadToLocalAsync(
        HttpClient http, string playerName, string? uuid = null, string? fallbackHash = null)
    {
        byte[]? bytes = null;
        foreach (var url in await BuildUrlsAsync(http, uuid, fallbackHash))
        {
            try
            {
                using var resp = await http.GetAsync(url);
                if (resp.IsSuccessStatusCode) { bytes = await resp.Content.ReadAsByteArrayAsync(); break; }
            }
            catch { /* 换下一个候选 */ }
        }
        if (bytes is null) return false;

        var size = SkinPngHeader.TryParse(bytes);
        if (size is not { } dims || !SkinPack.IsSupportedSize(dims.Width, dims.Height))
            return false; // 尺寸不支持（或非 PNG）——不写本地（游戏内 PUT 已生效）

        SkinFileWriter.ForceWrite(LocalSkinPath(playerName), bytes);
        return true;
    }

    private static async Task<List<string>> BuildUrlsAsync(HttpClient http, string? uuid, string? fallbackHash)
    {
        var urls = new List<string>();
        // 优先 yggdrasil profile 解析（uuid → textures URL）；失败再试 hash 直连
        if (!string.IsNullOrWhiteSpace(uuid) && await ResolveTextureUrlAsync(http, uuid) is { } tex)
            urls.Add(tex);
        if (!string.IsNullOrWhiteSpace(fallbackHash))
            urls.Add($"https://littleskin.cn/textures/{fallbackHash}");
        return urls;
    }

    /// <summary>yggdrasil profile → 皮肤纹理 URL（免 token 公开端点；角色未设纹理 → null）。
    /// 8-19 实机：LittleSkin 的 /skin/{name}.png 404，正确路径是 profile 里的 textures.url</summary>
    public static async Task<string?> ResolveTextureUrlAsync(HttpClient http, string uuid)
    {
        try
        {
            // 8-19 profile 端点要求无横线 UUID（dashed 404 / undashed 200 实测）
            using var resp = await http.GetAsync(
                $"https://littleskin.cn/api/yggdrasil/sessionserver/session/minecraft/profile/{uuid.Replace("-", "")}");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("properties", out var props)
                || props.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            foreach (var p in props.EnumerateArray())
            {
                if (p.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !p.TryGetProperty("name", out var n) || n.GetString() != "textures"
                    || !p.TryGetProperty("value", out var v) || v.GetString() is not { } b64) continue;
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                using var tex = System.Text.Json.JsonDocument.Parse(json);
                if (tex.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && tex.RootElement.TryGetProperty("textures", out var t)
                    && t.ValueKind == System.Text.Json.JsonValueKind.Object
                    && t.TryGetProperty("SKIN", out var skin)
                    && skin.ValueKind == System.Text.Json.JsonValueKind.Object
                    && skin.TryGetProperty("url", out var u))
                    return u.GetString();
            }
        }
        catch { /* 解析失败按未设纹理处理 */ }
        return null;
    }
}
