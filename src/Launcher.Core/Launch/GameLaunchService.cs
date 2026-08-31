using System.IO.Compression;
using System.Text.Json;
using Launcher.Core.Diagnostics;
using Launcher.Core.Events;
using Launcher.Core.Launch.Sandbox;
using Launcher.Core.Model.Mojang;
using Launcher.Core.Utils;

namespace Launcher.Core.Launch;

/// <summary>
/// 游戏启动编排：读版本 JSON → 自动中文 → Java 选配 → 构建档案 → 启动进程。
/// </summary>
public sealed class GameLaunchService
{
    public async Task<LaunchProcess.LaunchResult> LaunchAsync(
        string versionId, string gameDirectory,
        string accountName, string accountUuid, string accessToken,
        long memoryMb, string[]? extraJvmArgs, string? javaPathOverride = null,
        Action<string>? onLog = null, Action<string>? onStage = null, CancellationToken ct = default,
        string[]? extraGameArgs = null, string userType = "legacy", string? skinUrl = null,
        SandboxMode sandboxMode = SandboxMode.Disabled)
    {
        // 8-31 插件钩子：启动开始事件（AppEvents 定义已久但从未发布——补发布，插件订阅做"启动前"扩展）
        AppEvents.Publish(new LaunchStartedEvent(versionId, DateTime.Now));
        // 1. 读版本 JSON
        onStage?.Invoke("解析版本");
        var vjPath = Path.Combine(gameDirectory, "versions", versionId, $"{versionId}.json");
        if (!File.Exists(vjPath))
            throw new FileNotFoundException($"版本 {versionId} 未安装（请先在版本页下载）");

        var version = JsonSerializer.Deserialize<VersionJson>(await File.ReadAllTextAsync(vjPath, ct))
            ?? throw new InvalidDataException($"版本 JSON 解析失败: {versionId}");

        // AL29 H5：启动前完整校验（client jar + 本 OS 实际需要的 libraries，沿链合并）——
        // 缺失在启动前报错（HomeViewModel 既有 catch 走修复指引 + AutoFix），不再 JVM 启动后
        // ClassNotFoundException/ZipException 崩溃。父 json 缺失时链保留 → 只查子自身 → 落到 Build 抛
        // ParentVersionMissingException（C2 路径）。
        var report = await AutoRepairService.VerifyVersionAsync(version, gameDirectory);
        if (!report.IsComplete)
            throw new FileNotFoundException(
                $"版本 {versionId} 文件不完整：缺 {report.Missing} 个文件（首例：{report.MissingFiles[0]}）。可点重新下载补全");

        // 2. 版本隔离：game_directory 指向 versions/{id}，启动前建 saves/mods 等子目录（Minecraft 不会自建）；
        //    自动中文写隔离后的目录（options.txt 各版本独立，不串门）
        var isolated = LauncherSettings.Current.VersionIsolation;
        var applyDir = isolated ? Path.Combine(gameDirectory, "versions", versionId) : gameDirectory;
        if (isolated)
        {
            foreach (var sub in new[] { "saves", "mods", "resourcepacks", "shaderpacks" })
                Directory.CreateDirectory(Path.Combine(applyDir, sub));
        }
        if (LauncherSettings.Current.AutoChineseEnabled) AutoChinese.Apply(applyDir);

        // 3. Java：设置指定路径优先，否则自动选配（PCL runtime / PATH）。
        //    版本 JSON 无 javaVersion 时按 MC 版本推断（<1.17 → Java 8；1.17+ → 17/21），避免旧版本误选 Java 21
        onStage?.Invoke("检测 Java");
        // AL10.2：Java 大版本解析共用 JavaSelector.ResolveRequiredMajor——自身 javaVersion 缺失时
        // 沿 InheritsFrom 链继承父版本（fabric/forge profile 无 javaVersion，继承原版如 26.2 → Java 25），
        // 链断则按 MC 版本号推断；服务端开服（PickServerJava）同用，避免"只读自身 json → 默认 17"启动即崩
        var requiredMajor = JavaSelector.ResolveRequiredMajor(version, id =>
        {
            var parentJson = Path.Combine(gameDirectory, "versions", id, $"{id}.json");
            if (!File.Exists(parentJson)) return null;
            try { return JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(parentJson)); }
            catch { return null; } // 父 json 损坏则继续链上推断
        });
        var java = javaPathOverride is { } ov && File.Exists(ov)
            ? ov
            : LauncherSettings.Current.JavaPath is { } custom && File.Exists(custom)
                ? custom
                // 8-31 缺 Java 自动补齐：macOS（尤其 Apple Silicon 出厂不带 Java）探测失败 → 自动下载
                // Mojang 官方运行时到 JavaSelector 扫描路径。失败由 EnsureJavaAsync 抛清晰异常。
                : await JavaProvisioningService.EnsureJavaAsync(requiredMajor, onStage, ct);

        // 4. 构建档案 + natives 解压 + log4j 兜底 + 启动进程
        onStage?.Invoke("解压 natives");
        var builder = new JavaArgumentsBuilder();
        var profile = builder.Build(version, gameDirectory, java,
            accountName, accountUuid, accessToken, memoryMb, extraJvmArgs,
            versionIsolation: null, extraGameArgs, userType, skinUrl);

        // AL8：启动命令写入日志（onLog → launch-*.log 首行）——崩溃/启动失败时未替换的占位符等根因一眼可见
        onLog?.Invoke("§ 启动命令：" + LaunchProcess.DescribeCommandLine(profile));

        // log4j 配置兜底：version.json 指定的文件缺失时写入标准模板
        EnsureLog4jConfig(version, gameDirectory);

        ExtractNatives(profile.NativeJars, profile.NativesDirectory, onLog);

        onStage?.Invoke("启动 JVM");
        var result = LaunchProcess.Start(profile, onLog, ct, LauncherSettings.Current.GamePriority, sandboxMode);
        onStage?.Invoke("拉起游戏窗口"); // 8-30 进程已拉起——HomeViewModel 的"拉起游戏窗口"阶段
        return result;
    }

    /// <summary>解压 natives：只提取原生库文件（.dll/.so/.dylib）平铺到 natives 根目录（忽略 jar 内目录结构）。
    /// AL9 提取为静态方法供自修复（AutoRepairService.FixNatives）复用；clearFirst 先清残留 dll。</summary>
    public static void ExtractNatives(string[] nativeJars, string nativesDirectory, Action<string>? onLog = null, bool clearFirst = false)
    {
        if (clearFirst)
        {
            try { if (Directory.Exists(nativesDirectory)) Directory.Delete(nativesDirectory, true); } catch { }
        }
        Directory.CreateDirectory(nativesDirectory);
        foreach (var nativeJar in nativeJars)
        {
            if (!File.Exists(nativeJar)) continue;
            try
            {
                using var archive = ZipFile.OpenRead(nativeJar);
                foreach (var entry in archive.Entries)
                {
                    var name = Path.GetFileName(entry.FullName);
                    if (entry.FullName.EndsWith('/') || name.Length == 0) continue;
                    if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        && !name.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
                        && !name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var dest = Path.Combine(nativesDirectory, name);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }
            catch (Exception ex) { onLog?.Invoke($"§ natives 解压警告 {Path.GetFileName(nativeJar)}: {ex.Message}"); }
        }
    }

    /// <summary>log4j 配置文件缺失时写入 Minecraft 标准模板（防 log4j 无配置告警）</summary>
    private static void EnsureLog4jConfig(VersionJson version, string gameDirectory)
    {
        if (version.Logging?.Client?.File is not { } logFile) return;
        var dir = Path.Combine(gameDirectory, "assets", "log_configs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Path.GetFileName(new Uri(logFile.Url).LocalPath));
        if (File.Exists(path)) return;
        File.WriteAllText(path, DefaultLog4jXml);
    }

    private const string DefaultLog4jXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Configuration status="WARN">
          <Appenders>
            <Console name="SysOut" target="SYSTEM_OUT">
              <PatternLayout pattern="[%d{HH:mm:ss}] [%t/%level] [%logger]: %msg%n"/>
            </Console>
            <Queue name="ServerGuiConsole">
              <PatternLayout pattern="[%d{HH:mm:ss}] [%t/%level] [%logger]: %msg%n"/>
            </Queue>
          </Appenders>
          <Loggers>
            <Root level="info">
              <filters>
                <MarkerFilter marker="NETWORK_PACKETS" onMatch="DENY" onMismatch="NEUTRAL"/>
              </filters>
              <AppenderRef ref="SysOut"/>
              <AppenderRef ref="ServerGuiConsole"/>
            </Root>
            <Logger name="com.mojang.authlib" level="info"/>
            <Logger name="net.minecraft" level="info"/>
          </Loggers>
        </Configuration>
        """;
}

