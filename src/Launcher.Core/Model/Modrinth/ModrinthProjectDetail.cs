using System.Text.Json.Serialization;

namespace Launcher.Core.Model.Modrinth;

/// <summary>项目详情（GET /v2/project/{id}），详情页数据源</summary>
public sealed record ModrinthProjectDetail(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("project_type")] string ProjectType,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("categories")] List<string>? Categories,
    [property: JsonPropertyName("versions")] List<string>? Versions,
    [property: JsonPropertyName("downloads")] long Downloads,
    [property: JsonPropertyName("follows")] long Follows,
    [property: JsonPropertyName("icon_url")] string? IconUrl,
    [property: JsonPropertyName("gallery")] List<ModrinthGalleryItem>? Gallery,
    [property: JsonPropertyName("game_versions")] List<string>? GameVersions,
    [property: JsonPropertyName("source_url")] string? SourceUrl,
    [property: JsonPropertyName("client_side")] string? ClientSide,
    [property: JsonPropertyName("server_side")] string? ServerSide,
    [property: JsonPropertyName("date_created")] DateTime DateCreated,
    [property: JsonPropertyName("date_modified")] DateTime DateModified,
    [property: JsonPropertyName("license")] ModrinthLicenseInfo? License);

/// <summary>许可证信息</summary>
public sealed record ModrinthLicenseInfo(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("url")] string? Url);

/// <summary>图库条目（8-26 修：Modrinth 返回对象数组 {title,description,url,featured}，旧模型误声明
/// List&lt;string&gt; → JsonException「$.gallery[0] 无法转成 String」→ 所有带 gallery 的项目
/// GetProjectAsync 反序列化失败 → 中文搜索快路径与 mcmod 兜底全挂（真机「搜钠卡 90s」根因）。</summary>
public sealed record ModrinthGalleryItem(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("featured")] bool Featured);
