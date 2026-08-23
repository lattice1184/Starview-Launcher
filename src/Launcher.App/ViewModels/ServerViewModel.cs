using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Services;
using Launcher.Core.Diagnostics;
using Launcher.Core.Download;
using Launcher.Core.Launch;
using Launcher.Core.Server;
using Launcher.Core.Services;
using Launcher.Core.Utils;

namespace Launcher.App.ViewModels;

/// <summary>server.properties 编辑行控件类型</summary>
public enum PropControlKind { Text, Bool, Number, Choice }

/// <summary>在线玩家行（服务器图形化管理）</summary>
public sealed record ServerPlayerVM(string Name);

/// <summary>server.properties 编辑行（按类型渲染：文本/开关/数字/下拉）</summary>
public partial class PropRowVM : ObservableObject
{
    public string Key { get; }
    public string Label { get; }
    public PropControlKind Kind { get; }
    public IReadOnlyList<string> Options { get; }

    [ObservableProperty]
    public partial string Value { get; set; }

    public bool IsBool => Kind == PropControlKind.Bool;
    public bool IsNumber => Kind == PropControlKind.Number;
    public bool IsChoice => Kind == PropControlKind.Choice;
    public bool IsText => Kind == PropControlKind.Text;

    /// <summary>开关绑定（true/false ↔ Value）</summary>
    public bool BoolValue
    {
        get => Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }

    /// <summary>数字控件取值范围（0 = 不限）</summary>
    public int Min { get; }
    public int Max { get; }

    /// <summary>数字控件绑定（NumericUpDown decimal? ↔ Value 字符串）</summary>
    public decimal? NumberValue
    {
        get => decimal.TryParse(Value, out var d) ? d : null;
        set => Value = value?.ToString("0") ?? "";
    }

    public PropRowVM(string key, string label, string value, PropControlKind kind, IReadOnlyList<string>? options = null,
        int min = 0, int max = 0)
    {
        Key = key;
        Label = label;
        Value = value;
        Kind = kind;
        Options = options ?? [];
        Min = min;
        Max = max;
    }
}

/// <summary>
/// 开服页：选择已装版本 → 下载服务端 → 编辑 server.properties → 启动/停止/控制台。
/// </summary>
public partial class ServerViewModel : ViewModelBase
{
    private static readonly (string Key, string Label, PropControlKind Kind, string[]? Options)[] PropDefs =
    [
        ("server-port", "端口", PropControlKind.Number, null),
        ("level-name", "世界名", PropControlKind.Text, null),
        ("max-players", "最大玩家", PropControlKind.Number, null),
        ("motd", "服务器描述 (MOTD)", PropControlKind.Text, null),
        ("online-mode", "正版验证", PropControlKind.Bool, null),
        ("difficulty", "难度", PropControlKind.Choice, ["peaceful", "easy", "normal", "hard"]),
        ("gamemode", "游戏模式", PropControlKind.Choice, ["survival", "creative", "adventure", "spectator"]),
        ("view-distance", "视距（区块）", PropControlKind.Number, null),
        ("pvp", "PVP", PropControlKind.Bool, null),
        ("white-list", "白名单", PropControlKind.Bool, null),
    ];

    /// <summary>数字属性的取值范围（NumericUpDown 校验）</summary>
    private static readonly Dictionary<string, (int Min, int Max)> NumberRanges = new()
    {
        ["server-port"] = (1, 65535),
        ["view-distance"] = (2, 32),
        ["max-players"] = (1, 1000),
    };

    private readonly ServerInstaller _installer = new();
    private readonly ServerProcess _process = new();
    private const int MaxLogLines = 500;

    public ObservableCollection<VersionInstanceVM> InstalledVersions { get; } = [];
    public ObservableCollection<PropRowVM> PropRows { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];

    /// <summary>在线玩家（日志解析 joined/left the game + list 命令回填）</summary>
    public ObservableCollection<ServerPlayerVM> OnlinePlayers { get; } = [];

    /// <summary>在线玩家标题（在线玩家（N））</summary>
    public string PlayersCountText => $"在线玩家（{OnlinePlayers.Count}）";

    private static readonly Regex JoinedGame = new(@"]: (.+?) joined the game", RegexOptions.Compiled);
    private static readonly Regex LeftGame = new(@"]: (.+?) left the game", RegexOptions.Compiled);
    private static readonly Regex PlayerList = new(@"There are \d+ of a max of \d+ players online: (.+)", RegexOptions.Compiled);

    [ObservableProperty]
    public partial VersionInstanceVM? SelectedVersion { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "选一个版本开服";

    /// <summary>Status 是否错误态（AL7 红字规范：关键性/难以挽回的失败红字加粗）</summary>
    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    /// <summary>SetStatus 的错误标记（Status 赋值瞬间被 OnStatusChanged 读取后复位）</summary>
    private bool _statusSetError;

    /// <summary>Status 统一入口：error=true 时红字标注；普通赋值自动重置红字</summary>
    private void SetStatus(string text, bool error = false)
    {
        _statusSetError = error;
        Status = text;
        _statusSetError = false;
    }

    partial void OnStatusChanged(string value) => StatusIsError = _statusSetError;

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    /// <summary>运行状态上报全局（版本页徽章）——服务端启动时同步刷新局域网地址（AG2/AG3）</summary>
    partial void OnIsRunningChanged(bool value)
    {
        var main = MainViewModel.Current;
        if (main is not null)
        {
            if (value)
                main.RunningVersion = new RunningVersionInfo(SelectedVersion?.Name ?? "", "服务端");
            else if (main.RunningVersion?.Kind == "服务端")
                main.RunningVersion = null;
        }
        if (value) RefreshLanAddress();
        else { LanAddressText = ""; LocalAddressText = ""; }
    }

    [ObservableProperty]
    public partial bool IsInstalling { get; set; }

    [ObservableProperty]
    public partial string ServerDirText { get; set; } = "";

    /// <summary>机器状态摘要（内存/CPU/磁盘 + 建议配置）</summary>
    [ObservableProperty]
    public partial string MachineStatusText { get; set; } = "点击刷新查看机器状态与建议配置";

    public string CommandInput { get; set; } = "";

    /// <summary>
    /// 所选版本所在的游戏目录（AL7 主根因修复）：版本可能来自 PCL/官方等外部源（列表跨源扫描），
    /// 操作必须落到版本实际所在目录，而不是启动器自建目录——否则"版本未安装"、下载从未真正开始
    /// </summary>
    private static string VersionGameDir(VersionInstanceVM? v) =>
        string.IsNullOrEmpty(v?.GameDir) ? GameDirectory.InstallDir() : v!.GameDir;

    /// <summary>当前服务端目录（servers/{versionId}）</summary>
    private string? ServerDir => SelectedVersion is null
        ? null
        : ServerInstaller.ServerDir(VersionGameDir(SelectedVersion), SelectedVersion.Name);

    /// <summary>授予 OP 的玩家名（AH1：预填当前登录账号；可改任意名，无需在线）</summary>
    [ObservableProperty]
    public partial string OpNameText { get; set; } = "";

    /// <summary>授予 OP 操作反馈（成功/错误提示行）</summary>
    [ObservableProperty]
    public partial string OpStatusText { get; set; } = "";

    /// <summary>预生成世界标志：服务端就绪（Done）后自动 stop（AI 批次）</summary>
    private bool _autoStopOnReady;

    /// <summary>预生成世界完成标志：Exited 时提示"世界已生成"</summary>
    private bool _worldGenDone;

    /// <summary>一键开服标志：就绪（Done）后自动授予 OP + 拉起客户端（AJ 批次）</summary>
    private bool _autoJoinOnReady;

    /// <summary>一键开服进行中（防重入）</summary>
    private bool _oneClickActive;

    public ServerViewModel()
    {
        OpNameText = Launcher.Core.Account.AccountService.Shared.Current?.Name ?? "";
        // 8-15：账号切换后重预填 OP 默认值（此前构造时快照一次，切换账号「授予 OP」仍是旧账号）
        Launcher.Core.Account.AccountService.Shared.Changed += () =>
            Dispatcher.UIThread.Post(() =>
                OpNameText = Launcher.Core.Account.AccountService.Shared.Current?.Name ?? "");
        _process.OutputReceived += line => AppendLog(line);
        _process.Exited += code =>
        {
            Dispatcher.UIThread.Post(async () =>
            {
                IsRunning = false;
                if (_worldGenDone)
                {
                    // 预生成世界流程：世界已落盘，正常结束
                    _worldGenDone = false;
                    _autoStopOnReady = false;
                    var world = CurrentLevelName();
                    AppendLog($"§ 世界「{world}」生成完成，已自动停止");
                    Status = $"世界「{world}」已生成，可进入游戏";
                    NotificationService.Success($"世界「{world}」已生成");
                    RefreshOps();
                    return;
                }
                AppendLog(code == 0 ? "§ 服务端已停止" : $"§ 服务端异常退出（exitCode={code}）");
                if (code == 0) Status = "服务端已停止";
                else SetStatus("服务端异常退出，请查看日志", error: true);
                if (code != 0)
                {
                    // 动态诊断：等 stdout 缓冲刷完，用已收集日志匹配已知错误模式 → 中文原因弹窗
                    await Task.Delay(300);
                    var diag = LogDiagnostics.DiagnoseDetailed(string.Join(Environment.NewLine, Logs));
                    foreach (var d in diag) AppendLog("§ 诊断：" + d.Explanation);
                    if (diag.Count > 0 && DialogService.MainWindow() is { } owner)
                    {
                        // 开服自修复：诊断命中 Redownload（server.jar 缺失/损坏）→ 提供"自动修复并重新启动"
                        var fixable = diag.Any(d => d.Fix == FixKind.Redownload);
                        var ok = await DialogService.Warn(owner, $"服务端启动失败（exitCode={code}）",
                            string.Join(Environment.NewLine + Environment.NewLine, diag.Select(d => d.Explanation))
                            + (fixable
                                ? Environment.NewLine + Environment.NewLine + "检测到服务端文件问题。你可以让它自动重下服务端再重启。"
                                : "")
                            + Environment.NewLine + Environment.NewLine + "完整日志可在控制台复制或导出。",
                            "服务端异常退出", fixable ? "自动修复并重新启动" : "知道了", fixable ? "取消" : "");
                        if (ok && fixable && SelectedVersion is { } ver)
                        {
                            SetStatus($"正在重新下载 {ver.Name} 的服务端…");
                            try
                            {
                                await AutoRepairService.FixServerJarAsync(ver.Name, VersionGameDir(ver), _installer);
                                NotificationService.Success("服务端已重新下载，正在重新启动");
                                await StartServer();
                            }
                            catch (Exception ex)
                            {
                                SetStatus($"自动修复失败：{ex.Message}", error: true);
                                NotificationService.Error($"自动修复失败：{ex.Message}");
                            }
                        }
                    }
                }
                RefreshOps(); // 停止后 ops.json 为最终态，刷新 OP 列表
                RefreshBanned(); // 停止后 banned-players.json 为最终态，刷新封禁列表
            });
        };
        InitSuggestions();
        RefreshSuggestionDiff();
        _ = RefreshVersionsAsync();
        // 机器状态实时刷新（每 5 秒；后台读内存/CPU/磁盘，只更新状态文本不动建议输入）
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += async (_, _) => await RefreshStatusCoreAsync();
        _statusTimer.Start();
    }

    private readonly DispatcherTimer _statusTimer;

    /// <summary>8-19 内存瘦身：机器状态轮询只在开服页显示时运行——此前构造即常驻，
    /// 从不进开服页的会话也每 5 秒轮询内存/CPU/磁盘（后台采样 + UI 线程触发）</summary>
    public void OnPageActive()
    {
        if (!_statusTimer.IsEnabled) _statusTimer.Start();
    }

    public void OnPageInactive() => _statusTimer.Stop();

    /// <summary>建议配置编辑值（机器状态卡内直接可改，ApplySuggestion 应用）</summary>
    [ObservableProperty]
    public partial string SuggestionMemoryText { get; set; } = "2048";

    [ObservableProperty]
    public partial string SuggestionViewText { get; set; } = "10";

    [ObservableProperty]
    public partial string SuggestionPlayersText { get; set; } = "20";

    /// <summary>建议与当前参数的 diff 提示（应用后/输入变化时联动）</summary>
    [ObservableProperty]
    public partial string SuggestionStatusText { get; set; } = "";

    /// <summary>填入初始建议值（内存/视距/玩家）</summary>
    private void InitSuggestions()
    {
        var (xmx, view, players) = BuildSuggestion();
        SuggestionMemoryText = xmx.ToString();
        SuggestionViewText = view.ToString();
        SuggestionPlayersText = players.ToString();
    }

    partial void OnSuggestionMemoryTextChanged(string value) => RefreshSuggestionDiff();
    partial void OnSuggestionViewTextChanged(string value) => RefreshSuggestionDiff();
    partial void OnSuggestionPlayersTextChanged(string value) => RefreshSuggestionDiff();

    /// <summary>刷新已装版本（构造 + 每次进入开服页调用——新装的版本立即可见）</summary>
    public async Task RefreshVersionsAsync()
    {
        // 8-23 同 HomeViewModel：manifest 拉取失败不再整体空吞——磁盘扫描兜底，版本列表不消失
        var svc = new VersionManifestService();
        try
        {
            await svc.RefreshAsync();
        }
        catch { /* 断网/无缓存：仅保留磁盘扫描结果 */ }
        // 收集全部候选 (目录, 版本)
        var candidates = new List<(string Dir, string Id)>();
        try
        {
            foreach (var e in svc.Entries.Where(e => e.Installed && InstallMarker.ShouldShowInPage(e.GameDirectory, e.Id)))
                candidates.Add((e.GameDirectory, e.Id));
            // 目录补漏：加载器版本（fabric/forge 等不在 manifest）+ 自建扫描源（8-23 起不扫 PCL/官方）
            foreach (var (dir, _) in GameDirectory.ScanSourceDirs())
            {
                var versionsDir = Path.Combine(dir, "versions");
                if (!Directory.Exists(versionsDir)) continue;
                foreach (var d in Directory.EnumerateDirectories(versionsDir))
                {
                    var id = Path.GetFileName(d);
                    if (candidates.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
                    // 8-18 批次 73：预取原版父版本（仅供加载器继承）不入开服列表；隔离删除残件不显示
                    if (id.Contains(".deleting-", StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(Path.Combine(d, $"{id}.json")) && InstallMarker.ShouldShowInPage(dir, id))
                        candidates.Add((dir, id));
                }
            }
            // AL27：回滚 AL26 隐藏——原版与加载器都显示（友好名徽章保留；隐藏后用户失去原版可选）
            InstalledVersions.Clear();
            foreach (var (dir, id) in candidates)
            {
                var (loader, mc) = VersionScan.Inspect(dir, id);
                InstalledVersions.Add(new VersionInstanceVM(id, GameDir: dir, LoaderBadge: loader, McVersion: mc));
            }
            if (InstalledVersions.Count > 0 && SelectedVersion is null) SelectedVersion = InstalledVersions[0];
            // 全局版本绑定：主页当前版本优先选中（AF1）
            if (MainViewModel.Current?.CurrentVersion is { } cur)
            {
                var hit = InstalledVersions.FirstOrDefault(v => v.Name.Equals(cur.Name, StringComparison.OrdinalIgnoreCase));
                if (hit is not null) SelectedVersion = hit;
            }
        }
        catch { }
        finally
        {
            // 8-15 进页刷新 OP/封禁名单（此前只在启动/停止时刷——漏刷后列表一直 0，解封无处点）
            Dispatcher.UIThread.Post(() => { RefreshOps(); RefreshBanned(); });
        }
    }

    [RelayCommand]
    private void RefreshVersions() => _ = RefreshVersionsAsync();

    partial void OnSelectedVersionChanged(VersionInstanceVM? value)
    {
        if (value is null) return;
        var dir = ServerInstaller.ServerDir(VersionGameDir(value), value.Name);
        ServerDirText = dir;
        Status = File.Exists(Path.Combine(dir, "server.jar")) ? "服务端就绪，可启动" : "还没下载服务端";
        LoadProperties();
    }

    /// <summary>建议配置（供显示与应用共用）：按 CPU 核数 + 可用内存动态推算（不再写死 10/20）</summary>
    private (long XmxMb, int ViewDistance, int MaxPlayers) BuildSuggestion()
        => SuggestionPresets.Compute(Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);

    /// <summary>档位按钮：0=测试低配 / 1=推荐（现算）/ 2=高配，填充建议编辑框</summary>
    public void ApplyPreset(int preset)
    {
        var (xmx, view, players) = preset switch
        {
            0 => SuggestionPresets.Fixed(SuggestionPresets.Preset.Low),
            2 => SuggestionPresets.Fixed(SuggestionPresets.Preset.High),
            _ => BuildSuggestion(), // 推荐 = 按当前机器状态现算
        };
        SuggestionMemoryText = xmx.ToString();
        SuggestionViewText = view.ToString();
        SuggestionPlayersText = players.ToString();
        NotificationService.Info(preset switch
        {
            0 => "已填入测试低配（内存 1G · 视距 4 · 玩家 5）",
            2 => "已填入高配（内存 8G · 视距 16 · 玩家 40）",
            _ => "已填入按当前机器计算的推荐配置",
        });
    }

    /// <summary>机器状态实时刷新（每 5 秒自动；后台读内存/CPU/磁盘）</summary>
    private async Task RefreshStatusCoreAsync()
    {
        MachineStatusText = await Task.Run(() =>
        {
            try
            {
                var avail = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;      // 可用物理内存
                var total = TotalPhysicalMemory();                               // 总物理内存
                var diskFree = FreeDiskGb(VersionGameDir(SelectedVersion));
                var cpu = CpuUsagePercent();                                     // 实时 CPU 使用率（失败 -1）

                var cpuText = cpu >= 0 ? $"{cpu:0.#}%" : $"{Environment.ProcessorCount} 核";
                return $"内存：可用 {avail / 1024.0 / 1024 / 1024:0.#} GB / 总 {total / 1024.0 / 1024 / 1024:0.#} GB" + Environment.NewLine +
                       $"CPU：{cpuText}（{Environment.ProcessorCount} 核） · 磁盘剩余：{diskFree:0.#} GB";
            }
            catch (Exception ex)
            {
                return $"读取失败: {ex.Message}";
            }
        });
    }

    /// <summary>CPU 使用率（PerformanceCounter 两次采样差值；无权限/不支持返回 -1）</summary>
    private static double CpuUsagePercent()
    {
        try
        {
            using var counter = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total");
            counter.NextValue();
            Thread.Sleep(300);
            return Math.Round(counter.NextValue(), 1);
        }
        catch { return -1; }
    }

    /// <summary>应用建议配置（读建议编辑框值）：写 server.properties（视距/玩家）+ 更新全局内存</summary>
    [RelayCommand]
    private async Task ApplySuggestion()
    {
        var dir = ServerDir;
        if (dir is null)
        {
            await WarnNoVersion();
            return;
        }
        var xmxMb = long.TryParse(SuggestionMemoryText, out var m) && m >= 512 ? m : 2048;
        var view = int.TryParse(SuggestionViewText, out var v) && v >= 2 && v <= 32 ? v : 10;
        var players = int.TryParse(SuggestionPlayersText, out var p) && p >= 1 && p <= 1000 ? p : 20;

        // server.properties：只覆盖建议项，不碰用户已有配置
        var props = ServerProperties.Load(Path.Combine(dir, "server.properties"));
        props.Set("view-distance", view.ToString());
        props.Set("max-players", players.ToString());
        props.Save(Path.Combine(dir, "server.properties"));

        // 服务器内存 = 建议 Xmx（独立字段——不再覆盖全局 MemoryMb，避免误改客户端启动内存）
        var s = LauncherSettings.Current;
        s.ServerMemoryMb = (int)xmxMb;
        s.Save();

        // 刷新表单显示已应用值 + 建议区同步（改前可能是旧值，不刷则表单与建议区不同步）
        LoadProperties();
        RefreshSuggestionDiff();

        Status = $"已应用配置：内存 {xmxMb}MB · 视距 {view} · 玩家 {players}";
        NotificationService.Success("已应用服务器配置");
    }

    /// <summary>建议 diff：对比建议编辑框值与当前 server.properties 参数（输入变化/应用后联动）</summary>
    private void RefreshSuggestionDiff()
    {
        var view = int.TryParse(SuggestionViewText, out var sv) ? sv : 10;
        var players = int.TryParse(SuggestionPlayersText, out var sp) ? sp : 20;
        var diffs = new List<string>();
        if (int.TryParse(PropRows.FirstOrDefault(r => r.Key == "view-distance")?.Value, out var cv) && cv != view)
            diffs.Add($"视距 {view}（当前 {cv}）");
        if (int.TryParse(PropRows.FirstOrDefault(r => r.Key == "max-players")?.Value, out var cp) && cp != players)
            diffs.Add($"最大玩家 {players}（当前 {cp}）");
        SuggestionStatusText = diffs.Count == 0
            ? $"建议配置已与当前参数一致 ✓（视距 {view} · 最大玩家 {players}）"
            : $"建议调整：{string.Join("、", diffs)}（点[应用建议配置]生效）";
    }

    /// <summary>物理内存总量（GlobalMemoryStatusEx P/Invoke）</summary>
    private static ulong TotalPhysicalMemory()
    {
        try
        {
            var status = new MemoryStatusEx { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? status.ullTotalPhys : 0;
        }
        catch { return 0; }
    }

    private static double FreeDiskGb(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path) ?? "C:\\";
            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;
        }
        catch { return 0; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    /// <summary>下载服务端 jar（确认后执行；幂等跳过已有）——一条龙（AJ）走无确认的 core 版本</summary>
    [RelayCommand]
    private async Task DownloadServer()
    {
        var version = SelectedVersion;
        if (version is null)
        {
            await WarnNoVersion();
            return;
        }
        if (IsInstalling) return;
        if (DialogService.MainWindow() is { } owner
            && !await DialogService.Confirm(owner,
                $"下载 {version.Name} 服务端（约 50MB）？", "下载服务端", "下载", "取消"))
        {
            return;
        }
        await DownloadServerCoreAsync();
    }

    /// <summary>下载服务端核心（无确认弹窗——一条龙自动调用；幂等跳过已有）</summary>
    private async Task DownloadServerCoreAsync()
    {
        var version = SelectedVersion;
        if (version is null || IsInstalling) return;
        IsInstalling = true;
        Status = "正在下载服务端…";
        try
        {
            var installer = _installer;
            var dir = VersionGameDir(version);
            var task = DownloadManager.Instance.EnqueueGroup($"下载服务端 {version.Name}", (ctx, ct) =>
            {
                ctx.AddChild("server.jar", 1, (progress, c) => installer.InstallAsync(version.Name, dir, progress, c));
                return Task.CompletedTask;
            });
            // 自动跳到下载板块"下载记录"tab（角标已随 ActiveCountChanged 亮起）
            MainViewModel.Current?.NavigateToDownloadQueue();
            await task.Completion;
            VerifyServerJar(ServerDir); // 不完整则抛异常（进 catch 显式报错，防套娃）
            ServerDirText = ServerDir ?? "";
            LoadProperties();
            Status = "服务端下载完成，可启动";
            NotificationService.Success($"{version.Name} 服务端已就绪");
            // AL11：下载成功跳回开服页（下载中自动跳去了下载记录，不跳回用户看不到就绪状态）
            MainViewModel.Current?.NavigateToServer();
        }
        catch (Exception ex)
        {
            SetStatus($"下载失败: {ex.Message}", error: true);
            NotificationService.Error(ex.Message);
            // AL7：失败切回开服页（下载中自动跳去了下载板块，不切回用户看不到红字原因）
            MainViewModel.Current?.NavigateToServer();
            // 失败显式报出（终止"未安装→再下载→又失败"套娃）：单按钮弹窗说明原因
            if (DialogService.MainWindow() is { } dlg)
                await DialogService.Warn(dlg, "服务端下载失败",
                    ex.Message + "\n\n请稍后重试「下载服务端」。", "下载失败", "知道了", "");
        }
        finally
        {
            IsInstalling = false;
        }
    }

    /// <summary>前提不满足警告：未选版本（红字加粗原因 + 说明）</summary>
    private static async Task WarnNoVersion() =>
        await DialogService.Warn(DialogService.MainWindow(), "你还没选版本",
            "选顶部要开服的已装版本再继续。", "无法继续", "知道了", "");

    /// <summary>下载服务端并自动启动（弹窗"立即下载并启动"确认后走这里；下载完成前提已满足直接 StartServer）</summary>
    private async Task DownloadAndStartAsync()
    {
        var version = SelectedVersion;
        if (version is null || IsInstalling) return;
        IsInstalling = true;
        Status = "正在下载服务端…";
        var readyToStart = false;
        try
        {
            var installer = _installer;
            var dir = VersionGameDir(version);
            var task = DownloadManager.Instance.EnqueueGroup($"下载服务端 {version.Name}", (ctx, ct) =>
            {
                ctx.AddChild("server.jar", 1, (progress, c) => installer.InstallAsync(version.Name, dir, progress, c));
                return Task.CompletedTask;
            });
            // 自动跳到下载板块"下载记录"tab（与 DownloadServer 一致）
            MainViewModel.Current?.NavigateToDownloadQueue();
            await task.Completion;
            VerifyServerJar(ServerDir); // 不完整则抛异常（进 catch 显式报错，防套娃）
            ServerDirText = ServerDir ?? "";
            LoadProperties();
            Status = "服务端下载完成，正在启动…";
            readyToStart = true; // 校验通过后才启动（见 finally）
            MainViewModel.Current?.NavigateToServer(); // AL11：下载成功回开服页看控制台
        }
        catch (Exception ex)
        {
            SetStatus($"下载失败: {ex.Message}", error: true);
            NotificationService.Error(ex.Message);
            // AL7：失败切回开服页（下载中自动跳去了下载板块，不切回用户看不到红字原因）
            MainViewModel.Current?.NavigateToServer();
            // 失败显式报出（终止"未安装→再下载→又失败"套娃）：单按钮弹窗说明原因
            if (DialogService.MainWindow() is { } dlg)
                await DialogService.Warn(dlg, "服务端下载失败",
                    ex.Message + "\n\n请稍后重试「下载服务端」。", "下载失败", "知道了", "");
        }
        finally
        {
            IsInstalling = false;
            // 启动必须等 IsInstalling 复位之后：StartServer 开头有 IsInstalling 检查，
            // 在 try 里调用会被直接 return（真机验收抓到：重新下载后不自动启动）
            if (readyToStart) await StartServer();
        }
    }

    /// <summary>启动服务端（自动同意 EULA；Java 自动选配 + 设置页内存）；前提不满足弹红字警告对话框</summary>
    [RelayCommand]
    private async Task StartServer()
    {
        var version = SelectedVersion;
        var dir = ServerDir;
        if (version is null || dir is null)
        {
            await WarnNoVersion();
            return;
        }
        var jarPath = Path.Combine(dir, "server.jar");
        if (!ServerInstaller.IsValidServerJar(jarPath))
        {
            var missing = !File.Exists(jarPath);
            // 红字警告：未安装/损坏 → 提供"立即下载并启动"/"修复并启动"（坏 jar 先删再下——
            // InstallAsync 幂等跳过已有文件，不删会假装成功）
            if (DialogService.MainWindow() is { } owner
                && await DialogService.Warn(owner, missing ? "未安装服务端" : "服务端文件损坏",
                    missing
                        ? $"「{version.Name}」的服务端还没下载。可以现在下载并启动，或先取消。"
                        : $"「{version.Name}」的 server.jar 损坏（下载不完整或被清理）。将删除损坏文件后重新下载并启动。",
                    "无法启动服务端", missing ? "立即下载并启动" : "重新下载并启动", "取消"))
            {
                if (!missing)
                {
                    try { File.Delete(jarPath); }
                    catch (Exception ex) { SetStatus($"无法删除损坏的服务端文件：{ex.Message}（可手动删除后重试）", error: true); return; }
                }
                await DownloadAndStartAsync();
            }
            return;
        }
        if (IsRunning || IsInstalling) return;

        // 8-19 进服皮肤（online-mode 匹配）：LittleSkin/离线账号 + online-mode=true 的正版验证服过不了校验
        // （新服默认 offline 不受影响；老服/自建服默认 true）。用户改好配置后点「继续启动」即可
        var onlineMode = ReadOnlineMode(dir);
        if (onlineMode == true
            && Launcher.Core.Account.AccountService.Shared.Current is { Type: not "microsoft" })
        {
            if (DialogService.MainWindow() is { } owner
                && !await DialogService.Confirm(owner,
                    "这个服务器开了正版验证（online-mode=true），当前账号不是正版账号，启动后会被服务器挡在门外、角色皮肤也显示不出来。\n\n"
                    + "可以把 server.properties 的 online-mode 改成 false（离线模式），或换正版账号。改好后点「继续启动」。",
                    "正版验证会挡住当前账号", "我改好了，继续启动", "取消"))
                return;
        }

        ServerInstaller.AcceptEula(dir);
        // 启动前日志文件锁预检（AJ2）：服务端启动要删除旧的 latest.log，被残留进程/编辑器占用时启动即失败——先探测并明确提示
        var latest = Path.Combine(dir, "logs", "latest.log");
        if (File.Exists(latest))
        {
            try { using var probe = File.Open(latest, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch
            {
                SetStatus($"日志文件被占用，服务端起不来：\n{latest}\n\n一般是上一个服务端进程没退干净（任务管理器结束残留的 java.exe），或日志正被编辑器打开。", error: true);
                NotificationService.Error("日志文件被占用（可结束残留 java.exe 或关闭打开的日志后重试）");
                return;
            }
        }
        // REVIEW-D 高1：Java 选配曾在本 try 外——PickServerJava 找不到匹配版本时 throw 直接外抛：
        // 普通启动路径静默失败（无提示无状态），一键开服路径 _oneClickActive 永不复位卡死流程（高2 同源）。
        string java;
        int mem;
        try
        {
            java = LauncherSettings.Current.JavaPath is { } custom && File.Exists(custom)
                ? custom
                : PickServerJava(VersionGameDir(version), version.Name);
            mem = LauncherSettings.Current.ServerMemoryMb > 0
                ? LauncherSettings.Current.ServerMemoryMb
                : 2048;
        }
        catch (Exception ex)
        {
            SetStatus($"启动失败: {ex.Message}", error: true);
            NotificationService.Error(ex.Message);
            return;
        }
        try
        {
            Logs.Clear();
            _process.Start(dir, java, mem, LauncherSettings.Current.GamePriority); // 与游戏同进程优先级设置
            IsRunning = true;
            Status = "服务端运行中，可在控制台输入命令。";
            AppendLog($"§ 已启动：{java}");
            AppendLog($"§ 内存 {mem}MB · 世界目录 {dir}");
            // AL8：完整启动命令落日志（崩溃/启动失败时根因一眼可见）
            if (_process.CommandLine is { } cmd)
                AppendLog($"§ 启动命令：" + LaunchProcess.DescribeCommandLine(java, cmd));
            RefreshOps(); // 启动后 ops.json 已就绪（含已授予的 OP）
            RefreshBanned(); // 启动后 banned-players.json 已就绪
        }
        catch (Exception ex)
        {
            SetStatus($"启动失败: {ex.Message}", error: true);
            NotificationService.Error(ex.Message);
        }
    }

    /// <summary>8-19 读 server.properties 的 online-mode（文件缺失/读取失败 → null 不做校验）</summary>
    private static bool? ReadOnlineMode(string dir)
    {
        try
        {
            var path = Path.Combine(dir, "server.properties");
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadAllLines(path))
            {
                var s = line.Trim();
                if (!s.StartsWith("online-mode=", StringComparison.OrdinalIgnoreCase)) continue;
                return s["online-mode=".Length..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
        return null;
    }

    /// <summary>优雅停止（stop 命令 + 超时强杀；后台等待不阻塞 UI）</summary>
    /// <summary>服务端 Java 按版本选择：沿继承链解析所需大版本（fabric/整合包 profile 无 javaVersion，继承原版 26.2 → Java 25）；
    /// 找不到匹配版本直接报错（不静默降级——降级拿旧 Java 跑新版本必崩，26.2 曾默认 17 启动即 UnsupportedClassVersionError）。</summary>
    private static string PickServerJava(string gameDir, string versionId)
    {
        var major = 17;
        try
        {
            var p = Path.Combine(gameDir, "versions", versionId, $"{versionId}.json");
            if (File.Exists(p))
            {
                var v = System.Text.Json.JsonSerializer.Deserialize<Launcher.Core.Model.Mojang.VersionJson>(File.ReadAllText(p));
                if (v is not null)
                {
                    major = JavaSelector.ResolveRequiredMajor(v, id =>
                    {
                        var parentPath = Path.Combine(gameDir, "versions", id, $"{id}.json");
                        if (!File.Exists(parentPath)) return null;
                        try
                        {
                            return System.Text.Json.JsonSerializer.Deserialize<Launcher.Core.Model.Mojang.VersionJson>(
                                File.ReadAllText(parentPath));
                        }
                        catch { return null; }
                    });
                }
            }
        }
        catch { /* json 读取失败用默认推断 */ }
        try
        {
            var picked = JavaSelector.Pick(major);
            if (!string.IsNullOrEmpty(picked)) return picked;
        }
        catch { }
        throw new InvalidOperationException(
            $"需要 Java {major}，但本机未找到匹配版本（可在设置页手动指定 Java 路径）");
    }

    // ---------- 连接信息（AG3 + AH1：服务端运行中显示本机/局域网地址，朋友可连） ----------

    /// <summary>局域网地址行（ip:port，复制给局域网朋友）</summary>
    [ObservableProperty]
    public partial string LanAddressText { get; set; } = "";

    /// <summary>本机地址行（127.0.0.1:port，一键进服用）</summary>
    [ObservableProperty]
    public partial string LocalAddressText { get; set; } = "";

    /// <summary>8-15 对外地址（蓝盾/虚拟局域网 IP 或 IP:端口；填了复制按钮优先复制它——虚拟网外朋友连不上物理局域网 IP）</summary>
    [ObservableProperty]
    public partial string ExternalAddressText { get; set; } = "";

    /// <summary>服务端端口（RefreshLanAddress 读 server.properties；复制对外地址拼端口用）</summary>
    private int _serverPort = 25565;

    /// <summary>刷新连接信息（读 server.properties 端口；无内网 IP 时局域网行留空）</summary>
    private void RefreshLanAddress()
    {
        _serverPort = 25565;
        try
        {
            var dir = ServerDir;
            if (dir is not null)
                _serverPort = ServerProperties.Load(Path.Combine(dir, "server.properties")).GetInt("server-port", 25565);
        }
        catch { }
        LocalAddressText = $"本机 127.0.0.1:{_serverPort}";
        var ip = FindLanIp();
        LanAddressText = ip is null ? "" : $"局域网 {ip}:{_serverPort}";
    }

    /// <summary>复制连接地址：对外地址优先（填了蓝盾/虚拟网地址复制它；只填 IP 自动拼当前端口），
    /// 否则复制局域网地址（去掉前缀标签，得到 ip:port）</summary>
    [RelayCommand]
    private async Task CopyLanAddress()
    {
        var text = ExternalAddressText.Trim();
        if (text.Length == 0)
        {
            if (LanAddressText.Length == 0) return;
            text = LanAddressText.Replace("局域网 ", "");
        }
        else if (!text.Contains(':'))
        {
            text = $"{text}:{_serverPort}"; // 只填了 IP → 拼服务端端口（虚拟网内端口不变）
        }
        var top = DialogService.MainWindow();
        if (top is null) return;
        var cb = Avalonia.Controls.TopLevel.GetTopLevel(top)?.Clipboard;
        if (cb is null) return;
        await cb.SetTextAsync(text);
        NotificationService.Success(ExternalAddressText.Trim().Length > 0
            ? "对外地址已复制" : "局域网地址已复制");
    }

    /// <summary>取内网 IPv4（优先私有段 192.168 / 10.x / 172.16-31）</summary>
    private static string? FindLanIp()
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                var ip = ua.Address.ToString();
                if (ip.StartsWith("192.168.") || ip.StartsWith("10.")) return ip;
                if (ip.StartsWith("172.") && int.TryParse(ip.Split('.')[1], out var b) && b is >= 16 and <= 31) return ip;
            }
        }
        return null;
    }

    /// <summary>验证 server.jar 下载完整（缺失或 <1MB 视为失败，抛异常终止后续启动）</summary>
    private static void VerifyServerJar(string? dir)
    {
        var jar = Path.Combine(dir ?? "", "server.jar");
        if (!File.Exists(jar) || new FileInfo(jar).Length < 1024 * 1024)
            throw new InvalidDataException("服务端文件下载不完整（server.jar 缺失或过小），请重试");
    }

    /// <summary>一键进服：启动客户端并自动连接本地服务端（复用主页完整启动链路：阶段指示/日志/退出处理）</summary>
    [RelayCommand]
    private async Task JoinGame()
    {
        var version = SelectedVersion;
        if (version is null || !IsRunning) return;
        var dir = ServerDir;
        if (dir is null) return;
        var props = ServerProperties.Load(Path.Combine(dir, "server.properties"));
        var port = props.GetInt("server-port", 25565);
        Status = "正在拉起客户端并连接服务器…";
        if (MainViewModel.Current is { } main)
            await main.Home.RequestLaunchWithServerAsync(version.Name, VersionGameDir(version), "127.0.0.1", port);
    }

    [RelayCommand]
    private async Task StopServer()
    {
        if (!IsRunning) return;
        Status = "正在停止…";
        AppendLog("§ 发送 stop 命令…");
        await Task.Run(() => _process.Stop());
    }

    /// <summary>8-22 全栈排查：启动器退出时停服务端（fire-and-forget，异常静默——退出清理不阻断）</summary>
    public void StopOnExit()
    {
        if (!IsRunning) return;
        try { Task.Run(() => _process.Stop()); } catch { /* 进程已退出 */ }
    }

    /// <summary>发送控制台命令（回车触发；输入框清空）</summary>
    [RelayCommand]
    private void SendCommand(string command)
    {
        var cmd = command?.Trim();
        if (string.IsNullOrEmpty(cmd)) return;
        AppendLog($"> {cmd}");
        _process.SendCommand(cmd);
    }

    // ---------- 服务器图形化管理 ----------

    /// <summary>刷新玩家列表（list 命令 → 日志解析回填）</summary>
    [RelayCommand]
    private void RefreshPlayers()
    {
        if (!IsRunning) return;
        _process.SendCommand("list");
    }

    /// <summary>踢出玩家</summary>
    [RelayCommand]
    private void KickPlayer(ServerPlayerVM player) => PlayerOp($"kick {player.Name}", $"已踢出 {player.Name}");

    /// <summary>封禁玩家（封禁后带重试刷新封禁列表——ban 后玩家不在线，解封入口必须在列表里）</summary>
    [RelayCommand]
    private void BanPlayer(ServerPlayerVM player)
    {
        PlayerOp($"ban {player.Name}", $"已封禁 {player.Name}");
        RefreshBannedWithRetry(); // 8-15：服务端异步写盘可能 >500ms，单次刷新读空=名单 0 且无解封入口
    }

    /// <summary>
    /// 8-15 封禁/解封后带重试刷新名单：服务端异步写盘可能 &gt;500ms（读早=0 条，且半截文件
    /// 解析失败也返回空）——最多 5 次 ×500ms 轮询，名单非空即止。此前单次刷新漏读后
    /// 列表一直 0，解封入口无处可点（用户自封自己解不掉的现场）。
    /// </summary>
    private void RefreshBannedWithRetry()
    {
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                await Task.Delay(500);
                var count = 0;
                Dispatcher.UIThread.Invoke(() => { RefreshBanned(); count = BannedList.Count; });
                if (count > 0) return;
            }
        });
    }

    /// <summary>授予 OP</summary>
    [RelayCommand]
    private void OpPlayer(ServerPlayerVM player) => PlayerOp($"op {player.Name}", $"已授予 {player.Name} OP");

    private void PlayerOp(string command, string doneText)
    {
        if (!IsRunning) return;
        _process.SendCommand(command);
        NotificationService.Success(doneText);
    }

    /// <summary>授予任意玩家 OP（AH1：不需要玩家在线——MC 的 op 命令写 ops.json，上线即生效）</summary>
    [RelayCommand]
    private void GrantOp()
    {
        var name = OpNameText.Trim();
        if (!IsRunning) { OpStatusText = "先启动服务端再授予 OP"; return; }
        if (name.Length == 0) { OpStatusText = "先输入玩家名"; return; }
        _process.SendCommand($"op {name}");
        OpStatusText = $"已授予 {name} OP——玩家上线即生效";
        _ = Task.Run(async () => { await Task.Delay(500); Dispatcher.UIThread.Post(RefreshOps); });
    }

    // ---------- OP 列表（AI：启动器图形化管理服务器权限——读 ops.json，服务器权限 > 游戏内命令） ----------

    /// <summary>OP 列表（ops.json 展示：名字 + 权限等级）</summary>
    public ObservableCollection<ServerOpEntry> OpsList { get; } = [];

    /// <summary>OP 列表标题（OP 列表（N））</summary>
    public string OpsCountText => $"OP 列表（{OpsList.Count}）";

    /// <summary>刷新 OP 列表（读 ops.json；服务端启动/停止后 + 手动刷新）</summary>
    [RelayCommand]
    private void RefreshOps()
    {
        var dir = ServerDir;
        OpsList.Clear();
        if (dir is not null)
            foreach (var op in ServerOpsFile.Load(dir))
                OpsList.Add(op);
        OnPropertyChanged(nameof(OpsCountText));
    }

    /// <summary>移除 OP（运行中发 deop；停止时直接改 ops.json 文件——重启生效，按钮不再"点不动"）</summary>
    [RelayCommand]
    private async Task RemoveOp(ServerOpEntry entry)
    {
        if (!IsRunning)
        {
            // 文件级移除（AL3）：服务端下次启动读 ops.json 生效
            if (ServerDir is { } stopped)
            {
                // 8-15：写失败如实提示（此前 catch{} 吞错但 UI 谎报成功）
                var ok = ServerOpsFile.Remove(stopped, entry.Name);
                OpStatusText = ok
                    ? $"已移除 {entry.Name} 的 OP（文件已更新，重启服务端生效）"
                    : $"移除失败：{entry.Name} 的 ops.json 写入失败（文件被占用？）";
                RefreshOps();
            }
            return;
        }
        _process.SendCommand($"deop {entry.Name}");
        OpStatusText = $"已发送 deop {entry.Name}";
        await Task.Delay(500);
        RefreshOps();
    }

    // ---------- 封禁列表（AL2：图形化解封——ban 后玩家不在线，必须有独立入口） ----------

    /// <summary>封禁列表（banned-players.json 展示：名字 + 封禁时间）</summary>
    public ObservableCollection<ServerBannedEntry> BannedList { get; } = [];

    /// <summary>封禁列表标题（封禁列表（N））</summary>
    public string BannedCountText => $"封禁列表（{BannedList.Count}）";

    /// <summary>刷新封禁列表（读 banned-players.json；服务端启动/停止后 + 手动刷新）</summary>
    [RelayCommand]
    private void RefreshBanned()
    {
        var dir = ServerDir;
        BannedList.Clear();
        if (dir is not null)
            foreach (var b in ServerBannedFile.Load(dir))
                BannedList.Add(b);
        OnPropertyChanged(nameof(BannedCountText));
    }

    /// <summary>解封（运行中发 pardon；停止时直接改 banned-players.json 文件——重启生效，按钮不再"点不动"）</summary>
    [RelayCommand]
    private void Unban(ServerBannedEntry entry)
    {
        if (!IsRunning)
        {
            // 文件级解封（AL3）：服务端下次启动读 banned-players.json 生效
            if (ServerDir is { } stopped)
            {
                // 8-15：写失败如实提示（此前 catch{} 吞错但 UI 谎报成功）
                var ok = ServerBannedFile.Unban(stopped, entry.Name);
                OpStatusText = ok
                    ? $"已解封 {entry.Name}（文件已更新，重启服务端生效）"
                    : $"解封失败：{entry.Name} 的封禁文件写入失败（文件被占用？）";
                RefreshBanned();
            }
            return;
        }
        _process.SendCommand($"pardon {entry.Name}");
        OpStatusText = $"已发送 pardon {entry.Name}——该玩家可重新进服";
        RefreshBannedWithRetry(); // 8-15：带重试（服务端写盘慢时单次刷新读空）
    }

    // ---------- 预生成世界（AI：启动服务端 → 日志 Done → 自动 stop，空世界落盘） ----------

    private static readonly Regex ServerReady = new(@"Done \(\d+(\.\d+)?s\)!?", RegexOptions.Compiled);

    /// <summary>当前世界名（server.properties 的 level-name，默认 world）</summary>
    private string CurrentLevelName()
    {
        try
        {
            var dir = ServerDir;
            if (dir is not null
                && ServerProperties.Load(Path.Combine(dir, "server.properties")).Get("level-name") is { } n
                && n.Length > 0)
                return n;
        }
        catch { }
        return "world";
    }

    /// <summary>预生成空世界：启动服务端 → 就绪（Done）自动停止 → 玩家直接进服</summary>
    [RelayCommand]
    private async Task GenerateWorld()
    {
        var dir = ServerDir;
        if (dir is null) { await WarnNoVersion(); return; }
        if (IsRunning || IsInstalling) { Status = "服务端运行中，停止后再生成世界"; return; }
        if (!File.Exists(Path.Combine(dir, "server.jar"))) { Status = "先下载服务端再生成世界"; return; }
        var levelName = CurrentLevelName();
        if (Directory.Exists(Path.Combine(dir, levelName)))
        {
            Status = $"世界「{levelName}」已存在，无需生成";
            return;
        }
        if (DialogService.MainWindow() is not { } owner
            || !await DialogService.Confirm(owner,
                $"启动服务端生成新世界「{levelName}」（首次约 1~2 分钟），生成完自动停。继续？",
                "生成世界", "生成世界", "取消"))
        {
            return;
        }
        _autoStopOnReady = true;
        _worldGenDone = false;
        Status = $"正在生成世界「{levelName}」（生成完自动停）…";
        await StartServer();
    }

    /// <summary>行级检测：服务端就绪（Done）→ 预生成世界自动 stop / 一键开服自动授权进服</summary>
    private void ParseServerReady(string line)
    {
        if (!ServerReady.IsMatch(line)) return;
        if (_autoStopOnReady)
        {
            _autoStopOnReady = false;
            _worldGenDone = true;
            AppendLog("§ 世界生成完成，自动停止…");
            _process.SendCommand("stop");
            return;
        }
        if (_autoJoinOnReady)
        {
            _autoJoinOnReady = false;
            AppendLog("§ 服务端就绪——自动授予 OP 并拉起客户端…");
            _ = FinishOneClick(); // AppendLog 经 Dispatcher.Post 在 UI 线程执行，这里同样在 UI 线程
        }
    }

    /// <summary>一键开服（AJ：下载→生成世界→启动→就绪后自动授予 OP→拉起客户端进服；任一步失败中止，已完成部分保留）</summary>
    [RelayCommand]
    private async Task OneClickStart()
    {
        var version = SelectedVersion;
        var dir = ServerDir;
        if (version is null || dir is null) { await WarnNoVersion(); return; }
        if (IsRunning || IsInstalling || _oneClickActive) { Status = "服务端运行中或流程进行中，先停止再一键开服"; return; }
        _oneClickActive = true;

        // ① 缺服务端 → 自动下载
        if (!File.Exists(Path.Combine(dir, "server.jar")))
        {
            Status = "① 下载服务端…";
            await DownloadServerCoreAsync();
            if (!File.Exists(Path.Combine(dir, "server.jar")))
            {
                SetStatus("服务端下载失败，一键开服中止", error: true);
                _oneClickActive = false;
                // 8-22 全栈排查：一键开服/生成世界失败中止时 _autoStopOnReady/_autoJoinOnReady 未复位——
                // 状态泄漏到下一次手动开服（就绪被自动停/自动拉起客户端）。统一在此复位
                _autoStopOnReady = false;
                _autoJoinOnReady = false;
                return;
            }
        }

        // ② 无世界 → 生成（启动→Done→自动停→等退出）
        if (!Directory.Exists(Path.Combine(dir, CurrentLevelName())))
        {
            Status = "② 生成世界（首次约 1~2 分钟，完成自动停）…";
            _autoStopOnReady = true;
            _worldGenDone = false;
            await StartServer();
            while (IsRunning) await Task.Delay(400);
            if (!Directory.Exists(Path.Combine(dir, CurrentLevelName())))
            {
                SetStatus("世界生成失败，一键开服中止（可查看控制台日志）", error: true);
                _oneClickActive = false;
                // 8-22 全栈排查：一键开服/生成世界失败中止时 _autoStopOnReady/_autoJoinOnReady 未复位——
                // 状态泄漏到下一次手动开服（就绪被自动停/自动拉起客户端）。统一在此复位
                _autoStopOnReady = false;
                _autoJoinOnReady = false;
                return;
            }
        }

        // ③④⑤ 启动 → 就绪后自动授予 OP + 拉起客户端进服
        _autoJoinOnReady = true;
        Status = "③ 启动服务端…（就绪后自动授权并进服）";
        await StartServer();
    }

    /// <summary>一键开服收尾（服务端就绪后）：授予 OP（登录账号名）→ 拉起客户端自动连接</summary>
    private async Task FinishOneClick()
    {
        var name = OpNameText.Trim();
        if (name.Length > 0)
        {
            _process.SendCommand($"op {name}");
            Status = $"④ 已授予 {name} OP——拉起客户端…";
        }
        else Status = "④ 未登录账号，跳过自动 OP——拉起客户端…";
        await Task.Delay(800); // 等服务端写入 ops.json
        RefreshOps();
        Status = "⑤ 拉起客户端并连接服务器…";
        try
        {
            await JoinGame();
            Status = "一键开服完成——服务端运行中，客户端已连接";
            NotificationService.Success("一键开服完成");
        }
        catch (Exception ex)
        {
            SetStatus($"连接失败：{ex.Message}。服务端仍在运行，可手动使用「进入服务器」连接。", error: true);
        }
        finally
        {
            _oneClickActive = false;
                // 8-22 全栈排查：一键开服/生成世界失败中止时 _autoStopOnReady/_autoJoinOnReady 未复位——
                // 状态泄漏到下一次手动开服（就绪被自动停/自动拉起客户端）。统一在此复位
                _autoStopOnReady = false;
                _autoJoinOnReady = false;
        }
    }

    /// <summary>日志行玩家解析（joined/left 实时增删；list 输出整体重置）</summary>
    private void ParsePlayerLine(string line)
    {
        if (JoinedGame.Match(line) is { Success: true } j && j.Groups[1].Value is var jn
            && OnlinePlayers.All(p => p.Name != jn))
        {
            OnlinePlayers.Add(new ServerPlayerVM(jn));
            OnPropertyChanged(nameof(PlayersCountText));
        }
        else if (LeftGame.Match(line) is { Success: true } l)
        {
            var ln = l.Groups[1].Value;
            var hit = OnlinePlayers.FirstOrDefault(p => p.Name == ln);
            if (hit is not null)
            {
                OnlinePlayers.Remove(hit);
                OnPropertyChanged(nameof(PlayersCountText));
            }
        }
        else if (PlayerList.Match(line) is { Success: true } pl)
        {
            OnlinePlayers.Clear();
            foreach (var name in pl.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                OnlinePlayers.Add(new ServerPlayerVM(name));
            OnPropertyChanged(nameof(PlayersCountText));
        }
    }

    /// <summary>加载 server.properties 到编辑表单（默认值兜底）</summary>
    private void LoadProperties()
    {
        var dir = ServerDir;
        if (dir is null) return;
        var props = ServerProperties.Load(Path.Combine(dir, "server.properties"));
        PropRows.Clear();
        foreach (var (key, label, kind, options) in PropDefs)
        {
            var fallback = key switch
            {
                "server-port" => "25565",
                "max-players" => "20",
                "difficulty" => "normal",
                "gamemode" => "survival",
                "online-mode" => "true",
                "pvp" => "true",
                "white-list" => "false",
                "view-distance" => "10",
                _ => "",
            };
            var (min, max) = NumberRanges.TryGetValue(key, out var r) ? r : (0, 0);
            PropRows.Add(new PropRowVM(key, label, props.Get(key, fallback), kind, options, min, max));
        }
    }

    /// <summary>保存 server.properties（写回服务端目录）</summary>
    [RelayCommand]
    private void SaveProperties()
    {
        var dir = ServerDir;
        if (dir is null) return;
        var props = ServerProperties.Load(Path.Combine(dir, "server.properties"));
        foreach (var row in PropRows) props.Set(row.Key, row.Value);
        props.Save(Path.Combine(dir, "server.properties"));
        Status = "server.properties 已保存";
        NotificationService.Success("server.properties 已保存");
        RefreshSuggestionDiff(); // 手动改参数后建议区联动（与建议不一致时提示差异）
    }

    private void AppendLog(string line)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AppendLog(line));
            return;
        }
        if (Logs.Count >= MaxLogLines) Logs.RemoveAt(0);
        Logs.Add(line);
        ParsePlayerLine(line); // 玩家在线跟踪（joined/left/list）
        ParseServerReady(line); // 预生成世界：Done → 自动 stop
    }
}
