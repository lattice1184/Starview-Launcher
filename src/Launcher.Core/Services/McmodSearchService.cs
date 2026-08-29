using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Launcher.Core.Download;

namespace Launcher.Core.Services;

/// <summary>
/// MC百科（mcmod.cn）中文搜索（AL63）：Modrinth 搜索索引为英文标题——中文查询无结果
/// （实测「遗落荒野」Modrinth 原生 0 命中）。链路：
///   中文 → search.mcmod.cn 搜索结果页（HTML 静态，正则解析条目 id + 中文标题）
///   → 条目详情页（www.mcmod.cn/class/{id}.html）→ link.mcmod.cn/target/{base64(完整 URL)}
///   双层编码解出 Modrinth 链接 → slug → Modrinth API 拿项目。
/// 两个页面均为静态 HTML，正则解析（无第三方依赖）；mcmod 国内直连可达（实测 200/0.4s）。
/// </summary>
public sealed class McmodSearchService
{
    private static readonly HttpClient Http = HttpClientPool.Create();

    /// <summary>8-19 生态修缮：搜索页/详情页磁盘缓存 TTL（mcmod 静态 HTML 稳定，重复搜索零网络）</summary>
    private static readonly long TtlSeconds = 24 * 3600;
    private readonly string _cacheDir;

    public McmodSearchService(string? cacheDir = null)
        => _cacheDir = cacheDir ?? Path.Combine(
            Launcher.Core.Utils.AppPaths.DataRoot, "cache", "mcmod");

    /// <summary>缓存文件：{dir}/{sha256(key)}.html，内容 = 8 字节 unix 时间戳 + HTML</summary>
    private string CachePath(string key)
        => Path.Combine(_cacheDir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".html");

    private async Task<string?> ReadCachedAsync(string key, CancellationToken ct)
    {
        try
        {
            var path = CachePath(key);
            if (!File.Exists(path)) return null;
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (bytes.Length < 8) return null;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - BitConverter.ToInt64(bytes, 0) > TtlSeconds) return null;
            return Encoding.UTF8.GetString(bytes, 8, bytes.Length - 8);
        }
        catch { return null; }
    }

    private async Task WriteCachedAsync(string key, string html, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            var body = Encoding.UTF8.GetBytes(html);
            var bytes = new byte[8 + body.Length];
            BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).CopyTo(bytes, 0);
            body.CopyTo(bytes, 8);
            await File.WriteAllBytesAsync(CachePath(key), bytes, ct);
        }
        catch { /* 缓存失败无妨——下次直连 */ }
    }

    /// <summary>带缓存取页面：缓存命中（TTL 内）直读；否则网络 + 回写</summary>
    private async Task<string?> GetPageAsync(string key, string url, CancellationToken ct)
        => await ReadCachedAsync(key, ct) ?? await FetchAsync(url, ct);

    private async Task<string?> FetchAsync(string url, CancellationToken ct)
    {
        try { return await Http.GetStringAsync(url, ct); }
        catch { return null; }
    }

    /// <summary>搜索结果条目：&lt;a target="_blank" href="https://www.mcmod.cn/class/{id}.html"&gt;{中文标题，可能含 &lt;em&gt; 高亮}&lt;/a&gt;。
    /// 8-22 修复：旧正则要求标题首个字符非 &lt;（`[^&lt;]{1,60}`）——MC百科对命中词用 &lt;em&gt; 包裹，
    /// 搜「钠」时 Sodium 本体标题是 `&lt;em&gt;钠&lt;/em&gt; (Sodium)` → 首字符是 &lt; → 整条被跳过
    /// （真机：钠本体永远不出现在结果里）。改为捕获到 &lt;/a&gt; 前再剥标签。</summary>
    private static readonly Regex EntryRegex = new(
        @"href=""https://www\.mcmod\.cn/class/(\d+)\.html""[^>]*>(.{1,120}?)</a>",
        RegexOptions.Compiled);

    /// <summary>剥 HTML 标签（&lt;em&gt; 高亮等）</summary>
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>详情页 Modrinth 外链：data-original-title="Modrinth" ... href="//link.mcmod.cn/target/{base64}"</summary>
    private static readonly Regex ModrinthLinkRegex = new(
        @"data-original-title=""Modrinth""[^>]*?href=""//link\.mcmod\.cn/target/([A-Za-z0-9+/=]+)""",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>详情页 CurseForge 外链（8-24 CF 中文搜索）：与 Modrinth 同构，data-original-title="CurseForge"。
    /// 实抓 2723.html 确认：base64 解出 https://www.curseforge.com/minecraft/mc-mods/{slug}（slug 形式非数字 id）。</summary>
    private static readonly Regex CurseForgeLinkRegex = new(
        @"data-original-title=""CurseForge""[^>]*?href=""//link\.mcmod\.cn/target/([A-Za-z0-9+/=]+)""",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>解析搜索结果页 → (条目 id, 中文标题) 列表</summary>
    public static List<(string ClassId, string Title)> ParseSearchResults(string html)
    {
        var list = new List<(string, string)>();
        foreach (Match m in EntryRegex.Matches(html))
        {
            var title = HtmlTagRegex.Replace(m.Groups[2].Value, "").Trim();
            if (title.Length == 0) continue;
            list.Add((m.Groups[1].Value, title));
        }
        return list;
    }

    /// <summary>解析详情页 → Modrinth slug（无 Modrinth 外链返回 null）</summary>
    public static string? DecodeModrinthSlug(string detailHtml)
    {
        var m = ModrinthLinkRegex.Match(detailHtml);
        if (!m.Success) return null;
        try
        {
            var url = Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value));
            var idx = url.IndexOf("/mod/", StringComparison.Ordinal);
            return idx < 0 ? null : TrimSlug(url[(idx + 5)..]);
        }
        catch { return null; }
    }

    /// <summary>解析详情页 → CurseForge slug（8-24；无 CF 外链返回 null）。base64 解出
    /// curseforge.com/minecraft/mc-mods/{slug}，取 slug 段（截断 ?/# 防 query 串污染）。</summary>
    public static string? DecodeCurseforgeSlug(string detailHtml)
    {
        var m = CurseForgeLinkRegex.Match(detailHtml);
        if (!m.Success) return null;
        try
        {
            var url = Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value));
            var idx = url.IndexOf("/mc-mods/", StringComparison.Ordinal);
            return idx < 0 ? null : TrimSlug(url[(idx + "/mc-mods/".Length)..]);
        }
        catch { return null; }
    }

    /// <summary>截断 query/fragment（?/# 后），并 trim 尾部斜杠/空白</summary>
    private static string TrimSlug(string slug)
    {
        var q = slug.IndexOfAny(['?', '#']);
        var s = q >= 0 ? slug[..q] : slug;
        return s.TrimEnd('/', ' ', '\t').Trim();
    }

    /// <summary>中文查询 → 候选列表 (Modrinth slug?, CurseForge slug?, 中文标题)（去重，上限 maxResults；
    /// 失败/无外链条目跳过）。8-24 加 CF slug：同一详情页同时解 Modrinth + CurseForge 外链，无额外网络。
    /// 8-19 生态修缮：搜索页 + 详情页磁盘缓存（TTL 24h）——重复搜索零网络（此前每次重打 ≤11 请求）</summary>
    public async Task<List<(string? MrSlug, string? CfSlug, string ChineseTitle)>> SearchCandidatesAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var searchUrl = $"https://search.mcmod.cn/s?key={Uri.EscapeDataString(query)}";
        var html = await GetPageAsync($"s:{query}", searchUrl, ct);
        if (html is null) return [];
        await WriteCachedAsync($"s:{query}", html, ct);

        var candidates = new List<(string?, string?, string)>();
        var seenMr = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenCf = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = ParseSearchResults(html).Take(maxResults).ToList();
        // 8-22 详情页并行解析（旧串行 10 条目 × 0.4-2s = 10s+ 干等，观感像死掉）；门 4 防打爆 mcmod
        using var gate = new SemaphoreSlim(4);
        var tasks = entries.Select(async entry =>
        {
            await gate.WaitAsync(ct);
            var detail = await GetPageAsync($"d:{entry.ClassId}",
                $"https://www.mcmod.cn/class/{entry.ClassId}.html", ct);
            if (detail is not null) await WriteCachedAsync($"d:{entry.ClassId}", detail, ct);
            return (Mr: detail is null ? null : DecodeModrinthSlug(detail),
                    Cf: detail is null ? null : DecodeCurseforgeSlug(detail),
                    entry.Title);
        }).ToArray();
        foreach (var t in tasks)
        {
            var (mr, cf, title) = await t;
            var mrOk = mr is not null && seenMr.Add(mr);
            var cfOk = cf is not null && seenCf.Add(cf);
            if (mrOk || cfOk)
                candidates.Add((mr, cf, title));
        }
        return candidates;
    }

    /// <summary>查询是否含中文（CJK）——中文搜索链路触发条件</summary>
    public static bool ContainsChinese(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        return query.Any(c => (uint)c is >= 0x4E00 and <= 0x9FFF);
    }
}

/// <summary>
/// 常见模组中英别名表（8-22，PCL 式精准搜索）：中文查询命中内置映射 → 直接查 Modrinth slug
/// （缓存秒回）——「钠」直接出 Sodium 本体，不依赖 MC百科解析（<em> 高亮/无外链都绕开了）。
/// 只收录 Modrinth 上存在的项目（OptiFine 等无 Modrinth 的不收——避免 404）。
/// </summary>
public static class ModAliasTable
{
    /// <summary>中文名 → Modrinth slug（多义时多条：如「小地图」→ Xaero 两个）</summary>
    private static readonly Dictionary<string, string[]> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["钠"] = ["sodium"],
        ["钠扩展"] = ["sodium-extra"],
        ["虹吸"] = ["iris"],
        ["简单语音"] = ["simple-voice-chat"],
        ["旅行地图"] = ["journeymap"],
        ["小地图"] = ["xaeros-minimap", "xaeros-world-map"],
        ["世界地图"] = ["xaeros-world-map"],
        ["苹果皮"] = ["appleskin"],
        ["动态fps"] = ["dynamic-fps"],
        ["帕秋莉"] = ["patchouli"],
        ["玉"] = ["jade"],
        ["连锁采集"] = ["vein-miner"],
        ["一键整理"] = ["inventory-sorter"],
        ["鼠标手势"] = ["mouse-tweaks"],
        ["铁氧体"] = ["ferrite-core"],
        ["锂"] = ["lithium"],
        ["磷"] = ["phosphor"],
        ["懒加载语言"] = ["lazy-language-loader"],
        ["模组菜单"] = ["modmenu"],
        ["布匹配置"] = ["cloth-config"],
        // 8-19 生态修缮：字典扩充（Modrinth 存在性已核对）
        ["机械动力"] = ["create"],
        ["暮色森林"] = ["twilight-forest"],
        ["匠魂"] = ["tconstruct"],
        ["jei"] = ["jei"],
        ["rei"] = ["roughly-enough-items"],
        ["投影"] = ["litematica"],
        ["迷你hud"] = ["minihud"],
        ["动态光源"] = ["lambdabetterlighting"],
        ["更好的f3"] = ["betterf3"],
        ["实体剔除"] = ["entityculling"],
        ["实体渲染优化"] = ["entityculling"],
        ["沉浸式传送门"] = ["immersive-portals"],
        ["夸克"] = ["quark"],
        ["预生成区块"] = ["chunk-pregenerator"],
        ["旅行者背包"] = ["travelersbackpack"],
        ["等价交换"] = ["projecte"],
        ["农夫乐事"] = ["farmers-delight"],
        ["幸运方块"] = ["lucky-block"],
        ["创世神"] = ["worldedit"],
        ["宝可梦"] = ["cobblemon"],
        ["滚轮整理"] = ["inventory-profiles-next"],
        // ---- 8-26 中文搜索对齐 Verse：批量扩充（slug 已逐个 Modrinth API 验证存在）----
        // ---- 性能 / 渲染 ----
        ["现代修复"] = ["modernfix"],
        ["高清截图"] = ["fabrishot"],
        ["区块缓存"] = ["c2me-fabric"],
        ["声音物理"] = ["sound-physics-remastered"],
        ["nvidia优化"] = ["nvidium"],
        // ---- 存储 / 容器 ----
        ["铁箱子"] = ["iron-chests"],
        ["铁炉子"] = ["iron-furnaces"],
        ["精妙背包"] = ["sophisticated-backpacks"],
        ["精妙存储"] = ["sophisticated-storage"],
        ["传送石碑"] = ["waystones"],
        ["墓碑"] = ["gravestone-mod"],
        ["搬运"] = ["carry-on"],
        ["随身物品栏"] = ["curios"],
        ["饰品栏"] = ["trinkets"],
        // ---- 科技 / 魔法 ----
        ["通用机械"] = ["mekanism"],
        ["植物魔法"] = ["botania"],
        ["血魔法"] = ["blood-magic"],
        ["龙之研究"] = ["draconic-evolution"],
        ["精致存储"] = ["refined-storage"],
        ["应用能源"] = ["ae2"],
        ["应用能源2"] = ["ae2"],
        ["科技复兴"] = ["techreborn"],
        ["沉浸工程"] = ["immersiveengineering"],
        // 星辉魔法/神秘时代 Modrinth 无该项目（API 404）——不收录，走 mcmod 兜底
        // ---- 世界 / 内容 ----
        ["以太"] = ["aether"],
        ["星系"] = ["galacticraft-legacy"],
        ["冰与火之歌"] = ["ice-and-fire-dragons"],
        ["拔刀剑"] = ["slashblade-resharped"],
        ["维克的现代战争"] = ["modern-warfare-cubed"],
        ["挖矿与砍杀"] = ["mine-and-slash"],
        ["神化"] = ["apotheosis"],
        // ---- 工具 / QoL ----
        ["背包宠物"] = ["inventory-pets"],
        ["经验之书"] = ["xp-book"],
        ["商店告示牌"] = ["sign-shop"],
        ["铁质工具"] = ["iron-tools"],
        ["更多结构"] = ["more-structures"],
        ["高级附魔"] = ["advanced-enchantments"],
        ["更好的第一人称"] = ["better-than-first-person"],
    };

    /// <summary>全部别名条目（中文名, slug[]）——ChineseNameCache 种子用（8-24）</summary>
    public static IEnumerable<(string Chinese, string[] Slugs)> AllEntries()
        => Map.Select(kv => (kv.Key, kv.Value));

    /// <summary>query 分词分隔符（空格/制表/中文逗号/英文逗号/顿号）——8-30 关键词搜索</summary>
    private static readonly char[] QuerySeperators = [' ', '\t', '，', ',', '、'];

    /// <summary>中文 query → 命中的别名 slug 列表。
    /// 8-30 关键词化：query 分词，任一词是别名键的子串即命中——「机械动力」少一字成「机械动」也能命中；
    /// 保留 8-19 多词并集语义（「钠 锂」→ sodium + lithium）。行为变化：短词命中更宽（「钠」同时命中 钠 与 钠扩展）。</summary>
    public static IReadOnlyList<string> Resolve(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var words = query.Split(QuerySeperators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<string>();
        foreach (var (key, slugs) in Map)
            if (words.Any(w => key.Contains(w, StringComparison.OrdinalIgnoreCase)))
                result.AddRange(slugs);
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>命中时显示的中文标题（取命中的键——「钠」→「钠 (Sodium)」；8-30 同步关键词子串语义）</summary>
    public static string TitleFor(string? query, string slug)
    {
        if (string.IsNullOrWhiteSpace(query)) return slug;
        var words = query.Split(QuerySeperators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var (key, slugs) in Map)
            if (slugs.Contains(slug, StringComparer.OrdinalIgnoreCase)
                && words.Any(w => key.Contains(w, StringComparison.OrdinalIgnoreCase)))
                return $"{key} ({slug})";
        return slug;
    }
}
