using System.Net;
using System.Net.Http;
using System.Text.Json;
using Launcher.Core.Download;
using Launcher.Core.Ecosystem;
using Launcher.Core.Model.Modrinth;
using Launcher.Core.Utils;
using PCL.Core.Minecraft.ResourceProject.Curseforge;

namespace Launcher.Core.Services;

/// <summary>
/// 生态下载服务（CurseForge 源）：搜索 / 详情 / 文件匹配 / 安装到实例目录。
/// 依赖 x-api-key（设置页 CurseForgeApiKey 或环境变量 CURSEFORGE_API_KEY）；
/// 未配置时 IsEnabled=false（搜索返回空）。key 由 LauncherSettings 经 DPAPI 加密落盘（Secrets）。
/// 限流参考：官方 key 约 50 请求/30 秒，勿做深分页。
/// </summary>
public sealed class CurseForgeService
{
    public const string ApiBase = "https://api.curseforge.com/v1";
    private const int GameId = 432; // Minecraft

    private readonly string _apiBase;
    private readonly HttpClient _http;
    private readonly DownloadService _downloads;
    private readonly string _gameDirectory;
    private readonly string? _apiKeyOverride;

    /// <summary>是否启用 = 当前生效 key 非空（每次读设置/环境变量——设置页改 key 即时生效，无需重启）。
    /// key 由主进程 DPAPI 加密存设置（Secrets.Protect），不落明文磁盘。</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(EffectiveKey());

    /// <summary>当前生效 key：构造注入优先（null = 动态读设置/环境变量；空字符串 = 显式禁用），否则每次读设置（不再构造时缓存）</summary>
    private string? EffectiveKey() =>
        _apiKeyOverride is not null ? _apiKeyOverride : ResolveApiKey();

    /// <summary>8-16 检查结果日志（%AppData%\Launcher\logs\cf-check.log；只记状态/长度/异常，绝不记 key 内容）</summary>
    private static void LogCfCheck(string message)
    {
        try
        {
            var dir = Path.Combine(Launcher.Core.Utils.AppPaths.DataRoot, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "cf-check.log"),
                $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { /* 日志失败不阻塞 */ }
    }

    public CurseForgeService(HttpClient? http = null, DownloadService? downloads = null, string? gameDirectory = null,
        string? apiBase = null)
        : this(null, http, downloads, gameDirectory, apiBase) // null = 动态读设置/环境变量
    {
    }

    /// <summary>测试注入用：显式 key（null = 动态读设置；空字符串 = 禁用）；apiBase = 本地代理地址（key 由代理注入）</summary>
    public CurseForgeService(string? apiKey, HttpClient? http = null, DownloadService? downloads = null,
        string? gameDirectory = null, string? apiBase = null)
    {
        _apiKeyOverride = apiKey;
        _apiBase = apiBase ?? ApiBase;
        // 8-16 修复「检查 Key 超时」：共享池默认 HTTP/2，api.curseforge.com（CloudFront）的 h2 协商
        // 在国内网络实测挂起（15s 超时；HTTP/1.1 直连 0.4s 200）——CF 强制 HTTP/1.1，连接池照复用
        // 8-19 共享 handler 防销毁：CreateSharedClient（disposeHandler:false——此前默认 true，
        // 服务释放时把共享连接池销毁，后续请求报 disposed）。UA 已由工厂统一设置（含浏览器前缀 + 启动器标识）
        _http = http ?? CreateCurseClient();
        _downloads = downloads ?? new DownloadService();
        _gameDirectory = gameDirectory ?? GameDirectory.Detect();
    }

    /// <summary>8-19 CF 专用 client：共享 handler 防销毁 + 强制 HTTP/1.1（CloudFront h2 协商国内挂起，
    /// 原注释 8-16）+ 8s 超时（8-22：双源搜索等最慢源，CF 挂起时 15s 干等太久）</summary>
    private static HttpClient CreateCurseClient()
    {
        var client = HttpClientPool.CreateSharedClient(TimeSpan.FromSeconds(8));
        client.DefaultRequestVersion = HttpVersion.Version11;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        return client;
    }

    /// <summary>Key 解析：设置页优先（DPAPI 落盘），回退环境变量，最后内置混淆 key（开箱即用兜底）。
    /// 三级隔离：用户自填 key 即使被内置 key 牵连也不受影响（各自独立）。空 = 禁用。</summary>
    public static string? ResolveApiKey(LauncherSettings? s = null)
    {
        var fromSettings = (s ?? LauncherSettings.Current).CurseForgeApiKey;
        if (!string.IsNullOrWhiteSpace(fromSettings)) return fromSettings.Trim();
        var fromEnv = Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
        return BundledCfKey.Decode();
    }

    /// <summary>排序方式（CF sortField 为 1 基：1=Featured 2=Popularity 3=LastUpdated 6=TotalDownloads 11=ReleasedDate）</summary>
    public enum SortIndex { Relevance, Downloads, Newest, Updated }

    public static int SortFieldFor(SortIndex index) => index switch
    {
        SortIndex.Downloads => 6,   // TotalDownloads
        SortIndex.Newest => 11,     // ReleasedDate
        SortIndex.Updated => 3,     // LastUpdated
        _ => 1,                     // Featured ≈ 相关度
    };

    /// <summary>类型 → CF classId（mod=6 / modpack=4471 / resourcepack=12 / shader=6552）。
    /// 8-16 批次 54：Datapack → 0（CF 无数据包分类，UI 层已屏蔽 CF 源；兜底搜空防误当 mod）</summary>
    public static int ClassIdFor(ProjectType type) => type switch
    {
        ProjectType.Modpack => 4471,
        ProjectType.Resourcepack => 12,
        ProjectType.Shader => 6552,
        ProjectType.Datapack => 0,
        _ => 6,
    };

    /// <summary>搜索（classId 按类型过滤；gameVersion 字符串精确匹配；index 分页）</summary>
    public async Task<CurseForgeSearchPage?> SearchAsync(
        ProjectType type, string? query = null, string? gameVersion = null,
        SortIndex sort = SortIndex.Relevance,
        int limit = 20, int index = 0, CancellationToken ct = default)
    {
        if (!IsEnabled) return null;
        // 8-19 降级：CF 不认的版本号（26.2 年份格式）→ 400 或 200+空 → 自动不带版本重试（显示全部）
        // （无搜索词时 0 结果只可能是版本过滤所致；带搜索词 0 结果大概率词不匹配，不降级不误导）
        var (page, dropped) = await WithVersionFallbackAsync(gameVersion,
            gv => SearchCoreAsync(type, query, gv, sort, limit, index, ct),
            p => p is null || (string.IsNullOrEmpty(query) && p.Projects.Count == 0));
        return page is null ? null : new CurseForgeSearchPage(page.Projects, page.TotalCount, dropped);
    }

    private async Task<CurseForgeSearchPage?> SearchCoreAsync(
        ProjectType type, string? query, string? gameVersion, SortIndex sort, int limit, int index, CancellationToken ct)
    {
        var url = ToApiBase(BuildSearchUrl(type, query, gameVersion, sort, limit, index));
        var response = await GetJsonAsync<CurseforgeSearchResponse>(url, ct);
        if (response is null) return null;
        return new CurseForgeSearchPage(response.data ?? [], response.pagination?.totalCount ?? response.data?.Count ?? 0);
    }

    /// <summary>8-19 版本参数降级：CF 对非法 gameVersion 返回 400 **或 200+空列表**（26.2 年份号实测：files 返回空、search 忽略）
    /// → 自动不带版本重试一次（防循环：最多 2 请求）。isEmpty 判断结果是否为空（files 空 / 无搜索词时搜索 0 结果）</summary>
    private async Task<(T? Value, bool Dropped)> WithVersionFallbackAsync<T>(
        string? gameVersion, Func<string?, Task<T?>> call, Func<T?, bool>? isEmpty = null)
    {
        if (gameVersion is null) return (await call(null), false);
        try
        {
            var value = await call(gameVersion);
            if (isEmpty?.Invoke(value) == true)
                return (await call(null), true);
            return (value, false);
        }
        catch (CurseForgeApiException ex) when (ex.CfStatusCode == 400)
        {
            return (await call(null), true);
        }
    }

    /// <summary>
    /// 验证当前生效 key：调一次最小 search 请求。401/403 = 无效；其他状态码/网络错误 = 无法验证。
    /// 结果只含状态与 HTTP 码，**绝不包含 key 内容**（设置页填入后失焦即调，用于即时反馈）。
    /// </summary>
    public async Task<(bool Valid, string Message)> ValidateKeyAsync(CancellationToken ct = default)
    {
        if (!IsEnabled) return (false, "未配置 Key");
        try
        {
            var url = ToApiBase(BuildSearchUrl(ProjectType.Mod, null, null, SortIndex.Relevance, 1, 0));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-api-key", EffectiveKey());
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) { LogCfCheck($"通过（HTTP {(int)resp.StatusCode}）"); return (true, "Key 有效"); }
            var code = (int)resp.StatusCode;
            LogCfCheck($"拒绝（HTTP {code}，key 长度 {EffectiveKey()?.Length ?? 0}）");
            return (false, code is 401 or 403 ? $"Key 无效（HTTP {code}）" : $"验证失败（HTTP {code}）");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // 8-16 诊断版：带具体异常（超时/DNS/TLS 一眼可辨），排查「连不上」时别吞细节
            LogCfCheck($"异常（{ex.GetType().Name}: {ex.Message}）");
            return (false, $"无法连接 CurseForge API（{ex.GetType().Name}: {ex.Message}）");
        }
    }

    /// <summary>项目详情（含 logo / authors / 下载数）</summary>
    public async Task<CurseforgeProject?> GetProjectAsync(int modId, CancellationToken ct = default)
    {
        if (!IsEnabled) return null;
        var response = await GetJsonAsync<CurseforgeProjectResponse>($"{_apiBase}/mods/{modId}", ct);
        return response?.data;
    }

    /// <summary>文件列表（安装版本选择用，懒加载）；8-19 版本参数 400 → 自动降级返回全部文件</summary>
    public async Task<List<CurseforgeFile>> GetFilesAsync(int modId, string? gameVersion = null, CancellationToken ct = default)
    {
        var (files, _) = await GetFilesWithFallbackAsync(modId, gameVersion, ct);
        return files;
    }

    /// <summary>文件列表 + 版本过滤是否被丢弃（8-19：26.2 年份号 CF 返回 200+空 → 降级全量，Dropped=true 时调用方不得再按原版本过滤）。
    /// 8-22 实测：CF 的 gameVersionTypeId 参数对 26.x 新版 mod 不可靠——JEI 的 fabric 文件（jei-26.1.2-fabric-*.jar）用
    /// gameVersionTypeId=1 查询返回 0 条、neoforge 也只回远古文件（元数据未更新）→ 不再用请求参数过滤，
    /// loader 只留作调用方上下文（本地 SelectBestFile 按文件名过滤，实测可靠）</summary>
    public async Task<(List<CurseforgeFile> Files, bool Dropped)> GetFilesWithFallbackAsync(int modId, string? gameVersion, CancellationToken ct, string? loader = null)
    {
        return await WithVersionFallbackAsync(gameVersion, async gv =>
        {
            var url = $"{_apiBase}/mods/{modId}/files?pageSize=50"
                      + (gv is null ? "" : $"&gameVersion={Uri.EscapeDataString(gv)}");
            var response = await GetJsonAsync<CurseforgeFilesResponse>(url, ct);
            return response?.data ?? [];
        // 8-19 补：26.2 实测 files API 返回 200+空（非 400）——空列表也降级（否则详情页误报「没有适配版本」）
        }, files => files is null || files.Count == 0);
    }

    /// <summary>单文件详情（CF API 兜底：整合包 zip 内缺 jar 时按 projectID/fileID 拉取）</summary>
    public async Task<CurseforgeFile?> GetFileAsync(int modId, int fileId, CancellationToken ct = default)
    {
        if (!IsEnabled) return null;
        var response = await GetJsonAsync<CurseforgeFileResponse>($"{_apiBase}/mods/{modId}/files/{fileId}", ct);
        return response?.data;
    }

    /// <summary>匹配最佳文件：可用 + 版本兼容优先，releaseType=1（Release）优先，fileId 降序（近似"最新"）。
    /// 8-19 版本参数降级后不能再按原 gameVersion 过滤（CF 文件 gameVersions 不含 26.2——否则误报「没有适配文件」）。
    /// 8-22 loader：搜索页一键安装路径也要按加载器过滤（详情页已修，此调用点补齐）</summary>
    public async Task<CurseforgeFile?> FindBestFileAsync(int modId, string? gameVersion = null, CancellationToken ct = default, string? loader = null)
    {
        var (files, dropped) = await GetFilesWithFallbackAsync(modId, gameVersion, ct, loader);
        return SelectBestFile(files, dropped ? null : gameVersion, loader);
    }

    /// <summary>安装：下载文件到实例目录（mods/resourcepacks/shaderpacks），整合包到 downloads/modpacks。SHA1 幂等。
    /// gameDirOverride：版本来源目录（PCL/自建）——MOD 必须装进版本真实目录（AF2，与 Modrinth 侧对齐）。</summary>
    public async Task<string> InstallAsync(
        int projectId, CurseforgeFile file, string instanceId, ProjectType type,
        DownloadProgressHandler? progress = null, CancellationToken ct = default, string? gameDirOverride = null)
    {
        if (string.IsNullOrEmpty(file.downloadUrl))
            throw new InvalidOperationException("该文件没有下载地址");
        var targetDir = EcosystemService.ResolveInstallPath(gameDirOverride ?? _gameDirectory, instanceId, type);
        var destPath = Path.Combine(targetDir, Path.GetFileName(file.fileName));
        var sha1 = file.hashes?.FirstOrDefault(h => h.algo == 1)?.value; // CF algo: 1=SHA1 2=MD5
        // 8-24 竞速化：传原始 edge.forgecdn.net URL，镜像前缀由 CurseforgeCdnDlSourceMapper 在
        // ResolvingDlSourceMapper.Default 里映射为多候选（官方 vs 镜像）进 AL32 并行竞速（原 ApplyCdnPrefix
        // 单值替换已删——双路替换会冲突）。无镜像配置时单候选直连，行为不变。
        await _downloads.DownloadFileAsync(file.downloadUrl, destPath, sha1, file.fileLength, progress, ct);
        return destPath;
    }

    /// <summary>
    /// 安装主文件 + 解析并递归安装全部必需依赖（PCL2 式一键安装体验）。
    /// 依赖按解析器选定的文件安装；取不到时回退最佳文件。
    /// ctx 非空时主文件与每个依赖各成一个组子任务（下载中心可见、可暂停/重试）；
    /// 依赖并行安装（门 4——CF 限流 50 req/30s 的安全余量，原串行 10 依赖 = 20 次往返）。
    /// </summary>
    public async Task<DependencyInstallReport> InstallWithDependenciesAsync(
        int projectId, CurseforgeFile file, string instanceId, ProjectType type,
        string? gameVersion, string? loader = null,
        DownloadProgressHandler? progress = null, CancellationToken ct = default,
        string? gameDirOverride = null, DownloadGroupContext? ctx = null)
    {
        var report = new DependencyInstallReport();
        var projectIdText = projectId.ToString();

        // 1. 主文件
        try
        {
            var mainPath = await InstallOneAsync(ctx, $"主文件 {file.fileName}", file.fileLength,
                (p, c) => InstallAsync(projectId, file, instanceId, type, p, c, gameDirOverride), ct);
            report.Installed.Add(new InstalledDependency(projectIdText, file.id.ToString(), mainPath));
        }
        catch (Exception ex)
        {
            report.Failed.Add(new FailedDependency(projectIdText, ex.Message));
            return report;
        }

        // 2. 解析依赖树
        var resolver = new ModDependencyResolver();
        var request = new ModDependencyRequest
        {
            TargetMinecraftVersion = gameVersion ?? "",
            RequiredDependencies = EcosystemDependencyAdapter.ToDependencyReferences(file),
            // 8-22 补 loader：递归解析依赖的依赖时按加载器过滤（malilib 等双加载器库 mod 选错版本会装崩）
            ProjectResolver = EcosystemDependencyAdapter.CreateResolver(this, gameVersion, loader),
        };
        var result = resolver.Resolve(request);

        // 3. 依赖并行安装（依赖均为 MOD 类型，装到实例 mods 目录；结果收集加锁——多线程写 report）
        using var gate = new SemaphoreSlim(4);
        var depTasks = new List<Task>();
        foreach (var dep in result.ToInstall)
        {
            if (ct.IsCancellationRequested) break;
            depTasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (!int.TryParse(dep.ProjectId, out var depModId))
                    {
                        lock (report) report.Failed.Add(new FailedDependency(dep.ProjectId, "无效项目 ID"));
                        return;
                    }
                    // 8-19 补：GetFilesWithFallbackAsync 带 dropped——降级后不能再用 26.2 精确过滤（同 LoadCfAsync 修复）
                    // 8-22 补：loader 传递——依赖也要按加载器过滤（双加载器 mod 依赖混装 neoforge 变体会装错）
                    var (files, dropped) = await GetFilesWithFallbackAsync(depModId, gameVersion, ct, loader);
                    var depFile = files.FirstOrDefault(f => f.id.ToString() == dep.File.Id)
                                  ?? SelectBestFile(files, dropped ? null : gameVersion, loader);
                    if (depFile is null)
                    {
                        lock (report) report.Failed.Add(new FailedDependency(dep.ProjectId, "未找到兼容文件"));
                        return;
                    }
                    var path = await InstallOneAsync(ctx, $"依赖 {depFile.fileName}", depFile.fileLength,
                        (p, c) => InstallAsync(depModId, depFile, instanceId, ProjectType.Mod, p, c, gameDirOverride), ct);
                    lock (report) report.Installed.Add(new InstalledDependency(dep.ProjectId, depFile.id.ToString(), path));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* 组取消：其余任务一并终止 */ }
                catch (Exception ex)
                {
                    lock (report) report.Failed.Add(new FailedDependency(dep.ProjectId, ex.Message));
                }
                finally { gate.Release(); }
            }, ct));
        }
        await Task.WhenAll(depTasks);

        // 4. 未解析依赖
        foreach (var un in result.Unresolved)
            report.Failed.Add(new FailedDependency(un.ProjectId, un.Reason));

        return report;
    }

    /// <summary>安装单文件：有组上下文 → 子任务（下载中心可见）；否则直接装（测试/叶子调用兼容）</summary>
    private async Task<string> InstallOneAsync(DownloadGroupContext? ctx, string name, long weight,
        Func<DownloadProgressHandler, CancellationToken, Task<string>> work, CancellationToken ct)
    {
        if (ctx is null) return await work(null!, ct);
        string? path = null;
        var child = ctx.AddChild(name, weight, async (p, c) => { path = await work(p, c); });
        await child.Completion.WaitAsync(ct);
        return path ?? throw new InvalidOperationException($"{name} 未产生文件");
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        var key = EffectiveKey(); // 每次请求读最新 key——改 key 即时生效
        // AL50：5xx/404 瞬时故障（CloudFront 边缘抽风，实测偶发）自动重试一次——CF 官方限流是 429 不在此列
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(key))
                req.Headers.Add("x-api-key", key);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                // 8-19 容错：CF 对非法参数（如 26.2 年份版号）返回 200 + 错误 JSON（无 data）——
                // 直接 Deserialize 抛 JsonException → UI「匹配失败」；解析 CF 错误体转可读异常，
                // 结构不符（HTML/代理页）走通用文案。注意：错误 body 也能成功反序列化成 T（data=null）
                // ——Deserialize 成功后也必须显式检查 CF 错误体（data=null 的「空结果」≠ 合法空 data=[]）
                try
                {
                    var result = JsonSerializer.Deserialize<T>(json);
                    if (TryParseCfError(json, out var code, out var msg))
                        throw new CurseForgeApiException(code, $"CurseForge 请求失败：{msg}");
                    return result;
                }
                catch (JsonException)
                {
                    if (TryParseCfError(json, out var code, out var msg))
                        throw new CurseForgeApiException(code, $"CurseForge 请求失败：{msg}");
                    // 8-22 诊断：格式异常必留痕（URL + 状态码 + 响应前 160 字符）——实测 CF 侧永不产「格式异常」，
                    // 日志将暴露 App 实际请求 URL（疑似设置覆盖/中间层）与响应内容（HTML 拦截页/风控挑战页）
                    try { System.IO.File.AppendAllText(
                        System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cf-debug.log"),
                        $"[{DateTime.Now:HH:mm:ss}] CF GET {url} -> {(int)resp.StatusCode} ctype={resp.Content.Headers.ContentType?.MediaType} body={json[..Math.Min(160, json.Length)].Replace('\n', ' ')}\n"); } catch { }
                    // 8-22 200 + 非 JSON（CloudFront 边缘 HTML 错误页/WAF 拦截页）——瞬时故障重试一次自愈
                    if (attempt == 0)
                    {
                        await Task.Delay(500, ct);
                        continue;
                    }
                    throw new HttpRequestException("CurseForge 响应格式异常，请稍后重试");
                }
            }
            if (attempt == 0 && (int)resp.StatusCode is 404 or >= 500)
            {
                await Task.Delay(500, ct); // 半秒后重试一次（CF 边缘瞬时故障自愈）
                continue;
            }
            // 8-19 非 2xx 也读 body 提取 CF 错误消息（否则 400 只显示「Response status code does not indicate success」）
            try
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (TryParseCfError(body, out var code, out var msg))
                    throw new CurseForgeApiException(code, $"CurseForge 请求失败：{msg}");
            }
            catch (HttpRequestException) { throw; }
            catch { /* 读 body 失败不掩盖原错误 */ }
            resp.EnsureSuccessStatusCode(); // 其余（401/403/429/…）原样抛出
            return default;
        }
    }

    /// <summary>8-19 CF 错误体解析：camelCase {"statusCode":400,"error":...,"message":...}（与 PCL.Core 模型同款命名）</summary>
    private static bool TryParseCfError(string body, out int code, out string message)
    {
        code = 0;
        message = "";
        try
        {
            var err = JsonSerializer.Deserialize<CurseForgeError>(body);
            if (err is null || err.statusCode <= 0) return false;
            code = err.statusCode;
            message = err.message ?? err.error ?? "未知错误";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>8-19 CF 错误响应（camelCase 位置参数，直接匹配官方错误 JSON）</summary>
    private sealed record CurseForgeError(int statusCode, string? error, string? message);

    /// <summary>8-19 CF 拒绝异常（带状态码——降级重试识别 400 用；继承 HttpRequestException 保调用方兼容）</summary>
    public sealed class CurseForgeApiException(int cfStatusCode, string message) : HttpRequestException(message)
    {
        public int CfStatusCode { get; } = cfStatusCode;
    }

    /// <summary>
    /// 把静态 BuildSearchUrl 生成的官方地址切到目标 base（8-16 批次 52 三级优先）：
    /// ① 构造显式注入 apiBase（测试/嵌入场景）→ ② 设置页「CF API 地址覆盖」→ ③ 官方原样。
    /// 设置覆盖动态读取——改设置即时生效，无需重启。
    /// </summary>
    private string ToApiBase(string url)
    {
        if (_apiBase != ApiBase) return _apiBase + url[ApiBase.Length..]; // 显式注入优先
        var overridden = LauncherSettings.Current.CurseForgeApiBase;
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden.TrimEnd('/') + url[ApiBase.Length..];
        return url;
    }

    // ---------- 静态工具（离线可单测） ----------

    public static string BuildSearchUrl(ProjectType type, string? query, string? gameVersion, SortIndex sort, int limit, int index)
    {
        var url = $"{ApiBase}/mods/search?gameId={GameId}&classId={ClassIdFor(type)}";
        if (!string.IsNullOrEmpty(query))
            url += $"&searchFilter={Uri.EscapeDataString(query)}";
        if (gameVersion is not null)
            url += $"&gameVersion={Uri.EscapeDataString(gameVersion)}";
        url += $"&sortField={SortFieldFor(sort)}&sortOrder=desc&index={index}&pageSize={limit}";
        return url;
    }

    /// <summary>选最佳文件：可用 + 有下载地址；版本兼容优先（未知版本集合放行）；Release(1) 优先；fileId 降序。
    /// 8-22 loader 本地兜底：文件名带敌对加载器标记则排除（双加载器 mod 变体），无标记的通用文件放行——
    /// 与请求层 gameVersionTypeId 双保险（CF 参数语义最准，文件名兜底防参数失效）</summary>
    public static CurseforgeFile? SelectBestFile(IEnumerable<CurseforgeFile> files, string? gameVersion = null, string? loader = null)
    {
        var pool = files.Where(f => f.isAvailable && !string.IsNullOrEmpty(f.downloadUrl));
        if (gameVersion is not null)
            pool = pool.Where(f => f.gameVersions is null || f.gameVersions.Count == 0 || f.gameVersions.Contains(gameVersion));
        if (loader is not null)
            pool = pool.Where(f => IsCompatibleWithLoader(f, loader));
        return pool.OrderByDescending(f => loader is not null && NameMentionsLoader(f, loader)) // 明确标注目标加载器的最优先
                   .ThenByDescending(f => f.releaseType == 1)
                   .ThenByDescending(f => f.id)
                   .FirstOrDefault();
    }

    /// <summary>8-22 文件名是否明确标注目标加载器（fabric 文件带 "fabric"；无标记的通用文件/老版本排后面）。
    /// 8-22 修复：forge 目标时 neoforge 文件名也含 "forge" 子串——排除 neoforge 才对称</summary>
    private static bool NameMentionsLoader(CurseforgeFile f, string loader)
    {
        var name = (f.fileName ?? f.displayName ?? "").ToLowerInvariant();
        var target = loader.ToLowerInvariant();
        return target == "forge" ? name.Contains("forge") && !name.Contains("neoforge") : name.Contains(target);
    }

    /// <summary>8-22 文件名加载器判定：无标记放行；标记存在且不含目标则排除。注意 "neoforge" 含 "forge"——先判长词。
    /// 公开：依赖解析器（EcosystemDependencyAdapter.CreateResolver）侧也按此过滤敌对变体（resolver 无 Loader 维度）</summary>
    public static bool IsCompatibleWithLoader(CurseforgeFile f, string loader)
    {
        var name = (f.fileName ?? f.displayName ?? "").ToLowerInvariant();
        var target = loader.ToLowerInvariant();
        var hasNeo = name.Contains("neoforge");
        var hasForge = name.Contains("forge");
        var hasFabric = name.Contains("fabric");
        var hasQuilt = name.Contains("quilt");
        return target switch
        {
            "neoforge" => !hasFabric && !hasQuilt,
            // 8-22 修复：neoforge 文件名含 "forge" 子串（hasForge 恒真）——排除 neoforge 即对称
            "forge" => !hasFabric && !hasQuilt && !hasNeo,
            "fabric" => !hasNeo && !hasForge && !hasQuilt,
            "quilt" => !hasNeo && !hasForge && !hasFabric,
            _ => true
        };
    }
}

/// <summary>CF /files 响应包装（PCL.Core 缺 files 响应类型，本地补）</summary>
public sealed record CurseforgeFilesResponse(List<CurseforgeFile> data);

/// <summary>CF 单文件响应包装（/mods/{id}/files/{fileId}）</summary>
public sealed record CurseforgeFileResponse(CurseforgeFile? data);

/// <summary>CF /mods/search 响应（分页总数供 UI 分页栏）</summary>
public sealed record CurseforgeSearchPagination(int totalCount);

public sealed record CurseforgeSearchResponse(List<CurseforgeProject> data, CurseforgeSearchPagination? pagination);

/// <summary>搜索页结果（项目列表 + 总数；无分页信息时总数=当前页条数）</summary>
public sealed record CurseForgeSearchPage(List<CurseforgeProject> Projects, int TotalCount, bool VersionFilterDropped = false);
