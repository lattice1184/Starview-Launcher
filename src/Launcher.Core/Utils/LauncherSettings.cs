using System.Text.Json;
using Launcher.Core.Launch;

namespace Launcher.Core.Utils;

/// <summary>
/// 启动器设置（AppData\Launcher\settings.json）：自配游戏路径 + 版本隔离开关。
/// </summary>
public enum DensityMode { Compact = 0, Normal = 1, Comfortable = 2 }

/// <summary>窗口观感两档：透明（Blur 毛玻璃）/ 实色（纯不透明）——8-23 滑块改单选：连续透明度
/// 在 Popup 合成降级时体现不出来，最终感知只有两种状态，干脆写死两档。</summary>
public enum OpacityMode { Blur = 0, Solid = 1 }

/// <summary>下载并发档位（分片连接数：低 8 / 中 16 / 高 24）</summary>
public enum DownloadTier { Low = 8, Medium = 16, High = 24 }

/// <summary>下载源策略：官方优先（原回退行为）/ 镜像优先 / 仅镜像</summary>
public enum DownloadSourcePreference { OfficialFirst = 0, MirrorFirst = 1, MirrorOnly = 2 }

/// <summary>游戏/服务端 JVM 进程的 Windows 优先级（映射 ProcessPriorityClass）</summary>
public enum GamePriority { BelowNormal = 0, Normal = 1, AboveNormal = 2, High = 3, RealTime = 4 }

public sealed class LauncherSettings
{
    private static readonly string DefaultPath = Path.Combine(
        Launcher.Core.Utils.AppPaths.DataRoot, "settings.json");

    /// <summary>自配游戏目录（如 C:\Users\yanka\Downloads\YanKa Launcher\.minecraft）；null = 自动探测</summary>
    public string? GameDirectory { get; set; }

    /// <summary>版本隔离（每个版本独立 saves/mods/options.txt，不串门）</summary>
    public bool VersionIsolation { get; set; } = true;

    // ---------- 启动 ----------

    /// <summary>游戏内存上限（MB）；-2 = 自动（按可用内存留余量），0 = 总内存 60%</summary>
    public int MemoryMb { get; set; } = -2;

    /// <summary>服务器内存（MB）；>0 开服使用，否则默认 2048（独立于客户端内存——开服建议配置只改这里，不误改客户端）</summary>
    public int ServerMemoryMb { get; set; } = 2048;

    /// <summary>Java 路径；null = 自动选配（PCL runtime / PATH）</summary>
    public string? JavaPath { get; set; }

    /// <summary>额外 JVM 参数（空格分隔，如 "-Dxxx=1 -Xss2m"）；null = 无</summary>
    public string? ExtraJvmArgs { get; set; }

    /// <summary>启动时自动写入中文语言（options.txt lang:zh_cn）</summary>
    public bool AutoChineseEnabled { get; set; } = true;

    /// <summary>生态下载（MOD/光影包等）是否跟随实例自动带出加载器+版本去查询（8-19：PCL 版本号 CF 不认 → 交用户选择）</summary>
    public bool EcoFollowInstance { get; set; } = true;

    /// <summary>JVM 性能档位（轻量/均衡/流畅/极致 → GC 参数预设）</summary>
    public PerformanceProfile JvmProfile { get; set; } = PerformanceProfile.Medium;

    /// <summary>游戏/服务端进程优先级（独立设置，不随性能档位；默认正常）</summary>
    public GamePriority GamePriority { get; set; } = GamePriority.Normal;

    /// <summary>启动完成后随机弹一条小提示（彩蛋，可关）</summary>
    public bool StartupTipEnabled { get; set; } = true;

    // ---------- 下载 ----------

    /// <summary>下载源策略（官方优先 / 镜像优先 / 仅镜像）——8-18 默认改镜像优先：GitHub 下载先走加速镜像</summary>
    public DownloadSourcePreference DownloadSource { get; set; } = DownloadSourcePreference.MirrorFirst;

    /// <summary>最大并发下载数（0 = 默认）</summary>
    public int MaxConcurrentDownloads { get; set; }

    /// <summary>下载限速（KB/s；0 = 不限速）</summary>
    public int DownloadSpeedLimitKbps { get; set; }

    /// <summary>8-20 代理服务器（host:port，如 127.0.0.1:7890；留空 = 直连）。仅 Http 代理。
    /// 下载/API 全局生效（新任务用）；已运行中任务不受影响。</summary>
    public string? ProxyAddress { get; set; }

    /// <summary>8-20 Modrinth API 走 mcimirror 镜像。8-22 默认开：国内官方 api.modrinth.com 直连实测 18KB/s，
    /// mcimirror 73KB/s（快 4 倍）；只镜像公开 API（无 key 数据），设置可关回官方</summary>
    public bool ModrinthMirrorEnabled { get; set; } = true;

    /// <summary>下载并发档位（分片数：低 8 / 中 16 / 高 24）；默认中档——PCL2 参考上限 63，16 仍保守</summary>
    public DownloadTier DownloadTier { get; set; } = DownloadTier.Medium;

    /// <summary>分片连接数覆盖（0 = 用档位默认）</summary>
    public int ChunkCount { get; set; }

    /// <summary>分片缓冲区覆盖（字节；0 = 默认 81920）</summary>
    public int BufferSize { get; set; }

    /// <summary>CurseForge API Key（空 = 禁用 CF 源）</summary>
    public string CurseForgeApiKey { get; set; } = "";

    /// <summary>8-13 GitHub API Token（空 = 未认证 60 次/小时/IP——普通用户默认模式；
    /// 填了 = 换链 5000 次/小时，防限流）。DPAPI 加密落盘，同 CF key。</summary>
    public string GitHubApiToken { get; set; } = "";

    /// <summary>后台静默更新检查（启动延迟检查最新 release，就绪后提示重启生效）</summary>
    public bool AutoCheckUpdate { get; set; } = true;

    /// <summary>8-13 微软登录 client_id（空 = 走远程下发/内置兜底；手动填的值优先）。
    /// DPAPI 加密落盘——「藏」的第一层（防 grep 挖二进制级防护）。</summary>
    public string MicrosoftClientId { get; set; } = "";

    /// <summary>LittleSkin OAuth 应用 client_id（8-16 批次 51 皮肤库：设备码流必需）。
    /// 8-19 内部化：默认内置值 + 加载空值回填——用户界面不再暴露此设置（账号弹窗/皮肤库直接一条龙登录）</summary>
    public string LittleSkinClientId { get; set; } = Launcher.Core.Account.LittleSkinOAuth.DefaultClientId;

    /// <summary>8-16 批次 52 CF API 地址覆盖（空 = 官方 api.curseforge.com；填自建代理如 Cloudflare Worker 绕开直连抖动）</summary>
    public string CurseForgeApiBase { get; set; } = "";

    /// <summary>CurseForge 文件 CDN 镜像前缀（空 = 官方 edge.forgecdn.net 直连；可填镜像/代理根地址）</summary>
    public string CurseForgeCdnPrefix { get; set; } = "";

    /// <summary>第三方文件下载目录（空 = 首次取系统 Downloads）</summary>
    public string ThirdPartyDownloadDir { get; set; } = "";

    // ---------- 外观 ----------

    /// <summary>窗口观感档（透明 Blur / 实色）</summary>
    public OpacityMode Opacity { get; set; } = OpacityMode.Blur;

    /// <summary>遗留字段（8-23 滑块改两档单选前用，0.7-1.0 连续值）。保留仅兼容旧 settings.json
    /// 反序列化——新字段缺失时旧 JSON 也能读，读到的旧值不再使用。</summary>
    public double WindowOpacity { get; set; } = 0.9;

    /// <summary>强调色（#RRGGBB；空 = 默认靛蓝）</summary>
    public string AccentColor { get; set; } = "#6C8CFF";

    /// <summary>背景色（#RRGGBB 或 #AARRGGBB，alpha 参与透明；空 = 默认 #B81D222C）</summary>
    public string? BackgroundColor { get; set; }

    /// <summary>自定义背景图片（绝对路径；null/空 = 无背景，用亚克力纯色）</summary>
    public string? BackgroundImagePath { get; set; }

    /// <summary>界面密度（紧凑/标准/舒适 → 整 UI 缩放）</summary>
    public DensityMode Density { get; set; } = DensityMode.Normal;

    /// <summary>窗口宽度（0 = 未设置，用默认 860）</summary>
    public double WindowWidth { get; set; }

    /// <summary>窗口高度（0 = 未设置，用默认 560）</summary>
    public double WindowHeight { get; set; }

    // ---------- 存储 ----------

    /// <summary>各存储分组上限（MB；0 = 不限）</summary>
    public Dictionary<string, int> StorageCapsMb { get; set; } = new() { ["logs"] = 200, ["downloads"] = 2048 };

    public static LauncherSettings Current { get; } = Load();

    public static LauncherSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path));
                if (s is not null)
                {
                    // 落盘为 DPAPI 密文；旧版明文自动迁移（读取原样返回）
                    s.CurseForgeApiKey = Secrets.Read(s.CurseForgeApiKey) ?? "";
                    s.GitHubApiToken = Secrets.Read(s.GitHubApiToken) ?? "";
                    s.MicrosoftClientId = Secrets.Read(s.MicrosoftClientId) ?? "";
                    // 8-19 空值回填内置默认（旧配置存过空串会覆盖字段默认——不回填用户又被引导去设置）
                    s.LittleSkinClientId = Secrets.Read(s.LittleSkinClientId) ?? Launcher.Core.Account.LittleSkinOAuth.DefaultClientId;
                    return s;
                }
            }
        }
        catch { /* 坏 JSON 回退默认 */ }
        return new LauncherSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var plain = CurseForgeApiKey;
            var plainGit = GitHubApiToken;
            var plainMs = MicrosoftClientId;
            var plainLs = LittleSkinClientId;
            CurseForgeApiKey = Secrets.Protect(plain); // 落盘加密（DPAPI）
            GitHubApiToken = Secrets.Protect(plainGit);
            MicrosoftClientId = Secrets.Protect(plainMs);
            LittleSkinClientId = Secrets.Protect(plainLs);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                CurseForgeApiKey = plain; // 内存保持明文，其他调用方不受影响
                GitHubApiToken = plainGit;
                MicrosoftClientId = plainMs;
                LittleSkinClientId = plainLs;
            }
        }
        catch { /* 保存失败不阻塞 */ }
    }
}
