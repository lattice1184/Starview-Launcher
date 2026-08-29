using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Services;
using Launcher.Core.Account;
using Launcher.Core.Diagnostics;
using Launcher.Core.Download;
using Launcher.Core.Launch;
using Launcher.Core.Services;
using Launcher.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Launcher.App.ViewModels;

/// <summary>启动日志行类别：普通 / 报错（ERROR/WARN/FATAL/异常）/ 启动器事件(§)</summary>
public enum LogLineKind { Normal, Error, Launcher }

/// <summary>启动日志行（控制台显示）：文本 + 类别（着色用）</summary>
public sealed record LogLine(string Text, LogLineKind Kind);

/// <summary>
/// 主页：玩家信息 + 版本选择 + 启动状态机（阶段指示条）+ 游戏控制台。
/// </summary>
public partial class HomeViewModel : ViewModelBase
{
    /// <summary>8-19 主页快捷入口（百宝箱式）：复用设置页的点击式释放命令</summary>
    public SettingsViewModel Settings => MainViewModel.Current?.Settings!;

    private static readonly string[] StageNames =
        ["解析版本", "检测 Java", "解压 natives", "启动 JVM", "游戏加载中", "运行中"];

    private readonly GameLaunchService _launcher = new();
    private readonly AccountService _accounts = AccountService.Shared;
    private LaunchProcess.LaunchResult? _running;
    private const int MaxLogLines = 300; // 8-26 内存瘦身：500→300（每行可能含长堆栈串）
    private volatile bool _userStopped;

    public ObservableCollection<VersionInstanceVM> InstalledVersions { get; } = [];

    /// <summary>8-18 内存让渡：切走主页时释放版本列表（切回时 RefreshVersionsAsync 重建）</summary>
    public void ReleaseData() => InstalledVersions.Clear();
    public ObservableCollection<LaunchStageVM> Stages { get; } = [];

    [ObservableProperty]
    public partial VersionInstanceVM? SelectedVersion { get; set; }

    /// <summary>主页版本选择是全局权威：同步到 MainViewModel.CurrentVersion（下载/开服页跟随）+ Core AppState（修复/日志读）</summary>
    partial void OnSelectedVersionChanged(VersionInstanceVM? value)
    {
        MainViewModel.Current!.CurrentVersion = value;
        Launcher.Core.AppState.SetCurrentVersion(value?.Name); // 8-22 步骤1：Core 层统一状态
        OnPropertyChanged(nameof(CanLaunch)); // 8-26 启动按钮可点性随版本变化
    }

    /// <summary>8-26 启动更直接：无版本/已在运行/正在启动时启动按钮置灰（不再弹「你还没选版本」模态）</summary>
    public bool CanLaunch => SelectedVersion is not null && !IsLaunching && !IsRunning;

    [ObservableProperty]
    public partial string LaunchState { get; set; } = "就绪";

    /// <summary>启动状态圆点颜色（状态语言与阶段指示条一致：灰=待机、青=进行、红=失败）</summary>
    public IBrush StateColor
    {
        get
        {
            var s = LaunchState;
            if (s == "失败" || s.StartsWith("异常退出")) return new SolidColorBrush(Color.Parse("#E05A5A"));
            if (s is "运行中" or "准备中") return new SolidColorBrush(Color.Parse("#6C8CFF"));
            if (s.StartsWith("已退出")) return new SolidColorBrush(Color.Parse("#6F7B90"));
            return new SolidColorBrush(Color.Parse("#3A4250"));
        }
    }

    partial void OnLaunchStateChanged(string value) => OnPropertyChanged(nameof(StateColor));

    [ObservableProperty]
    public partial string LaunchStatus { get; set; } = "选择版本并启动";

    /// <summary>启动失败且为客户端文件缺失时显示修复入口（去版本页补全 / 官方下载）</summary>
    [ObservableProperty]
    public partial bool ShowRepairGuide { get; set; }

    public string RepairGuideText => "客户端文件缺失，可补全下载或去官方页面：";

    private string? _lastLaunchVersionId;

    /// <summary>AL9 自修复：本次启动是否已应用过自动修复（最多一次；重试经递归调用不重置）</summary>
    private bool _autoFixApplied;

    /// <summary>跳版本页并选中该版本（补全下载；等待列表加载完成再选中）</summary>
    [RelayCommand]
    private async Task GoRepair() => await (MainViewModel.Current?.NavigateToVersionAsync(_lastLaunchVersionId) ?? Task.CompletedTask);

    /// <summary>打开官方下载页（minecraft.net）</summary>
    [RelayCommand]
    private void OpenOfficialDownload()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://www.minecraft.net/zh-hans/download") { UseShellExecute = true });
        }
        catch { /* 无法打开浏览器忽略 */ }
    }

    /// <summary>启动配置摘要（内存/Java/隔离，显示在启动区小字）</summary>
    [ObservableProperty]
    public partial string LaunchConfigText { get; set; } = "";

    /// <summary>从版本页请求启动（自动选中版本并走 Launch 流程）</summary>
    public async Task RequestLaunchAsync(string versionId, string gameDir)
    {
        await RefreshVersionsAsync();
        var found = InstalledVersions.FirstOrDefault(v => v.Name.Equals(versionId, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            InstalledVersions.Add(new VersionInstanceVM(versionId, "本启动器", gameDir));
            found = InstalledVersions[^1];
        }
        SelectedVersion = found;
        await LaunchAsync();
    }

    /// <summary>刷新配置摘要（启动区小字；设置页改动后切回主页即更新）</summary>
    public void RefreshConfigText()
    {
        var s = LauncherSettings.Current;
        var mem = s.MemoryMb > 0 ? $"{s.MemoryMb / 1024.0:0.#}G" : "总内存 60%";
        var java = string.IsNullOrWhiteSpace(s.JavaPath) ? "自动" : Path.GetFileName(s.JavaPath);
        var iso = s.VersionIsolation ? "隔离" : "共享";
        LaunchConfigText = $"内存 {mem} · Java {java} · 版本{iso}";
    }

    [ObservableProperty]
    public partial bool IsLaunching { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    /// <summary>运行状态上报全局（版本页徽章；服务端运行不覆盖客户端）</summary>
    partial void OnIsRunningChanged(bool value)
    {
        var main = MainViewModel.Current;
        if (main is null) return;
        if (value)
            main.RunningVersion = new RunningVersionInfo(SelectedVersion?.Name ?? "", "客户端");
        else if (main.RunningVersion?.Kind == "客户端")
            main.RunningVersion = null;
        OnPropertyChanged(nameof(CanLaunch));
    }

    /// <summary>启动按钮可点性随启动态变化</summary>
    partial void OnIsLaunchingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(LogBodyVisible)); // 8-26 日志卡主体：启动中或看历史时展开
    }

    /// <summary>8-27 日志卡主体可见性：控制台 tab 选中就常显（空时显示「控制台还没动静」占位，不再空壳）；
    /// 启动中自动展开。8-26 曾改为「平时折叠成一条」，用户反馈主页控制台空白——改回常显。</summary>
    public bool LogBodyVisible => IsConsoleTabSelected || IsHistoryTabSelected || IsLaunching;

    partial void OnIsHistoryTabSelectedChanged(bool value) => OnPropertyChanged(nameof(LogBodyVisible));

    partial void OnIsConsoleTabSelectedChanged(bool value) => OnPropertyChanged(nameof(LogBodyVisible));

    [ObservableProperty]
    public partial double LaunchProgress { get; set; }

    [ObservableProperty]
    public partial int CurrentStageIndex { get; set; } = -1;

    [ObservableProperty]
    /// <summary>主页头像（8-15 改为 IImage：本地皮肤 CroppedBitmap 裁脸 / 网络 minotar 位图）</summary>
    public partial IImage? PlayerAvatar { get; set; }

    /// <summary>8-13 头像未就绪时的首字母占位（网络头像下载期间不露白）</summary>
    [ObservableProperty]
    public partial string PlayerAvatarFallback { get; set; } = "";

    [ObservableProperty]
    public partial string PlayerName { get; set; } = "未登录";

    /// <summary>账号类型徽章（正版/离线/未登录）——账号页已融合进主页，头像 Popup 承载管理</summary>
    [ObservableProperty]
    public partial string AccountTypeText { get; set; } = "未登录";

    /// <summary>账号管理（登录/切换/删除，头像 Popup 面板承载）</summary>
    public AccountViewModel Account { get; } = new();

    public ObservableCollection<LogLine> GameLogs { get; } = [];

    /// <summary>启动记录（跨会话，可回看失败原因）</summary>
    public ObservableCollection<LaunchHistoryEntry> LaunchHistory { get; } = [];

    // ---------- 日志卡 Tab（控制台 / 启动记录） ----------

    [ObservableProperty]
    public partial bool IsConsoleTabSelected { get; set; } = true;

    [ObservableProperty]
    public partial bool IsHistoryTabSelected { get; set; }

    /// <summary>控制台是否有日志（空状态显示用）</summary>
    [ObservableProperty]
    public partial bool HasLogs { get; set; }

    /// <summary>是否有启动记录（空状态显示用）</summary>
    [ObservableProperty]
    public partial bool HasHistory { get; set; }

    /// <summary>启动记录条数（Tab 计数徽章）</summary>
    [ObservableProperty]
    public partial int HistoryCount { get; set; }

    [RelayCommand]
    private void SwitchLogTab(string tab)
    {
        IsConsoleTabSelected = tab == "console";
        IsHistoryTabSelected = tab == "history";
    }

    private System.Diagnostics.Stopwatch? _launchWatch;

    // 8-28 启动前模组检查可跳过（大型整合包交给用户决定）：IsPreCheckRunning 显示「跳过模组检查」按钮
    private CancellationTokenSource? _preCheckCts;

    [ObservableProperty]
    public partial bool IsPreCheckRunning { get; set; }

    [RelayCommand]
    private void SkipPreCheck() => _preCheckCts?.Cancel();

    public HomeViewModel()
    {
        foreach (var name in StageNames) Stages.Add(new LaunchStageVM(name));
        // 账号状态实时同步：账号页登录/切换/退出后主页玩家区立即刷新
        _accounts.Changed += RefreshPlayer;
        LaunchHistoryService.Changed += ReloadLaunchHistory;
        ReloadLaunchHistory();
    }

    private void ReloadLaunchHistory()
    {
        LaunchHistory.Clear();
        foreach (var h in LaunchHistoryService.All) LaunchHistory.Add(h);
        HistoryCount = LaunchHistory.Count;
        HasHistory = LaunchHistory.Count > 0;
    }

    [RelayCommand]
    private async Task ClearLaunchHistory()
    {
        var owner = DialogService.MainWindow();
        if (owner is null || !await DialogService.Confirm(owner, "清除全部启动记录？", "清除记录", "清除", "取消"))
            return;
        LaunchHistoryService.Clear();
    }

    public async Task InitializeAsync()
    {
        // 8-22 同步重活挪后台：账号 DPAPI 解密 + 目录迁移不阻塞 MainViewModel 构造——
        // 目录窗口已提前弹出，主 VM 应立即返回让启动链跑完（不卡目录窗口出现）
        await Task.Run(() =>
        {
            _accounts.Load();
            // 目录树重构（AE3）：旧 .minecraft\servers 一次性迁移到启动器目录树 servers\
            Launcher.Core.Server.ServerInstaller.MigrateLegacy(GameDirectory.InstallDir());
        });
        RefreshPlayer();
        RefreshConfigText();
        await RefreshVersionsAsync();
        // 8-19 启动清理（后台，失败静默）：跨源预取残留目录（主页隐藏的占位）+ 过期下载缓存——
        // 用户视角「删了版本但数据夹里还残留」的观感来源，启动时顺带清
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var (dir, _) in GameDirectory.ScanSourceDirs())
                    Launcher.Core.Download.VersionInstaller.CleanupOrphanPrefetches(dir);
                CleanExpiredCache();
            }
            catch { }
        });
        // 8-22 移除 TrimStartup：启动峰值后立即做全代 compacting GC（STW 暂停）
        // 会打断「版本扫描/加载器加载」的响应——改为由闲置定时器（5 分钟无操作）自然接管，
        // GC 只在真正闲置时静默进行，保证启动/加载体验（用户拍板保留自动机制、仅消除启动干扰）
    }

    /// <summary>8-19 过期下载缓存清理：AppData\Launcher\cache 的 eco-*/loader-* json
    /// 超过 24h 未使用即删（搜索 5min/版本列表 30min/详情 24h 分级 TTL 的保守上限）——此前过期文件永不清理会累积</summary>
    private static void CleanExpiredCache()
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Launcher", "cache");
        if (!Directory.Exists(cacheDir)) return;
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
        foreach (var f in Directory.GetFiles(cacheDir))
        {
            try
            {
                var name = Path.GetFileName(f);
                if (!name.StartsWith("eco-", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("loader-", StringComparison.OrdinalIgnoreCase)) continue;
                if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
            }
            catch { /* 单文件清理失败跳过 */ }
        }
    }

    /// <summary>
    /// 刷新已安装版本列表（下载/安装完成后切回主页时调用——列表不能停留在启动时的快照）。
    /// 跨所有扫描源（自建目录 + PCL/官方等已有环境）；来源标签按版本判定：
    /// 本启动器安装的标"本启动器"（.yanla-installed 标记）；否则标所在目录来源（PCL2 / 官方 / 自配）。
    /// </summary>
    public async Task RefreshVersionsAsync()
    {
        // 8-23 主页版本消失修复：原实现整体 try/catch 空吞——切回主页时若 manifest 拉取抛异常
        // （断网 + 无 24h 内缓存），重建被整个跳过，InstalledVersions 停留在 ReleaseData 清空后的空态，
        // 主页版本列表永久空白。现拆三个独立 try-catch：manifest 失败只丢清单条目（磁盘扫描兜底）、
        // 磁盘扫描失败退化为仅 manifest、单版本损坏只跳过该版——重建与自动选中无条件执行。

        // ① manifest 拉取（可能因网络/镜像失败）：失败仅清空 entries，不清列表
        IReadOnlyList<VersionManifestService.GameVersionEntry> entries = [];
        try
        {
            var svc = new VersionManifestService();
            await svc.RefreshAsync();
            entries = svc.Entries;
        }
        catch { /* 断网/无缓存：仅保留磁盘扫描结果 */ }

        // ② 合并已装候选（manifest 已装原版 + 目录扫描补漏加载器，CollectInstalledCandidates 磁盘兜底）
        List<(string Dir, string Id)> candidates;
        try
        {
            candidates = VersionManifestService.CollectInstalledCandidates(
                entries, GameDirectory.ScanSourceDirs().Select(x => x.Dir), cleanForeignMarkers: true);
        }
        catch
        {
            // 磁盘扫描异常（纯本地 IO，几乎不触发）：退化为仅 manifest 已装，尽力而为
            candidates = entries.Where(e => e.Installed && InstallMarker.ShouldShowInPage(e.GameDirectory, e.Id))
                .Select(e => (e.GameDirectory, e.Id)).ToList();
        }

        // ③ 逐版本重建（先构建本地列表再整体替换——单版 json 损坏只跳过该版，不留「清空后半截」中间态）
        var rebuilt = new List<VersionInstanceVM>();
        foreach (var (dir, id) in candidates)
        {
            try
            {
                var (loader, mc) = VersionScan.Inspect(dir, id);
                rebuilt.Add(new VersionInstanceVM(id, LabelFor(id, dir), dir, loader, mc));
            }
            catch { /* 单版本损坏跳过 */ }
        }
        InstalledVersions.Clear();
        foreach (var v in rebuilt) InstalledVersions.Add(v);

        // 8-19 修复：切走主页时 ReleaseData 清空列表但 SelectedVersion 保留旧对象——
        // 重建后旧对象不在新列表 → 版本下拉显示空白（SelectedItem 不在 ItemsSource）但启动仍用旧对象（能启动）。
        // 按名字重新匹配新对象；匹配不到置 null 走自动选第一个
        if (SelectedVersion is { } sv
            && !InstalledVersions.Any(v => v.Name.Equals(sv.Name, StringComparison.OrdinalIgnoreCase)))
            SelectedVersion = null;
        if (InstalledVersions.Count > 0 && SelectedVersion is null)
        {
            // 8-23：优先自动选中「最近安装的版本」——此前恒选列表第一个（清单序 = ReleaseTime 最新原版），
            // 刚装的加载器版本从不被选中 → 下载模组「跟随实例」落到旧/错实例（用户反馈 TACZ 落错版的根因之一）
            var lastInstalled = Launcher.Core.AppState.LastInstalledVersionId;
            SelectedVersion = lastInstalled.Length > 0
                && InstalledVersions.FirstOrDefault(v => v.Name.Equals(lastInstalled, StringComparison.OrdinalIgnoreCase)) is { } li
                ? li
                : InstalledVersions[0];
        }
    }

    /// <summary>版本标签：本启动器安装 → "本启动器"；否则所在目录来源（PCL2 扫描/官方/自配）</summary>
    private static string LabelFor(string id, string gameDir)
        => InstallMarker.IsMarked(gameDir, id) ? "本启动器" : GameDirectory.SourceLabel(GameDirectory.SourceOf(gameDir));

    /// <summary>刷新头像区（8-16 批次 51：皮肤库应用皮肤后由外部调用刷新本地头像）</summary>
    public void RefreshPlayer()
    {
        var acc = _accounts.Current;
        PlayerName = acc?.Name ?? "未登录";
        // 8-22 全栈排查：空 Name（accounts.json 被手工编辑/外部账号返回空名）→ [..1] 索引越界崩
        PlayerAvatarFallback = acc is null || string.IsNullOrEmpty(acc.Name) ? "" : acc.Name[..1].ToUpperInvariant();
        AccountTypeText = acc?.Type == "microsoft" ? "正版"
            : acc?.Type == "littleskin" ? "Littleskin"
            : acc?.Type == "offline" ? "离线" : "未登录";
        // 8-13：不置空——网络头像加载期间保留旧头像（首字母块兜底由视图层做），避免每次刷新闪空白
        if (acc is null) return;

        // 8-18 终局（正版/离线）：头像走 minotar 3D 渲染（helm——脸永远正确）。
        // 本地皮肤（换肤）只作用于游戏内，不做主头像：自定义皮肤图（非标准布局）裁脸必失败
        // （实机：整图=全身照缩小、裁(0,0)=透明、(8,8)=纯白——三种都不像头像）；helm 失败时
        // 本地皮肤整图作离线兜底，再失败保持首字母。
        // 8-19 LittleSkin 分支：minotar 对非 Mojang 名实测返回 200+Steve 默认图（非 404）——
        // 头像永不更新、撞名还显示别人脸；LittleSkin 皮肤是标准 64×64 布局，本地裁脸可靠
        var skinPath = LocalSkinPath(acc.Name);
        Launcher.Core.Utils.AppLog.Instance?.LogInformation("[avatar] refresh: {Name}, skin={Exists}", acc.Name, File.Exists(skinPath));
        if (acc.Type == "littleskin")
        {
            LoadLittleSkinAvatar(acc.Name, skinPath, acc.Uuid ?? "");
            return;
        }
        _ = ImageLoader.LoadAsync($"https://minotar.net/helm/{Uri.EscapeDataString(acc.Name)}/64.png", bmp =>
        {
            if (bmp is not null) { PlayerAvatar = bmp; return; }
            if (File.Exists(skinPath))
            {
                try { using var s = File.OpenRead(skinPath); PlayerAvatar = new Avalonia.Media.Imaging.Bitmap(s); }
                catch { /* 本地兜底失败保持首字母 */ }
            }
        });
    }

    /// <summary>8-19 LittleSkin 头像：本地皮肤（标准布局）裁脸 → yggdrasil 纹理图裁脸 → 首字母。
    /// 皮肤库应用皮肤会写本地并触发 RefreshPlayer——头像随皮肤库即时更新</summary>
    private void LoadLittleSkinAvatar(string name, string skinPath, string uuid)
    {
        // 本地皮肤优先（皮肤库应用后已写入；标准 64×64/64×32 布局，脸在 (8,8)-(16,16)）
        if (File.Exists(skinPath))
        {
            try
            {
                using var s = File.OpenRead(skinPath);
                using var bmp = new Avalonia.Media.Imaging.Bitmap(s);
                if (bmp.PixelSize.Width == 64 && (bmp.PixelSize.Height is 64 or 32))
                {
                    PlayerAvatar = new Avalonia.Media.Imaging.CroppedBitmap(bmp,
                        new Avalonia.PixelRect(8, 8, 8, 8));
                    return;
                }
            }
            catch { /* 本地图损坏走网络 */ }
        }
        // 网络 yggdrasil 纹理（免 token；/skin/{name}.png 实测 404，走 profile 解析的真纹理 URL）
        _ = Task.Run(async () =>
        {
            using var http = Launcher.Core.Download.HttpClientPool.CreateSharedClient(TimeSpan.FromSeconds(8));
            var url = await Launcher.Core.Account.LittleSkinSkinSync.ResolveTextureUrlAsync(http, uuid);
            if (string.IsNullOrEmpty(url)) return;
            ImageLoader.LoadAsync(url, bmp => // 回调已封送 UI 线程
            {
                if (bmp is null) return;
                try
                {
                    PlayerAvatar = bmp.PixelSize.Width == 64 && (bmp.PixelSize.Height is 64 or 32)
                        ? new Avalonia.Media.Imaging.CroppedBitmap(bmp, new Avalonia.PixelRect(8, 8, 8, 8))
                        : bmp;
                }
                catch { PlayerAvatar = bmp; } // 裁脸失败整图兜底
            });
        });
    }

    /// <summary>本地皮肤路径（AppData\Launcher\skins\{name}.png）</summary>
    private static string LocalSkinPath(string name)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Launcher", "skins", $"{name}.png");

    /// <summary>8-13 更换皮肤：复制本地图片为头像 + 启动器头像。游戏内下次启动生效
    /// （SkinPack 资源包在启动前自动写入——此前「游戏内不生效」的限制已解除）。
    /// 尺寸校验：只认 64×64 / 64×32 皮肤格式（图标/截图等杂图拒绝）。</summary>
    public void ApplyLocalSkin(string sourcePath)
    {
        var acc = _accounts.Current;
        if (acc is null) return;
        try
        {
            using var probe = new Avalonia.Media.Imaging.Bitmap(sourcePath);
            if (!Launcher.Core.Launch.SkinPack.IsSupportedSize(probe.PixelSize.Width, probe.PixelSize.Height))
            {
                NotificationService.Error(
                    $"不是皮肤图片：需要 64×64 或 64×32 的 PNG（这张是 {probe.PixelSize.Width}×{probe.PixelSize.Height}）");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(LocalSkinPath(acc.Name))!);
            // 8-16 不再显式 Dispose 头像（Image 控件引用中，释放即竞态崩溃）；强制写（GC 回收锁 + 清只读 + 重试 + tmp 原子替换）
            PlayerAvatar = null;
            ForceWriteSkinFile(LocalSkinPath(acc.Name), File.ReadAllBytes(sourcePath));
            RefreshPlayer();
            NotificationService.Success("已更换皮肤，下次启动游戏生效");
        }
        catch (Exception ex)
        {
            NotificationService.Error($"换肤失败: {ex.Message}");
        }
    }

    /// <summary>8-14 重置皮肤（PCL 式强硬版）：正版 = 从正版账号**强制同步官方皮肤**覆盖本地
    /// （minotar.net 镜像国内可达，拉取的是账号皮肤原图；不再依赖「删文件」——删不掉也覆盖写）；
    /// 离线/Littleskin = 随机 Steve/Alex 默认皮肤（内置资源，游戏内同样生效）</summary>
    public async Task ResetSkin()
    {
        var acc = _accounts.Current;
        if (acc is null) return;
        try
        {
            var dest = LocalSkinPath(acc.Name);
            if (acc.Type == "microsoft")
            {
                // 8-16 不再显式 Dispose 头像（同上）；删除（失败不阻塞）→ 从正版拉官方皮覆盖写
                PlayerAvatar = null;
                try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                await SyncOfficialSkinAsync(acc.Name, dest);
                NotificationService.Success("已强制同步正版账号的官方皮肤，游戏内生效");
            }
            else
            {
                var asset = Random.Shared.Next(2) == 0 ? "steve.png" : "alex.png";
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using (var stream = Avalonia.Platform.AssetLoader.Open(
                    new Uri($"avares://Launcher.App/Assets/{asset}")))
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    ForceWriteSkinFile(dest, ms.ToArray());
                }
                NotificationService.Success("已重置为默认皮肤，下次启动游戏生效");
            }
            RefreshPlayer();
        }
        catch (Exception ex)
        {
            NotificationService.Error($"重置失败: {ex.Message}");
        }
    }

    /// <summary>从正版账号拉取官方皮肤（minotar.net/skin/{name}——Mojang 皮肤镜像，国内可达，
    /// 返回 64×64/64×32 皮肤原图，正版账号未换过皮则返回默认皮）覆盖本地。若目标仍被外部
    /// 进程独占锁住（游戏运行中），异常如实抛出并提示。</summary>
    private async Task SyncOfficialSkinAsync(string name, string dest)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        var bytes = await http.GetByteArrayAsync($"https://minotar.net/skin/{Uri.EscapeDataString(name)}");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        ForceWriteSkinFile(dest, bytes); // 强制写：GC 回收锁 + 重试 + 原子替换
    }

    /// <summary>8-16 批次 51：强写逻辑已迁移 Core（SkinFileWriter.ForceWrite），皮肤库窗口共用</summary>
    private static void ForceWriteSkinFile(string dest, byte[] bytes) => SkinFileWriter.ForceWrite(dest, bytes);

    /// <summary>推进阶段指示条</summary>
    private void SetStage(string stageName)
    {
        var idx = Array.IndexOf(StageNames, stageName);
        if (idx < 0) return;
        CurrentStageIndex = idx;
        for (var i = 0; i < Stages.Count; i++)
        {
            Stages[i].IsDone = i < idx;
            Stages[i].IsCurrent = i == idx;
        }
        // 阶段进度映射：前 4 个阶段占 0-80%，游戏加载 80-100%
        LaunchProgress = idx switch
        {
            0 => 15,
            1 => 35,
            2 => 55,
            3 => 75,
            4 => 85,
            _ => LaunchProgress,
        };
        LaunchStatus = stageName == "启动 JVM" ? "正在启动 JVM…" : $"{stageName}…";
    }

    [RelayCommand]
    private Task Launch() => LaunchAsync();

    /// <summary>启动核心（主页按钮/版本页 [启动] 共用；额外参数为空 = 普通启动）</summary>
    private async Task LaunchAsync()
    {
        _autoFixApplied = false; // AL9：新启动重置自修复标志（重试经递归调用不重置，天然最多一次）
        await LaunchCoreAsync(null, "", null);
    }

    /// <summary>一键进服：启动客户端并自动连接本地服务端（开服页调用；host/port 由开服页读取）</summary>
    public async Task RequestLaunchWithServerAsync(string versionId, string gameDir, string host, int port)
    {
        _autoFixApplied = false; // AL9：新启动重置自修复标志
        await RefreshVersionsAsync();
        var found = InstalledVersions.FirstOrDefault(v => v.Name.Equals(versionId, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            InstalledVersions.Add(new VersionInstanceVM(versionId, "本启动器", gameDir));
            found = InstalledVersions[^1];
        }
        SelectedVersion = found;
        await LaunchCoreAsync(found, gameDir, ["--server", host, "--port", port.ToString()]);
    }

    /// <summary>启动核心（主页按钮/版本页 [启动]/一键进服共用）</summary>
    private async Task LaunchCoreAsync(VersionInstanceVM? overrideVersion, string overrideGameDir, string[]? extraGameArgs)
    {
        if (IsLaunching || IsRunning) return;
        ResetLaunchLog(); // 8-18：新启动会话新日志文件（与启动记录一一对应）
        // REVIEW-A1：_userStopped 每次启动入口重置——旧代码置 true 后永不复位，
        // 本会话停过一次游戏，之后任何崩溃都被误判「已停止」（不弹崩溃窗/不自动修复/历史记 Stopped）
        _userStopped = false;
        var version = overrideVersion ?? SelectedVersion;
        // 8-26 启动更直接：不弹模态——无版本时按钮已置灰（CanLaunch），这里只内联提示兜底
        if (version is null)
        {
            LaunchStatus = "你还没选版本";
            return;
        }
        _lastLaunchVersionId = version.Name;
        ShowRepairGuide = false; // 清除上次失败的修复入口
        var account = _accounts.Current;
        // 8-26 启动更直接：不弹模态——启动卡内联提示（点头像可登录）
        if (account is null)
        {
            LaunchStatus = "你还没登录账号";
            return;
        }

        GameLogs.Clear();
        HasLogs = false;
        IsLaunching = true;
        LaunchProgress = 0;
        CurrentStageIndex = -1;
        foreach (var s in Stages) { s.IsDone = false; s.IsCurrent = false; }
        LaunchState = "准备中";
        LaunchStatus = $"正在准备 {version.Name}…";
        _launchWatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // 启动链路（后台线程；阶段回调切回 UI 更新指示条）——内存/Java/参数：版本级配置覆盖全局
            var gameDir = overrideGameDir.Length > 0 ? overrideGameDir
                : version.GameDir.Length > 0 ? version.GameDir : GameDirectory.Detect();
            var s = LauncherSettings.Current;
            // 8-13 离线皮肤游戏内生效（SkinPack 资源包）：非正版账号有本地皮肤 → 启动前写入资源包 + options.txt 注入
            if (account.Type != "microsoft")
            {
                var skinPath = LocalSkinPath(account.Name);
                if (File.Exists(skinPath))
                {
                    var applyDir = s.VersionIsolation
                        ? Path.Combine(gameDir, "versions", version.Name) : gameDir;
                    Launcher.Core.Launch.SkinPack.Apply(
                        applyDir, skinPath, Launcher.Core.Launch.SkinPack.PackFormatFor(version.Name));
                }
            }
            var (memCfg, javaCfg, argsCfg) = VersionConfigService.Merge(gameDir, version.Name, s);
            var memMb = memCfg switch
            {
                -2 => MemoryAllocator.AutoMb(), // 自动：按可用内存留余量
                > 0 => memCfg,
                _ => (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024 * 0.6), // 0=极致
            };
            // 性能档位：GC 参数预设前置合并（用户"额外 JVM 参数"在后，优先级更高）
            var (_, _, gcArgs) = PerformanceProfiles.Resolve(
                s.JvmProfile, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);
            var extraArgs = gcArgs.Concat(argsCfg?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? []).ToArray();
            if (!string.IsNullOrEmpty(javaCfg))
                s.JavaPath = javaCfg; // 版本级 Java 优先（GameLaunchService 读 LauncherSettings）
            // 正版账号：Minecraft token 未过期直接用缓存（跳过 5 连网络刷新——启动变慢根因）；
            // 过期才静默刷新（过期自动换新，用户无感；刷新失败提示重新登录）
            var accessToken = "token";
            if (account.Type == "microsoft")
            {
                try
                {
                    if (_accounts.MicrosoftSession is { } ms
                        && ms.AccessToken.Length > 0
                        && ms.ExpiresAtUtc > DateTime.UtcNow)
                    {
                        accessToken = ms.AccessToken;
                    }
                    else
                    {
                        var session = await Task.Run(() => _accounts.RefreshMicrosoftAsync());
                        accessToken = session.AccessToken;
                    }
                }
                catch (Exception ex)
                {
                    LaunchStatus = $"正版登录已失效：{ex.Message}（请到账号页重新登录）";
                    LaunchState = "失败";
                    IsLaunching = false;
                    return;
                }
            }
            // 8-19 进服皮肤：LittleSkin 账号把角色皮肤纹理 URL 透传给服务端（offline 服其他玩家可见真实皮肤）。
            // 8-19 修死 URL：/skin/{name}.png 实测 404，走 yggdrasil profile 解析真纹理 URL（拿不到则不透传）
            string? skinUrl = null;
            if (account.Type == "littleskin" && !string.IsNullOrEmpty(account.Uuid))
            {
                using var skinHttp = Launcher.Core.Download.HttpClientPool.CreateSharedClient(TimeSpan.FromSeconds(8));
                skinUrl = await Launcher.Core.Account.LittleSkinSkinSync.ResolveTextureUrlAsync(skinHttp, account.Uuid);
            }
            // 8-26 启动前模组兼容检查：发现与游戏版本明显不兼容的 mod → 启动前自动禁用（.jar→.jar.disabled）。
            // 解决「开始前不检查冲突模组」——不等 Fabric 崩溃先查先禁用，用户可在版本页重新启用。
            // 8-26 修：游戏版本必须解析真值（McVersion → inheritsFrom → 版本名）——此前直接传 version.Name
            // （fabric-loader-0.19.3-26.1.2 实例 id），ModCompatibilityChecker 解析不出 → 预检静默跳过（「没看出来」根因）
            var checkModsDir = Path.Combine(ModRepairService.InstanceRoot(gameDir, version.Name), "mods");
            if (Directory.Exists(checkModsDir))
            {
                // 8-28 可跳过：大型整合包交给用户决定——随时取消扫描直接启动（不误停调好的包）
                using var preCts = new CancellationTokenSource();
                _preCheckCts = preCts;
                IsPreCheckRunning = true;
                try
                {
                    var checkGameVersion = ResolveCheckGameVersion(version, gameDir);
                    var modCount = ModCompatibilityChecker.CountMods(checkModsDir);
                    // 8-27 可视化：扫描开始即切阶段 + 控制台先亮一句——此前扫描全程零输出像卡死
                    SetStage("检查模组兼容性");
                    AppendLog($"§ 正在检查 {modCount} 个模组的兼容性…");
                    List<ModCompatibilityChecker.IncompatibleMod> incompatible = [];
                    try
                    {
                        incompatible = await Task.Run(() => ModCompatibilityChecker.FindIncompatible(checkModsDir, checkGameVersion,
                            (done, total) =>
                            {
                                // 节流：每 5 个 / 最后一个才刷状态（几十个 mods 不刷屏）
                                if (total > 0 && (done % 5 == 0 || done == total))
                                {
                                    var d = done;
                                    Dispatcher.UIThread.Post(() => LaunchStatus = $"正在检查模组（{d}/{total}）…");
                                }
                            }, preCts.Token), preCts.Token);
                    }
                    catch (OperationCanceledException) { /* 用户跳过 → 当无结果 */ }

                    // 8-29 缺失前置检测（minihud 缺 malilib 实锤）：minecraft 版本匹配 ≠ 能启动——
                    // 模组间硬前置缺失照样崩 Fabric 报错页。jar 直读元数据，不靠日志（Fabric 26.x 明细只在屏幕）。
                    List<ModCompatibilityChecker.MissingDependency> missingDeps = [];
                    if (!preCts.IsCancellationRequested)
                    {
                        try
                        {
                            missingDeps = await Task.Run(
                                () => ModCompatibilityChecker.FindMissingDependencies(checkModsDir, preCts.Token), preCts.Token);
                        }
                        catch (OperationCanceledException) { /* 用户跳过 → 当无结果 */ }
                    }

                    if (!preCts.IsCancellationRequested && incompatible.Count == 0 && missingDeps.Count == 0)
                    {
                        // 可见证据：无冲突也报告检查结果（回应「没看出来检查跑没跑」）
                        AppendLog($"§ 模组兼容检查：游戏版本 {checkGameVersion}，共 {modCount} 个模组，未发现冲突或缺失前置");
                    }
                    else if (!preCts.IsCancellationRequested)
                    {
                        var loader = version.LoaderBadge.Length > 0 ? version.LoaderBadge : EcosystemService.GuessLoader(version.Name);
                        // 1) 与游戏版本不兼容 → 先停用旧 jar（保证即使下载失败也能启动），再下载兼容版替换
                        if (incompatible.Count > 0)
                        {
                            // 8-26 自动修复升级：不止停用——下载兼容版（找不到适配版/下载失败才维持停用）
                            AppendLog($"§ 模组兼容检查：游戏版本 {checkGameVersion}，发现 {incompatible.Count} 个不兼容模组，自动修复中…");
                            // 8-27 复用已扫结果禁用（DisableIncompatible 不再内部重扫一遍）
                            List<ModCompatibilityChecker.IncompatibleMod> disabled = [];
                            try
                            {
                                disabled = await Task.Run(() => ModCompatibilityChecker.DisableIncompatible(checkModsDir, checkGameVersion, incompatible, preCts.Token), preCts.Token);
                            }
                            catch (OperationCanceledException) { /* 取消中途禁用 → 跳过 */ }
                            if (!preCts.IsCancellationRequested)
                            {
                                foreach (var m in disabled)
                                    AppendLog($"§ 已停用不兼容模组 {m.Id}（需要 {m.DeclaredRange}，游戏 {m.GameVersion}）");
                                AppendLog("§ 正在下载兼容版本替换（下载中心可见进度）…");
                                var replace = await ModRepairFlow.TryReplaceModsAsync(
                                    gameDir, version.Name, checkGameVersion, loader, disabled.Select(m => m.Id).ToList(), preCts.Token);
                                if (!preCts.IsCancellationRequested)
                                {
                                    foreach (var r in replace.Replaced)
                                        AppendLog($"§ 已自动替换 {r}，正在启动…");
                                    foreach (var d in replace.DisabledOnly)
                                        AppendLog($"§ {d}，已停用");
                                    if (replace.Replaced.Count > 0)
                                        NotificationService.Success($"已自动修复：{string.Join("、", replace.Replaced)}", 5000);
                                    if (replace.DisabledOnly.Count > 0)
                                        NotificationService.Error($"已停用无适配版：{string.Join("、", replace.DisabledOnly)}");
                                    // 8-29 复检（溯源约定：修复必带复检）：替换后重扫，仍不兼容 → 再禁用 + 如实报，不假称已处理
                                    var stillBad = await Task.Run(
                                        () => ModCompatibilityChecker.FindIncompatible(checkModsDir, checkGameVersion, ct: preCts.Token), preCts.Token);
                                    if (!preCts.IsCancellationRequested && stillBad.Count > 0)
                                    {
                                        var stillDisabled = ModCompatibilityChecker.DisableIncompatible(checkModsDir, checkGameVersion, stillBad);
                                        foreach (var sb in stillDisabled)
                                            AppendLog($"§ 复检仍发现 {sb.Id} 不兼容（需要 {sb.DeclaredRange}，游戏 {sb.GameVersion}），已停用");
                                        NotificationService.Error($"复检发现 {stillBad.Count} 个模组替换后仍不兼容，已停用：{string.Join("、", stillBad.Select(m => m.Id))}");
                                        LaunchStatus = $"仍有 {stillBad.Count} 个不兼容模组，已停用";
                                    }
                                    else if (!preCts.IsCancellationRequested)
                                    {
                                        LaunchStatus = "不兼容模组已处理，正在启动…";
                                    }
                                }
                            }
                        }
                        // 2) 缺失前置 → 自动安装缺失的前置；装失败的禁用其依赖方（否则启动仍崩报错页）
                        if (missingDeps.Count > 0)
                        {
                            var depIds = missingDeps.Select(m => m.DepId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                            foreach (var m in missingDeps)
                                AppendLog($"§ 检测到 {m.DependentModId} 缺少前置 {m.DepId}（需要 {m.RequiredRange}），自动安装…");
                            AppendLog("§ 正在下载缺失前置（下载中心可见进度）…");
                            var depReplace = await ModRepairFlow.TryReplaceModsAsync(
                                gameDir, version.Name, checkGameVersion, loader, depIds, preCts.Token);
                            if (!preCts.IsCancellationRequested)
                            {
                                foreach (var r in depReplace.Replaced)
                                    AppendLog($"§ 已自动安装前置 {r}");
                                foreach (var d in depReplace.DisabledOnly)
                                    AppendLog($"§ 前置安装失败：{d}");
                                // 重扫仍缺失的 = 装失败的前置 → 禁用其依赖方（保证能启动，不崩 Fabric）
                                var stillMissing = ModCompatibilityChecker.FindMissingDependencies(checkModsDir);
                                foreach (var sm in stillMissing)
                                {
                                    var depJar = Path.Combine(checkModsDir, sm.DependentFileName);
                                    if (!File.Exists(depJar)) continue;
                                    try
                                    {
                                        File.Move(depJar, depJar + ".disabled");
                                        AppendLog($"§ 前置 {sm.DepId} 安装失败，已禁用依赖方 {sm.DependentModId}，避免启动报错");
                                    }
                                    catch { /* 单个禁用失败不阻断 */ }
                                }
                            }
                        }
                    }

                    if (preCts.IsCancellationRequested)
                    {
                        AppendLog("§ 已跳过模组检查，直接启动（用户选择）");
                        LaunchStatus = "已跳过模组检查，正在启动…";
                    }
                }
                finally
                {
                    IsPreCheckRunning = false;
                    _preCheckCts = null;
                }
            }
            _running = await Task.Run(() => _launcher.LaunchAsync(
                version.Name, gameDir, account.Name, account.Uuid, accessToken,
                memoryMb: memMb, extraJvmArgs: extraArgs,
                onLog: AppendLog, onStage: st => Dispatcher.UIThread.Post(() => SetStage(st)),
                ct: CancellationToken.None, extraGameArgs: extraGameArgs,
                userType: account.Type == "microsoft" ? "msa" : "legacy",
                skinUrl: skinUrl));

            // 游戏进程已启动（窗口拉起）
            IsLaunching = false;
            IsRunning = true;
            LaunchState = "运行中";
            LaunchProgress = 100;
            LaunchStatus = $"游戏运行中，账号 {account.Name}。点停止结束";
            SetStage("运行中"); // 8-26 删「已拉起」toast——窗口出现即反馈

            // 等待退出
            await Task.Run(() => _running.Process.WaitForExit());
            var code = LaunchProcess.GetExitCode(_running);
            AppendLog($"§ 游戏进程已退出（exitStatus={code}）");
            if (_userStopped)
            {
                LaunchState = "已退出";
                LaunchStatus = "已停止游戏";
                LaunchHistoryService.Record(version.Name, LaunchOutcome.Stopped, null, _launchWatch?.Elapsed.TotalSeconds ?? 0, _launchLogPath);
            }
            else if (code == 0)
            {
                LaunchState = "已退出";
                LaunchStatus = "游戏正常退出";
                LaunchHistoryService.Record(version.Name, LaunchOutcome.Success, null, _launchWatch?.Elapsed.TotalSeconds ?? 0, _launchLogPath);
            }
            else
            {
                // 8-26 模组冲突清晰化：崩溃日志命中「Incompatible mods found」→ 明示冲突模组清单（兜底——
                // 启动前预检已自动禁用大部分；能走到崩溃，通常是禁用失败/非 mods 目录场景）。
                // 8-26 修：改读游戏自身 latest.log——启动器控制台/launch-*.log 被 IsKeyLine 过滤掉 INFO 级冲突明细，
                // 旧代码从 BuildDiagText 提取恒为空（「自动修复没生效」根因）
                var conflictIds = AutoRepairService.ExtractConflictingModIds(gameDir, version.Name);
                var conflictHint = conflictIds.Count > 0
                    ? $"模组冲突：{string.Join("、", conflictIds)} 与游戏版本 {version.Name} 不兼容。自动禁用未生效，请到版本页禁用这些模组或换适配当前游戏版本的版本。"
                    : null;
                LaunchState = $"异常退出（{code}）";
                LaunchStatus = conflictHint ?? "游戏异常退出，请查看日志";
                if (conflictHint is not null) AppendLog($"§ {conflictHint}");
                LaunchHistoryService.Record(version.Name, LaunchOutcome.Crashed, $"退出码 {code}", _launchWatch?.Elapsed.TotalSeconds ?? 0, _launchLogPath);
                // 崩溃弹窗（PCL 式）：游戏日志尾部 + 导出报告
                var logTail = string.Join(Environment.NewLine, GameLogs.TakeLast(40));
                // AL9/AL10 自修复：日志诊断 → 可自动修复 → 修复后自动重新启动一次（最多一次；修复本身最多试 2 次，全失败才弹窗）
                if (!_autoFixApplied && await TryAutoFixWithRetryAsync(version, gameDir, BuildDiagText("")))
                {
                    _autoFixApplied = true;
                    IsLaunching = false;
                    IsRunning = false;
                    _running = null;
                    CurrentStageIndex = -1;
                    await LaunchCoreAsync(overrideVersion, overrideGameDir, extraGameArgs);
                    return;
                }
                // AL43：退出码诊断——无命中时按退出码补「人话」原因（-1 被终止 / 兜底），诊断区不再空
                var diag = LogDiagnostics.DiagnoseExit(code, string.Join(Environment.NewLine, GameLogs));
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    Views.CrashReportWindow.Show($"游戏崩溃退出（退出码 {code}）",
                        (conflictHint ?? $"版本 {version.Name} 异常退出，退出码 {code}。") + Environment.NewLine
                        + Environment.NewLine + "最近日志：" + Environment.NewLine + logTail,
                        logTail, diag, version.Name, gameDir));
            }
            IsRunning = false;
            _running = null;
            CurrentStageIndex = -1;
            GameLogs.Clear(); // 退出后自动清空控制台（启动记录/日志文件保留本次错误）
            HasLogs = false;
        }
        catch (Exception ex)
        {
            LaunchState = "失败";
            // 客户端文件缺失（残件版本）：显示修复入口按钮
            ShowRepairGuide = ex is FileNotFoundException;
            LaunchStatus = ShowRepairGuide
                ? "你的客户端文件缺失，启动不了。可以补全下载，或去官方页面下载。"
                : ex.Message;
            AppendLog($"§ 启动失败: {ex.Message}");
            // 8-19 失败记录也关联日志文件（会话开始即建，失败也可回看）
            LaunchHistoryService.Record(version.Name, LaunchOutcome.Failed, ex.Message, _launchWatch?.Elapsed.TotalSeconds ?? 0, _launchLogPath);
            // AL9/AL10 自修复：文件缺失（异常即证据，跳过诊断直接重下）或诊断命中可自动修复项 → 修复后自动重试一次
            // gameDir 是 try 块局部变量，catch 不可见——这里按相同规则重算
            var gameDir = overrideGameDir.Length > 0 ? overrideGameDir
                : version.GameDir.Length > 0 ? version.GameDir : GameDirectory.Detect();
            var shouldFix = !_autoFixApplied
                && (ex is FileNotFoundException or ParentVersionMissingException || await TryAutoFixWithRetryAsync(version, gameDir, BuildDiagText(ex.Message)));
            string? lastFixError = null;
            if (shouldFix)
            {
                if (ex is FileNotFoundException or ParentVersionMissingException)
                {
                    AppendLog("§ 检测到问题：客户端文件缺失，正在自动重新下载补全…");
                    var fixedOk = false;
                    for (var attempt = 1; attempt <= 2 && !fixedOk; attempt++)
                    {
                        if (attempt > 1) AppendLog($"§ 自动修复失败，正在重试（第 {attempt}/2 次）…");
                        try { AppendLog($"§ 自动修复完成：{await AutoRepairService.FixRedownloadAsync(version.Name, gameDir)}"); fixedOk = true; }
                        catch (Exception fx) { lastFixError = fx.Message; AppendLog($"§ 自动修复失败: {fx.Message}"); }
                    }
                    shouldFix = fixedOk;
                }
                if (shouldFix)
                {
                    _autoFixApplied = true;
                    IsLaunching = false;
                    IsRunning = false;
                    await LaunchCoreAsync(overrideVersion, overrideGameDir, extraGameArgs);
                    return;
                }
            }
            // 8-23 修：自修复后仍失败 → 错误正大光明贴出来（此前只有红字 + 4.5s Toast，用户在别的页面看不到——
            // 用户反馈「自动修复失效且没有报错提示」的根因）。复用崩溃窗带诊断 + 一键修复，真实原因 fx.Message 展示。
            if (lastFixError is not null)
            {
                // 8-23 修：修复失败 → 大窗口弹窗 + 附带完整日志（用户明确不要 toast）
                var logPreview = BuildDiagText(ex.Message); // 内存控制台 + 本次 launch-*.log 完整内容
                var diag = LogDiagnostics.DiagnoseDetailed(ex.Message + "\n" + string.Join("\n", GameLogs.TakeLast(60)));
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    Views.CrashReportWindow.Show($"启动失败 · 自动修复未成功",
                        $"版本 {version.Name} 启动失败，自动修复未能完成。\n\n修复原因：{lastFixError}\n\n" +
                        (ShowRepairGuide ? "你的客户端文件缺失，启动不了。" : ex.Message),
                        logPreview, diag, version.Name, gameDir));
            }
            else
            {
                // 非修复路径：保持通用 Toast（修复失败已有全局 RepairFailedEvent Toast + 上方崩溃窗，避免双弹）
                NotificationService.Error($"启动失败：{LaunchStatus}");
            }
            IsLaunching = false;
            IsRunning = false;
        }
    }

    /// <summary>8-26 解析预检用的真游戏版本（兜底链）：
    /// ① VersionInstanceVM.McVersion（由 VersionScan.Inspect 读版本 json 的 inheritsFrom 填充）；
    /// ② 兜底读 versions/{id}/{id}.json 的 inheritsFrom（部分构造路径 McVersion 为空）；
    /// ③ 版本名本身（原生如 26.1.2 直接可解析；loader 名 fabric-loader-… 解析不出 → 预检跳过）。
    /// 不能直接传 version.Name——实例 id 不是游戏版本，预检会静默空转。</summary>
    private static string ResolveCheckGameVersion(VersionInstanceVM version, string gameDir)
    {
        if (!string.IsNullOrEmpty(version.McVersion)) return version.McVersion;
        try
        {
            var json = Path.Combine(gameDir, "versions", version.Name, $"{version.Name}.json");
            if (File.Exists(json))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
                if (doc.RootElement.TryGetProperty("inheritsFrom", out var p) && p.GetString() is { Length: > 0 } pid)
                    return pid;
            }
        }
        catch { /* 读不到就退回版本名 */ }
        return version.Name;
    }

    /// <summary>8-23 修复：自修复诊断输入 = 额外原因 + 内存控制台 + 本次启动日志文件（launch-*.log）。
    /// 此前只诊断内存 GameLogs（进程退出后丢失），用户诉求「自动读取当时日志执行修复」——日志文件才是完整证据。</summary>
    private string BuildDiagText(string extra)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(extra);
        foreach (var l in GameLogs) sb.AppendLine(l.Text);
        try
        {
            if (_launchLogPath is not null && File.Exists(_launchLogPath))
                sb.Append(File.ReadAllText(_launchLogPath));
        }
        catch { /* 读日志失败不影响诊断 */ }
        return sb.ToString();
    }

    /// <summary>AL10 自修复全自动：最多尝试 2 次（修复幂等只补缺失，瞬时网络失败自愈）；全失败返回 false</summary>
    private async Task<bool> TryAutoFixWithRetryAsync(VersionInstanceVM version, string gameDir, string diagText)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (attempt > 1) AppendLog($"§ 自动修复失败，正在重试（第 {attempt}/2 次）…");
            if (await TryAutoFixAsync(version, gameDir, diagText)) return true;
        }
        return false;
    }

    /// <summary>AL9 自修复：诊断日志 → 命中可自动修复项（Redownload/ReExtractNatives/DisableConflictingMods）→ 执行修复。
    /// 返回 true 表示修复成功（调用方负责自动重新启动一次）；AdviceOnly / 修复失败 / 空手而归返回 false。
    /// 8-29 诚实化：没实际处理任何东西就不返回 true——否则调用方自动重启一次又崩，正是「自动修复说修好没实行」。</summary>
    private async Task<bool> TryAutoFixAsync(VersionInstanceVM version, string gameDir, string diagText)
    {
        var hit = LogDiagnostics.DiagnoseDetailed(diagText)
            .FirstOrDefault(h => h.Fix is FixKind.Redownload or FixKind.ReExtractNatives or FixKind.DisableConflictingMods);
        if (hit is null) return false;
        AppendLog($"§ 检测到问题：{hit.Explanation}，正在自动修复…");
        try
        {
            if (hit.Fix == FixKind.DisableConflictingMods)
            {
                // 8-26 修：不传 diagText（null → 走 ExtractConflictingModIds 读游戏自身 latest.log）——
                // 启动器控制台/launch-*.log 被 IsKeyLine 过滤掉 INFO 级冲突明细，传过滤后文本恒提取不到 id
                var (text, didSomething) = await FixConflictsWithReplaceAsync(version, gameDir);
                if (didSomething)
                    AppendLog($"§ 自动修复完成：{text}");
                else
                {
                    AppendLog($"§ 未发现可自动修复项（{text}）");
                    return false;
                }
            }
            else
            {
                var result = hit.Fix switch
                {
                    FixKind.ReExtractNatives => AutoRepairService.FixNatives(version.Name, gameDir),
                    _ => await AutoRepairService.FixRedownloadAsync(version.Name, gameDir),
                };
                AppendLog($"§ 自动修复完成：{result}");
            }
            // AL57.1 模组缺失自愈（自动路径）：无人值守直接补全，不弹确认框（弹框与崩溃窗模态冲突 + 破坏全自动语义）
            var hadMissing = await ModRepairFlow.TryRepairAsync(gameDir, version.Name, null, requireConfirm: false);
            if (hadMissing) AppendLog("§ 已检查缺失前置模组并自动补全（详见下载中心）");
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"§ 自动修复失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>8-26 崩溃冲突修复升级：停用冲突模组（FixConflictingMods，读游戏自身 latest.log 提取 id）后，
    /// 再下载兼容版替换（自动修复 = 换成正确版本，不是只停用）。
    /// 8-29 兜底（同类问题）：Fabric 26.x 冲突明细只在报错屏幕（latest.log 只有堆栈标题），日志提取常为空 →
    /// 回退 jar 直读缺失前置（minihud 缺 malilib 场景），装前置 / 禁用依赖方。返回（描述, 是否实际处理了东西）。</summary>
    private async Task<(string Text, bool DidSomething)> FixConflictsWithReplaceAsync(VersionInstanceVM version, string gameDir)
    {
        var gv = ResolveCheckGameVersion(version, gameDir);
        var loader = version.LoaderBadge.Length > 0 ? version.LoaderBadge : EcosystemService.GuessLoader(version.Name);
        var result = AutoRepairService.FixConflictingMods(gameDir, version.Name, null);
        var didSomething = !result.StartsWith("日志中未识别到明确的冲突模组", StringComparison.Ordinal);
        var conflictIds = AutoRepairService.ExtractConflictingModIds(gameDir, version.Name);
        if (conflictIds.Count > 0)
        {
            AppendLog($"§ 正在下载兼容版本替换（下载中心可见进度）…");
            var replace = await ModRepairFlow.TryReplaceModsAsync(gameDir, version.Name, gv, loader, conflictIds);
            if (replace.Replaced.Count > 0)
                result += "；并已下载兼容版：" + string.Join("、", replace.Replaced);
            foreach (var r in replace.Replaced) AppendLog($"§ 已自动替换 {r}");
            foreach (var d in replace.DisabledOnly) AppendLog($"§ {d}，已停用");
        }
        // 8-29 复检（溯源约定：修复必带复检）：替换后重扫 minecraft 兼容，仍不兼容 → 再禁用，别让下次启动又崩
        var modsDir = Path.Combine(ModRepairService.InstanceRoot(gameDir, version.Name), "mods");
        if (Directory.Exists(modsDir) && ModCompatibilityChecker.FindIncompatible(modsDir, gv) is { Count: > 0 } stillBad)
        {
            var stillDisabled = ModCompatibilityChecker.DisableIncompatible(modsDir, gv, stillBad);
            foreach (var sb in stillDisabled)
            {
                AppendLog($"§ 复检仍发现 {sb.Id} 不兼容（需要 {sb.DeclaredRange}），已停用");
                result += $"；已停用 {sb.Id}";
                didSomething = true;
            }
        }
        // 8-29 缺失前置兜底：日志没说出明细，但 jar 直读有实锤 → 装前置 / 禁用依赖方，别空手宣称修复
        var missing = Directory.Exists(modsDir) ? ModCompatibilityChecker.FindMissingDependencies(modsDir) : [];
        if (missing.Count > 0)
        {
            if (!didSomething) result = "检测到缺失模组前置"; // 日志没说出来，jar 直读有实锤
            var depIds = missing.Select(m => m.DepId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var m in missing)
                AppendLog($"§ 检测到 {m.DependentModId} 缺少前置 {m.DepId}（需要 {m.RequiredRange}），自动安装…");
            AppendLog("§ 正在下载缺失前置（下载中心可见进度）…");
            var depReplace = await ModRepairFlow.TryReplaceModsAsync(gameDir, version.Name, gv, loader, depIds);
            foreach (var r in depReplace.Replaced)
            {
                AppendLog($"§ 已自动安装前置 {r}");
                result += $"；已补装前置 {r}";
                didSomething = true;
            }
            foreach (var d in depReplace.DisabledOnly) AppendLog($"§ 前置安装失败：{d}");
            // 装失败的前置 → 禁用其依赖方（否则自动重启还是崩报错页）
            var stillMissing = ModCompatibilityChecker.FindMissingDependencies(modsDir);
            foreach (var sm in stillMissing)
            {
                var depJar = Path.Combine(modsDir, sm.DependentFileName);
                if (!File.Exists(depJar)) continue;
                try
                {
                    File.Move(depJar, depJar + ".disabled");
                    AppendLog($"§ 前置 {sm.DepId} 安装失败，已禁用依赖方 {sm.DependentModId}，避免启动报错");
                    result += $"；已禁用 {sm.DependentModId}";
                    didSomething = true;
                }
                catch { /* 单个禁用失败不阻断 */ }
            }
        }
        return (result, didSomething);
    }

    [RelayCommand]
    private void StopGame()
    {
        _userStopped = true;
        try { _running?.Process.Kill(); } catch { }
        AppendLog("§ 已请求停止游戏");
    }

    private void AppendLog(string line)
    {
        // 进程输出事件来自后台线程，切回 UI 线程操作集合
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AppendLog(line));
            return;
        }
        // 8-26 屏幕也过滤 INFO 噪音（同落盘）：控制台只显示启动器事件 + 错误/警告/异常，不再刷屏
        if (!IsKeyLine(line)) return;
        if (GameLogs.Count >= MaxLogLines) GameLogs.RemoveAt(0);
        GameLogs.Add(new LogLine(line, Classify(line)));
        HasLogs = true;
        AppendToLaunchLog(line);
    }

    /// <summary>8-26 日志行着色类别：启动器事件(§)强调色；ERROR/WARN/FATAL/异常标红（报错区域标记）</summary>
    private static LogLineKind Classify(string line)
    {
        if (line.StartsWith('§')) return LogLineKind.Launcher;
        if (line.Contains("Exception", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Caused by", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Crashed!", StringComparison.OrdinalIgnoreCase)
            || line.TrimStart().StartsWith("at ", StringComparison.Ordinal)) return LogLineKind.Error;
        return System.Text.RegularExpressions.Regex.IsMatch(line,
            @"\[(\w+)\/(ERROR|WARN|FATAL)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            ? LogLineKind.Error : LogLineKind.Normal;
    }

    /// <summary>8-18 本次启动的日志文件路径（启动会话固定——启动记录可关联查看；null=未开始落盘）</summary>
    private string? _launchLogPath;

    /// <summary>控制台同步落盘（AppData\Launcher\logs\launch-*.log）——启动报错可回看；会话内固定同一文件。
    /// 8-19 简化日志：游戏 INFO 噪音（状态轮询/心跳刷屏）不落盘，只留启动器事件 + 错误/警告/异常堆栈——打开日志不再几千行刷屏</summary>
    private void AppendToLaunchLog(string line)
    {
        if (!IsKeyLine(line)) return;
        try
        {
            _launchLogPath ??= BuildLaunchLogPath();
            File.AppendAllText(_launchLogPath, line + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>8-19 日志行筛选：启动器事件（§）全保留；游戏输出只保留级别 ERROR/WARN/FATAL 与异常堆栈行</summary>
    private static bool IsKeyLine(string line)
    {
        if (line.StartsWith('§')) return true;
        if (line.Contains("Exception", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Caused by", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Crashed!", StringComparison.OrdinalIgnoreCase)) return true;
        // 堆栈行（"at ..."）属于异常上下文，保留
        if (line.TrimStart().StartsWith("at ", StringComparison.Ordinal)) return true;
        // 带级别标记的游戏行：ERROR/WARN/FATAL 保留，INFO/DEBUG 丢
        var m = System.Text.RegularExpressions.Regex.Match(line,
            @"\[(\w+)\/(ERROR|WARN|FATAL)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success;
    }

    /// <summary>8-19 新启动会话：启动即定日志文件并创建（不等待首次输出——
    /// 失败/无输出会话也能关联日志按钮；与启动记录一一对应）</summary>
    private void ResetLaunchLog()
    {
        try
        {
            _launchLogPath = BuildLaunchLogPath();
            File.WriteAllText(_launchLogPath, $"=== Starview 启动日志 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
        }
        catch { _launchLogPath = null; }
    }

    private static string BuildLaunchLogPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Launcher", "logs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"launch-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }
}

/// <summary>启动阶段指示项</summary>
public partial class LaunchStageVM : ObservableObject
{
    public string Name { get; }

    [ObservableProperty]
    public partial bool IsDone { get; set; }

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>指示点颜色：完成=暗青、当前=主强调、未到=灰（单一强调色系）</summary>
    public IBrush DotColor => IsDone ? new SolidColorBrush(Color.Parse("#1E8F82"))
        : IsCurrent ? new SolidColorBrush(Color.Parse("#6C8CFF"))
        : new SolidColorBrush(Color.Parse("#3A4250"));

    public LaunchStageVM(string name) => Name = name;

    partial void OnIsDoneChanged(bool value) => OnPropertyChanged(nameof(DotColor));
    partial void OnIsCurrentChanged(bool value) => OnPropertyChanged(nameof(DotColor));
}
