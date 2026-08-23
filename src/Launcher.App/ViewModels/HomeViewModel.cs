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
    private const int MaxLogLines = 500;
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
    }

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
    }

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

    public ObservableCollection<string> GameLogs { get; } = [];

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
        try
        {
            var svc = new VersionManifestService();
            await svc.RefreshAsync();
            // 收集全部候选 (目录, 版本)
            var candidates = new List<(string Dir, string Id)>();
            foreach (var e in svc.Entries.Where(e => e.Installed && InstallMarker.ShouldShowInPage(e.GameDirectory, e.Id)))
                candidates.Add((e.GameDirectory, e.Id));
            // 目录扫描补漏：加载器版本（fabric/forge/neoforge/quilt 等不在 Mojang manifest）
            // + 三路 jar 判定（8-14：原版父版本 jar 落加载器子目录也算——与版本页侧栏同口径）
            foreach (var (id, dir) in VersionManifestService.ScanUsableInstances(
                         GameDirectory.ScanSourceDirs().Select(x => x.Dir), cleanForeignMarkers: true))
            {
                if (candidates.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
                // 8-18 批次 75：预取父版本（.prefetched，Fabric 下载的依赖）主页不显示——
                // 非用户主动安装；正式安装的原版照常显示。要启动原版去下载页正式下载
                if (!InstallMarker.ShouldShowInPage(dir, id)) continue;
                candidates.Add((dir, id));
            }
            InstalledVersions.Clear();
            foreach (var (dir, id) in candidates)
            {
                var (loader, mc) = VersionScan.Inspect(dir, id);
                InstalledVersions.Add(new VersionInstanceVM(id, LabelFor(id, dir), dir, loader, mc));
            }
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
        catch { }
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
        if (version is null)
        {
            LaunchStatus = "你还没选版本";
            await DialogService.Warn(DialogService.MainWindow(), "你还没选版本",
                "选一个已安装的版本，再点启动。", "无法启动游戏", "知道了", "");
            return;
        }
        _lastLaunchVersionId = version.Name;
        ShowRepairGuide = false; // 清除上次失败的修复入口
        var account = _accounts.Current;
        if (account is null)
        {
            LaunchStatus = "你还没登录账号";
            await DialogService.Warn(DialogService.MainWindow(), "你还没登录账号",
                "启动游戏要登录账号。点头像菜单离线登录，或用正版账号。", "无法启动游戏", "知道了", "");
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
            SetStage("运行中");
            NotificationService.Success("游戏窗口已拉起");

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
                LaunchState = $"异常退出（{code}）";
                LaunchStatus = "游戏异常退出，请查看日志";
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
                        $"版本 {version.Name} 异常退出，退出码 {code}。" + Environment.NewLine
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

    /// <summary>8-23 修复：自修复诊断输入 = 额外原因 + 内存控制台 + 本次启动日志文件（launch-*.log）。
    /// 此前只诊断内存 GameLogs（进程退出后丢失），用户诉求「自动读取当时日志执行修复」——日志文件才是完整证据。</summary>
    private string BuildDiagText(string extra)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(extra);
        foreach (var l in GameLogs) sb.AppendLine(l);
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

    /// <summary>AL9 自修复：诊断日志 → 命中可自动修复项（Redownload/ReExtractNatives）→ 执行修复。
    /// 返回 true 表示修复成功（调用方负责自动重新启动一次）；AdviceOnly 或修复失败返回 false。</summary>
    private async Task<bool> TryAutoFixAsync(VersionInstanceVM version, string gameDir, string diagText)
    {
        var hit = LogDiagnostics.DiagnoseDetailed(diagText).FirstOrDefault(h => h.Fix is FixKind.Redownload or FixKind.ReExtractNatives);
        if (hit is null) return false;
        AppendLog($"§ 检测到问题：{hit.Explanation}，正在自动修复…");
        try
        {
            var result = hit.Fix == FixKind.ReExtractNatives
                ? AutoRepairService.FixNatives(version.Name, gameDir)
                : await AutoRepairService.FixRedownloadAsync(version.Name, gameDir);
            AppendLog($"§ 自动修复完成：{result}");
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
        if (GameLogs.Count >= MaxLogLines) GameLogs.RemoveAt(0);
        GameLogs.Add(line);
        HasLogs = true;
        AppendToLaunchLog(line);
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
            File.WriteAllText(_launchLogPath, $"=== Lattice 启动日志 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
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
