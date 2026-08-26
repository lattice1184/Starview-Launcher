using Launcher.Core.Model.Modrinth;
using Launcher.Core.Services;
using PCL.Core.Minecraft.ResourceProject.Curseforge;

namespace Launcher.Core.Ecosystem;

/// <summary>
/// ModDependencyResolver 与生态 API（Modrinth / CurseForge）的适配：
/// ProjectResolver 从各源拉项目版本并映射为依赖模型。
/// </summary>
public static class EcosystemDependencyAdapter
{
    /// <summary>创建 ProjectResolver（同步签名，内部同步等待——依赖数量少，可接受）</summary>
    public static Func<string, string, ModDependencyProject?> CreateResolver(
        EcosystemService eco, string? gameVersion, string? loader)
    {
        return (source, projectId) =>
        {
            try
            {
                // 8-26 改走镜像快路径：官方 project/version 端点 2-7s 抖动时这里静默返回 null →
                // 「前置不起作用」。镜像稳定 ~1.7s，失败回退官方；仍抛则记真实原因（见 ModDependencyResolver）
                var versions = eco.GetVersionsFastAsync(projectId, gameVersion, loader)
                    .GetAwaiter().GetResult();
                if (versions.Count == 0) return null;
                return new ModDependencyProject
                {
                    ProjectId = projectId,
                    Source = source,
                    Files = versions.Select(ToFile).ToList(),
                };
            }
            catch
            {
                return null;
            }
        };
    }

    private static ModDependencyFile ToFile(ModrinthVersion v) => new()
    {
        Id = v.Id,
        DisplayName = v.Name,
        Version = v.VersionNumber,
        GameVersions = v.GameVersions ?? [],
        Loaders = v.Loaders ?? [],
        ReleaseType = v.VersionType switch
        {
            "release" => 1,
            "beta" => 2,
            _ => 3,
        },
        ReleaseDate = v.DatePublished,
        RequiredDependencies = (v.Dependencies ?? [])
            .Where(d => d.DependencyType == "required" && d.ProjectId is not null)
            .Select(d => new ModDependencyReference
            {
                ProjectId = d.ProjectId!,
                Source = "modrinth",
                IsRequired = true,
            })
            .ToList(),
    };

    /// <summary>把 Modrinth 版本的依赖提取为请求输入</summary>
    public static List<ModDependencyReference> ToDependencyReferences(ModrinthVersion version) =>
        (version.Dependencies ?? [])
            .Where(d => d.DependencyType == "required" && d.ProjectId is not null)
            .Select(d => new ModDependencyReference
            {
                ProjectId = d.ProjectId!,
                Source = "modrinth",
                IsRequired = true,
            })
            .ToList();

    // ---------- CurseForge ----------

    /// <summary>创建 CurseForge ProjectResolver（同步签名，内部同步等待——依赖数量少，可接受）。
    /// 8-22 loader 透传：CF 依赖（如 JEI→cloth-config）匹配文件时按加载器过滤（SelectBestFile 兜底）</summary>
    public static Func<string, string, ModDependencyProject?> CreateResolver(
        CurseForgeService cf, string? gameVersion, string? loader = null)
    {
        return (source, projectId) =>
        {
            try
            {
                if (!int.TryParse(projectId, out var modId)) return null;
                var files = cf.GetFilesWithFallbackAsync(modId, gameVersion, default, loader).GetAwaiter().GetResult().Files;
                // 8-22 修复：resolver 侧按加载器剔除敌对变体（ToFile 置 Loaders=[] 使解析器无法过滤——
                // 否则双加载器依赖 malilib 等会把 neoforge 变体装进 fabric 实例）
                if (loader is not null)
                    files = files.Where(f => CurseForgeService.IsCompatibleWithLoader(f, loader)).ToList();
                if (files.Count == 0) return null;
                return new ModDependencyProject
                {
                    ProjectId = projectId,
                    Source = source,
                    Files = files.Select(ToFile).ToList(),
                };
            }
            catch
            {
                return null;
            }
        };
    }

    private static ModDependencyFile ToFile(CurseforgeFile f) => new()
    {
        Id = f.id.ToString(),
        DisplayName = f.displayName,
        Version = f.fileName,
        GameVersions = f.gameVersions ?? [],
        Loaders = [], // CF 无 loader 维度
        ReleaseType = f.releaseType,
        RequiredDependencies = (f.dependencies ?? [])
            .Where(d => d.relationType == 1) // 1=Required
            .Select(d => new ModDependencyReference
            {
                ProjectId = d.modId.ToString(),
                Source = "curseforge",
                IsRequired = true,
            })
            .ToList(),
    };

    /// <summary>把 CurseForge 文件的依赖提取为请求输入（relationType==1 必需）</summary>
    public static List<ModDependencyReference> ToDependencyReferences(CurseforgeFile file) =>
        (file.dependencies ?? [])
            .Where(d => d.relationType == 1)
            .Select(d => new ModDependencyReference
            {
                ProjectId = d.modId.ToString(),
                Source = "curseforge",
                IsRequired = true,
            })
            .ToList();
}
