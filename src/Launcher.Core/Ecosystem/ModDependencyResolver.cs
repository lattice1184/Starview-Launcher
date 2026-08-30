using System;
using System.Collections.Generic;
using System.Linq;

namespace Launcher.Core.Ecosystem;

public sealed record class ModDependencyReference
{
    public string ProjectId { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public bool IsRequired { get; init; } = true;
}

public sealed record class ModDependencyRequest
{
    public string TargetMinecraftVersion { get; init; } = string.Empty;
    public List<string> TargetLoaders { get; init; } = [];
    public List<ModDependencyReference> RequiredDependencies { get; init; } = [];
    public List<InstalledModIdentity> InstalledMods { get; init; } = [];
    public Func<string, string, ModDependencyProject?> ProjectResolver { get; init; } = (_, _) => null;
}

public sealed record class ModDependencyProject
{
    public string ProjectId { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string? ProjectName { get; init; }
    public List<ModDependencyFile> Files { get; init; } = [];
}

public sealed record class ModDependencyFile
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Version { get; init; }
    public List<string> GameVersions { get; init; } = [];
    public List<string> Loaders { get; init; } = [];
    public int ReleaseType { get; init; }
    public DateTime ReleaseDate { get; init; }
    public List<ModDependencyReference> RequiredDependencies { get; init; } = [];
}

public sealed record class InstalledModIdentity
{
    public string? SourceProjectId { get; init; }
    public string? Source { get; init; }
    public string? ModId { get; init; }
    public List<string> GameVersions { get; init; } = [];
    public List<string> Loaders { get; init; } = [];
}

public sealed record class ModDependencyResolutionResult
{
    public List<ResolvedDependencyInstall> ToInstall { get; } = [];
    public List<UnresolvedDependency> Unresolved { get; } = [];
    public List<IgnoredDependency> Satisfied { get; } = [];
}

public sealed record class ResolvedDependencyInstall
{
    public string ProjectId { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string? ProjectName { get; init; }
    public ModDependencyFile File { get; init; } = new();
}

public sealed record class UnresolvedDependency
{
    public string ProjectId { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed record class IgnoredDependency
{
    public string ProjectId { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed class ModDependencyResolver
{
    private const int MaxDepth = 32;
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public ModDependencyResolutionResult Resolve(ModDependencyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ProjectResolver);

        var context = new ResolutionContext(request);
        // 8-30 顶层依赖并行：每个依赖的 resolver 是网络密集（拉版本列表 1.5-7s），串行 N 个叠加
        // 拖慢（详情页前置提示 / 依赖安装的"读前置慢"主因）。context 变更加锁，resolver 调用在锁外；
        // 单依赖场景走原串行零开销（无线程切换）。
        if (request.RequiredDependencies.Count > 1)
        {
            Task.WaitAll(request.RequiredDependencies
                .Select(d => Task.Run(() => ResolveDependency(context, d, 0)))
                .ToArray());
        }
        else
        {
            foreach (var dependency in request.RequiredDependencies)
                ResolveDependency(context, dependency, 0);
        }

        return context.Result;
    }

    private static void ResolveDependency(ResolutionContext context, ModDependencyReference dependency, int depth)
    {
        if (string.IsNullOrWhiteSpace(dependency.ProjectId) || string.IsNullOrWhiteSpace(dependency.Source))
        {
            return;
        }

        // 并行下 context 可变状态（Visited/Result）必须锁；resolver 网络调用放锁外避免持锁等待
        lock (context)
        {
            if (!dependency.IsRequired)
            {
                context.AddSatisfied(dependency.ProjectId, dependency.Source, "Optional dependency ignored.");
                return;
            }

            if (depth > MaxDepth)
            {
                context.AddUnresolved(dependency.ProjectId, dependency.Source, "Maximum dependency depth exceeded.");
                return;
            }

            var visitedKey = context.GetVisitedKey(dependency.ProjectId, dependency.Source);
            if (!context.Visited.Add(visitedKey))
            {
                return;
            }

            if (context.IsInstalledCompatible(dependency.ProjectId, dependency.Source))
            {
                context.AddSatisfied(dependency.ProjectId, dependency.Source, "Already installed and compatible.");
                return;
            }
        }

        var project = context.Request.ProjectResolver(dependency.Source, dependency.ProjectId); // 网络密集，锁外
        if (project is null)
        {
            lock (context) { context.AddUnresolved(dependency.ProjectId, dependency.Source, "Dependency project was not found."); }
            return;
        }

        var selectedFile = SelectBestFile(project.Files, context.TargetMinecraftVersion, context.TargetLoaders);
        if (selectedFile is null)
        {
            lock (context) { context.AddUnresolved(project.ProjectId, project.Source, "No compatible file was found."); }
            return;
        }

        lock (context) { context.AddInstall(project, selectedFile); }

        foreach (var nestedDependency in selectedFile.RequiredDependencies)
        {
            ResolveDependency(context, nestedDependency, depth + 1);
        }
    }

    private static ModDependencyFile? SelectBestFile(
        IEnumerable<ModDependencyFile> files,
        string targetMinecraftVersion,
        HashSet<string> targetLoaders)
    {
        return files
            .Where(file => IsCompatibleFile(file, targetMinecraftVersion, targetLoaders))
            .OrderByDescending(file => HasExactGameVersionMatch(file, targetMinecraftVersion))
            .ThenByDescending(file => HasLoaderMatch(file, targetLoaders))
            .ThenBy(file => NormalizeReleaseType(file.ReleaseType))
            .ThenByDescending(file => file.ReleaseDate)
            .FirstOrDefault();
    }

    private static bool IsCompatibleFile(ModDependencyFile file, string targetMinecraftVersion, HashSet<string> targetLoaders)
    {
        // 8-19：年份号（26.2）或空 target 在 CF/Modrinth 文件版本（1.21.6 格式）中永不精确匹配——
        // 放宽为不要求版本（loader 过滤保留；SelectBestFile 排序仍精确优先→选最新构建）；
        // 传统 1.x target 严格匹配不变（1.20.4 不匹配必须失败——Resolve_VersionMismatch 锁定）
        if (targetMinecraftVersion.Length > 0
            && !Launcher.Core.Services.EcosystemService.IsYearFormatVersion(targetMinecraftVersion)
            && !HasExactGameVersionMatch(file, targetMinecraftVersion))
        {
            return false;
        }

        if (targetLoaders.Count == 0)
        {
            return true;
        }

        if (file.Loaders.Count == 0)
        {
            return true;
        }

        return file.Loaders.Any(loader => targetLoaders.Contains(loader));
    }

    private static bool HasExactGameVersionMatch(ModDependencyFile file, string targetMinecraftVersion)
    {
        return file.GameVersions.Any(version => Comparer.Equals(version, targetMinecraftVersion));
    }

    private static bool HasLoaderMatch(ModDependencyFile file, HashSet<string> targetLoaders)
    {
        if (targetLoaders.Count == 0)
        {
            return true;
        }

        return file.Loaders.Any(loader => targetLoaders.Contains(loader));
    }

    private static int NormalizeReleaseType(int releaseType)
    {
        return releaseType switch
        {
            1 => 1,
            2 => 2,
            3 => 3,
            _ => int.MaxValue,
        };
    }

    private sealed class ResolutionContext
    {
        private readonly HashSet<string> _installDedupe = new(Comparer);
        private readonly HashSet<string> _unresolvedDedupe = new(Comparer);
        private readonly HashSet<string> _satisfiedDedupe = new(Comparer);

        public ResolutionContext(ModDependencyRequest request)
        {
            Request = request;
            Result = new ModDependencyResolutionResult();
            Visited = new HashSet<string>(Comparer);
            TargetMinecraftVersion = request.TargetMinecraftVersion ?? string.Empty;
            TargetLoaders = new HashSet<string>(
                request.TargetLoaders.Where(static loader => !string.IsNullOrWhiteSpace(loader)),
                Comparer);
            LoaderSetKey = string.Join(",", TargetLoaders.OrderBy(static loader => loader, Comparer));
        }

        public ModDependencyRequest Request { get; }
        public ModDependencyResolutionResult Result { get; }
        public HashSet<string> Visited { get; }
        public string TargetMinecraftVersion { get; }
        public HashSet<string> TargetLoaders { get; }
        private string LoaderSetKey { get; }

        public string GetVisitedKey(string projectId, string source)
        {
            return $"{source}:{projectId}:{TargetMinecraftVersion}:{LoaderSetKey}";
        }

        public bool IsInstalledCompatible(string projectId, string source)
        {
            return Request.InstalledMods.Any(installed =>
                Comparer.Equals(installed.SourceProjectId, projectId)
                && Comparer.Equals(installed.Source, source)
                && installed.GameVersions.Any(version => Comparer.Equals(version, TargetMinecraftVersion))
                && LoadersCompatible(installed.Loaders));
        }

        public void AddInstall(ModDependencyProject project, ModDependencyFile file)
        {
            var dedupeKey = GetProjectKey(project.ProjectId, project.Source);
            if (!_installDedupe.Add(dedupeKey))
            {
                return;
            }

            Result.ToInstall.Add(new ResolvedDependencyInstall
            {
                ProjectId = project.ProjectId,
                Source = project.Source,
                ProjectName = project.ProjectName,
                File = file,
            });
        }

        public void AddUnresolved(string projectId, string source, string reason)
        {
            var dedupeKey = GetProjectKey(projectId, source);
            if (!_unresolvedDedupe.Add(dedupeKey))
            {
                return;
            }

            Result.Unresolved.Add(new UnresolvedDependency
            {
                ProjectId = projectId,
                Source = source,
                Reason = reason,
            });
        }

        public void AddSatisfied(string projectId, string source, string reason)
        {
            var dedupeKey = GetProjectKey(projectId, source);
            if (!_satisfiedDedupe.Add(dedupeKey))
            {
                return;
            }

            Result.Satisfied.Add(new IgnoredDependency
            {
                ProjectId = projectId,
                Source = source,
                Reason = reason,
            });
        }

        private bool LoadersCompatible(List<string> installedLoaders)
        {
            if (TargetLoaders.Count == 0 || installedLoaders.Count == 0)
            {
                return true;
            }

            return installedLoaders.Any(loader => TargetLoaders.Contains(loader));
        }

        private static string GetProjectKey(string projectId, string source)
        {
            return $"{source}:{projectId}";
        }
    }
}
