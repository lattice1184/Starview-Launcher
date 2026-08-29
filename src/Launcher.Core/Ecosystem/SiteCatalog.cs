namespace Launcher.Core.Ecosystem;

/// <summary>生态导航站点条目（mcnav 式内置精选清单——点击开浏览器）</summary>
public sealed record NavSite(string Name, string Description, string Url, string Category);

/// <summary>
/// 生态站点目录（8-16 批次 51：mcnav 无公开 API，启动器通行做法 = 精选站点硬编码内置）。
/// 11 类对齐 mcnav.net 的分类体系；纯静态数据，可单测。
/// </summary>
public static class SiteCatalog
{
    /// <summary>有序类别（与 Sites 里的 Category 一一对应）</summary>
    public static IReadOnlyList<string> Categories { get; } =
    [
        "官网", "启动器", "在线工具", "实用软件", "工作室", "服务器",
        "服务端", "百科", "社区", "资源", "面板",
    ];

    /// <summary>全量精选清单（每类 3-6 个，URL 均为 https）</summary>
    public static IReadOnlyList<NavSite> Sites { get; } =
    [
        // ---------- 官网 ----------
        new("Minecraft 官网", "游戏官网：购买、启动器、官方新闻", "https://www.minecraft.net", "官网"),
        new("Mojang", "开发商官网：账号与工作室动态", "https://www.mojang.com", "官网"),
        new("Java 下载", "游戏运行所需的 Java 官方下载", "https://www.java.com", "官网"),
        new("LittleSkin", "国内人气皮肤站：皮肤库与账号系统", "https://littleskin.cn", "官网"),

        // ---------- 启动器 ----------
        new("HMCL", "Hello Minecraft! Launcher，老牌开源启动器", "https://hmcl.huangyuhui.net", "启动器"),
        new("PCL2", "Plain Craft Launcher 2，操作手感优秀的启动器", "https://github.com/Hex-Dragon/PCL2", "启动器"),
        new("BakaXL", "新一代启动器，注重界面与扩展", "https://www.bakaxl.com", "启动器"),
        new("Prism Launcher", "开源启动器，注重实例管理与 Mod 支持", "https://prismlauncher.org", "启动器"),
        new("FCL", "Fold Craft Launcher，移动端启动器", "https://github.com/FCL-Team/FoldCraftLauncher", "启动器"),

        // ---------- 在线工具 ----------
        new("Chunkbase", "种子查询、结构定位、生物群系地图", "https://www.chunkbase.com", "在线工具"),
        new("Minecraft Tools", "合成表、命令生成等实用小工具", "https://minecraft.tools", "在线工具"),
        new("SkinMC", "皮肤预览与展示（Steve/Alex 3D 预览）", "https://skinmc.net", "在线工具"),
        new("MCSeedMap", "种子地图渲染与结构查看", "https://mcseedmap.net", "在线工具"),

        // ---------- 实用软件 ----------
        new("Iris Shaders", "现代光影加载器（Sodium 系）", "https://irisshaders.dev", "实用软件"),
        new("Geyser", "基岩版连 Java 版服务器的互通桥", "https://geysermc.org", "实用软件"),
        new("ViaVersion", "跨版本联机协议转换", "https://viaversion.com", "实用软件"),
        new("MCSManager", "开箱即用的面板类服务器管理工具", "https://mcsmanager.com", "实用软件"),

        // ---------- 工作室 ----------
        new("FTB", "Feed The Beast：经典整合包团队", "https://ftb.team", "工作室"),
        new("All The Mods", "大型整合包系列 ATM", "https://github.com/AllTheMods", "工作室"),
        new("Enigmatica", "知名整合包系列（仓库）", "https://github.com/EnigmaticaModpacks", "工作室"),

        // ---------- 服务器 ----------
        new("MineBBS 服务器", "MineBBS 服务器版块（找服/招人）", "https://www.minebbs.com/server/", "服务器"),
        new("Minecraft Server List", "国际服务器列表与统计", "https://minecraft-server-list.com", "服务器"),
        new("MC 转发", "服务器列表与收录", "https://mczfw.cn", "服务器"),

        // ---------- 服务端 ----------
        new("PaperMC", "高性能服务端 + 插件生态", "https://papermc.io", "服务端"),
        new("Fabric", "轻量模组加载器（服务端/客户端）", "https://fabricmc.net", "服务端"),
        new("NeoForge", "新一代 Forge 分支", "https://neoforged.net", "服务端"),
        new("Forge 文件", "Minecraft Forge 官方文件分发", "https://files.minecraftforge.net", "服务端"),
        new("SpigotMC", "经典插件服务端与资源社区", "https://www.spigotmc.org", "服务端"),
        new("Purpur", "Paper 分支，性能与配置增强", "https://purpurmc.org", "服务端"),

        // ---------- 百科 ----------
        new("中文 Minecraft Wiki", "官方中文百科（迁移后新站）", "https://zh.minecraft.wiki", "百科"),
        new("Minecraft Wiki", "英文官方百科", "https://minecraft.wiki", "百科"),
        new("MC 百科", "模组/整合包中文百科", "https://www.mcmod.cn", "百科"),
        new("MinePlugin", "插件百科与文档", "https://mineplugin.org", "百科"),
        new("LittleSkin 手册", "皮肤站用户手册与 API 文档", "https://manual.littleskin.cn", "百科"),

        // ---------- 社区 ----------
        new("CFPA 汉化组", "中文补丁计划：模组汉化资源库", "https://cfpa.site", "社区"),
        new("苦力怕论坛", "Klpbbs 综合社区", "https://klpbbs.com", "社区"),
        new("MCBBS 纪念版", "老 MCBBS 的延续社区", "https://www.mcbbs.co", "社区"),
        new("MineBBS", "综合性社区（资源/问答/开服）", "https://www.minebbs.com", "社区"),

        // ---------- 资源 ----------
        new("Modrinth", "现代模组平台（API 友好）", "https://modrinth.com", "资源"),
        new("CurseForge", "老牌模组/整合包平台", "https://www.curseforge.com", "资源"),
        new("Planet Minecraft", "全球玩家作品分享（皮肤/地图/建筑）", "https://www.planetminecraft.com", "资源"),

        // ---------- 面板 ----------
        new("MCSManager 面板", "免费开源的服务器面板（源码仓库）", "https://github.com/MCSManager/MCSManager", "面板"),
        new("Pterodactyl", "游戏服务器面板（开源）", "https://pterodactyl.io", "面板"),
        new("Crafty Controller", "网页控制面板（新手友好）", "https://craftycontrol.com", "面板"),
    ];

    /// <summary>按类别过滤（null/空 = 全部；未知类别返回空列表）</summary>
    public static IReadOnlyList<NavSite> ByCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category) || category == "全部") return Sites;
        return Sites.Where(s => s.Category == category).ToList();
    }
}
