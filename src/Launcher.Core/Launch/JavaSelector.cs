using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Launcher.Core.Model.Mojang;

namespace Launcher.Core.Launch;

/// <summary>
/// Java 自动选配：扫描本机所有 Java（AppData\.minecraft\runtime 各组件 + 注册表 JDK/JRE/Adoptium +
/// JAVA_HOME + PATH + Program Files 常见目录），按版本要求的 JDK 大版本选择最接近的可用 Java。
/// AL10.2：不再只看 PCL 缓存的 runtime——用户"直接调用电脑里已有的"；找不到匹配返回 null 由调用方
/// 触发下载或提示（GameLaunchService 已处理父版本继承 javaVersion）。
/// </summary>
public static class JavaSelector
{
    /// <summary>已知 runtime 组件名 → 大版本（AppData\.minecraft\runtime 官方布局）。
    /// 8-31 internal：JavaProvisioningService 复用选组件（缺 Java 自动补齐）</summary>
    internal static readonly (string Name, int Major)[] Runtimes =
    [
        ("java-runtime-epsilon", 25),
        ("java-runtime-delta", 21),
        ("java-runtime-beta", 17),
        ("java-runtime-alpha", 16),
        ("jre-legacy", 8),
    ];

    /// <summary>平台 Java 可执行文件名（Windows: java.exe；Unix: java）</summary>
    internal static string JavaExe => OperatingSystem.IsWindows() ? "java.exe" : "java";

    /// <summary>Mojang 官方 runtime 平台子目录（windows-x64 / linux-x64 / osx-arm64 / osx-x86_64）</summary>
    internal static string OsRuntimeDir => OperatingSystem.IsMacOS()
        ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x86_64")
        : OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64";

    /// <summary>8-31 Mojang 官方 runtime 根目录（硬编码家目录，不跟配置/XDG——与扫描一致）：
    /// Windows %AppData%\.minecraft / macOS ~/Library/Application Support/minecraft / Linux ~/.minecraft</summary>
    internal static string MinecraftRoot() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft")
        : OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "minecraft")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft");

    public sealed record JavaInstall(string Path, int Major);

    /// <summary>选择 Java 可执行文件路径；找不到匹配版本时返回 null（调用方决定下载或提示）。</summary>
    public static string? Pick(int? requiredMajor) => BestMatch(ScanInstalled(), requiredMajor);

    /// <summary>
    /// 解析版本所需 Java 大版本：自身 javaVersion → 沿 InheritsFrom 链向父版本继承 → 按 MC 版本号推断。
    /// 客户端与服务端共用——服务端曾只读自身 json，Fabric/整合包 profile 无 javaVersion → 默认 17，
    /// 26.2（需 Java 25）开服直接 UnsupportedClassVersionError。
    /// </summary>
    public static int ResolveRequiredMajor(VersionJson version, Func<string, VersionJson?> loadParent)
    {
        if (version.JavaVersion?.MajorVersion is { } m && m > 0) return m;
        if (version.InheritsFrom is { } parentId && loadParent(parentId) is { } parent)
            return ResolveRequiredMajor(parent, loadParent); // 递归沿链（父版本再继承）
        return InferMajorFromId(version.Id);
    }

    /// <summary>按 MC 版本推断所需 Java 大版本（无 javaVersion 时兜底）：1.17+ → 17；更旧 → 8</summary>
    private static int InferMajorFromId(string versionId)
    {
        var m = Regex.Match(versionId, @"^(\d+)\.(\d+)");
        if (!m.Success) return 17;
        var major = int.Parse(m.Groups[1].Value);
        var minor = int.Parse(m.Groups[2].Value);
        return major > 1 || (major == 1 && minor >= 17) ? 17 : 8;
    }

    /// <summary>纯选型逻辑（可单测）：版本要求是"最低 Java"，选 ≥ 要求且最接近的
    /// （JVM 向后兼容低 class 文件版本，不能向前）；无要求选最高可用。</summary>
    public static string? BestMatch(IReadOnlyList<JavaInstall> installed, int? requiredMajor)
    {
        if (installed.Count == 0) return "java"; // PATH 兜底（极端环境）
        if (requiredMajor is { } req)
        {
            var best = installed.Where(j => j.Major >= req).OrderBy(j => j.Major).FirstOrDefault();
            return best?.Path; // null → 本机无满足版本，调用方自动下载/提示
        }
        return installed.OrderByDescending(j => j.Major).First().Path;
    }

    /// <summary>扫描本机所有 Java 安装（去重；路径优先推断版本，推断失败才跑 java -version）</summary>
    public static List<JavaInstall> ScanInstalled()
    {
        var found = new List<JavaInstall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? exe, int? hintMajor = null, string? hintVersion = null)
        {
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;
            if (!seen.Add(exe)) return;
            var major = hintMajor
                ?? ParseVersionMajor(hintVersion)
                ?? ParseMajorFromPath(exe)
                ?? RunJavaMajor(exe);
            found.Add(new JavaInstall(exe, major ?? 0));
        }

        // 1. .minecraft\runtime 官方布局（PCL / 官方启动器缓存）——大版本已知（平台子目录）。
        //    Windows 在 %AppData%\.minecraft；macOS 在 ~/Library/Application Support/minecraft；
        //    Linux 在 ~/.minecraft（不跟 XDG——Mojang 硬编码家目录）
        var runtimeBase = Path.Combine(MinecraftRoot(), "runtime");
        foreach (var (name, major) in Runtimes)
        {
            Add(Path.Combine(runtimeBase, name, OsRuntimeDir, name, "bin", JavaExe), major);
            Add(Path.Combine(runtimeBase, name, "bin", JavaExe), major);
        }

        // 2. 注册表 JavaSoft（Oracle/OpenJDK）与 Adoptium（仅 Windows；Linux 无注册表）
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var javaSoft = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\JavaSoft");
                if (javaSoft is not null)
                {
                    AddRegistryFamily(javaSoft, "JDK", Add);
                    AddRegistryFamily(javaSoft, "JRE", Add);
                }
                using var adoptium = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Eclipse Adoptium\JDK");
                if (adoptium is not null) AddRegistryFamily(adoptium, null, Add);
            }
            catch { /* 注册表不可读则跳过 */ }
        }

        // 3. JAVA_HOME
        Add(Environment.GetEnvironmentVariable("JAVA_HOME") is { } jh ? Path.Combine(jh, "bin", JavaExe) : null);

        // 4. PATH 中的 java（Windows 分号 / Unix 冒号）
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            Add(Path.Combine(dir.Trim('"'), JavaExe));

        // 4b. macOS 官方 JVM 发现（java_home -V 列出所有已注册 JVM）——补标准扫描漏掉的安装
        ScanJavaHomeV(Add);

        // 5. 常见 JDK 目录（Windows: Program Files；macOS: /Library/Java + 用户级 + Homebrew；Linux: /usr/lib/jvm、/opt 等）
        var userJvm = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Java", "JavaVirtualMachines");
        var baseDirs = OperatingSystem.IsWindows()
            ? new[]
            {
                @"C:\Program Files\Java", @"C:\Program Files\Eclipse Adoptium", @"C:\Program Files\Microsoft",
                @"C:\Program Files\Zulu", @"C:\Program Files\Amazon Corretto", @"D:\Program Files\Java",
            }
            : OperatingSystem.IsMacOS()
                ? new[] { "/Library/Java/JavaVirtualMachines", userJvm, "/opt", "/opt/homebrew/opt", "/usr/local/opt" }
                : new[] { "/usr/lib/jvm", "/usr/java", "/opt" };
        foreach (var baseDir in baseDirs)
        {
            if (!Directory.Exists(baseDir)) continue;
            foreach (var d in Directory.EnumerateDirectories(baseDir))
            {
                Add(Path.Combine(d, "bin", JavaExe));
                // 8-31 修「Mac 装了 Java 却扫不到」：macOS 的 .jdk 是 bundle，java 在 Contents/Home/bin/
                //（不在 d/bin/——旧代码对标准 Oracle/Temurin JDK 永远漏扫）。Homebrew opt 的 java 也走 d/bin。
                if (OperatingSystem.IsMacOS())
                    Add(Path.Combine(d, "Contents", "Home", "bin", JavaExe));
            }
        }

        return found;
    }

    /// <summary>macOS 官方 JVM 发现：/usr/libexec/java_home -V 列出所有已注册 JVM（Oracle/Temurin .pkg 等）。
    /// 输出到 stderr，形如 `21.0.5 (arm64) "Eclipse Temurin" - "21.0.5+11" /path/Contents/Home`。</summary>
    private static void ScanJavaHomeV(Action<string?, int?, string?> add)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var psi = new ProcessStartInfo("/usr/libexec/java_home", "-V")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null) return;
            var output = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            foreach (var line in output.Split('\n'))
            {
                if (ParseJavaHomeVLine(line) is { } hit)
                    add(Path.Combine(hit.Home, "bin", JavaExe), hit.Major, null);
            }
        }
        catch { /* java_home 不可用则跳过 */ }
    }

    /// <summary>解析 java_home -V 的一行（可单测）：`21.0.5 (arm64) "Eclipse Temurin" - "21.0.5+11" /path/Contents/Home`</summary>
    internal static (string Home, int Major)? ParseJavaHomeVLine(string line)
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var first = parts[0];
        if (first.Length == 0 || first[0] < '0' || first[0] > '9') return null; // 跳过表头/空行
        if (!int.TryParse(first.Split('.')[0], out var major)) return null;
        return (parts[^1].Trim('"'), major);
    }

    /// <summary>注册表一个族（JDK/JRE 或 Adoptium）下所有版本的 JavaHome（仅 Windows 调用）</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddRegistryFamily(Microsoft.Win32.RegistryKey family, string? child,
        Action<string?, int?, string?> add)
    {
        using var root = child is null ? family : family.OpenSubKey(child);
        if (root is null) return;
        foreach (var versionName in root.GetSubKeyNames())
        {
            try
            {
                using var vk = root.OpenSubKey(versionName);
                if (vk?.GetValue("JavaHome") is string home)
                    add(Path.Combine(home, "bin", "java.exe"), null, versionName);
            }
            catch { /* 单个条目损坏跳过 */ }
        }
    }

    /// <summary>解析 "25.0.1" / "1.8.0_51" 形式版本号 → 大版本</summary>
    private static int? ParseVersionMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var m = Regex.Match(version, @"^(\d+)(?:\.(\d+))?");
        if (!m.Success) return null;
        var first = int.Parse(m.Groups[1].Value);
        if (first == 1) // 1.8.0_51 → 8
            return m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
        return first;
    }

    /// <summary>从路径推断大版本：jdk-25 / jdk1.8 / 17.0.15 等</summary>
    private static int? ParseMajorFromPath(string exe)
    {
        var m = Regex.Match(exe, @"(?i)[-/\\](?:jdk|jre)[-]?(?:1\.)?(\d{1,2})(?:\D|$)");
        if (m.Success) return int.Parse(m.Groups[1].Value);
        var m2 = Regex.Match(exe, @"(?i)java-runtime-(\w+)");
        if (m2.Success)
        {
            var name = "java-runtime-" + m2.Groups[1].Value;
            return Runtimes.FirstOrDefault(r => r.Name == name).Major is { } mm ? mm : null;
        }
        return null;
    }

    /// <summary>兜底：运行 java -version 解析大版本（仅推断失败时，慢）</summary>
    private static int? RunJavaMajor(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe, Arguments = "-version", RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            });
            if (p is null) return null;
            var text = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            var m = Regex.Match(text, @"""?(\d+)(?:\.(\d+))?");
            if (!m.Success) return null;
            var first = int.Parse(m.Groups[1].Value);
            return first == 1 && m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : first;
        }
        catch { return null; }
    }
}
