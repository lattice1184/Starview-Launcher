using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Services;
using Launcher.Core.Diagnostics;
using Launcher.Core.Download;
using Launcher.Core.Launch;
using Launcher.Core.Model.Mojang;
using Launcher.Core.Services;
using Launcher.Core.Utils;

namespace Launcher.App.ViewModels;

/// <summary>已检测 Java 的下拉选项（版本设置「Java 版本」选择）</summary>
public sealed record JavaChoice(string Label, string Path);

/// <summary>已装版本行（左栏）：DisplayName 为 PCL 式显示名（"1.21.11 (Fabric)"），Id 为真实目录名；
/// 来源标签 + 加载器徽章 + 所在目录 + 客户端文件缺失标记（有 json 无 jar 的残件版本）+ 继承的原版版本</summary>
public sealed record InstalledVersionRowVM(
    string Id, string DisplayName, string SourceLabel, string LoaderBadge,
    string GameDir, string ReleaseDate, bool IsJarMissing, string McVersion);

/// <summary>
/// 版本页（PCL2 式已装管理）：左栏已装版本列表（跨源扫描 + 搜索 + 行启动），
/// 右栏选中版本的完整设置分区（基本信息/启动配置/加载器/模组/存档/版本操作）。
/// 下载新版本在【下载】板块的"下载游戏"tab。
/// </summary>
public partial class VersionBrowseViewModel : ViewModelBase
{
    private readonly VersionManifestService _svc;
    private readonly VersionInstaller _installer;

    public ObservableCollection<InstalledVersionRowVM> Versions { get; } = [];
    public InstalledVersionDetailVM Detail { get; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial InstalledVersionRowVM? SelectedVersion { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "加载中…";

    /// <summary>左栏底部统计条（已装总数/本启动器/PCL）</summary>
    [ObservableProperty]
    public partial string StatsText { get; set; } = "";

    private List<InstalledVersionRowVM> _all = [];

    /// <summary>8-18 内存让渡：切走版本页时释放列表（切回时 LoadAsync 重建）</summary>
    public void ReleaseData()
    {
        Versions.Clear();
        _all.Clear();
    }

    public VersionBrowseViewModel()
    {
        _svc = new VersionManifestService();
        _installer = new VersionInstaller();
        Detail = new InstalledVersionDetailVM(_installer, OnInstalled, this);
        StartWatching(); // 秒同步：磁盘变化自动重扫列表/刷新详情
    }

    private int _loaded;

    /// <summary>幂等加载（首次进入才扫描；失败可重试）</summary>
    public async Task EnsureLoadedAsync()
    {
        if (Volatile.Read(ref _loaded) == 1) return;
        try
        {
            await LoadAsync();
            Volatile.Write(ref _loaded, 1);
        }
        catch { /* 失败保持 0，下次进入重试 */ }
    }

    public async Task LoadAsync()
    {
        try
        {
            StartWatching(); // 8-23 修复：每次进入版本页重建 watcher——目录变更后跟随新目录，不再锁死旧目录
            await _svc.RefreshAsync();
            var installed = _svc.Entries.Where(e => e.Installed && InstallMarker.ShouldShowInPage(e.GameDirectory, e.Id))
                .ToDictionary(e => e.Id, e => e.GameDirectory, StringComparer.OrdinalIgnoreCase);

            _all.Clear();
            foreach (var (id, dir) in installed)
                _all.Add(MakeRow(id, dir));

            // 目录补漏：加载器版本（fabric/forge 等不在 manifest）+ PCL/官方扫描源
            foreach (var (dir, _) in GameDirectory.ScanSourceDirs())
            {
                var versionsDir = Path.Combine(dir, "versions");
                if (!Directory.Exists(versionsDir)) continue;
                foreach (var d in Directory.EnumerateDirectories(versionsDir))
                {
                    var id = Path.GetFileName(d);
                    if (_all.Any(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
                    // AL29 C1 有意分层：版本页是「管理页」，保持 json-only 判定——残件版本
                    // （只有预取 json 无 jar）必须在此可见可修（JarMissing 徽章 + 重新下载）。
                    // 主页/Mod 目标用 VersionManifestService.IsInstalled(json+jar) 过滤，勿统一这里。
                    // AL42：预取残留（.prefetched，仅供加载器继承）不显示——避免「下载 1.21.10+Fabric
                    // 后多出分开的原版条目」的混乱；下载中断的无标记残件照常可见可修
                    if (File.Exists(Path.Combine(d, $"{id}.json")) && InstallMarker.ShouldShowInPage(dir, id))
                        _all.Add(MakeRow(id, dir));
                }
            }

            // AL33：jar 缺失判定沿父链（HasUsableClientJar 三路）——Lattice 下载 jar 落加载器目录、
            // 官方 Forge 安装器落父版本目录，只查自身目录会把「实际能跑」的版本误报红字（用户删原版的根因）
            FixupJarMissing();

            // AL27：回滚 AL26 隐藏——原版与加载器都显示（徽章保留；隐藏后用户失去原版可选）
            _all = [.. _all.OrderByDescending(r => r.Id)];
            Rebuild();
            var own = _all.Count(r => r.SourceLabel == "本启动器");
            var pcl = _all.Count(r => r.SourceLabel == "PCL2");
            StatsText = $"已装 {_all.Count} 个 · 本启动器 {own} · PCL {pcl}";
            Status = _all.Count == 0
                ? "还没有已装版本，去【下载】里下载一个"
                : $"已安装 {_all.Count} 个版本";
        }
        catch (Exception ex)
        {
            Status = $"加载失败: {ex.Message}";
        }
    }

    // ---------- 秒同步：FileSystemWatcher 监听版本目录 ----------
    // VM 常驻单例 + _loaded 幂等缓存 → 首次进入后列表/详情永不再扫，外部变化（删文件、补全、
    // 手动增删版本）看不到。这里监听所有源 versions 目录，磁盘变化 500ms 防抖后重扫。

    private readonly List<FileSystemWatcher> _watchers = [];
    private CancellationTokenSource? _syncCts;

    private void StartWatching()
    {
        // 8-23 修复：原「只建一次」锁死旧目录（改目录 + InvalidateScanCache 后 watcher 仍指旧路径）。
        // 改为每次调用先停旧再建新（幂等）——进版本页即重建，跟随当前 ScanSourceDirs。
        StopWatching();
        foreach (var (dir, _) in GameDirectory.ScanSourceDirs())
        {
            var vd = Path.Combine(dir, "versions");
            if (!Directory.Exists(vd)) continue;
            try
            {
                var w = new FileSystemWatcher(vd)
                {
                    // 子目录也要监听：versions/{id}/ 下 jar/json 的增删改（补全下载、删 jar）
                    NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                };
                w.Created += OnDiskChanged;
                w.Deleted += OnDiskChanged;
                w.Changed += OnDiskChanged;
                w.Renamed += OnDiskChanged;
                // 目录被删/权限问题：忽略，同步由主动刷新（下载/删除完成）兜底
                w.Error += (_, _) => { };
                _watchers.Add(w);
            }
            catch { /* 监听失败降级为操作后主动刷新 */ }
        }
    }

    /// <summary>8-23：释放全部旧 watcher（目录变更后重建用；幂等，重复调用安全）</summary>
    private void StopWatching()
    {
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { /* 已释放/监听失败 */ }
        }
        _watchers.Clear();
    }

    private void OnDiskChanged(object _, FileSystemEventArgs e)
    {
        _syncCts?.Cancel();
        var cts = _syncCts = new CancellationTokenSource();
        _ = Task.Delay(500, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return; // 事件风暴中只执行最后一次
            Dispatcher.UIThread.Post(SyncFromDisk);
        }, TaskScheduler.Default);
    }

    /// <summary>磁盘状态同步：重扫列表（保留选中）+ 刷新详情红字/徽章</summary>
    private void SyncFromDisk()
    {
        var keep = SelectedVersion?.Id;
        var keepGameDir = SelectedVersion?.GameDir ?? ""; // 快照：RescanLocal 重建列表会清空选中
        // （8-18 批次 73：原在 else 分支读，此时 SelectedVersion 已为 null → diskGone 恒真 → 点选原版行误弹"已被删除"）
        RescanLocal();
        if (keep is null) return;
        var row = _all.FirstOrDefault(r => r.Id.Equals(keep, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            // 列表已重建，SelectedVersion 旧对象失效 → 重新选中；Detail 数据未变走 Select 早退，
            // JarMissing 等磁盘态由 RefreshJarMissing 独立刷新
            SelectedVersion = row;
            Detail.RefreshJarMissing();
        }
        else
        {
            // 8-22 消失必有反馈：磁盘态变化把选中的版本滤掉时静默清空 = 「点一下版本就没了」的
            // 幽灵体验（真机 26.2 原版消失：json 残件被重标 .prefetched → 按预取残留隐藏）。
            // 先查磁盘区分「预取隐藏」和「真被删」，别给删版本的用户误报预取文案
            var diskGone = !Directory.Exists(Path.Combine(keepGameDir, "versions", keep))
                           || !File.Exists(Path.Combine(keepGameDir, "versions", keep, $"{keep}.json"));
            Detail.ClearSelection(); // 版本被删 → 详情清空
            if (diskGone)
                NotificationService.Error($"{keep} 已被删除");
            else
                NotificationService.Error($"{keep} 已从列表移除：磁盘状态变为「预取残留」（仅供加载器继承，不单独显示）。如需保留请重新下载。");
        }
    }

    /// <summary>纯本地重扫（watcher 高频触发用）：只遍历各源 versions 目录，不依赖 manifest（无网络）</summary>
    private void RescanLocal()
    {
        _all.Clear();
        foreach (var (dir, _) in GameDirectory.ScanSourceDirs())
        {
            var versionsDir = Path.Combine(dir, "versions");
            if (!Directory.Exists(versionsDir)) continue;
            foreach (var d in Directory.EnumerateDirectories(versionsDir))
            {
                var id = Path.GetFileName(d);
                // json-only 判定（同 LoadAsync 目录补漏）：残件版本必须可见可修
                // AL57 防御：同 (Id, 目录) 不重复加（ScanSourceDirs 已按物理路径去重，此处兜底）
                if (File.Exists(Path.Combine(d, $"{id}.json")) && InstallMarker.ShouldShowInPage(dir, id)
                    && !_all.Any(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                                      && string.Equals(r.GameDir, dir, StringComparison.OrdinalIgnoreCase)))
                    _all.Add(MakeRow(id, dir));
            }
        }
        FixupJarMissing();
        _all = [.. _all.OrderByDescending(r => r.Id)];
        Rebuild();
        var own = _all.Count(r => r.SourceLabel == "本启动器");
        var pcl = _all.Count(r => r.SourceLabel == "PCL2");
        StatsText = $"已装 {_all.Count} 个 · 本启动器 {own} · PCL {pcl}";
        Status = _all.Count == 0
            ? "还没有已装版本，去【下载】里下载一个"
            : $"已安装 {_all.Count} 个版本";
    }

    private static InstalledVersionRowVM MakeRow(string id, string dir)
    {
        var (loader, mc) = VersionScan.Inspect(dir, id);
        return new(
            id,
            VersionScan.FriendlyName(id, loader, mc), // PCL 式显示名："1.21.11 (Fabric)"，原版原名
            InstallMarker.IsMarked(dir, id) ? "本启动器" : GameDirectory.SourceLabel(GameDirectory.SourceOf(dir)),
            loader,
            dir,
            GetReleaseDate(dir, id),
            !File.Exists(Path.Combine(dir, "versions", id, $"{id}.jar")), // 粗判；FixupJarMissing 沿父链修正
            mc);
    }

    /// <summary>引用表：父版本 id → 引用它的已装子版本（(子 id, 子目录)）。跨源（PCL/官方目录）同样计入。</summary>
    internal Dictionary<string, List<(string ChildId, string ChildDir)>> BuildChildrenMap()
    {
        var map = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _all)
        {
            if (string.IsNullOrEmpty(r.McVersion)) continue;
            if (!map.TryGetValue(r.McVersion, out var list))
                map[r.McVersion] = list = [];
            list.Add((r.Id, r.GameDir));
        }
        return map;
    }

    /// <summary>jar 缺失统一修正（须在 _all 完整构建后）：自身/父/引用子三路判定（见 VersionScan.HasUsableClientJar）。</summary>
    private void FixupJarMissing()
    {
        var children = BuildChildrenMap();
        for (var i = 0; i < _all.Count; i++)
        {
            var r = _all[i];
            _all[i] = r with
            {
                IsJarMissing = !VersionScan.HasUsableClientJar(r.GameDir, r.Id, r.McVersion, children),
            };
        }
    }

    /// <summary>从版本 JSON 读发布时间（懒，缺省空）</summary>
    private static string GetReleaseDate(string dir, string id)
    {
        try
        {
            var json = Path.Combine(dir, "versions", id, $"{id}.json");
            if (!File.Exists(json)) return "";
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
            return doc.RootElement.TryGetProperty("releaseTime", out var t) && t.GetString() is { } s
                ? s[..10] : "";
        }
        catch { return ""; }
    }

    private void Rebuild()
    {
        Versions.Clear();
        var kw = SearchText.Trim();
        foreach (var row in _all)
        {
            if (kw.Length == 0 || row.Id.Contains(kw, StringComparison.OrdinalIgnoreCase)
                || row.DisplayName.Contains(kw, StringComparison.OrdinalIgnoreCase)
                || row.LoaderBadge.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                Versions.Add(row);
            }
        }
    }

    partial void OnSearchTextChanged(string value) => Rebuild();

    partial void OnSelectedVersionChanged(InstalledVersionRowVM? value)
    {
        if (value is not null) Detail.Select(value);
    }

    /// <summary>按版本 id 选中（主页"去版本页补全"跳转用）</summary>
    public void SelectById(string id)
    {
        var row = _all.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (row is not null) SelectedVersion = row;
    }

    /// <summary>安装完成重扫并选中新版本（下载页/导入整合包完成后调用；先加载再选中——否则 _all 为空选不中）</summary>
    public async void OnInstalled(string versionId)
    {
        await LoadAsync();
        SelectById(versionId); // AL11：导入完成刷新列表并选中新版本
    }
}

/// <summary>
/// 右栏版本详情（PCL2 六分区）：基本信息+启动 / 启动配置（版本级覆盖）/ 加载器 / 模组 / 存档 / 版本操作。
/// </summary>
public partial class InstalledVersionDetailVM : ViewModelBase
{
    private readonly VersionInstaller _installer;
    private readonly Action<string> _onInstalled;
    private readonly VersionBrowseViewModel _vm;
    private int _sizeGeneration;

    // ---------- 基本信息 ----------

    [ObservableProperty]
    public partial string Id { get; set; } = "";

    /// <summary>PCL 式显示名（"1.21.11 (Fabric)"）；Id 保留真实目录名供管理操作</summary>
    [ObservableProperty]
    public partial string DisplayName { get; set; } = "";

    [ObservableProperty]
    public partial string SourceLabel { get; set; } = "";

    [ObservableProperty]
    public partial string ReleaseDate { get; set; } = "";

    /// <summary>继承的原版父版本（fabric-loader-x-26.3-snapshot-7 → 26.3-snapshot-7）——缺失判定沿父链（AL33）</summary>
    [ObservableProperty]
    public partial string McVersion { get; set; } = "";

    [ObservableProperty]
    public partial string SizeText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasSelection { get; set; }

    [ObservableProperty]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    public partial double DownloadProgressPercent { get; set; }

    [ObservableProperty]
    public partial string ErrorText { get; set; } = "";

    /// <summary>客户端文件缺失（扫描层只认 json，残件版本启动时才暴露）</summary>
    [ObservableProperty]
    public partial bool JarMissing { get; set; }

    /// <summary>运行状态徽章（AG2：客户端/服务端运行中——全局 RunningVersion 驱动）</summary>
    [ObservableProperty]
    public partial string RunningText { get; set; } = "";

    public string JarMissingText
    {
        get
        {
            var rv = MainViewModel.Current?.RunningVersion;
            if (rv is not null && rv.VersionId.Equals(Id, StringComparison.OrdinalIgnoreCase) && rv.Kind == "服务端")
                return $"版本 {Id} 客户端文件缺失，不影响开服（服务端运行中），需要启动客户端时再补全下载。";
            return $"版本 {Id} 客户端文件缺失，无法启动。可补全下载，或前往官方页面手动下载。";
        }
    }

    /// <summary>全局运行状态变化 → 刷新本详情徽章（服务端运行中时缺失红字弱化）</summary>
    private void RefreshRunning()
    {
        var rv = MainViewModel.Current?.RunningVersion;
        RunningText = rv is not null && rv.VersionId.Equals(Id, StringComparison.OrdinalIgnoreCase)
            ? $"运行中（{rv.Kind}）"
            : "";
        OnPropertyChanged(nameof(JarMissingText));
    }

    /// <summary>打开官方下载页（minecraft.net）——无文件版本的"链接跳转下载"入口</summary>
    [RelayCommand]
    private void OpenOfficialDownload()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://www.minecraft.net/zh-hans/download") { UseShellExecute = true });
        }
        catch { /* 无法打开浏览器忽略 */ }
    }

    public string GameDir { get; private set; } = "";
    public bool ShowRepairButton => !IsDownloading;
    public bool ShowProgress => IsDownloading;
    public bool HasError => ErrorText.Length > 0;

    // ---------- 分区 ----------

    /// <summary>版本管理（模组/存档/删除/备份/导出/打开）</summary>
    [ObservableProperty]
    public partial VersionManageViewModel? Manage { get; set; }

    /// <summary>加载器安装面板（版本 id 无加载器徽章时显示）</summary>
    [ObservableProperty]
    public partial LoaderPickerViewModel? Loader { get; set; }

    // ---------- 版本级启动配置（VersionConfigService） ----------

    [ObservableProperty]
    public partial string ConfigMemoryText { get; set; } = "";

    [ObservableProperty]
    public partial string ConfigJavaText { get; set; } = "";

    [ObservableProperty]
    public partial string ConfigArgsText { get; set; } = "";

    /// <summary>已检测的 Java（版本设置「Java 版本」下拉；后台扫描填充）</summary>
    [ObservableProperty]
    public partial IReadOnlyList<JavaChoice> JavaOptions { get; set; } = [];

    /// <summary>下拉当前选中（选中即写入 ConfigJavaText 版本级 Java 路径）</summary>
    [ObservableProperty]
    public partial JavaChoice? SelectedJava { get; set; }

    partial void OnSelectedJavaChanged(JavaChoice? value)
    {
        if (value is not null) ConfigJavaText = value.Path;
    }

    [ObservableProperty]
    public partial bool HasConfigOverrides { get; set; }

    public InstalledVersionDetailVM(VersionInstaller installer, Action<string> onInstalled, VersionBrowseViewModel vm)
    {
        _installer = installer;
        _onInstalled = onInstalled;
        _vm = vm;
        // 全局运行状态订阅（版本页徽章——AG2）
        if (MainViewModel.Current is { } main)
            main.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.RunningVersion)) RefreshRunning();
            };
    }

    /// <summary>选中左栏版本 → 填充六分区（加载器徽章为空才显示安装面板）</summary>
    public void Select(InstalledVersionRowVM row)
    {
        if (HasSelection && Id == row.Id) return;
        Id = row.Id;
        DisplayName = row.DisplayName;
        GameDir = row.GameDir;
        SourceLabel = row.SourceLabel;
        ReleaseDate = row.ReleaseDate;
        SizeText = "预估体积：计算中…";
        ErrorText = "";
        DownloadProgressPercent = 0;
        McVersion = row.McVersion;
        JarMissing = !VersionScan.HasUsableClientJar(row.GameDir, row.Id, row.McVersion, _vm.BuildChildrenMap());
        if (JarMissing)
            NotificationService.Error($"{Id} 客户端文件缺失（jar 缺失或损坏），可点「重新下载」补全");
        OnPropertyChanged(nameof(JarMissingText));
        RefreshRunning();
        HasSelection = true;

        // 分区：版本管理（模组/存档/操作）——加载器在下载时选择（下载页融合流程）
        Manage = new VersionManageViewModel(GameDir, Id, OnVersionDeleted);
        Loader = null;

        LoadConfig();
        _ = LoadSizeAsync(row);
    }

    /// <summary>磁盘状态重查：jar 出现/消失时刷新红字与徽章（补全下载完成、watcher 秒同步用）。
    /// Select() 有同版本早退 + VM 常驻，仅靠 Select 无法反映外部变化，必须主动重查。</summary>
    public void RefreshJarMissing()
    {
        if (string.IsNullOrEmpty(GameDir) || string.IsNullOrEmpty(Id)) return;
        var missing = !VersionScan.HasUsableClientJar(GameDir, Id, McVersion, _vm.BuildChildrenMap());
        if (missing != JarMissing)
        {
            JarMissing = missing;
            OnPropertyChanged(nameof(JarMissingText));
        }
    }

    /// <summary>清空详情（版本被删等场景）</summary>
    public void ClearSelection()
    {
        HasSelection = false;
        Manage = null;
        Loader = null;
        JarMissing = false;
        ErrorText = "";
    }

    private async Task LoadSizeAsync(InstalledVersionRowVM row)
    {
        var gen = ++_sizeGeneration;
        try
        {
            var version = await _installer.GetOrFetchVersionJsonAsync(row.Id, null, CancellationToken.None);
            if (gen != _sizeGeneration) return;
            long total = version.Downloads?.Client?.Size ?? 0;
            foreach (var lib in version.Libraries ?? [])
            {
                total += lib.Downloads?.Artifact?.Size ?? 0;
                if (lib.Downloads?.Classifiers is { } c) total += c.Values.Sum(x => x.Size ?? 0);
            }
            total += version.AssetIndex?.TotalSize ?? 0;
            total += version.Logging?.Client?.File?.Size ?? 0;
            SizeText = total > 0 ? $"预估体积：{FormatMb(total)}" : "";
        }
        catch
        {
            if (gen == _sizeGeneration) SizeText = "";
        }
    }

    private static string FormatMb(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024.0 / 1024:0.0} MB" : $"{bytes / 1024.0:0} KB";

    // ---------- 启动 / 停止（跳主页执行） ----------

    [RelayCommand]
    private void Launch() => MainViewModel.Current?.LaunchVersion(Id, GameDir);

    [RelayCommand]
    private void Stop() => MainViewModel.Current?.StopGame();

    // ---------- 版本级启动配置 ----------

    private void LoadConfig()
    {
        var cfg = VersionConfigService.Load(GameDir, Id);
        ConfigMemoryText = cfg.MemoryMb?.ToString() ?? "";
        ConfigJavaText = cfg.JavaPath ?? "";
        ConfigArgsText = cfg.ExtraJvmArgs ?? "";
        HasConfigOverrides = cfg.HasOverrides;
        LoadJavaOptionsAsync();
    }

    /// <summary>后台扫描本机已检测 Java，填充「Java 版本」下拉并对齐当前配置（起进程解析版本，不能卡 UI 线程）</summary>
    private void LoadJavaOptionsAsync()
    {
        _ = Task.Run(() =>
        {
            try { return JavaSelector.ScanInstalled(); }
            catch { return new List<JavaSelector.JavaInstall>(); }
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            var opts = t.Result
                .Select(j => new JavaChoice($"JDK {j.Major} — {j.Path}", j.Path))
                .OrderByDescending(o => o.Path.Contains("jre-legacy", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();
            JavaOptions = opts;
            SelectedJava = opts.FirstOrDefault(o =>
                string.Equals(o.Path, ConfigJavaText, StringComparison.OrdinalIgnoreCase));
        }), TaskScheduler.Default);
    }

    /// <summary>保存版本级配置（空 = 跟随全局）</summary>
    [RelayCommand]
    private void SaveConfig()
    {
        var cfg = new VersionConfig
        {
            MemoryMb = int.TryParse(ConfigMemoryText, out var mb) && mb >= 512 ? mb : null,
            JavaPath = string.IsNullOrWhiteSpace(ConfigJavaText) ? null : ConfigJavaText.Trim(),
            ExtraJvmArgs = string.IsNullOrWhiteSpace(ConfigArgsText) ? null : ConfigArgsText.Trim(),
        };
        VersionConfigService.Save(GameDir, Id, cfg);
        HasConfigOverrides = cfg.HasOverrides;
        NotificationService.Success($"已保存 {Id} 的启动配置");
    }

    /// <summary>恢复跟随全局（清除版本级覆盖）</summary>
    [RelayCommand]
    private void ResetConfig()
    {
        VersionConfigService.Reset(GameDir, Id);
        LoadConfig();
        NotificationService.Success($"已恢复 {Id} 跟随全局设置");
    }

    // ---------- 重新下载（修复） ----------

    [RelayCommand]
    private async Task Repair()
    {
        if (IsDownloading) return;
        var owner = DialogService.MainWindow();
        if (owner is null || !await DialogService.Confirm(owner,
                $"重新下载 {Id} 缺失或损坏的文件（已有的自动跳过）。继续？",
                "重新下载", "重新下载", "取消"))
        {
            return;
        }
        var targetId = Id;
        IsDownloading = true;
        ErrorText = "";
        DownloadProgressPercent = 0;
        try
        {
            var installer = new VersionInstaller(gameDirectory: GameDir);
            var version = await installer.GetOrFetchVersionJsonAsync(targetId, null, CancellationToken.None);
            var task = DownloadManager.Instance.EnqueueGroup($"修复 {targetId}", (ctx, ct) =>
                installer.InstallAsync(version, ctx, ct));
            // 跳转①：入队即去下载记录看进度；完成后跳回版本页（跳转②由下载中心统一处理）
            MainViewModel.Current?.NavigateToDownloadQueue("version");
            void Sync(object? _, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(DownloadTask.ProgressPercent))
                    DownloadProgressPercent = task.ProgressPercent;
                if (e.PropertyName == nameof(DownloadTask.Error) && task.Error is { } err)
                    ErrorText = err;
            }
            task.PropertyChanged += Sync;
            try { await task.Completion; }
            finally { task.PropertyChanged -= Sync; }
            if (task.State == DownloadTaskState.Completed)
            {
                NotificationService.Success($"{targetId} 修复完成");
                RefreshJarMissing(); // 补全完成 → 红字/「缺文件」徽章立即消失（秒同步）
                // AL57 模组缺失自愈：读游戏日志，缺失前置 → 确认 → 自动补全
                await ModRepairFlow.TryRepairAsync(GameDir, targetId, owner);
            }
            else if (task.Error is { } failed) ErrorText = failed;
        }
        catch (Exception ex) { ErrorText = ex.Message; }
        finally
        {
            IsDownloading = false;
            OnPropertyChanged(nameof(ShowRepairButton));
            OnPropertyChanged(nameof(ShowProgress));
            OnPropertyChanged(nameof(HasError));
        }
    }

    // ---------- 检查文件完整性 ----------

    [ObservableProperty]
    public partial bool IsChecking { get; set; }

    public bool ShowCheckButton => !IsDownloading && !IsChecking;
    public string CheckButtonText => IsChecking ? "检查中…" : "检查文件";

    [RelayCommand]
    private async Task CheckIntegrity()
    {
        if (IsDownloading || IsChecking) return;
        IsChecking = true;
        ErrorText = "";
        OnPropertyChanged(nameof(ShowCheckButton));
        OnPropertyChanged(nameof(CheckButtonText));
        try
        {
            // 磁盘直读版本 json（不联网），沿 inheritsFrom 链合并后校验 jar/库文件是否存在
            var jsonPath = Path.Combine(GameDir, "versions", Id, $"{Id}.json");
            var version = JsonSerializer.Deserialize<VersionJson>(
                await File.ReadAllTextAsync(jsonPath));
            if (version is null)
            {
                ErrorText = "版本 JSON 解析失败，无法检查（可点「重新下载」补全）";
                NotificationService.Error($"{Id} 版本 JSON 解析失败，无法检查");
                return;
            }
            // AL62 质检：存在性 + SHA1 哈希 + 统计（用户主动检查 → 哈希全验）
            var report = await AutoRepairService.VerifyVersionAsync(version, GameDir, verifyHashes: true);
            if (report.IsComplete)
            {
                NotificationService.Success($"{Id} {report.SummaryText}");
            }
            else
            {
                ErrorText = $"文件不完整：缺 {report.Missing} 个（首例：{Path.GetFileName(report.MissingFiles[0])}）。可点「重新下载」补全";
                NotificationService.Error($"{Id} 文件不完整：缺 {report.Missing} 个");
            }
        }
        catch (Exception ex)
        {
            ErrorText = $"检查失败：{ex.Message}";
            NotificationService.Error($"{Id} 检查失败：{ex.Message}");
        }
        finally
        {
            IsChecking = false;
            OnPropertyChanged(nameof(ShowCheckButton));
            OnPropertyChanged(nameof(CheckButtonText));
            OnPropertyChanged(nameof(HasError));
            RefreshJarMissing(); // 检查后同步磁盘态（用户可能已手动补文件/删文件）
        }
    }

    private void OnVersionDeleted()
    {
        ClearSelection();
        _onInstalled(Id);
    }
}
