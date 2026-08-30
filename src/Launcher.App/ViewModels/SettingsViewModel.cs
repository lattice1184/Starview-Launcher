using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Services;
using Launcher.Core.Launch;
using Launcher.Core.Launch.Sandbox;
using Launcher.Core.Services;
using Launcher.Core.Update;
using Launcher.Core.Utils;

namespace Launcher.App.ViewModels;

/// <summary>强调色预设项（选色器圆点 + 名字）</summary>
public sealed record AccentPresetVM(string Name, string Hex);

/// <summary>内存预设项（Mb=-2 自动按可用内存，0 总内存 60%，Mb=-1 自定义）</summary>
public sealed record MemoryPresetVM(string Name, int Mb)
{
    public bool IsCustom => Mb == -1;
}

/// <summary>下载源策略选项（设置页 ComboBox）</summary>
public sealed record DownloadSourceOption(string Name, DownloadSourcePreference Value);

/// <summary>性能档位选项（设置页 ComboBox）</summary>
public sealed record JvmProfileOption(string Name, PerformanceProfile Value);

/// <summary>进程优先级选项（游戏 JVM 进程；独立设置不随性能档位）</summary>
public sealed record GamePriorityOption(string Name, GamePriority Value);

/// <summary>沙盒模式选项（设置页/主页下拉共用文案）</summary>
public sealed record SandboxModeOption(string Name, SandboxMode Value);

/// <summary>
/// 设置页：游戏目录 / 版本隔离 / 内存预设 / Java 路径 / 额外 JVM 参数 / 下载选项。
/// 所有改动即时写入 settings.json（LauncherSettings.Save）。
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    public List<MemoryPresetVM> MemoryPresets { get; } =
    [
        new("自动（按可用内存）", -2),
        new("低（2G）", 2048),
        new("中（4G）", 4096),
        new("高（8G）", 8192),
        new("极致（总内存 60%）", 0),
        new("自定义", -1),
    ];

    // ---------- 游戏目录 ----------

    /// <summary>当前目录（安装目标）</summary>
    [ObservableProperty]
    public partial string GameDirectoryText { get; set; }

    /// <summary>目录来源标签（本启动器 / 自配 / PCL2 / 官方）</summary>
    [ObservableProperty]
    public partial string SourceLabelText { get; set; }

    /// <summary>版本隔离开关（各版本独立 saves/mods）</summary>
    [ObservableProperty]
    public partial bool VersionIsolation { get; set; }

    // ---------- 启动 ----------

    [ObservableProperty]
    public partial MemoryPresetVM? SelectedMemoryPreset { get; set; }

    /// <summary>自定义内存输入（MB）</summary>
    [ObservableProperty]
    public partial string MemoryCustomText { get; set; } = "";

    public bool IsCustomMemory => SelectedMemoryPreset?.IsCustom == true;

    /// <summary>Java 路径（空 = 自动选配）</summary>
    [ObservableProperty]
    public partial string JavaPathText { get; set; } = "";

    [ObservableProperty]
    public partial string ExtraJvmArgsText { get; set; } = "";

    [ObservableProperty]
    public partial bool AutoChineseEnabled { get; set; } = true;

    /// <summary>8-19 生态下载跟随实例查询（关 = 显示全部版本）</summary>
    [ObservableProperty]
    public partial bool EcoFollowInstance { get; set; } = true;

    /// <summary>性能档位选项与选中项（GC 参数预设；不影响内存）</summary>
    public IReadOnlyList<JvmProfileOption> JvmProfileOptions { get; } =
    [
        new("轻量", PerformanceProfile.Low),
        new("均衡", PerformanceProfile.Medium),
        new("流畅", PerformanceProfile.High),
        new("极致", PerformanceProfile.Ultra),
    ];

    /// <summary>进程优先级档位（低/正常/高/最高——独立于性能档位）</summary>
    public IReadOnlyList<GamePriorityOption> GamePriorityOptions { get; } =
    [
        new("低", GamePriority.BelowNormal),
        new("正常", GamePriority.Normal),
        new("高", GamePriority.AboveNormal),
        new("最高", GamePriority.High),
    ];

    [ObservableProperty]
    public partial JvmProfileOption? SelectedJvmProfile { get; set; }

    /// <summary>当前进程优先级（游戏）</summary>
    [ObservableProperty]
    public partial GamePriorityOption? SelectedGamePriority { get; set; }

    /// <summary>沙盒模式档位（普通/保护/严格隔离——严格隔离断网会挡联机，tooltip 已提示）</summary>
    public IReadOnlyList<SandboxModeOption> SandboxModeOptions { get; } =
    [
        new("普通启动", SandboxMode.Disabled),
        new("保护模式", SandboxMode.Protected),
        new("严格隔离", SandboxMode.StrictIsolation),
    ];

    /// <summary>当前沙盒模式（默认启动模式；主页下拉可临时改单次）</summary>
    [ObservableProperty]
    public partial SandboxModeOption? SelectedSandboxMode { get; set; }

    /// <summary>启动随机小提示（彩蛋开关）</summary>
    [ObservableProperty]
    public partial bool StartupTipEnabled { get; set; } = true;

    // ---------- 更新（8-30 后台静默更新：自动检查开关 + 手动检查 + 重启安装） ----------

    /// <summary>自动检查更新（启动延迟后台检查，有新版下载好后提示重启生效）</summary>
    [ObservableProperty]
    public partial bool AutoCheckUpdate { get; set; } = true;

    /// <summary>更新状态文案（检查中/已是最新/发现新版本/失败原因）</summary>
    [ObservableProperty]
    public partial string UpdateStatusText { get; set; } = "";

    /// <summary>检查更新进行中（禁用按钮防连点）</summary>
    [ObservableProperty]
    public partial bool IsCheckingUpdate { get; set; }

    /// <summary>有新版本已下载就绪（显示「重启安装」按钮）</summary>
    [ObservableProperty]
    public partial bool IsUpdateReady { get; set; }

    /// <summary>就绪的更新包路径（重启安装用）</summary>
    private string? _readyUpdatePath;

    // ---------- 内存优化（8-19 第二批：轻度 GC 零盘写默认开；工作集修剪默认关） ----------

    // 8-25 内存优化区已整体移除（IdleMemoryTuner 假释放，宁缺毋假）

    // ---------- 下载 ----------

    /// <summary>下载源策略选项与选中项（官方优先/镜像优先/仅镜像）</summary>
    public IReadOnlyList<DownloadSourceOption> DownloadSourceOptions { get; } =
    [
        new("官方优先", DownloadSourcePreference.OfficialFirst),
        new("镜像优先", DownloadSourcePreference.MirrorFirst),
        new("仅镜像", DownloadSourcePreference.MirrorOnly),
    ];

    [ObservableProperty]
    public partial DownloadSourceOption? SelectedDownloadSource { get; set; }

    [ObservableProperty]
    public partial int MaxConcurrentDownloads { get; set; }

    /// <summary>下载限速（KB/s；0 = 不限）</summary>
    [ObservableProperty]
    public partial int SpeedLimitKbps { get; set; }

    /// <summary>分片数（每文件并发连接数，1-32；0=用档位默认）</summary>
    [ObservableProperty]
    public partial int ChunkCount { get; set; } = 8;

    /// <summary>Key 有效性验证状态（只含 有效/无效/HTTP 码——**永不包含 key 内容**）</summary>
    [ObservableProperty]
    public partial string CurseForgeApiKeyStatus { get; set; } = "";

    /// <summary>8-13 验证状态图形（Cloudflare 风图标块动画驱动：绿块弹对勾 / 红块叉）</summary>
    public enum CfStatusKind { None, Checking, Valid, Invalid }

    [ObservableProperty]
    public partial CfStatusKind CfStatus { get; set; } = CfStatusKind.None;

    /// <summary>构造加载阶段：属性赋值会触发 OnXxxChanged → Save，此时未加载字段还是默认值——
    /// 若不拦截，会把空值写回文件覆盖已保存的设置（如 CurseForgeApiKey）。</summary>
    private bool _loading = true;

    /// <summary>CF 服务（key 直连：设置 DPAPI 密文落盘；构造含 GameDirectory.Detect() 文件扫描——缓存实例避免每次重扫）</summary>
    private readonly CurseForgeService _curseForge = new();

    /// <summary>CF Key 输入框（PasswordBox；不回显内容。留空提交 = 保留现有 key，不覆盖）</summary>
    [ObservableProperty]
    public partial string CurseForgeApiKeyInput { get; set; } = "";

    /// <summary>8-13 GitHub API Token 输入框（不回显；留空提交 = 保留现有 token，同 CF key 模式）</summary>
    [ObservableProperty]
    public partial string GitHubApiTokenInput { get; set; } = "";

    /// <summary>「检查」入口：先提交输入框里的新 Key（有输入才覆盖），再直连验证</summary>
    public async Task SubmitApiKeyAsync()
    {
        Save(); // CurseForgeApiKeyInput 非空 → 写入设置（DPAPI 加密落盘）后清空输入框
        await ValidateApiKeyAsync();
    }

    /// <summary>直连 CurseForge 验证当前 key（结果只含状态与 HTTP 码，不含 key）</summary>
    public async Task ValidateApiKeyAsync()
    {
        CfStatus = CfStatusKind.Checking;
        CurseForgeApiKeyStatus = "正在检查…";
        try
        {
            if (!_curseForge.IsEnabled)
            {
                CfStatus = CfStatusKind.None;
                CurseForgeApiKeyStatus = "请先填写 API Key";
                return;
            }
            var (valid, message) = await _curseForge.ValidateKeyAsync();
            if (valid)
            {
                CfStatus = CfStatusKind.Valid;
                CurseForgeApiKeyStatus = "你的 API 有效，并已被 DPAPI 加密保护";
            }
            else
            {
                CfStatus = CfStatusKind.Invalid;
                CurseForgeApiKeyStatus = message;
            }
        }
        catch
        {
            CfStatus = CfStatusKind.Invalid;
            CurseForgeApiKeyStatus = "连不上 CurseForge API，稍后再试";
        }
    }

    /// <summary>CurseForge 文件 CDN 镜像前缀（空 = 官方 CDN 直连）</summary>
    [ObservableProperty]
    public partial string CurseForgeCdnPrefixText { get; set; } = "";

    /// <summary>8-16 批次 52 CF API 地址覆盖（空 = 官方；填代理地址绕开直连抖动）</summary>
    [ObservableProperty]
    public partial string CurseForgeApiBaseText { get; set; } = "";

    /// <summary>8-20 全局代理（host:port，如 127.0.0.1:7890；空 = 直连）。仅 Http 代理——加速器开本地端口填这里</summary>
    [ObservableProperty]
    public partial string ProxyAddressText { get; set; } = "";

    // ---------- 外观 ----------

    /// <summary>窗口观感档（透明 Blur / 实色）——两档单选，点击即落盘生效</summary>
    [ObservableProperty]
    public partial OpacityMode Opacity { get; set; } = OpacityMode.Blur;

    /// <summary>当前强调色（#RRGGBB）</summary>
    [ObservableProperty]
    public partial string AccentColor { get; set; } = "#6C8CFF";

    /// <summary>背景色（#RRGGBB 或 #AARRGGBB，含 alpha 透明；预览不写盘）</summary>
    [ObservableProperty]
    public partial string BackgroundColor { get; set; } = BackgroundPaletteMath.DefaultBackground;

    partial void OnBackgroundColorChanged(string value) => PreviewChanged?.Invoke();

    /// <summary>背景色 Avalonia 值（ColorPicker 双向绑定用；用户改色 → 同步回字符串）</summary>
    [ObservableProperty]
    public partial Avalonia.Media.Color BackgroundColorValue { get; set; } = Avalonia.Media.Color.Parse(BackgroundPaletteMath.DefaultBackground);

    partial void OnBackgroundColorValueChanged(Avalonia.Media.Color value) => BackgroundColor = value.ToString(); // #AARRGGBB

    /// <summary>界面密度（默认紧凑）</summary>
    [ObservableProperty]
    public partial int DensityIndex { get; set; } = 1; // AL7：默认标准（0=紧凑 1=标准 2=舒适）

    /// <summary>外观变化（MainWindow/App 应用透明度/强调色/密度）</summary>
    public event Action? AppearanceChanged;

    /// <summary>外观预览（点击选项即时预览，不写盘；保存才持久化）</summary>
    public event Action? PreviewChanged;

    /// <summary>开源声明（关于页；静态清单见 Models/ThirdPartyLicenses.cs）</summary>
    public IReadOnlyList<string> ProjectNotices => Models.ThirdPartyLicenses.ProjectNotices;

    /// <summary>8-31 更新内容版本分组（关于页 + 升级弹窗共用 ChangelogCatalog；数据在 Core 便于弹窗复用）</summary>
    public IReadOnlyList<ChangelogCatalog.ChangelogGroup> ChangelogGroups => ChangelogCatalog.Groups;

    /// <summary>关于页「最近更新」：最新版本组条目取前 5 条（v1.1.9 起）</summary>
    public IEnumerable<string> ChangelogItemsRecent => ChangelogCatalog.Groups
        .Where(g => g.Version != ChangelogCatalog.HistoricalVersion)
        .SelectMany(g => g.Items)
        .Take(5);

    /// <summary>8-23 赞助者名单（用户的资金支持——启动器开发离不开他们）</summary>
    public IReadOnlyList<string> Sponsors { get; } =
    [
        "jam🐏", "磊", "心做时间", "鳞x梦", "邪恶无极限", "懿筱'", "彭鱼宴", "江Lay",
    ];

    /// <summary>「存储」分区（存储占用/上限/清理）</summary>
    public StorageSettingsViewModel Storage { get; } = new();

    /// <summary>第三方依赖清单（关于页折叠区）</summary>
    public IReadOnlyList<Models.ThirdPartyLicense> ThirdPartyPackages => Models.ThirdPartyLicenses.Packages;

    /// <summary>启动器版本（关于页；读嵌入 PCL.metadata.json）</summary>
    public string AppVersion => $"v{PCL.Core.App.Basics.VersionName}";

    /// <summary>预设强调色（圆点+名字；非预设颜色动态插入「自定义 #HEX」项）</summary>
    public static IReadOnlyList<AccentPresetVM> AccentPresets { get; } =
    [
        new("靛蓝", "#6C8CFF"),
        new("蓝", "#3B82F6"),
        new("紫", "#8B5CF6"),
        new("琥珀", "#F59E0B"),
        new("玫红", "#EC4899"),
        new("红", "#EF4444"),
        new("绿", "#22C55E"),
        new("橙", "#F97316"),
    ];

    /// <summary>选色器列表（含自定义兜底项）</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AccentPresetVM> AccentPresetItems { get; set; } = AccentPresets;

    /// <summary>当前选中的预设（null = 自定义色未匹配，回退显示 hex）</summary>
    [ObservableProperty]
    public partial AccentPresetVM? SelectedAccent { get; set; }

    partial void OnSelectedAccentChanged(AccentPresetVM? value)
    {
        if (value is not null) AccentColor = value.Hex; // 触发 PreviewChanged 预览
    }

    public SettingsViewModel()
    {
        var s = LauncherSettings.Current;
        GameDirectoryText = GameDirectory.InstallDir();
        SourceLabelText = GameDirectory.SourceLabel(GameDirectory.DetectSource());
        VersionIsolation = s.VersionIsolation;
        SelectedMemoryPreset = MemoryPresets.FirstOrDefault(p => p.Mb == s.MemoryMb)
            ?? MemoryPresets[^1]; // 非预设值 → 自定义
        MemoryCustomText = s.MemoryMb > 0 ? s.MemoryMb.ToString() : "";
        JavaPathText = s.JavaPath ?? "";
        ExtraJvmArgsText = s.ExtraJvmArgs ?? "";
        AutoChineseEnabled = s.AutoChineseEnabled;
        EcoFollowInstance = s.EcoFollowInstance;
        SelectedJvmProfile = JvmProfileOptions.FirstOrDefault(o => o.Value == s.JvmProfile) ?? JvmProfileOptions[1];
        SelectedGamePriority = GamePriorityOptions.FirstOrDefault(o => o.Value == s.GamePriority) ?? GamePriorityOptions[1];
        SelectedSandboxMode = SandboxModeOptions.FirstOrDefault(o => o.Value == s.SandboxMode) ?? SandboxModeOptions[0];
        StartupTipEnabled = s.StartupTipEnabled;
        AutoCheckUpdate = s.AutoCheckUpdate;
        SelectedDownloadSource = DownloadSourceOptions.FirstOrDefault(o => o.Value == s.DownloadSource) ?? DownloadSourceOptions[0];
        MaxConcurrentDownloads = s.MaxConcurrentDownloads;
        SpeedLimitKbps = s.DownloadSpeedLimitKbps;
        ChunkCount = s.ChunkCount > 0 ? s.ChunkCount : (int)s.DownloadTier; // 老用户继承当前档位，新装默认 8
        // CF key 不回显（PasswordBox 留空）；设置里的值是 DPAPI 密文，状态区显示验证结果
        CurseForgeCdnPrefixText = s.CurseForgeCdnPrefix ?? "";
        CurseForgeApiBaseText = s.CurseForgeApiBase ?? "";
        ProxyAddressText = s.ProxyAddress ?? "";
        Opacity = s.Opacity;
        DensityIndex = (int)s.Density;
        BackgroundImagePathText = s.BackgroundImagePath ?? "";
        // 强调色：非预设值（老用户自定义）动态插「自定义 #HEX」项；选中项触发 AccentColor 赋值预览
        AccentColor = s.AccentColor;
        BackgroundColor = s.BackgroundColor ?? BackgroundPaletteMath.DefaultBackground; // 旧 JSON 缺键防御
        BackgroundColorValue = Avalonia.Media.Color.Parse(BackgroundColor); // 同步 ColorPicker
        if (AccentPresets.All(p => p.Hex != s.AccentColor))
        {
            AccentPresetItems = AccentPresets
                .Prepend(new AccentPresetVM($"自定义 {s.AccentColor.ToUpperInvariant()}", s.AccentColor))
                .ToList();
        }
        SelectedAccent = AccentPresetItems.FirstOrDefault(p => p.Hex == s.AccentColor);

        _ = ValidateApiKeyAsync(); // 打开设置页即查一次代理状态（只含状态，不含 key）

        _loading = false; // 加载完成，之后属性变化才允许落盘
    }

    // ---------- 写入 ----------

    /// <summary>8-14 GitHub token 输入框失焦即保存（此前「改其他设置才顺带落盘」——填完就关设置页会丢 token）</summary>
    public void SaveGitHubApiToken()
    {
        if (_loading) return;
        if (!string.IsNullOrWhiteSpace(GitHubApiTokenInput))
        {
            Save();
            NotificationService.Success("GitHub API Token 已加密保存");
        }
        else
        {
            NotificationService.Info("先粘贴 Token 再点别处保存");
        }
    }

    private void Save()
    {
        if (_loading) return; // 构造加载阶段不落盘（防未加载字段的空值覆盖）
        var s = LauncherSettings.Current;
        s.VersionIsolation = VersionIsolation;
        s.JavaPath = string.IsNullOrWhiteSpace(JavaPathText) ? null : JavaPathText.Trim();
        s.ExtraJvmArgs = string.IsNullOrWhiteSpace(ExtraJvmArgsText) ? null : ExtraJvmArgsText.Trim();
        s.AutoChineseEnabled = AutoChineseEnabled;
        s.EcoFollowInstance = EcoFollowInstance;
        s.DownloadSource = SelectedDownloadSource?.Value ?? DownloadSourcePreference.OfficialFirst;
        s.JvmProfile = SelectedJvmProfile?.Value ?? PerformanceProfile.Medium;
        s.GamePriority = SelectedGamePriority?.Value ?? GamePriority.Normal;
        s.SandboxMode = SelectedSandboxMode?.Value ?? SandboxMode.Disabled;
        s.StartupTipEnabled = StartupTipEnabled;
        s.AutoCheckUpdate = AutoCheckUpdate;
        s.MaxConcurrentDownloads = MaxConcurrentDownloads;
        s.DownloadSpeedLimitKbps = SpeedLimitKbps;
        s.ChunkCount = ChunkCount;
        s.CurseForgeCdnPrefix = CurseForgeCdnPrefixText.Trim();
        s.CurseForgeApiBase = CurseForgeApiBaseText.Trim();
        s.ProxyAddress = ProxyAddressText.Trim(); // 8-20 全局代理（host:port）
        // CF key：仅输入框有非空内容才覆盖（留空 = 保留现有 key，防误清空）；Save 内 DPAPI 加密落盘
        if (!string.IsNullOrWhiteSpace(CurseForgeApiKeyInput))
        {
            s.CurseForgeApiKey = CurseForgeApiKeyInput.Trim();
            CurseForgeApiKeyInput = ""; // 写入后清空输入（不回显，二次保存不重复写）
        }
        // 8-13 GitHub token：同 CF key 模式（留空 = 保留现有——普通用户未认证模式）
        if (!string.IsNullOrWhiteSpace(GitHubApiTokenInput))
        {
            s.GitHubApiToken = GitHubApiTokenInput.Trim();
            GitHubApiTokenInput = ""; // 写入后清空输入（不回显）
        }
        // 8-13 微软 client_id：设置页已移除（登录有远程下发/缓存/内置三层兜底，无需用户填；
        // LauncherSettings.MicrosoftClientId 字段保留——高级用户可手动编辑 settings.json 覆盖）
        s.Save();
        // 8-20 代理变更后重建共享连接池：新下载任务立即走新代理（进行中任务不受影响）
        Launcher.Core.Download.HttpClientPool.RebuildShared();
    }

    partial void OnVersionIsolationChanged(bool value) => Save();

    partial void OnSelectedMemoryPresetChanged(MemoryPresetVM? value)
    {
        if (_loading) return; // 构造加载阶段：仅完成 UI 赋值，不落盘
        OnPropertyChanged(nameof(IsCustomMemory));
        if (value is { } preset)
        {
            if (preset.IsCustom) return; // 自定义值从输入框提交
            LauncherSettings.Current.MemoryMb = preset.Mb;
            LauncherSettings.Current.Save();
        }
    }

    /// <summary>自定义内存输入提交（回车/失焦）</summary>
    public void ApplyCustomMemory(string text)
    {
        if (!IsCustomMemory) return;
        if (int.TryParse(text, out var mb) && mb >= 512)
        {
            LauncherSettings.Current.MemoryMb = mb;
            LauncherSettings.Current.Save();
        }
    }

    partial void OnJavaPathTextChanged(string value) => Save();
    partial void OnExtraJvmArgsTextChanged(string value) => Save();
    partial void OnAutoChineseEnabledChanged(bool value) => Save();
    partial void OnEcoFollowInstanceChanged(bool value) => Save();
    partial void OnSelectedJvmProfileChanged(JvmProfileOption? value) => Save();

    /// <summary>进程优先级改动即时保存</summary>
    partial void OnSelectedGamePriorityChanged(GamePriorityOption? value) => Save();

    /// <summary>沙盒模式改动即时保存（主页下拉与之联动的是 LauncherSettings.Current.SandboxMode）</summary>
    partial void OnSelectedSandboxModeChanged(SandboxModeOption? value) => Save();
    partial void OnStartupTipEnabledChanged(bool value)
    {
        Save();
        NotificationService.Info(value ? "已开启小提示，下次启动生效" : "已关闭小提示，下次启动生效");
    }
    partial void OnAutoCheckUpdateChanged(bool value) => Save(); // 即时落盘（后台检查下次启动生效）
    partial void OnSelectedDownloadSourceChanged(DownloadSourceOption? value) => Save();
    // 滑块拖动连续触发——150ms 防抖写盘（避免每 tick 写 settings.json）
    private CancellationTokenSource? _saveDebounce;

    partial void OnMaxConcurrentDownloadsChanged(int value) => DebouncedSave();
    partial void OnSpeedLimitKbpsChanged(int value) => DebouncedSave();
    partial void OnChunkCountChanged(int value) => DebouncedSave(); // 滑块拖动防抖写盘
    partial void OnCurseForgeCdnPrefixTextChanged(string value) => Save();

    /// <summary>8-16 CF API 地址覆盖：变更即落盘（ToApiBase 动态读设置，即时生效）</summary>
    partial void OnCurseForgeApiBaseTextChanged(string value) => Save();

    // 外观：预览模式（改动即时预览，[保存并应用] 才写盘）。
    // 8-23 窗口观感两档例外：单选点击即时落盘（持久值恒等于实时值），两档点击无连续触发、不需防抖。
    partial void OnOpacityChanged(OpacityMode value)
    {
        PreviewChanged?.Invoke();
        if (_loading) return;
        LauncherSettings.Current.Opacity = value;
        LauncherSettings.Current.Save();
    }
    partial void OnAccentColorChanged(string value) => PreviewChanged?.Invoke();
    partial void OnDensityIndexChanged(int value) => PreviewChanged?.Invoke();

    /// <summary>背景图片路径（""=无；预览模式，保存才写盘）</summary>
    [ObservableProperty]
    public partial string BackgroundImagePathText { get; set; } = "";

    /// <summary>是否已设置背景（缩略预览/移除按钮显隐）</summary>
    public bool HasBackgroundImage => BackgroundImagePathText.Length > 0;

    /// <summary>背景预览位图（8-26 限宽 320 解码——此前绑路径字符串被 Avalonia 类型转换器全尺寸解码，
    /// 4K 壁纸常驻 ~33MB、1080p 也 ~8MB，换图不释放。这是单块最大的可砍内存）</summary>
    [ObservableProperty]
    public partial IImage? BackgroundPreview { get; set; }

    private string? _previewPath; // 快速连选竞态守卫：解码回来只认最后一次选的路径

    partial void OnBackgroundImagePathTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasBackgroundImage));
        PreviewChanged?.Invoke();
        _ = RefreshBackgroundPreviewAsync(value);
    }

    private async Task RefreshBackgroundPreviewAsync(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            DisposePreview();
            BackgroundPreview = null;
            return;
        }
        _previewPath = path;
        try
        {
            var bmp = await Task.Run(() =>
            {
                using var fs = File.OpenRead(path);
                return Bitmap.DecodeToWidth(fs, 320);
            });
            if (_previewPath != path) { bmp.Dispose(); return; } // 已改选，丢弃过期预览
            DisposePreview();
            BackgroundPreview = bmp;
        }
        catch { /* 解码失败不打断设置流程（预览留空） */ }
    }

    private void DisposePreview()
    {
        if (BackgroundPreview is IDisposable d) { try { d.Dispose(); } catch { } }
    }

    /// <summary>选择图片后应用（View code-behind FilePicker 回调；预览不写盘）</summary>
    public void ApplyBackgroundImage(string path) => BackgroundImagePathText = path;

    /// <summary>应用自定义强调色（View 已校验格式）：预览 + 非预设时插入「自定义 #HEX」项并选中</summary>
    public void ApplyCustomAccent(string hex)
    {
        AccentColor = hex; // 触发 PreviewChanged 预览
        if (AccentPresets.All(p => p.Hex != hex))
        {
            var item = new AccentPresetVM($"自定义 {hex.ToUpperInvariant()}", hex);
            AccentPresetItems = AccentPresets.Prepend(item).ToList();
            SelectedAccent = item;
        }
    }

    /// <summary>移除背景（还原亚克力纯色；预览不写盘）</summary>
    [RelayCommand]
    public void RemoveBackgroundImage() => BackgroundImagePathText = "";

    /// <summary>8-31 后台自动检查成功：把就绪状态落常驻（关于页「重启安装」按钮出现，不靠瞬时 Toast）。
    /// 后台任务（App.axaml.cs）调用——与手动 CheckUpdate 共用同一就绪状态。</summary>
    public void SetUpdateReady(string tag, string path)
    {
        _readyUpdatePath = path;
        IsUpdateReady = true;
        UpdateStatusText = $"发现新版本 {tag}，已下载，重启后生效";
    }

    /// <summary>8-31 后台自动检查失败：写进关于页状态（不再静默——用户能看到「上次自动检查失败」）</summary>
    public void SetAutoCheckFailed(string error)
    {
        UpdateStatusText = $"上次自动检查失败：{error}，可点「检查更新」重试";
    }

    /// <summary>手动检查更新（force 忽略冷却；有新版本自动后台下载到就绪）</summary>
    [RelayCommand]
    private async Task CheckUpdate()
    {
        IsCheckingUpdate = true;
        IsUpdateReady = false;
        UpdateStatusText = "检查中…";
        try
        {
            var r = await UpdateCheckService.CheckAsync(PCL.Core.App.Basics.VersionName, force: true);
            if (r.HasUpdate)
            {
                _readyUpdatePath = r.ReadyPath;
                IsUpdateReady = true;
                UpdateStatusText = $"发现新版本 {r.LatestTag}，已下载，重启后生效";
            }
            else if (r.Error is not null)
            {
                UpdateStatusText = r.Error;
            }
            else
            {
                UpdateStatusText = $"已是最新版本（{PCL.Core.App.Basics.VersionName}）";
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"检查失败：{ex.Message}";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    /// <summary>重启安装已就绪的更新（Windows 子进程 / Unix 延迟脚本接管替换与重启）</summary>
    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (string.IsNullOrEmpty(_readyUpdatePath)) return;
        var err = await UpdateInstaller.StartAsync(_readyUpdatePath);
        if (err is not null)
        {
            NotificationService.Error($"更新失败：{err}");
            return;
        }
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.Shutdown();
        }
        catch { }
        Environment.Exit(0);
    }

    /// <summary>设置-游戏目录「打开文件夹」：explorer 定位当前安装目录</summary>
    [RelayCommand]
    private void OpenGameDirectory() => FolderOpener.Open(GameDirectory.InstallDir());

    /// <summary>保存并应用外观（写盘 + 持久应用）</summary>
    [RelayCommand]
    private void SaveAppearance()
    {
        var s = LauncherSettings.Current;
        s.Opacity = Opacity;
        s.AccentColor = AccentColor;
        s.BackgroundColor = BackgroundColor;
        s.Density = (DensityMode)DensityIndex;
        s.BackgroundImagePath = string.IsNullOrWhiteSpace(BackgroundImagePathText) ? null : BackgroundImagePathText;
        s.Save();
        AppearanceChanged?.Invoke();
        NotificationService.Success("外观已保存并应用");
    }

    /// <summary>重置外观（恢复默认：透明档 / 靛蓝 / 标准 / 无背景）</summary>
    [RelayCommand]
    private async Task ResetAppearance()
    {
        var owner = DialogService.MainWindow();
        if (owner is null || !await DialogService.Confirm(owner,
                "把外观（观感档/强调色/密度/背景）重置回默认？", "重置外观", "重置", "取消"))
        {
            return;
        }
        Opacity = OpacityMode.Blur;
        AccentColor = "#6C8CFF";
        BackgroundColor = BackgroundPaletteMath.DefaultBackground;
        BackgroundColorValue = Avalonia.Media.Color.Parse(BackgroundColor); // 同步 ColorPicker
        DensityIndex = 1; // 默认标准（AL7：不再默认紧凑缩小 10%）
        BackgroundImagePathText = "";
        PreviewChanged?.Invoke();
        NotificationService.Success("已重置为默认外观（点击「保存并应用」生效）");
    }

    private async void DebouncedSave()
    {
        _saveDebounce?.Cancel();
        var cts = _saveDebounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(150, cts.Token);
            Save();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>游戏目录：浏览选择后应用（由 View code-behind 的 FolderPicker 回调）</summary>
    public void ApplyGameDirectory(string path)
    {
        var s = LauncherSettings.Current;
        s.GameDirectory = path;
        s.Save();
        GameDirectoryText = GameDirectory.InstallDir();
        SourceLabelText = GameDirectory.SourceLabel(GameDirectory.DetectSource());
        // 8-22：实例根跟随目录选择 + 清扫描缓存（下次扫描反映新目录）
        Launcher.Core.AppState.UpdateInstanceRoot(path);
        Launcher.Core.Utils.GameDirectory.InvalidateScanCache();
    }

    /// <summary>游戏目录：重置为默认（D 盘优先）</summary>
    public void ResetGameDirectory()
    {
        var s = LauncherSettings.Current;
        s.GameDirectory = null;
        s.Save();
        GameDirectoryText = GameDirectory.InstallDir();
        SourceLabelText = "本启动器";
        // 8-22：实例根回退默认 + 清扫描缓存
        Launcher.Core.AppState.UpdateInstanceRoot(GameDirectory.InstallDir());
        Launcher.Core.Utils.GameDirectory.InvalidateScanCache();
    }

    /// <summary>Java 路径：浏览选择后应用（FilePicker 回调）</summary>
    public void ApplyJavaPath(string path)
    {
        JavaPathText = path;
        Save();
    }

    /// <summary>Java 路径：恢复自动选配</summary>
    public void ResetJavaPath()
    {
        JavaPathText = "";
        Save();
    }

    /// <summary>清理下载缓存：删除断点续传残留的 *.parts 临时目录（不影响已装版本）</summary>
    public (int Dirs, long Bytes) ClearDownloadCache()
    {
        var gameDir = LauncherSettings.Current.GameDirectory ?? GameDirectory.Detect();
        var removed = 0;
        long freed = 0;
        if (Directory.Exists(gameDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(gameDir, "*.parts", SearchOption.AllDirectories))
            {
                try
                {
                    freed += DirSize(dir);
                    Directory.Delete(dir, true);
                    removed++;
                }
                catch { /* 占用中跳过 */ }
            }
        }
        return (removed, freed);
    }

    private static long DirSize(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }
}
