using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Services;
using Launcher.Core.Model.Modrinth;
using Launcher.Core.Services;
using PCL.Core.Minecraft.ResourceProject.Curseforge;

namespace Launcher.App.ViewModels;

/// <summary>生态卡片 VM（图标异步加载，下载量格式化；来源：modrinth/curseforge）</summary>
public partial class ProjectCardVM : ObservableObject
{
    public string Id { get; }
    public string Title { get; }
    public string Author { get; }
    public string Description { get; }
    public string DownloadsText { get; }
    public string FollowsText { get; }
    public string UpdatedText { get; }
    public string IconUrl { get; }
    public ProjectType Type { get; }
    /// <summary>来源键：modrinth / curseforge</summary>
    public string Source { get; }
    /// <summary>来源显示名（卡片角标）</summary>
    public string SourceText { get; }
    /// <summary>是否显示关注数（CF 无关注字段）</summary>
    public bool ShowFollows => Source != "curseforge";
    public string Initial => Title.Length > 0 ? Title[..1] : "?";

    [ObservableProperty]
    public partial Bitmap? Icon { get; set; }

    /// <summary>收藏星标（FavoritesService 持久化）</summary>
    [ObservableProperty]
    public partial bool IsFavorite { get; set; }

    /// <summary>星标字符（★已收藏/☆未收藏）</summary>
    public string StarText => IsFavorite ? "★" : "☆";

    /// <summary>星标颜色（收藏=强调青，未收藏=弱灰）</summary>
    public IBrush StarColor => IsFavorite
        ? new SolidColorBrush(Color.Parse("#6C8CFF"))
        : new SolidColorBrush(Color.Parse("#6F7B90"));

    partial void OnIsFavoriteChanged(bool value)
    {
        OnPropertyChanged(nameof(StarText));
        OnPropertyChanged(nameof(StarColor));
    }

    [RelayCommand]
    private void ToggleFavorite()
    {
        FavoritesService.Toggle(Id);
        IsFavorite = !IsFavorite;
    }

    public ProjectCardVM(ModrinthSearchHit hit)
    {
        Id = hit.ProjectId;
        Source = "modrinth";
        SourceText = "Modrinth";
        Title = ChineseNameCache.Apply("mr:" + hit.Slug, hit.Title); // 8-24 命中本地缓存 → 英文（中文）
        Author = hit.Author;
        Description = hit.Description;
        DownloadsText = FormatCount(hit.Downloads);
        FollowsText = FormatCount(hit.Follows);
        UpdatedText = FormatDate(hit.DateModified);
        IconUrl = hit.IconUrl ?? "";
        Type = hit.ProjectType switch
        {
            "modpack" => ProjectType.Modpack,
            "resourcepack" => ProjectType.Resourcepack,
            "shader" => ProjectType.Shader,
            _ => ProjectType.Mod,
        };
        IsFavorite = FavoritesService.IsFavorite(Id);
        _ = ImageLoader.LoadAsync(IconUrl, bmp => Icon = bmp);
    }

    /// <summary>收藏列表构造（用项目详情；无描述/作者时取字段）</summary>
    public ProjectCardVM(ModrinthProjectDetail d)
    {
        Id = d.Id;
        Source = "modrinth";
        SourceText = "Modrinth";
        Title = ChineseNameCache.Apply("mr:" + d.Slug, d.Title); // 8-24 收藏/详情卡片也显示中文
        Author = "";
        Description = d.Description;
        DownloadsText = FormatCount(d.Downloads);
        FollowsText = FormatCount(d.Follows);
        UpdatedText = FormatDate(d.DateModified);
        IconUrl = d.IconUrl ?? "";
        Type = d.ProjectType switch
        {
            "modpack" => ProjectType.Modpack,
            "resourcepack" => ProjectType.Resourcepack,
            "shader" => ProjectType.Shader,
            _ => ProjectType.Mod,
        };
        IsFavorite = FavoritesService.IsFavorite(Id);
        _ = ImageLoader.LoadAsync(IconUrl, bmp => Icon = bmp);
    }

    /// <summary>CurseForge 搜索卡片（Id 带 cf- 前缀防收藏冲突；CF 无关注/更新日期字段）</summary>
    public ProjectCardVM(CurseforgeProject p)
    {
        Id = $"cf-{p.id}";
        Source = "curseforge";
        SourceText = "CurseForge";
        Title = ChineseNameCache.Apply("cf:" + p.slug, p.name); // 8-24 CF 卡片也显示中文（中文链路的 name 已中文，Apply 原样）
        Author = p.authors is { Count: > 0 } ? string.Join("、", p.authors.Select(a => a.name)) : "";
        Description = p.summary ?? "";
        DownloadsText = FormatCount(p.downloadCount);
        FollowsText = "";
        UpdatedText = "";
        IconUrl = p.logo?.thumbnailUrl ?? "";
        Type = p.classId switch
        {
            4471 => ProjectType.Modpack,
            12 => ProjectType.Resourcepack,
            6552 => ProjectType.Shader,
            _ => ProjectType.Mod,
        };
        IsFavorite = FavoritesService.IsFavorite(Id);
        _ = ImageLoader.LoadAsync(IconUrl, bmp => Icon = bmp);
    }

    /// <summary>解析卡片 Id → (来源, 原始 id)：cf- 前缀 → curseforge；否则 modrinth</summary>
    public static (string Source, string RawId) ParseId(string id)
        => id.StartsWith("cf-", StringComparison.Ordinal)
            ? ("curseforge", id[3..])
            : ("modrinth", id);

    /// <summary>下载量格式化：1234567 → 1.2M，12345 → 12.3K</summary>
    public static string FormatCount(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 1_000 => $"{n / 1_000.0:0.#}K",
        _ => n.ToString(),
    };

    /// <summary>最后更新时间："更新于 2026-07-20"（异常/默认值容错）</summary>
    private static string FormatDate(DateTime d)
        => d.Year > 2000 ? $"更新于 {d:yyyy-MM-dd}" : "";
}

/// <summary>目标版本实例（生态安装目标 / 主页启动选择）；SourceLabel 标识版本来源（PCL2/本启动器等）；GameDir 为版本所在游戏目录；LoaderBadge 为真实加载器徽章（fabric/forge/neoforge/quilt，AG1 检测）；McVersion 为加载器版本继承的原版版本号</summary>
public sealed record VersionInstanceVM(string Name, string SourceLabel = "", string GameDir = "", string LoaderBadge = "", string McVersion = "")
{
    /// <summary>
    /// 显示名：加载器版本 → "26.2 (Fabric)"，原版 → "26.2 (原版)"（8-16 批次 53：主页下拉
    /// 「自配原版 vs 带加载器版」并列是 AL27 刻意设计——原版必须保留可选；标注后一眼区分），附来源标签。
    /// </summary>
    public string DisplayName
    {
        get
        {
            var core = LoaderBadge.Length > 0
                ? $"{(McVersion.Length > 0 ? McVersion : Name)} ({Cap(LoaderBadge)})"
                : $"{Name} (原版)";
            return SourceLabel.Length > 0 ? $"{core} · {SourceLabel}" : core;
        }
    }

    private static string Cap(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;
}
