using System.Collections.ObjectModel;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Services;
using Launcher.App.Views;
using Launcher.Core.Account;
using Launcher.Core.Diagnostics;
using Launcher.Core.Multiplayer;

namespace Launcher.App.ViewModels;

/// <summary>联机页区块（互斥显示）</summary>
public enum MultiplayerPageStep
{
    Welcome,  // 主界面：创建 / 加入卡片
    Busy,     // 创建中 / 加入中（可取消）
    Active,   // 房间已就绪
    Declined, // 未同意协议，功能不可用
}

/// <summary>房间内玩家行（展示便捷属性）</summary>
public sealed record MultiplayerPlayerVM(MultiplayerPlayer Player)
{
    public string Name => Player.Name;
    public string LatencyText => Player.LatencyMs is { } ms ? $"{ms} ms" : "—";
    public bool IsHost => Player.IsHost;
    public bool IsLocal => Player.IsLocal;
}

/// <summary>
/// 联机页：陶瓦（Terracotta）或 EasyTier 两种方案。
/// 陶瓦：房主在游戏里开「局域网世界」→ 本页「创建房间」→ 出房间码；客机：输码加入（自动代理局域网世界）。
/// EasyTier：虚拟组网——房间码带房主地址，双方互通后游戏内开局域网世界，直接连接输 虚拟IP:端口。
/// 未装模块 → 弹协议窗/直接下载；失败/停止 → 复位 + 人话文案。
/// </summary>
public partial class MultiplayerViewModel : ViewModelBase
{
    private readonly TerracottaProvisioningService _terracottaProvisioning = new();
    private readonly EasyTierProvisioningService _easytierProvisioning = new();
    private IMultiplayerLobbyService? _lobby;
    private CancellationTokenSource? _sessionCts;
    private bool _initialized;
    private bool _resetting;

    /// <summary>联机方案选择（8-14：陶瓦出问题时的第二方案）</summary>
    public IReadOnlyList<MultiplayerBackendOption> BackendOptions { get; } =
    [
        new MultiplayerBackendOption(MultiplayerBackend.Terracotta, "陶瓦联机（局域网世界自动代理）"),
        new MultiplayerBackendOption(MultiplayerBackend.EasyTier, "EasyTier 组网（直接连接虚拟 IP）"),
    ];

    public sealed record MultiplayerBackendOption(MultiplayerBackend Backend, string Display);

    [ObservableProperty]
    public partial MultiplayerBackendOption SelectedBackend { get; set; }

    /// <summary>EasyTier 主机：游戏端口（加入者直接连接用；默认 25565）</summary>
    [ObservableProperty]
    public partial string GamePortText { get; set; } = "25565";

    /// <summary>EasyTier 房间：服务器地址（主机虚拟 IP:端口——游戏内「直接连接」用）</summary>
    [ObservableProperty]
    public partial string? ServerAddress { get; set; }

    public bool IsEasyTier => SelectedBackend.Backend == MultiplayerBackend.EasyTier;

    partial void OnSelectedBackendChanged(MultiplayerBackendOption value)
    {
        OnPropertyChanged(nameof(IsEasyTier));
        OnPropertyChanged(nameof(ModuleVersionText));
    }

    public MultiplayerViewModel()
    {
        SelectedBackend = BackendOptions[0];
    }

    /// <summary>当前区块</summary>
    [ObservableProperty]
    public partial MultiplayerPageStep Step { get; set; } = MultiplayerPageStep.Welcome;

    [ObservableProperty]
    public partial bool IsWelcome { get; set; } = true;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial bool IsDeclined { get; set; }

    partial void OnStepChanged(MultiplayerPageStep value)
    {
        IsWelcome = value == MultiplayerPageStep.Welcome;
        IsBusy = value == MultiplayerPageStep.Busy;
        IsActive = value == MultiplayerPageStep.Active;
        IsDeclined = value == MultiplayerPageStep.Declined;
    }

    /// <summary>欢迎态 tab：默认创建房间</summary>
    [ObservableProperty]
    public partial bool IsCreateTab { get; set; } = true;

    [ObservableProperty]
    public partial bool IsJoinTab { get; set; }

    partial void OnIsCreateTabChanged(bool value) => IsJoinTab = !value;

    /// <summary>欢迎态 tab 切换（create / join）</summary>
    [RelayCommand]
    private void SwitchTab(string which) => IsCreateTab = which == "create";

    /// <summary>创建中 / 加入中的说明文字</summary>
    [ObservableProperty]
    public partial string BusyText { get; set; } = "";

    /// <summary>错误/停止原因文案（人话）</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }


    /// <summary>房主 / 客机</summary>
    [ObservableProperty]
    public partial bool IsHost { get; set; }

    /// <summary>房间码（XXXX-XXXX）</summary>
    [ObservableProperty]
    public partial string? RoomCode { get; set; }

    /// <summary>房主名（房间标题用）</summary>
    [ObservableProperty]
    public partial string? HostName { get; set; }

    /// <summary>客机：房间码输入</summary>
    [ObservableProperty]
    public partial string JoinCode { get; set; } = "";

    /// <summary>房间内玩家</summary>
    public ObservableCollection<MultiplayerPlayerVM> Players { get; } = [];

    private static string PlayerName => AccountService.Shared.Current?.Name ?? "Player";

    /// <summary>进入联机页（View Loaded）：首次检查模块，未装弹协议窗（陶瓦）/直接下载（EasyTier）</summary>
    public async Task OnPageLoadedAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await EnsureBackendReadyAsync();
    }

    // ---------- 协议 ----------

    /// <summary>
    /// 模块就绪检查：陶瓦未装弹协议窗（不同意 → Declined）；EasyTier 无协议（LGPL 开源）——
    /// 首次使用直接下载安装（下载失败走诊断）。
    /// </summary>
    private async Task<bool> EnsureBackendReadyAsync()
    {
        if (IsEasyTier)
        {
            try
            {
                await _easytierProvisioning.EnsureAvailableAsync();
                return true;
            }
            catch (MultiplayerLobbyException ex)
            {
                ShowFailure(ex);
                return false;
            }
        }
        var installed = _terracottaProvisioning.TryGetAvailable();
        MultiplayerLog.Log($"协议检查: 已装模块={(installed is null ? "无" : $"v{installed.Version}")}");
        if (installed is not null) return true;
        MultiplayerLog.Log("协议检查: 弹协议窗");
        if (DialogService.MainWindow() is not { } owner) return false;
        var ok = await new TerracottaAgreementDialog(_terracottaProvisioning).ShowDialog<bool>(owner);
        if (!ok)
        {
            Step = MultiplayerPageStep.Declined;
            return false;
        }
        return true;
    }

    /// <summary>Declined 区块：重新阅读并同意协议</summary>
    [RelayCommand]
    private async Task ReopenAgreement()
    {
        if (await EnsureBackendReadyAsync()) Step = MultiplayerPageStep.Welcome;
    }

    // ---------- 房主：创建房间 ----------

    [RelayCommand]
    private async Task CreateRoom()
    {
        if (!await EnsureBackendReadyAsync()) return;
        if (IsEasyTier)
        {
            try { await _easytierProvisioning.EnsureAvailableAsync(); }
            catch (MultiplayerLobbyException ex) { ShowFailure(ex); return; }
        }

        _lastAction = "create";
        if (!StartSession(isHost: true)) return;
        BusyText = IsEasyTier ? "正在启动组网节点…" : "正在查找局域网世界…";
        try
        {
            await _lobby!.CreateHostAsync(PlayerName, _sessionCts!.Token);
        }
        catch (OperationCanceledException)
        {
            ResetAfterFailure();
        }
        catch (MultiplayerLobbyException ex)
        {
            ShowFailure(ex);
        }
    }

    // ---------- 客机：加入房间 ----------

    [RelayCommand]
    private async Task JoinRoom()
    {
        var code = JoinCode.Trim();
        if (code.Length == 0)
        {
            ErrorText = "把房主给的房间代码填进来。";
            return;
        }
        if (!await EnsureBackendReadyAsync()) return;
        if (IsEasyTier)
        {
            try { await _easytierProvisioning.EnsureAvailableAsync(); }
            catch (MultiplayerLobbyException ex) { ShowFailure(ex); return; }
        }

        _lastAction = "join";
        if (!StartSession(isHost: false)) return;
        BusyText = IsEasyTier ? "正在连接房主节点…" : "正在加入房间…";
        try
        {
            await _lobby!.JoinAsync(code, PlayerName, _sessionCts!.Token);
        }
        catch (OperationCanceledException)
        {
            ResetAfterFailure();
        }
        catch (MultiplayerLobbyException ex)
        {
            ShowFailure(ex);
        }
    }

    /// <summary>从剪贴板粘贴房间码</summary>
    [RelayCommand]
    private async Task PasteCode()
    {
        if (DialogService.MainWindow() is not { } top) return;
        var cb = Avalonia.Controls.TopLevel.GetTopLevel(top)?.Clipboard;
        if (cb is null) return;
        var text = await cb.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            ErrorText = "你的剪贴板里没有房间代码，先复制一个。";
            return;
        }
        JoinCode = text.Trim();
    }

    // ---------- 房间内 ----------

    /// <summary>复制房间码（发给朋友用）</summary>
    [RelayCommand]
    private async Task CopyCode()
    {
        if (RoomCode is null || DialogService.MainWindow() is not { } top) return;
        var cb = Avalonia.Controls.TopLevel.GetTopLevel(top)?.Clipboard;
        if (cb is null) return;
        await cb.SetTextAsync(RoomCode);
        NotificationService.Success("已复制房间代码");
    }

    /// <summary>离开房间：确认 → 陶瓦收尾（/state/ide → /panic）→ 复位</summary>
    [RelayCommand]
    private async Task LeaveRoom()
    {
        if (_lobby is null) return;
        var (title, message, confirm) = IsHost
            ? ("退出并解散房间？", "解散后所有玩家都会断开连接。你确定退出吗？", "退出")
            : ("离开房间？", "离开后将断开连接。", "离开房间");
        if (!await DialogService.Confirm(DialogService.MainWindow(), message, title, confirm, "取消")) return;

        _resetting = true; // 主动路径不发 Stopped，事件也忽略
        try
        {
            await _lobby.StopAsync(CancellationToken.None);
        }
        catch { /* 收尾失败也复位 */ }
        finally
        {
            _resetting = false;
            Reset();
        }
    }

    /// <summary>创建中 / 加入中：取消</summary>
    [RelayCommand]
    private void CancelBusy() => _sessionCts?.Cancel();

    // ---------- 会话管理 ----------

    /// <summary>按方案实例化联机服务；false = 方案所需模块不可用</summary>
    private bool StartSession(bool isHost)
    {
        if (IsEasyTier)
        {
            if (!_easytierProvisioning.TryGetAvailable(out var moduleDir)) return false;
            _lobby = new EasyTierLobbyService(moduleDir, PlayerName);
            if (_lobby is EasyTierLobbyService et && int.TryParse(GamePortText, out var port))
                et.HostGamePort = port;
        }
        else
        {
            var module = _terracottaProvisioning.TryGetAvailable();
            if (module is null) return false;
            _lobby = new TerracottaLobbyService(module);
        }
        _lobby.SnapshotChanged += OnSnapshotChanged;
        _lobby.Stopped += OnStopped;
        _sessionCts = new CancellationTokenSource();
        IsHost = isHost;
        ErrorText = null;
        RoomCode = null;
        ServerAddress = null;
        Players.Clear();
        Step = MultiplayerPageStep.Busy;
        return true;
    }

    /// <summary>Core 轮询线程回调 → 切回 UI 线程应用快照（Core 已做签名去重，只在变化时发）</summary>
    private void OnSnapshotChanged(MultiplayerSnapshot snap)
    {
        if (_resetting) return;
        if (Dispatcher.UIThread.CheckAccess()) ApplySnapshot(snap);
        else Dispatcher.UIThread.Post(() => ApplySnapshot(snap));
    }

    private void ApplySnapshot(MultiplayerSnapshot snap)
    {
        if (_resetting || snap.State != MultiplayerSessionState.Active) return;
        Step = MultiplayerPageStep.Active;
        RoomCode = snap.RoomCode;
        HostName = snap.Players.FirstOrDefault(p => p.IsHost)?.Name ?? PlayerName;
        Players.Clear();
        foreach (var p in snap.Players) Players.Add(new MultiplayerPlayerVM(p));
        // EasyTier：房间就绪后亮出服务器地址（游戏内「直接连接」用）
        if (_lobby is EasyTierLobbyService et)
        {
            var ip = et.FindHostVirtualIp();
            ServerAddress = ip is null ? null : $"{ip}:{et.HostGamePort}";
        }
        else
        {
            ServerAddress = null;
        }
    }

    /// <summary>异常终止（陶瓦退出 / 世界关闭 / 服务异常）→ 复位 + 文案</summary>
    private void OnStopped(MultiplayerStopReason reason)
    {
        if (_resetting) return;
        if (Dispatcher.UIThread.CheckAccess()) HandleStopped(reason);
        else Dispatcher.UIThread.Post(() => HandleStopped(reason));
    }

    private void HandleStopped(MultiplayerStopReason reason)
    {
        if (_resetting) return;
        Reset();
        ErrorText = reason switch
        {
            MultiplayerStopReason.BackendExited => "联机模块已停止，房间已解散。",
            MultiplayerStopReason.WorldClosed => "局域网世界已关闭，房间已解散。",
            MultiplayerStopReason.ServiceFailed => "联机服务异常，房间已解散。",
            _ => null,
        };
    }

    /// <summary>失败复位（无文案——取消场景），随后带文案时再 set</summary>
    private void ResetAfterFailure() => Reset();

    private void ShowFailure(MultiplayerLobbyException ex)
    {
        Reset();
        // AL44：统一诊断——枚举 → 人话原因+建议+修复动作（替代私有 switch，覆盖真实失败子类型）
        _lastFailure = FailureDiagnostics.ForMultiplayer(ex.Failure, ex.Message);
        ErrorText = _lastFailure.Explanation;
    }

    /// <summary>最近一次失败诊断（「一键修复」依据）</summary>
    private DiagnosticHit? _lastFailure;

    /// <summary>模块版本（欢迎页展示，帮朋友双方对齐版本）</summary>
    public string ModuleVersionText
        => IsEasyTier
            ? (_easytierProvisioning.TryGetAvailable(out _) ? $"EasyTier v{EasyTierProvisioningService.LockedVersion}" : "EasyTier 未安装")
            : _terracottaProvisioning.TryGetAvailable() is { } m ? $"联机模块 v{m.Version}" : "联机模块未安装";

    /// <summary>失败可一键修复（RestartService/ReinstallModule）</summary>
    public bool HasFixableError => _lastFailure?.IsAutoFixable == true && _lastFailure.Fix is FixKind.RestartService or FixKind.ReinstallModule;

    /// <summary>一键修复执行中（按钮禁用）</summary>
    [ObservableProperty]
    public partial bool IsRepairing { get; set; }

    partial void OnErrorTextChanged(string? value)
    {
        HasError = value is not null;
        OnPropertyChanged(nameof(HasFixableError));
    }

    /// <summary>
    /// AL44 一键修复：RestartService → 杀残留陶瓦进程/删锁文件；ReinstallModule → 重装模块。
    /// 完成后自动重试原动作一次（镜像启动模块「修复后自动重启一次」）；二次失败显示新诊断。
    /// </summary>
    [RelayCommand]
    private async Task RepairNow()
    {
        if (_lastFailure is not { IsAutoFixable: true }) return;
        var fix = _lastFailure.Fix;
        IsRepairing = true;
        try
        {
            if (fix == FixKind.RestartService)
            {
                TerracottaRepairService.KillStaleInstances();
            }
            else if (fix == FixKind.ReinstallModule)
            {
                if (IsEasyTier) await _easytierProvisioning.ReinstallAsync();
                else await _terracottaProvisioning.ReinstallAsync();
            }
            // 清错误 → 自动重试原动作一次（Snippet 记录失败来源）
            ErrorText = null;
            _lastFailure = null;
            var action = _lastAction;
            if (action == null) return;
            if (action == "join")
            {
                if (JoinCode.Length == 0) { ErrorText = "先把房主给的房间代码填进去。"; return; }
                await JoinRoom();
            }
            else
            {
                await CreateRoom();
            }
        }
        catch (Exception ex)
        {
            ErrorText = $"修复失败：{ex.Message}";
        }
        finally
        {
            IsRepairing = false;
        }
    }

    /// <summary>最近一次失败的动作来源（create/join），供一键修复后自动重试</summary>
    private string? _lastAction;

    /// <summary>复制服务器地址（EasyTier 直接连接用）</summary>
    [RelayCommand]
    private async Task CopyServerAddress()
    {
        if (ServerAddress is null || DialogService.MainWindow() is not { } top) return;
        var cb = Avalonia.Controls.TopLevel.GetTopLevel(top)?.Clipboard;
        if (cb is null) return;
        await cb.SetTextAsync(ServerAddress);
        NotificationService.Success("已复制服务器地址");
    }

    private void Reset()
    {
        if (_lobby is not null)
        {
            _lobby.SnapshotChanged -= OnSnapshotChanged;
            _lobby.Stopped -= OnStopped;
            _lobby.Dispose();
            _lobby = null;
        }
        _sessionCts?.Dispose();
        _sessionCts = null;
        RoomCode = null;
        HostName = null;
        ServerAddress = null;
        IsHost = false;
        Players.Clear();
        Step = MultiplayerPageStep.Welcome;
    }
}
