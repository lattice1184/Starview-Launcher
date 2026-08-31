using System.IO;
using System.Text.Json;
using Launcher.Core.Launch;
using Launcher.Core.Model.Mojang;

namespace Launcher.Core.Tests;

/// <summary>JVM 参数组装：arguments.jvm 发射 / classpath 统一末尾 / inheritsFrom 链 / 缺失父版本报错</summary>
public class JavaArgumentsBuilderTests
{
    private static VersionJson Load(string id)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "versions", $"{id}.json");
        return JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(path))!;
    }

    private static JavaArgumentsBuilder.LaunchProfile Build(VersionJson version, string gameDir = @"C:\mc", bool? versionIsolation = null)
        => new JavaArgumentsBuilder().Build(version, gameDir, @"C:\java\bin\java.exe",
            "YanKa", "00000000-0000-0000-0000-000000000000", "token", 4096, versionIsolation: versionIsolation);

    [Fact]
    public void Modern_1_21_1_JvmArgsEmittedAndDeduped()
    {
        var p = Build(Load("1.21.1"));

        // -Djava.library.path 恰好一次（基础参数与 json 的 ${natives_directory} 去重）
        Assert.Single(p.JvmArgs, a => a.StartsWith("-Djava.library.path="));
        // classpath 统一在末尾，且无残留 ${classpath}
        Assert.Equal("-cp", p.JvmArgs[^2]);
        Assert.Equal(p.ClassPath, p.JvmArgs[^1]);
        Assert.DoesNotContain(p.JvmArgs, a => a.Contains("${classpath}"));
        Assert.Contains("-Xmx4096m", p.JvmArgs);
        Assert.Equal("net.minecraft.client.main.Main", p.MainClass);
    }

    [Fact]
    public void MsaAccount_UserTypeArgIsMsa()
    {
        // 8-13：正版账号 user_type 必须 msa（游戏读它决定在线认证；legacy = 离线模式）
        var p = new JavaArgumentsBuilder().Build(Load("1.21.1"), @"C:\mc", @"C:\java\bin\java.exe",
            "YanKa", "069a79f4-44e9-4726-a5be-fca90e38aaf5", "mc-token", 4096, userType: "msa");
        Assert.Contains("--userType", p.GameArgs);
        Assert.Contains("msa", p.GameArgs);
        Assert.DoesNotContain("legacy", p.GameArgs);
    }

    [Fact]
    public void Legacy_1_8_9_Unchanged_ClasspathAppended()
    {
        var p = Build(Load("1.8.9"));

        Assert.Contains("-Xmx4096m", p.JvmArgs);
        Assert.Contains("YanKa", p.GameArgs);
        Assert.DoesNotContain(p.GameArgs, a => a.Contains("${auth_player_name}"));
        Assert.Equal(p.ClassPath, p.JvmArgs[^1]);
        Assert.Contains(@"1.8.9\1.8.9.jar", p.ClassPath);
    }

    /// <summary>8-31 修老版本 natives：twitch-platform 的 natives 值是 "natives-windows-${arch}"——
    /// 必须展开（classpath 不得留字面量 ${arch}，否则 natives 解压后加载不到）</summary>
    [Fact]
    public void Legacy_1_8_9_TwitchNatives_ArchExpanded_NoPlaceholderInClasspath()
    {
        var p = Build(Load("1.8.9"));

        Assert.DoesNotContain("${arch}", p.ClassPath);
        Assert.Contains("twitch-platform-6.5-natives-windows-64.jar", p.ClassPath);
    }

    [Fact]
    public void ForgeStyle_InheritsFrom_MergesParentAndEmitsModuleArgs()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"launch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "versions", "1.16.5"));
        var childDir = Path.Combine(dir, "versions", "1.16.5-forge-36.2.0");
        Directory.CreateDirectory(childDir);
        try
        {
            // 父：原版（minecraftArguments 旧式 + 基础库）
            var parent = new VersionJson("1.16.5", "release", "net.minecraft.client.main.Main", "1.16",
                new AssetIndexInfo("1.16", "https://mc/i.json", "s", 10, 100),
                null, "--username ${auth_player_name} --gameDir ${game_directory}",
                [new LibraryJson("org.lwjgl:lwjgl:3.2.2", null, null, null, new LibraryDownloads(
                    new DownloadFileInfo("https://mc/lwjgl.jar", "s1", 100), null), null, null, null)],
                null, null, null, null);
            // 子：Forge（inheritsFrom + bootstraplauncher jvm 参数）
            var child = new VersionJson("1.16.5-forge-36.2.0", "release",
                "cpw.mods.bootstraplauncher.BootstrapLauncher", null, null,
                new ArgumentsInfo(null, [JsonSerializer.SerializeToElement("-p"),
                    JsonSerializer.SerializeToElement("C:/bootstraplauncher.jar"),
                    JsonSerializer.SerializeToElement("--add-modules"),
                    JsonSerializer.SerializeToElement("ALL-MODULE-PATH"),
                    JsonSerializer.SerializeToElement("-Djava.library.path=${natives_directory}")]),
                null,
                [new LibraryJson("net.minecraftforge:forge:36.2.0", null, null, null, new LibraryDownloads(
                    new DownloadFileInfo("https://mc/forge.jar", "s2", 200), null), null, null, null)],
                null, null, null, "1.16.5");

            File.WriteAllText(Path.Combine(dir, "versions", "1.16.5", "1.16.5.json"), JsonSerializer.Serialize(parent));
            File.WriteAllText(Path.Combine(childDir, "1.16.5-forge-36.2.0.json"), JsonSerializer.Serialize(child));

            var p = Build(child, dir);

            Assert.Equal("cpw.mods.bootstraplauncher.BootstrapLauncher", p.MainClass); // 子优先
            Assert.Contains("-p", p.JvmArgs);                                          // 模块参数已发射
            Assert.Contains("C:/bootstraplauncher.jar", p.JvmArgs);
            Assert.Contains("--add-modules", p.JvmArgs);
            Assert.Contains("ALL-MODULE-PATH", p.JvmArgs);
            Assert.Single(p.JvmArgs, a => a.StartsWith("-Djava.library.path="));       // 去重
            Assert.Contains(@"org\lwjgl\lwjgl\3.2.2\lwjgl-3.2.2.jar", p.ClassPath);      // 父库已合并
            Assert.Contains(@"net\minecraftforge\forge\36.2.0\forge-36.2.0.jar", p.ClassPath);
            Assert.DoesNotContain(p.JvmArgs, a => a.Contains("${"));
            Assert.Equal(p.ClassPath, p.JvmArgs[^1]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void VersionIsolation_GameDirectoryPointsToVersionDir()
    {
        var p = Build(Load("1.21.1"), @"C:\mc", versionIsolation: true);

        Assert.Contains("--gameDir", p.GameArgs);
        Assert.Contains("C:/mc/versions/1.21.1", p.GameArgs);   // 隔离：game_directory → versions/{id}
        // assets 保持绝对指向共享目录
        Assert.Contains("--assetsDir", p.GameArgs);
        Assert.Contains("C:/mc/assets", p.GameArgs);
    }

    [Fact]
    public void NoIsolation_GameDirectoryIsRoot()
    {
        var p = Build(Load("1.21.1"), @"C:\mc", versionIsolation: false);

        Assert.Contains("C:/mc", p.GameArgs);
        Assert.DoesNotContain(p.GameArgs, a => a.Contains("versions/1.21.1"));
    }

    /// <summary>26.2 形态：jvm 参数 java.library.path 带 /java 子目录后缀 + natives-windows/arm64 变体。
    /// 修复：java.library.path 统一根目录（/java 后缀去重）、arm64 变体不误判为 natives。</summary>
    [Fact]
    public void Modern262_NativesJavaSubdir_DeduplicatedToRoot()
    {
        var json = """
            {
              "id":"26.2","type":"release","mainClass":"net.minecraft.client.main.Main",
              "arguments":{"jvm":["-Djava.library.path=${natives_directory}/java"]},
              "libraries":[
                {"name":"org.lwjgl:lwjgl:3.4.1","downloads":{"artifact":{"url":"https://x/l.jar","size":5}}},
                {"name":"org.lwjgl:lwjgl:3.4.1:natives-windows","downloads":{"artifact":{"url":"https://x/l-natives.jar","size":5}}},
                {"name":"org.lwjgl:lwjgl:3.4.1:natives-windows-arm64","downloads":{"artifact":{"url":"https://x/l-arm64.jar","size":5}}}
              ]
            }
            """;
        var p = Build(JsonSerializer.Deserialize<VersionJson>(json)!, @"C:\mc", versionIsolation: false);

        // java.library.path 恰好一条且指向 natives 根（JSON 的 /java 后缀被去重）
        Assert.Single(p.JvmArgs, a => a.StartsWith("-Djava.library.path="));
        var libPath = p.JvmArgs.First(a => a.StartsWith("-Djava.library.path="));
        Assert.DoesNotContain("/java", libPath);
        Assert.Contains(@"C:\mc\versions\26.2\26.2-natives", libPath);
        // natives-windows 不进 classpath（新版只解压）；arm64 变体按普通库进 classpath（精确匹配生效）
        Assert.DoesNotContain(p.ClassPath, "natives-windows.jar");
        Assert.Contains("arm64", p.ClassPath);
    }

    [Fact]
    public void ForgeStyle_MissingParent_Throws()
    {
        var child = new VersionJson("1.16.5-forge-36.2.0", "release", "forge.Launcher", null, null,
            null, null, null, null, null, null, "1.16.5");

        // AL29 C2：父版本缺失是 ParentVersionMissingException（与「客户端文件缺失」区分，修复目标不同）
        var ex = Assert.Throws<ParentVersionMissingException>(() => Build(child, @"C:\mc-empty"));

        Assert.Contains("1.16.5", ex.Message);
    }

    /// <summary>AK：PCL/第三方安装器 profile 库无 downloads 字段——按 maven 坐标推导进 classpath
    /// （旧逻辑只认 downloads.artifact，fabric-loader 链整个跳过 → KnotClient ClassNotFoundException）</summary>
    [Fact]
    public void LibraryWithoutDownloads_IncludedByMavenPath()
    {
        var json = """
            {
              "id":"1.21.6-fabric-0.19.3","type":"release","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient",
              "libraries":[
                {"name":"net.fabricmc:fabric-loader:0.19.3","url":"https://maven.fabricmc.net/"},
                {"name":"net.fabricmc:sponge-mixin:0.17.3+mixin.0.8.7","url":"https://maven.fabricmc.net/"}
              ]
            }
            """;
        var p = Build(JsonSerializer.Deserialize<VersionJson>(json)!, @"C:\mc", versionIsolation: false);

        Assert.Contains(@"net\fabricmc\fabric-loader\0.19.3\fabric-loader-0.19.3.jar", p.ClassPath);
        Assert.Contains(@"net\fabricmc\sponge-mixin\0.17.3+mixin.0.8.7\sponge-mixin-0.17.3+mixin.0.8.7.jar", p.ClassPath);
        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", p.MainClass);
    }

    /// <summary>AL8：Forge 1.20+ 的 -p 模块路径含 ${classpath_separator}——token 缺失则整串未替换，
    /// java 模块系统把路径串当单一文件解析 → InvalidPathException（TACZgun 崩溃根因）</summary>
    [Fact]
    public void Forge120_ClasspathSeparator_ReplacedInModulePath()
    {
        var json = """
            {
              "id":"TACZgun","type":"release","mainClass":"cpw.mods.bootstraplauncher.BootstrapLauncher",
              "arguments":{"jvm":[
                "-p",
                "${library_directory}/cpw/mods/bootstraplauncher/1.1.2/bootstraplauncher-1.1.2.jar${classpath_separator}${library_directory}/cpw/mods/securejarhandler/2.1.10/securejarhandler-2.1.10.jar",
                "-DlibraryDirectory=${library_directory}",
                "-cp",
                "${classpath}",
                "--add-modules",
                "ALL-MODULE-PATH"
              ]}
            }
            """;
        var p = Build(JsonSerializer.Deserialize<VersionJson>(json)!, @"C:\mc", versionIsolation: false);

        // -p 的值：${classpath_separator} 已替换为路径分隔符、library_directory 已替换、无残留占位符
        var idx = Array.IndexOf(p.JvmArgs, "-p");
        Assert.True(idx >= 0, "-p 模块路径参数应存在");
        var modulePath = p.JvmArgs[idx + 1];
        Assert.DoesNotContain("${classpath_separator}", modulePath);
        Assert.Contains(
            "C:/mc/libraries/cpw/mods/bootstraplauncher/1.1.2/bootstraplauncher-1.1.2.jar" + Path.PathSeparator
            + "C:/mc/libraries/cpw/mods/securejarhandler/2.1.10/securejarhandler-2.1.10.jar", modulePath);
        Assert.DoesNotContain(p.JvmArgs, a => a.Contains("${"));
        // -DlibraryDirectory 已替换
        Assert.Contains("-DlibraryDirectory=C:/mc/libraries", p.JvmArgs);
        // json 的 -cp ${classpath} 被过滤，classpath 由构建器末尾追加
        Assert.Equal("-cp", p.JvmArgs[^2]);
        Assert.Equal(p.ClassPath, p.JvmArgs[^1]);
        Assert.Equal("cpw.mods.bootstraplauncher.BootstrapLauncher", p.MainClass);
    }

    /// <summary>AL8 修复：--add-opens/--add-exports 等成对参数（选项+值）不能去重——
    /// 通用去重吃掉第二个选项名 → 值错位 → ClassNotFoundException（TACZgun 崩溃）；
    /// 自包含 -D 参数仍去重（重复赋值无害）。顺带验证 ${clientid}/${auth_xuid} 替换（1.20.1+ json 带）。</summary>
    [Fact]
    public void PairedOptions_NotDeduplicated_ClientIdTokensReplaced()
    {
        var json = """
            {
              "id":"paired","type":"release","mainClass":"net.minecraft.client.main.Main",
              "arguments":{"jvm":[
                "--add-opens","java.base/java.util.jar=cpw.mods.securejarhandler",
                "--add-opens","java.base/java.lang.invoke=cpw.mods.securejarhandler",
                "--add-exports","java.base/sun.security.util=cpw.mods.securejarhandler",
                "--add-exports","jdk.naming.dns/com.sun.jndi.dns=java.naming",
                "--add-modules","ALL-MODULE-PATH",
                "-Ddup=1",
                "-Ddup=1"
              ],
              "game":["--clientId","${clientid}","--xuid","${auth_xuid}"]}
            }
            """;
        var p = Build(JsonSerializer.Deserialize<VersionJson>(json)!, @"C:\mc", versionIsolation: false);

        // 成对选项各出现两次（选项名+值一一配对，不被去重）
        Assert.Equal(2, p.JvmArgs.Count(a => a == "--add-opens"));
        Assert.Equal(2, p.JvmArgs.Count(a => a == "--add-exports"));
        var idx = Array.IndexOf(p.JvmArgs, "--add-opens");
        Assert.Equal("java.base/java.util.jar=cpw.mods.securejarhandler", p.JvmArgs[idx + 1]);
        Assert.Equal("java.base/java.lang.invoke=cpw.mods.securejarhandler", p.JvmArgs[idx + 3]);
        // 自包含 -D 仍去重
        Assert.Single(p.JvmArgs, a => a == "-Ddup=1");
        // 1.20.1+ game 参数 token 已替换
        Assert.Contains("0", p.GameArgs);
        Assert.DoesNotContain(p.GameArgs, a => a.Contains("${clientid}") || a.Contains("${auth_xuid}"));
    }

    /// <summary>AK：混搭 profile（部分带 downloads）——两类库都在 classpath（带 downloads 行为不回归）</summary>
    [Fact]
    public void MixedLibraries_BothIncluded()
    {
        var json = """
            {
              "id":"mixed","type":"release","mainClass":"net.minecraft.client.main.Main",
              "libraries":[
                {"name":"org.lwjgl:lwjgl:3.4.1","downloads":{"artifact":{"url":"https://x/l.jar","size":5}}},
                {"name":"net.fabricmc:fabric-loader:0.19.3","url":"https://maven.fabricmc.net/"}
              ]
            }
            """;
        var p = Build(JsonSerializer.Deserialize<VersionJson>(json)!, @"C:\mc", versionIsolation: false);

        Assert.Contains(@"org\lwjgl\lwjgl\3.4.1\lwjgl-3.4.1.jar", p.ClassPath);
        Assert.Contains(@"net\fabricmc\fabric-loader\0.19.3\fabric-loader-0.19.3.jar", p.ClassPath);
    }

    // ---------- 8-13 启动命令日志脱敏（token 打码） ----------

    [Fact]
    public void RedactTokens_SeparateValueForm()
    {
        // --auth_access_token xxx（两独立参数）→ 值打码；参数名保留（诊断不受影响）；其他参数不受影响
        var redacted = LaunchProcess.RedactTokens(
            ["--auth_access_token", "eyJhbGciOi.real.token", "--uuid", "abc"]).ToList();
        Assert.Equal(["--auth_access_token", "***", "--uuid", "abc"], redacted);
    }

    [Fact]
    public void RedactTokens_EqualsForm()
    {
        // --accessToken=xxx（单参数等号形态，老版本 minecraftArguments 模板）→ 值打码
        var redacted = LaunchProcess.RedactTokens(["--accessToken=abc123", "--auth_session=xyz"]).ToList();
        Assert.Equal("--accessToken=***", redacted[0]);
        Assert.Equal("--auth_session=***", redacted[1]);
    }

    [Fact]
    public void RedactTokens_OfflineLiteralToken_RedactedToo()
    {
        // 离线模式的字面量 "token" 也被打码（语义一致——日志里永远不出现 accessToken 值）
        var redacted = LaunchProcess.RedactTokens(["--accessToken", "token"]).ToList();
        Assert.Equal("***", redacted[1]);
    }
}
