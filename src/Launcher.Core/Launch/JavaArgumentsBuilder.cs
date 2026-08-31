using System.Runtime.InteropServices;
using System.Text.Json;
using Launcher.Core.Model.Mojang;
using Launcher.Core.Utils;

namespace Launcher.Core.Launch;

/// <summary>
/// JVM 与游戏参数组装：从版本 JSON 展开 libraries（rules + natives）→ classpath，
/// 解析 inheritsFrom 链（加载器版本继承原版），发射 arguments.jvm / arguments.game，
/// classpath 统一在 JVM 参数末尾追加（加载器 json 中的 ${classpath} 由本构建器计算并过滤）。
/// </summary>
public sealed class JavaArgumentsBuilder
{
    private readonly RulesResolver _rules;

    public JavaArgumentsBuilder(RulesResolver? rules = null) => _rules = rules ?? new RulesResolver();

    /// <summary>启动档案：完整参数列表 + 供日志展示的参数快照</summary>
    public sealed record LaunchProfile(
        string JavaPath,
        string[] JvmArgs,
        string[] GameArgs,
        string WorkingDirectory,
        string ClassPath,
        string MainClass,
        string Log4jConfigPath,
        string NativesDirectory,
        string[] NativeJars);

    /// <summary>
    /// 构建启动档案。
    /// </summary>
    /// <param name="version">版本 JSON（已解析）</param>
    /// <param name="gameDir">游戏目录（.minecraft）</param>
    /// <param name="javaPath">Java 可执行文件完整路径</param>
    /// <param name="accountName">用户名（离线）</param>
    /// <param name="accountUuid">UUID（离线为 OfflinePlayer 哈希；正版为带横线 profile UUID）</param>
    /// <param name="accessToken">访问令牌（离线固定值；正版为 Minecraft token）</param>
    /// <param name="userType">账号类型（"legacy" = 离线 / "msa" = 正版微软账号——1.16+ 游戏读它决定认证模式）</param>
    /// <param name="memoryMb">内存上限 MB</param>
    /// <param name="extraJvmArgs">额外 JVM 参数（性能管线等，用户覆盖优先）</param>
    /// <param name="versionIsolation">版本隔离（game_directory 指向 versions/{id}，saves/mods 不串门）；null = 读设置</param>
    /// <param name="extraGameArgs">附加游戏参数（如一键进服 --server host --port N，追加在 arguments.game 之后）</param>
    /// <param name="skinUrl">8-19 皮肤纹理 URL（LittleSkin 等第三方账号）：填入 user_properties——
    /// 离线服（online-mode=false）服务端不验签，其他玩家从该 URL 拉取你的真实皮肤；null = 不传（"{}"）</param>
    public LaunchProfile Build(
        VersionJson version, string gameDir, string javaPath,
        string accountName, string accountUuid, string accessToken,
        long memoryMb, string[]? extraJvmArgs = null, bool? versionIsolation = null,
        string[]? extraGameArgs = null, string userType = "legacy", string? skinUrl = null)
    {
        // 0. inheritsFrom 链解析（Forge/NeoForge/Fabric 生成的 version.json 继承原版）
        var v = version;
        if (v.InheritsFrom is { } parentId)
        {
            v = VersionJsonMerger.ResolveChain(v, id => LoadParent(gameDir, id));
            if (v.InheritsFrom is { } unresolved)
                throw new ParentVersionMissingException(
                    $"加载器版本依赖的父版本 {unresolved} 未安装（请先在版本页安装原版 {unresolved}）");
        }

        // 防御：版本 id 拼入文件路径前净化（拒绝 .. 与分隔符）
        var safeId = v.Id.Replace("..", "").Replace('/', '_').Replace('\\', '_');
        var versionDir = Path.Combine(gameDir, "versions", safeId);
        var librariesDir = Path.Combine(gameDir, "libraries");
        var assetsDir = Path.Combine(gameDir, "assets");

        // 1. classpath：client jar + 过滤后的 libraries（同时收集 natives jar 供解压）
        var classPathParts = new List<string>
        {
            Path.Combine(versionDir, $"{safeId}.jar"),
        };
        var nativesJars = new List<string>();
        // AL27：同 group:artifact 只保留最后出现的（fabric loader 校验重复 ASM 拒绝启动——
        // 原版库 asm-9.6 + fabric 库 asm-9.10.1 冲突；继承链末尾是加载器自己的库，版本更新）
        var seenLibs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var lib in v.Libraries ?? [])
        {
            if (!_rules.IsAllowed(lib.Rules)) continue;
            // natives 判定：旧版 natives 字段映射 或 新版独立条目（classifier 以 natives- 开头且含 OS 名）
            var (isNative, nativeFullName, oldStyle) = ResolveNativeClassifier(lib);
            if (isNative && nativeFullName is not null)
            {
                var nativeName = Utils.MavenPath.FileName(nativeFullName);
                var relNative = Utils.MavenPath.DirectoryPath(nativeFullName).Replace('/', Path.DirectorySeparatorChar);
                var nativeJar = Path.Combine(librariesDir, relNative, nativeName);
                if (File.Exists(nativeJar)) nativesJars.Add(nativeJar);
                // 旧版（1.12.2 及以下）natives classifier jar 在 classpath；新版只解压
                if (oldStyle) classPathParts.Add(nativeJar);
                continue;
            }
            // 有 name 即按 maven 坐标推导进 classpath——PCL/第三方安装器 profile 的库无 downloads 字段
            // （fabric-loader 等加载器链会被旧逻辑整个跳过 → ClassNotFoundException，AK 修复）
            if (lib.Name is { Length: > 0 } libName)
            {
                var rel = Utils.MavenPath.FullPath(libName).Replace('/', Path.DirectorySeparatorChar);
                var path = Path.Combine(librariesDir, rel);
                var key = MavenKey(libName);
                if (seenLibs.TryGetValue(key, out var idx))
                    classPathParts[idx] = path; // 覆盖旧版本路径（保留继承链末尾的）
                else
                {
                    seenLibs[key] = classPathParts.Count;
                    classPathParts.Add(path);
                }
            }
        }
        var classPath = string.Join(Path.PathSeparator, classPathParts);

        // 2. natives 目录（启动前解压 dll）
        var nativesDir = Path.Combine(versionDir, $"{safeId}-natives");
        Directory.CreateDirectory(nativesDir);

        // 3. 共享 token（game/jvm 参数替换）
        var isolated = versionIsolation ?? LauncherSettings.Current.VersionIsolation;
        var tokens = BuildTokens(v, gameDir, assetsDir, nativesDir, accountName, accountUuid, accessToken, isolated, userType, skinUrl);

        // 4. 基础 JVM 参数
        var jvmArgs = new List<string>
        {
            $"-Xmx{memoryMb}m",
            "-XX:+UseG1GC",
            $"-Djava.library.path={nativesDir}",
            "-Dminecraft.launcher.brand=Starview",
            "-Dminecraft.launcher.version=0.1.0",
            "-Dlog4j.configurationFile=" + (v.Logging?.Client?.File?.Url is { } logUrl
                ? "file:///" + Path.Combine(assetsDir, "log_configs", Path.GetFileName(new Uri(logUrl).LocalPath)).Replace('\\', '/')
                : ""),
        };

        // 5. 版本 JSON 的 arguments.jvm（加载器专用：-p bootstraplauncher / --add-modules 等）
        AppendJsonArgs(jvmArgs, v.Arguments?.Jvm, tokens);

        // 6. 额外参数（用户覆盖优先）
        if (extraJvmArgs is not null) jvmArgs.AddRange(extraJvmArgs);

        // 7. classpath 统一末尾追加（跳过 json 中的 ${classpath}/-cp，避免重复）
        jvmArgs.Add("-cp");
        jvmArgs.Add(classPath);

        // 8. 游戏参数（附加参数追加在 arguments.game 之后，如一键进服的 --server/--port）
        var gameArgs = BuildGameArgs(v, tokens);
        if (extraGameArgs is { Length: > 0 }) gameArgs = [.. gameArgs, .. extraGameArgs];

        return new LaunchProfile(javaPath, [.. jvmArgs], gameArgs, gameDir, classPath,
            v.MainClass ?? "net.minecraft.client.main.Main", "", nativesDir, [.. nativesJars]);
    }

    /// <summary>追加 arguments.jvm：字符串或 {rules, value}；跳过 ${classpath}/-cp；与现有参数去重</summary>
    private void AppendJsonArgs(List<string> jvmArgs, List<JsonElement>? jsonArgs, Dictionary<string, string> tokens)
    {
        if (jsonArgs is null) return;
        foreach (var el in jsonArgs)
        {
            if (el.ValueKind == JsonValueKind.String)
                AddJvmArg(jvmArgs, ReplaceTokens(el.GetString()!, tokens));
            else if (el.ValueKind == JsonValueKind.Object)
            {
                var rules = el.GetProperty("rules").Deserialize<List<RuleJson>>();
                if (!_rules.IsAllowed(rules)) continue;
                var value = el.GetProperty("value");
                if (value.ValueKind == JsonValueKind.String)
                    AddJvmArg(jvmArgs, ReplaceTokens(value.GetString()!, tokens));
                else if (value.ValueKind == JsonValueKind.Array)
                    foreach (var val in value.EnumerateArray())
                        AddJvmArg(jvmArgs, ReplaceTokens(val.GetString()!, tokens));
            }
        }
    }

    private static void AddJvmArg(List<string> jvmArgs, string arg)
    {
        if (string.IsNullOrEmpty(arg)) return;
        if (arg.Contains("${classpath}", StringComparison.OrdinalIgnoreCase) || arg == "-cp") return;
        // java.library.path 与硬编码参数去重：版本 JSON 可能带子目录后缀（如 26.2 的 ${natives_directory}/java），
        // 统一使用本构建器计算的 natives 根目录（dll 平铺解压即可被找到）
        if (arg.StartsWith("-Djava.library.path=", StringComparison.OrdinalIgnoreCase)) return;
        // AL8 修复：裸选项（-p/--add-opens/--add-exports/--add-modules 等）是成对参数（选项+值），
        // 通用去重会吃掉第二个选项名 → 值错位 → ClassNotFoundException（TACZgun 崩溃）——
        // 去重只适用于自包含的 -D 参数（重复赋值无害，java 后者覆盖前者）
        if (arg.StartsWith("-D", StringComparison.OrdinalIgnoreCase))
        {
            if (!jvmArgs.Contains(arg)) jvmArgs.Add(arg);
            return;
        }
        jvmArgs.Add(arg);
    }

    private static Dictionary<string, string> BuildTokens(
        VersionJson version, string gameDir, string assetsDir, string nativesDir,
        string accountName, string accountUuid, string accessToken, bool isolated, string userType,
        string? skinUrl)
    {
        // 版本隔离：game_directory 指向 versions/{id}（saves/mods/options 各自独立）；
        // assets_root/game_assets 保持绝对指向共享 assets 目录
        var gameDirArg = isolated
            ? Path.Combine(gameDir, "versions", version.Id).Replace('\\', '/')
            : gameDir.Replace('\\', '/');
        var assetsIndexId = version.AssetIndex?.Id ?? version.Assets ?? "legacy";
        return new Dictionary<string, string>
        {
            ["auth_player_name"] = accountName,
            ["auth_uuid"] = accountUuid,
            ["auth_access_token"] = accessToken,
            ["auth_session"] = accessToken,
            ["version_name"] = version.Id,
            ["game_directory"] = gameDirArg,
            ["game_assets"] = Path.Combine(assetsDir, "legacy").Replace('\\', '/'),
            ["assets_root"] = assetsDir.Replace('\\', '/'),
            ["assets_index_name"] = assetsIndexId,
            // 8-19 皮肤透传（LittleSkin 等第三方）：offline 服其他玩家从 URL 拉取真实皮肤；
            // 正版服验签会忽略未签名 properties，无副作用
            ["user_properties"] = skinUrl is null ? "{}" : $"{{\"textures\":{{\"SKIN\":{{\"url\":\"{skinUrl}\"}}}}}}",
            // 8-13：按账号类型传——正版 msa（游戏走在线认证），离线 legacy；此前硬编码 legacy 正版账号也按离线跑
            ["user_type"] = userType,
            ["version_type"] = version.Type ?? "release",
            ["resolution_width"] = "854",
            ["resolution_height"] = "480",
            ["natives_directory"] = nativesDir,
            ["launcher_name"] = "Starview",
            ["launcher_version"] = "0.1.0",
            // Forge/NeoForge 1.17+ 安装器生成的 version.json 含 ${library_directory}（bootstraplauncher 路径）
            ["library_directory"] = Path.Combine(gameDir, "libraries").Replace('\\', '/'),
            // AL8：Forge 1.20+ 的 -p 模块路径用 ${classpath_separator} 连接模块 jar——缺失则整串
            // 未替换，java 模块系统把路径串当单一文件解析 → InvalidPathException（TACZgun 崩溃根因）
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            // AL8：1.20.1+ 官方/Forge json 的 game 参数带 ${clientid}/${auth_xuid}（官方启动器专属 token），
            // 缺失则原样传给游戏——离线用 0 安全
            ["clientid"] = "0",
            ["auth_xuid"] = "0",
        };
    }

    private string[] BuildGameArgs(VersionJson version, Dictionary<string, string> tokens)
    {
        // 新版：arguments.game（混合字符串与规则对象）
        if (version.Arguments?.Game is { } gameList)
        {
            var args = new List<string>();
            foreach (var el in gameList)
            {
                if (el.ValueKind == JsonValueKind.String)
                    args.Add(ReplaceTokens(el.GetString()!, tokens));
                else if (el.ValueKind == JsonValueKind.Object)
                {
                    var rules = el.GetProperty("rules").Deserialize<List<RuleJson>>();
                    if (_rules.IsAllowed(rules))
                    {
                        var value = el.GetProperty("value");
                        if (value.ValueKind == JsonValueKind.String)
                            args.Add(ReplaceTokens(value.GetString()!, tokens));
                        else if (value.ValueKind == JsonValueKind.Array)
                            foreach (var val in value.EnumerateArray())
                                args.Add(ReplaceTokens(val.GetString()!, tokens));
                    }
                }
            }
            return [.. args];
        }

        // 旧版：minecraftArguments 空格分割 + token 替换
        if (version.MinecraftArguments is { } legacy)
        {
            return legacy.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(a => ReplaceTokens(a, tokens))
                .ToArray();
        }

        return [];
    }

    /// <summary>Maven 坐标去重键：group:artifact（带 classifier 时含 classifier，避免 natives/普通撞键）</summary>
    private static string MavenKey(string name)
    {
        var parts = name.Split(':');
        return parts.Length >= 4 ? $"{parts[0]}:{parts[1]}:{parts[3]}" : parts.Length >= 2 ? $"{parts[0]}:{parts[1]}" : name;
    }

    /// <summary>解析 natives：(是否为 natives, 完整 Maven 名, 是否旧版样式)。旧版 natives 字段映射需拼 classifier；新版条目名自带。</summary>
    private (bool IsNative, string? NativeFullName, bool OldStyle) ResolveNativeClassifier(LibraryJson lib)
    {
        // 旧版：natives 字段按 OS 映射（classifier 同条目，进 classpath）
        if (lib.Natives is { } natives && natives.TryGetValue(_rules.OsName, out var mappedKey))
        {
            // 8-31 展开 ${arch}（老版本 twitch-platform 的 "windows":"natives-windows-${arch}"）——
            // 不展开 classpath 会出现字面量 ${arch} 路径，natives 解压后加载不到
            mappedKey = ExpandArch(mappedKey);
            return (true, lib.Name + ":" + mappedKey, true);
        }
        // 新版：独立条目名字带 :natives-xxx classifier（如 org.lwjgl:lwjgl-stb:3.3.1:natives-windows）。
        // 精确匹配 natives-{os}（防 natives-windows-arm64/x86 变体误判为当前架构的 natives）
        var parts = lib.Name.Split(':');
        if (parts.Length == 4 && parts[3].Equals($"natives-{_rules.OsName}", StringComparison.OrdinalIgnoreCase))
            return (true, lib.Name, false);
        return (false, null, false);
    }

    /// <summary>${arch} → "64"/"32"（x64/arm64 → 64，否则 32）；与 Download/PlatformNatives 一致</summary>
    private static string ExpandArch(string key)
    {
        if (!key.Contains("${arch}", StringComparison.Ordinal)) return key;
        var bits = RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64 ? "64" : "32";
        return key.Replace("${arch}", bits, StringComparison.Ordinal);
    }

    /// <summary>读磁盘上的父版本 JSON（版本页已下载的原版）</summary>
    private static VersionJson? LoadParent(string gameDir, string id)
    {
        var path = Path.Combine(gameDir, "versions", id, $"{id}.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(path)); }
        catch (Exception) { return null; }
    }

    private static string ReplaceTokens(string arg, Dictionary<string, string> tokens)
    {
        foreach (var (key, value) in tokens)
            arg = arg.Replace("${" + key + "}", value);
        return arg;
    }
}
