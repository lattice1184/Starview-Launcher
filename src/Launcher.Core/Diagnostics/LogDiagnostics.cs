using System.Text.RegularExpressions;

namespace Launcher.Core.Diagnostics;

/// <summary>修复动作分类（AL9 自修复引擎）：AdviceOnly=仅建议；Redownload=版本文件补全重下；ReExtractNatives=重解压 natives；
/// RetryDownload=下载自动重试一次；RestartService=重启联机服务；ReinstallModule=重装联机模块；CheckNetwork=网络建议（建议类）</summary>
public enum FixKind { AdviceOnly, Redownload, ReExtractNatives, DisableConflictingMods, RetryDownload, RestartService, ReinstallModule, CheckNetwork }

/// <summary>单条诊断命中（结构化，供自修复引擎与崩溃窗诊断区使用）</summary>
public sealed record DiagnosticHit(string Snippet, string Explanation, FixKind Fix)
{
    /// <summary>是否可执行修复动作（UI「一键修复」按钮显隐）；CheckNetwork/AdviceOnly 属建议类</summary>
    public bool IsAutoFixable => Fix is FixKind.Redownload or FixKind.ReExtractNatives or FixKind.DisableConflictingMods or FixKind.RetryDownload or FixKind.RestartService or FixKind.ReinstallModule;
}

/// <summary>
/// 日志动态诊断：按实际日志内容正则匹配已知错误模式，逐条补中文说明与建议。
/// AL9：规则升级为带 FixKind 修复分类，供自动修复（AutoRepairService）与崩溃窗诊断区使用。
/// 共用方：导出报告（LogExportHelper 生成 诊断说明.txt）+ 服务端异常退出弹窗（ServerViewModel）+ HomeViewModel 自修复。
/// 扩展方式：往 Patterns 追加 (正则, 中文说明, FixKind) 即可。
/// </summary>
public static class LogDiagnostics
{
    private static readonly (Regex Re, string Explanation, FixKind Fix)[] Patterns =
    [
        (new Regex(@"OutOfMemoryError|Java heap space", RegexOptions.IgnoreCase),
            "内存不足（Java 堆溢出）：分配的内存不够用。可在设置页调高「内存分配」，或关闭占用内存大的程序。",
            FixKind.AdviceOnly),
        (new Regex(@"Failed to allocate memory|Native memory allocation \(mmap\) failed|Cannot allocate memory", RegexOptions.IgnoreCase),
            "系统内存不足：物理内存不够分配。请关闭其他程序后重试，或调低内存分配。",
            FixKind.AdviceOnly),
        (new Regex(@"UnsupportedClassVersionError|class file version \d+\.\d+ is invalid", RegexOptions.IgnoreCase),
            "Java 版本过低：该版本需要更高版本的 Java。请在设置页更换新版 Java 路径。",
            FixKind.AdviceOnly),
        (new Regex(@"Could not create the Java Virtual Machine|Unable to start the JVM", RegexOptions.IgnoreCase),
            "JVM 创建失败：内存参数或 Java 安装异常。检查「内存分配」与 Java 路径。",
            FixKind.AdviceOnly),
        (new Regex(@"Invalid maximum heap size|Invalid initial heap size", RegexOptions.IgnoreCase),
            "内存参数无效：分配值超出上限或格式错误。请调整「内存分配」。",
            FixKind.AdviceOnly),
        (new Regex(@"Could not find or load main class", RegexOptions.IgnoreCase),
            "主类加载失败：版本文件或启动参数损坏。将自动重新下载补全该版本。",
            FixKind.Redownload),
        (new Regex(@"UnsatisfiedLinkError|Could not load library|Failed to extract natives|no \w+ in java\.library\.path", RegexOptions.IgnoreCase),
            "本地库（natives）加载失败：文件缺失或损坏。将自动重新解压 natives。",
            FixKind.ReExtractNatives),
        (new Regex(@"Exception in thread .* GLFW|GLFW error|Failed to init GLFW", RegexOptions.IgnoreCase),
            "图形窗口初始化失败（GLFW）：显卡驱动或窗口环境异常。更新显卡驱动后重试。",
            FixKind.AdviceOnly),
        (new Regex(@"OpenGL.*(not supported|error|failed)|Error creating GL context", RegexOptions.IgnoreCase),
            "OpenGL 创建失败：显卡驱动过旧或不支持所需版本。请更新显卡驱动。",
            FixKind.AdviceOnly),
        (new Regex(@"Unexpected error while creating framebuffer|Draw buffers \[\d+, \d+\] Status", RegexOptions.IgnoreCase),
            "渲染帧缓冲创建失败：常见于 Iris 光影与显卡驱动冲突。可尝试关闭光影或更新显卡驱动。",
            FixKind.AdviceOnly),
        (new Regex(@"Missing or unsupported mandatory dependencies|Could not find required mod|requires .* that is missing", RegexOptions.IgnoreCase),
            "模组依赖缺失或不兼容：缺少依赖模组或版本冲突。请补全依赖或移除冲突模组。",
            FixKind.AdviceOnly),
        // 8-23 模组版本不匹配游戏版本：Fabric 启动器「Incompatible mods found」（如 iris/sodium 是 1.21 版配 26.1 游戏、malilib/tweakeroo 要 26.2）
        // 自动修复=禁用冲突模组 jar（.jar→.jar.disabled，可在版本页重新启用），让游戏能启动。
        (new Regex(@"Incompatible mods found|Some of your mods are incompatible|is not compatible with the game|to a compatible version", RegexOptions.IgnoreCase),
            "模组版本与游戏版本不匹配（如模组是 1.21 版、游戏是 26.1；或模组要求 26.2 而装了 26.1）。将自动禁用这些冲突模组，游戏即可启动（可在版本页重新启用或换适配版本）。",
            FixKind.DisableConflictingMods),
        (new Regex(@"BindException|Address already in use|Port \d+ was already in use", RegexOptions.IgnoreCase),
            "端口被占用：服务端口已被其他程序（或另一个服务端）占用。修改 server.properties 的 server-port 后重试。",
            FixKind.AdviceOnly),
        (new Regex(@"Segmentation fault|SIGSEGV", RegexOptions.IgnoreCase),
            "程序段错误崩溃：底层崩溃，多为驱动或内存问题。尝试更新驱动或降低渲染设置。",
            FixKind.AdviceOnly),
        (new Regex(@"java\.lang\.NoClassDefFoundError", RegexOptions.IgnoreCase),
            "缺少类定义：模组或库文件损坏/缺失。将自动重新下载补全该版本。",
            FixKind.Redownload),
        (new Regex(@"A fatal error has been detected by the Java Runtime Environment", RegexOptions.IgnoreCase),
            "JVM 致命错误（hs_err）：底层崩溃，多为驱动或硬件问题。可将崩溃文件一并反馈。",
            FixKind.AdviceOnly),
        (new Regex(@"The required mods are missing|It appears .* did not load correctly|Failed to load mod", RegexOptions.IgnoreCase),
            "模组加载失败：模组文件损坏或与当前版本不兼容。请检查最近安装的模组。",
            FixKind.AdviceOnly),
        (new Regex(@"Invalid session|Failed to verify username", RegexOptions.IgnoreCase),
            "会话校验失败：服务端仍按正版模式运行。若已在 server.properties 关闭正版验证（online-mode=false），必须重启服务端才生效（配置只在启动时读取一次）；若玩家用的是离线客户端，请确认服务端是离线模式。",
            FixKind.AdviceOnly),
        (new Regex(@"Unable to delete file|FileSystemException|Being used by another process|另一个程序已锁定|The process cannot access the file", RegexOptions.IgnoreCase),
            "日志文件被占用：服务端启动时要删除旧的 latest.log，但文件被其他程序锁定——最常见是上一个服务端进程未完全退出（任务管理器结束残留的 java.exe），或你正用编辑器打开着日志。关闭占用后重试。",
            FixKind.AdviceOnly),
        (new Regex(@"java\.lang\.ClassNotFoundException", RegexOptions.IgnoreCase),
            "类加载失败：版本所需库文件未进入启动类路径（常见于 PCL/第三方安装器生成的版本，加载器 jar 缺失）。将自动重新下载补全该版本。",
            FixKind.Redownload),
        // AL9 新增：加载器主类/服务端 jar 缺失/损坏（此前仅靠"Could not find main class"近似兜底）
        (new Regex(@"net\.fabricmc\.loader\.impl\.launch\.knot\.KnotClient|cpw\.mods\.bootstraplauncher\.BootstrapLauncher", RegexOptions.IgnoreCase),
            "加载器主类未找到：加载器库文件缺失。将自动重新下载补全该版本。",
            FixKind.Redownload),
        (new Regex(@"Unable to access jarfile|Could not open input file|Error: Unable to access", RegexOptions.IgnoreCase),
            "jar 文件打不开：文件缺失或路径错误。将自动重新下载补全该版本。",
            FixKind.Redownload),
        (new Regex(@"Failed to load main manifest attribute|Invalid or corrupt jarfile|ZipException: invalid", RegexOptions.IgnoreCase),
            "jar 文件损坏：下载不完整或磁盘错误。将自动重新下载补全该版本。",
            FixKind.Redownload),
        // AL43：Realms 网络错误——非致命（单机/局域网不受影响），真机 08-09 被杀日志常见，此前零命中导致诊断区空
        (new Regex(@"Realms service error|Couldn't connect to realms|Failed to fetch Realms feature flags|Realms authentication error|无法连接至Realm", RegexOptions.IgnoreCase),
            "Realms 服务连接失败（网络或账号问题），不影响单机/局域网游戏，可忽略。",
            FixKind.AdviceOnly),
    ];

    /// <summary>
    /// AL43 退出码诊断：DiagnoseDetailed 命中保留；无命中时按退出码补「人话」解释——
    /// -1（进程被终止）给被终止说明，其余退出码给兜底。保证崩溃弹窗诊断区永远有内容。
    /// </summary>
    public static List<DiagnosticHit> DiagnoseExit(int exitCode, string logText)
    {
        var hits = DiagnoseDetailed(logText);
        if (hits.Count > 0) return hits;
        if (exitCode == -1)
        {
            return
            [
                new DiagnosticHit($"exitStatus=-1",
                    "游戏进程被强制终止（退出码 -1）：常见于手动关闭窗口、杀毒软件误杀、系统强制结束。若日志无崩溃错误而进程中途消失，多半是被终止而非崩溃；反复出现请检查杀毒软件是否拦截了游戏进程。",
                    FixKind.AdviceOnly),
            ];
        }
        return
        [
            new DiagnosticHit($"exitStatus={exitCode}",
                "未检测到已知崩溃模式——可能是进程被外部终止或环境问题。可尝试重新启动游戏，若反复出现请导出报告反馈。",
                FixKind.AdviceOnly),
        ];
    }

    /// <summary>对日志文本结构化诊断：返回命中列表（同模式只报一次，按规则顺序）</summary>
    public static List<DiagnosticHit> DiagnoseDetailed(string logText)
    {
        var result = new List<DiagnosticHit>();
        if (string.IsNullOrWhiteSpace(logText)) return result;
        var seen = new HashSet<int>();
        for (var i = 0; i < Patterns.Length; i++)
        {
            var (re, explanation, fix) = Patterns[i];
            var m = re.Match(logText);
            if (!m.Success) continue;
            if (!seen.Add(i)) continue;
            var snippet = m.Value.Trim();
            if (snippet.Length > 80) snippet = snippet[..80];
            result.Add(new DiagnosticHit(snippet, explanation, fix));
        }
        return result;
    }

    /// <summary>对日志文本诊断（兼容旧接口）：返回「匹配原文 → 中文说明」字符串列表</summary>
    public static List<string> Diagnose(string logText)
        => [.. DiagnoseDetailed(logText).Select(h => $"▸ 匹配：{h.Snippet}\n  说明：{h.Explanation}")];
}
