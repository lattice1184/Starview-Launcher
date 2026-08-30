using System.IO.Compression;
using System.Text.Json;
using Launcher.Core.Model.Loader;

namespace Launcher.Core.Download;

/// <summary>整合包格式（识别结果）</summary>
public enum ModpackFormat
{
    /// <summary>本启动器自家导出（manifest.json：name/mcVersion/loader/fileCount）</summary>
    Own,
    /// <summary>CurseForge 导出 zip（manifest.json：minecraft 对象 + files[] + modLoaders，mods 实体在 zip 内）</summary>
    CurseForge,
    /// <summary>Modrinth mrpack（modrinth.index.json：dependencies + files[] 直链，mods 需在线下载）</summary>
    Modrinth,
}

/// <summary>整合包导入信息（Parse 识别结果；自家格式旧字段原样保留）</summary>
public sealed record ModpackImportInfo(
    string VersionId,
    string McVersion,
    string? Loader,
    int FileCount,
    ModpackFormat Format = ModpackFormat.Own,
    IReadOnlyList<CurseForgeFileRef>? CurseForgeFiles = null,
    IReadOnlyList<ModrinthPackFile>? MrpackFiles = null,
    string? LoaderVersion = null,
    IReadOnlyList<MrpackModDependency>? ModDependencies = null);

/// <summary>CurseForge manifest files[] 条目（API 兜底下载用）</summary>
public sealed record CurseForgeFileRef(int ProjectId, int FileId, bool Required);

/// <summary>mrpack files[] 条目（downloads 直链，含 sha1/size）</summary>
public sealed record ModrinthPackFile(string Path, string Url, string? Sha1, long Size, bool ClientUnsupported);

/// <summary>mrpack dependencies[] 里非 minecraft/loader 的模组前置（8-26：如 fabric-api → Modrinth 版本 id，
/// 旧实现只取 minecraft + loader，模组类前置全丢 → 整合包「不下前置」）</summary>
public sealed record MrpackModDependency(string ProjectKey, string VersionId);

/// <summary>
/// 整合包导入：解析自家/CurseForge/Modrinth 三种 zip 格式，
/// 自家格式解压为隔离版本实例（InstallDir/versions/{id}）并写安装标记；
/// CF/mrpack 的内容安装由 ModpackInstaller 编排（本类只做解析 + 通用解压工具）。
/// </summary>
public sealed class ModpackImporter
{
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    /// <summary>整合包清单（自家格式；公开类型：System.Text.Json 反射要求 public）</summary>
    public sealed class ManifestJson
    {
        public string? Name { get; set; }
        public string? McVersion { get; set; }
        public string? Loader { get; set; }
        public int? FileCount { get; set; }
    }

    // ---------- 三格式识别 ----------

    /// <summary>解析 zip → 导入信息；不支持的格式返回 null 并给出原因</summary>
    public static ModpackImportInfo? Parse(string zipPath, out string? unsupportedReason)
    {
        unsupportedReason = null;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            // 8-28 大小写不敏感找索引（部分包用 Manifest.json 等大小写）
            var mrIndex = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase));
            if (mrIndex is not null)
                return ParseMrpack(mrIndex, out unsupportedReason);
            var manifest = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
            if (manifest is not null)
            {
                using var sr = new StreamReader(manifest.Open());
                var json = sr.ReadToEnd();
                if (TryParseCurseForge(json) is { } cf) return cf;
                if (TryParseOwn(json) is { } own) return own;
                unsupportedReason = "manifest.json 解析失败（既非本启动器导出格式，也非 CurseForge 格式）";
                return null;
            }
            // 8-28 诊断：无索引 → 看是不是「模组压缩包」，给更清楚的理由（保留「不支持」字样）
            var jarCount = zip.Entries.Count(e =>
                e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
            unsupportedReason = jarCount > 0
                ? $"不支持该整合包格式：像是模组压缩包（内含 {jarCount} 个 .jar），整合包根目录需 manifest.json 或 modrinth.index.json"
                : "未找到 manifest.json 或 modrinth.index.json（不支持该整合包格式）";
            return null;
        }
        catch (Exception ex)
        {
            unsupportedReason = $"读取失败: {ex.Message}";
            return null;
        }
    }

    /// <summary>自家格式：name +（mcVersion 字符串 或 fileCount）——与 CF（minecraft 对象）字段天然互斥</summary>
    private static ModpackImportInfo? TryParseOwn(string json)
    {
        var m = JsonSerializer.Deserialize<ManifestJson>(json, CaseInsensitive);
        if (m is null || string.IsNullOrEmpty(m.Name)) return null;
        return new ModpackImportInfo(m.Name, m.McVersion ?? "", m.Loader, m.FileCount ?? 0, ModpackFormat.Own);
    }

    /// <summary>CurseForge：manifest.json 含 minecraft 对象（version）+ files[]（projectID/fileID）+ modLoaders</summary>
    private static ModpackImportInfo? TryParseCurseForge(string json)
    {
        var m = JsonSerializer.Deserialize<CfManifestJson>(json, CaseInsensitive);
        if (m is null || string.IsNullOrEmpty(m.Name) || m.Minecraft is null) return null;
        var mc = m.Minecraft;
        if (string.IsNullOrEmpty(mc.Version)) return null;
        var files = (m.Files ?? []).Where(f => f.ProjectId > 0).Select(f => new CurseForgeFileRef(f.ProjectId, f.FileId, f.Required)).ToList();
        // loader：modLoaders[0].id（forge-47.1.0）优先；旧格式顶层 "forge": "47.1.0" 兜底
        (LoaderKind Kind, string? Version)? loader = null;
        if (mc.ModLoaders is { Count: > 0 } && !string.IsNullOrEmpty(mc.ModLoaders[0].Id))
            loader = ParseCfModLoader(mc.ModLoaders[0].Id);
        if (loader is null && m.Forge is not null)
            loader = (LoaderKind.Forge, m.Forge);
        return new ModpackImportInfo(
            m.Name, mc.Version, loader?.Kind.ToString().ToLowerInvariant(), files.Count,
            ModpackFormat.CurseForge, CurseForgeFiles: files, LoaderVersion: loader?.Version);
    }

    /// <summary>mrpack：modrinth.index.json（formatVersion 1/2、dependencies、files[] 直链）</summary>
    private static ModpackImportInfo? ParseMrpack(ZipArchiveEntry entry, out string? unsupportedReason)
    {
        unsupportedReason = null;
        using var sr = new StreamReader(entry.Open());
        var m = JsonSerializer.Deserialize<MrpackIndexJson>(sr.ReadToEnd(), CaseInsensitive);
        if (m is null || string.IsNullOrEmpty(m.Name))
        {
            unsupportedReason = "modrinth.index.json 解析失败";
            return null;
        }
        var deps = m.Dependencies ?? new Dictionary<string, string>();
        if (!deps.TryGetValue("minecraft", out var mc) || string.IsNullOrEmpty(mc))
        {
            unsupportedReason = "mrpack 缺少 minecraft 依赖版本";
            return null;
        }
        // loader 键：fabric-loader / quilt-loader / forge / neoforge（值可为具体版本或 "*"）
        (LoaderKind Kind, string? Version)? loader = null;
        foreach (var (key, value) in deps)
        {
            var kind = key switch
            {
                "fabric-loader" => (LoaderKind?)LoaderKind.Fabric,
                "quilt-loader" => (LoaderKind?)LoaderKind.Quilt,
                "forge" => (LoaderKind?)LoaderKind.Forge,
                "neoforge" => (LoaderKind?)LoaderKind.NeoForge,
                _ => null,
            };
            if (kind is not null)
            {
                loader = (kind.Value, value is "*" or "" ? null : value);
                break;
            }
        }
        var files = new List<ModrinthPackFile>();
        foreach (var f in m.Files ?? [])
        {
            if (string.IsNullOrEmpty(f.Path)) continue;
            // REVIEW-C：无 downloads 但有 sha1 的文件保留——导入时按 sha1 反查补直链
            // （自己导出的 mrpack downloads 为空，旧代码在此直接跳过 → 自导自入模组全丢）
            var hasSha1 = f.Hashes is not null && f.Hashes.TryGetValue("sha1", out _);
            if (f.Downloads is not { Count: > 0 } && !hasSha1) continue; // 无直链且无 sha1 → 无法解析，跳过
            files.Add(new ModrinthPackFile(
                f.Path, f.Downloads is { Count: > 0 } ? f.Downloads[0] : "",
                f.Hashes is not null && f.Hashes.TryGetValue("sha1", out var s) ? s : null,
                f.FileSize,
                f.Env?.Client == "unsupported"));
        }
        // 8-26 模组类前置（fabric-api 等非 minecraft/loader 键）：值 = Modrinth 版本 id，按版本直装。
        // 旧实现只取 minecraft + loader → 作者没把前置塞进 files[] 时「不下前置」。
        var modDeps = deps
            .Where(kv => kv.Key is not ("minecraft" or "fabric-loader" or "quilt-loader" or "forge" or "neoforge")
                         && !string.IsNullOrWhiteSpace(kv.Value) && kv.Value != "*")
            .Select(kv => new MrpackModDependency(kv.Key, kv.Value))
            .ToList();
        return new ModpackImportInfo(
            m.Name, mc, loader?.Kind.ToString().ToLowerInvariant(), files.Count,
            ModpackFormat.Modrinth, MrpackFiles: files, LoaderVersion: loader?.Version,
            ModDependencies: modDeps.Count > 0 ? modDeps : null);
    }

    // ---------- 静态工具（可单测） ----------

    /// <summary>CF modLoaders id → (LoaderKind, version)：forge-47.1.0 / fabricloader-0.15.0 / neoforge- / quiltloader-；未知前缀 null</summary>
    public static (LoaderKind Kind, string? Version)? ParseCfModLoader(string id)
    {
        foreach (var (prefix, kind) in new[]
                 {
                     ("fabricloader-", LoaderKind.Fabric), ("quiltloader-", LoaderKind.Quilt),
                     ("neoforge-", LoaderKind.NeoForge), ("forge-", LoaderKind.Forge),
                 })
        {
            if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (kind, id[prefix.Length..]);
        }
        return null;
    }

    /// <summary>实例 id 冲突消解：SafeId 后若 versions/{id} 已存在（或等于 sourceId——防止包名与预取父版本撞名），
    /// 追加 " (2)"、" (3)"…。确认框应展示最终实例名。</summary>
    public static string ResolvePackId(string gameDir, string name, string? sourceId = null)
    {
        var baseId = SafeId(name);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var versionsDir = Path.Combine(gameDir, "versions");
        if (Directory.Exists(versionsDir))
            foreach (var d in Directory.EnumerateDirectories(versionsDir))
                taken.Add(Path.GetFileName(d));
        if (sourceId is not null) taken.Add(sourceId);
        if (!taken.Contains(baseId)) return baseId;
        for (var n = 2; ; n++)
            if (!taken.Contains($"{baseId} ({n})")) return $"{baseId} ({n})";
    }

    /// <summary>版本 id 清洗：非法文件名字符替换为下划线（防路径注入/目录穿越/非法目录名）</summary>
    public static string SafeId(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch == '\\' || ch == '/' || invalid.Contains(ch)) sb.Append('_');
            else sb.Append(ch);
        }
        var id = sb.ToString().Trim();
        return string.IsNullOrEmpty(id) ? "modpack" : id;
    }

    /// <summary>通用解压子例程：include 过滤条目，prefixStrip 剥离前缀（返回 null = 命中排除前缀）；目录穿越防护。</summary>
    public static void ExtractZipEntries(ZipArchive zip, string versionDir, Func<string, bool> include,
        Func<string, string?>? prefixStrip, CancellationToken ct, Action<long>? onBytes = null)
    {
        var written = 0L;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue; // 目录条目由 ExtractToFile 隐式创建
            var rel = entry.FullName;
            if (prefixStrip is not null)
            {
                var stripped = prefixStrip(rel);
                if (stripped is null) continue;
                rel = stripped;
            }
            if (!include(rel)) continue;
            var dest = Path.GetFullPath(Path.Combine(versionDir, rel));
            if (!dest.StartsWith(versionDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue; // 目录穿越防护
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
            // 8-31 解压进度：累计已写字节上报（weight=0 时整合包进度不显示真实大小）
            if (onBytes is not null)
            {
                written += entry.Length;
                onBytes(written);
            }
        }
    }

    /// <summary>解压为隔离版本实例并写安装标记（自家格式：zip 内清单文件跳过；targetId 为空时用清单 name）。</summary>
    public static void Import(string zipPath, string gameDir, CancellationToken ct, string? targetId = null)
    {
        var info = Parse(zipPath, out _) ?? throw new InvalidDataException("不支持的整合包格式");
        var versionId = targetId ?? SafeId(info.VersionId);
        var versionDir = Path.Combine(gameDir, "versions", versionId);
        Directory.CreateDirectory(versionDir);

        using var zip = ZipFile.OpenRead(zipPath);
        ExtractZipEntries(zip, versionDir,
            rel => rel != "manifest.json" && rel != "modrinth.index.json", null, ct);

        InstallMarker.Mark(gameDir, versionId);
    }

    // ---------- CF / mrpack JSON 结构 ----------

    private sealed class CfManifestJson
    {
        public CfMinecraftJson? Minecraft { get; set; }
        public string? Name { get; set; }
        public List<CfFileJson>? Files { get; set; }
        public string? Forge { get; set; } // 旧格式顶层 "forge": "47.1.0"
    }

    private sealed class CfMinecraftJson
    {
        public string? Version { get; set; }
        public List<CfModLoaderJson>? ModLoaders { get; set; }
    }

    private sealed class CfModLoaderJson
    {
        public string? Id { get; set; }
    }

    private sealed class CfFileJson
    {
        public int ProjectId { get; set; }
        public int FileId { get; set; }
        public bool Required { get; set; }
    }

    private sealed class MrpackIndexJson
    {
        public int FormatVersion { get; set; }
        public string? Name { get; set; }
        public Dictionary<string, string>? Dependencies { get; set; }
        public List<MrpackFileJson>? Files { get; set; }
    }

    private sealed class MrpackFileJson
    {
        public string? Path { get; set; }
        public Dictionary<string, string>? Hashes { get; set; }
        public List<string>? Downloads { get; set; }
        public long FileSize { get; set; }
        public MrpackEnvJson? Env { get; set; }
    }

    private sealed class MrpackEnvJson
    {
        public string? Client { get; set; }
    }
}
