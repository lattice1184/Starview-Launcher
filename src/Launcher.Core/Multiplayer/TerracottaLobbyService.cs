using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Launcher.Core.Multiplayer;

/// <summary>
/// 陶瓦联机会话：拉起 terracotta.exe（--hmcl2 握手）→ HTTP API 控制状态机（房主扫描 / 客机加入 / 监控 / 收尾）。
/// 移植自 BlockHelm-Launcher（GPL-3.0）的 MultiplayerLobbyService，进程 seam 可注入以便单测。
/// </summary>
public sealed class TerracottaLobbyService : IMultiplayerLobbyService
{
    private const int RequestTimeoutMs = 3000;
    private const int HandoffTimeoutMs = 12_000;
    private const int StartupTimeoutMs = 20_000;
    private const int PollIntervalMs = 500;
    private const int WaitForWaitingTimeoutMs = 5000;
    private const int OwnershipGraceMs = 750;
    private const int MaxResponseBytes = 1024 * 1024;

    private readonly TerracottaModule _module;
    private readonly HttpClient _http;
    private readonly Func<ProcessStartInfo, IProcessHandle> _processFactory;

    private readonly object _gate = new();
    private IProcessHandle? _ownedProcess;   // 本会话拉起的进程（复用现役实例时为 null）
    private int _controllerPort;             // 陶瓦 HTTP API 端口
    private SessionMode _mode = SessionMode.None;
    private CancellationTokenSource? _monitorCts;
    private ControllerState _lastState = new();
    private bool _disposed;

    public event Action<MultiplayerSnapshot>? SnapshotChanged;
    public event Action<MultiplayerStopReason>? Stopped;

    public MultiplayerSnapshot? Current { get; private set; }

    public TerracottaLobbyService(
        TerracottaModule module,
        Func<ProcessStartInfo, IProcessHandle>? processFactory = null,
        HttpMessageHandler? handler = null)
    {
        _module = module;
        _processFactory = processFactory ?? (psi => new ProcessHandle(Process.Start(psi)));
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = TimeSpan.FromMilliseconds(RequestTimeoutMs), // REVIEW-D 高3：旧代码 FromSeconds(3000)=50 分钟——离房请求可挂 50 分钟
        };
    }

    // ---------- 房主 / 客机入口 ----------

    /// <summary>房主：扫描本机局域网世界并建立房间。返回房间快照（含房间码）。</summary>
    public async Task<MultiplayerSnapshot> CreateHostAsync(string playerName, CancellationToken ct)
    {
        MultiplayerLog.Log($"CreateHostAsync 开始 player={playerName}");
        try
        {
            await GetOrStartEndpointAsync(ct);
            await FireActionAsync($"/state/scanning?player={Uri.EscapeDataString(playerName)}", ct);
            _mode = SessionMode.Host;
            return await PollUntilReadyAsync(isHost: true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MultiplayerLog.Log($"CreateHostAsync 失败: {ex}");
            await CleanupFailedCreationAsync();
            if (ex is MultiplayerLobbyException) throw;
            throw new MultiplayerLobbyException(MultiplayerLobbyFailure.StartupFailed, $"创建房间失败：{ex.Message}", ex);
        }
    }

    /// <summary>客机：按房间码加入。返回房间快照（room 以服务端回填为准）。</summary>
    public async Task<MultiplayerSnapshot> JoinAsync(string roomCode, string playerName, CancellationToken ct)
    {
        try
        {
            await GetOrStartEndpointAsync(ct);
            await FireActionAsync(
                $"/state/guesting?room={Uri.EscapeDataString(roomCode)}&player={Uri.EscapeDataString(playerName)}", ct);
            _mode = SessionMode.Guest;
            return await PollUntilReadyAsync(isHost: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await CleanupFailedCreationAsync();
            if (ex is MultiplayerLobbyException) throw;
            throw new MultiplayerLobbyException(MultiplayerLobbyFailure.RoomConnectionFailed, $"加入房间失败：{ex.Message}", ex);
        }
    }

    // ---------- 主动离开 ----------

    /// <summary>离开房间并收尾陶瓦进程（主动路径不发 Stopped 事件，UI await 完成后自己复位）。</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        MultiplayerLog.Log("StopAsync: 用户离开");
        lock (_gate) _mode = SessionMode.Stopping;
        Publish(MakeSnapshot(MultiplayerSessionState.Stopping));
        await StopRuntimeAsync(ct);
        lock (_gate) { _mode = SessionMode.None; Current = null; }
    }

    // ---------- 端点准备：复用现役实例或拉起新进程 ----------

    private async Task GetOrStartEndpointAsync(CancellationToken ct)
    {
        if (_controllerPort > 0) return;

        // 1. 已有实例（%TEMP%\terracotta\terracotta.lock 2 字节大端端口）→ /meta 校验通过即复用
        var lockPath = Path.Combine(Path.GetTempPath(), "terracotta", "terracotta.lock");
        var lockPort = ReadLockPort(lockPath);
        var metaOk = lockPort is not null && await MetaIsValidAsync(lockPort.Value, requireExactVersion: false, ct);
        MultiplayerLog.Log($"端点准备: lock端口={lockPort}, meta校验={(metaOk ? "通过" : "失败")}");
        if (lockPort is { } existingPort && metaOk)
        {
            _controllerPort = existingPort;
            _ownedProcess = null;
            return;
        }

        // 2. 拉起新进程：--hmcl2 {handoff}，50ms 轮询 handoff JSON（含 port），12s 超时
        var handoffPath = Path.Combine(Path.GetTempPath(), $"terracotta-handoff-{Guid.NewGuid():N}.json");
        var psi = new ProcessStartInfo(_module.ExePath)
        {
            WorkingDirectory = _module.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--hmcl2");
        psi.ArgumentList.Add(handoffPath);
        IProcessHandle process;
        try
        {
            process = _processFactory(psi);
        }
        catch (Exception ex)
        {
            MultiplayerLog.Log($"联机模块启动失败: {ex.Message}");
            throw new MultiplayerLobbyException(MultiplayerLobbyFailure.BackendUnavailable, $"联机模块启动失败：{ex.Message}", ex);
        }
        MultiplayerLog.Log($"陶瓦进程已启动: handoff={Path.GetFileName(handoffPath)}");

        // 排空 stdout/stderr，防管道写满阻塞进程
        _ = Task.Run(async () => { try { while (await process.ReadLineAsync() is not null) { } } catch { } });

        var owns = false;
        var failed = false;
        var port = 0;
        try
        {
            port = await WaitForHandoffAsync(process, handoffPath, ct);

            // 所有权判定：750ms 内进程退出 = 已有实例（我们只是传递启动）；仍存活 = 本会话拥有
            owns = !process.HasExited && await AwaitExitWithinAsync(process, OwnershipGraceMs, ct) == false;
            if (!owns) process.Dispose(); // 非拥有者：dispose 句柄，继续用现役实例
            MultiplayerLog.Log($"handoff 端口={port}, 所有权={owns}");

            // 拥有进程才要求版本精确一致；复用实例只校验平台字段
            if (!await MetaIsValidAsync(port, requireExactVersion: owns, ct))
            {
                throw new MultiplayerLobbyException(
                    MultiplayerLobbyFailure.BackendBusy,
                    "联机模块版本不匹配，或正被其他启动器使用");
            }
            _controllerPort = port;
            _ownedProcess = owns ? process : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            failed = true;
            throw;
        }
        finally
        {
            // 清理 handoff（含 .tmp 变体）
            TryDelete(handoffPath);
            TryDelete(handoffPath + ".tmp");
            if (failed)
            {
                // 握手失败但进程仍存活 → 收尾防僵尸（端口未知时直接 Kill）
                if (!process.HasExited)
                {
                    if (port > 0)
                    {
                        try
                        {
                            _http.GetAsync($"http://127.0.0.1:{port}/panic?peaceful=true", CancellationToken.None)
                                .Wait();
                        }
                        catch { }
                    }
                    try { process.Kill(entireProcessTree: true); } catch { }
                }
                process.Dispose();
            }
        }
    }

    private async Task<int> WaitForHandoffAsync(IProcessHandle process, string handoffPath, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(HandoffTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new MultiplayerLobbyException(
                    MultiplayerLobbyFailure.BackendBusy,
                    "联机模块启动后立即退出（可能正被其他启动器使用）");
            try
            {
                if (File.Exists(handoffPath))
                {
                    var json = File.ReadAllText(handoffPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("port", out var portEl)
                        && portEl.TryGetInt32(out var port) && port is > 0 and <= 65535)
                        return port;
                    throw new MultiplayerLobbyException(
                        MultiplayerLobbyFailure.ProtocolFailed, "联机模块握手数据异常");
                }
            }
            catch (IOException) { /* 进程写入中，稍后重读 */ }
            await Task.Delay(50, ct);
        }
        throw new MultiplayerLobbyException(MultiplayerLobbyFailure.StartupFailed, "联机模块握手超时（12 秒）");
    }

    /// <summary>进程是否在宽限期内退出：退出 → true；存活 → false</summary>
    private static async Task<bool> AwaitExitWithinAsync(IProcessHandle process, int ms, CancellationToken ct)
    {
        try { return await process.WaitForExitAsync(ms, ct); }
        catch (TimeoutException) { return false; }
    }

    // ---------- HTTP / 状态机 ----------

    /// <summary>GET 状态端点（400 → InvalidRoomCode；非 200 → ProtocolFailed；网络异常上抛）</summary>
    private async Task<ControllerState> CallStateAsync(string path, CancellationToken ct)
    {
        using var resp = await GetAsync(path, ct);
        if (resp.StatusCode == HttpStatusCode.BadRequest)
            throw new MultiplayerLobbyException(MultiplayerLobbyFailure.InvalidRoomCode, "房间码无效（400）");
        if (!resp.IsSuccessStatusCode)
            throw new MultiplayerLobbyException(
                MultiplayerLobbyFailure.ProtocolFailed, $"联机模块接口返回 {(int)resp.StatusCode}");
        return await ParseStateAsync(resp, ct);
    }

    /// <summary>动作端点（/state/scanning、/state/guesting）：真实 terracotta 返回 200 + 空 body，
    /// 状态只能靠 /state 轮询——只检查状态码，不解析响应体。</summary>
    private async Task FireActionAsync(string path, CancellationToken ct)
    {
        using var resp = await GetAsync(path, ct);
        if (resp.StatusCode == HttpStatusCode.BadRequest)
            throw new MultiplayerLobbyException(MultiplayerLobbyFailure.InvalidRoomCode, "房间码无效（400）");
        if (!resp.IsSuccessStatusCode)
            throw new MultiplayerLobbyException(
                MultiplayerLobbyFailure.ProtocolFailed, $"联机模块接口返回 {(int)resp.StatusCode}");
    }

    private async Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeoutMs);
        try
        {
            return await _http.GetAsync(Url(path), timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MultiplayerLobbyException(MultiplayerLobbyFailure.ProtocolFailed, "联机模块响应超时");
        }
    }

    private async Task<bool> MetaIsValidAsync(int port, bool requireExactVersion, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"http://127.0.0.1:{port}/meta", ct);
            if (!resp.IsSuccessStatusCode) return false;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            if (!root.TryGetProperty("version", out var ver) || string.IsNullOrEmpty(ver.GetString())) return false;
            if (!root.TryGetProperty("target_os", out var os)
                || !string.Equals(os.GetString(), "windows", StringComparison.OrdinalIgnoreCase)) return false;
            if (!root.TryGetProperty("target_arch", out var arch)) return false;
            var archText = arch.GetString();
            var expectedArch = _module.Architecture == "arm64" ? "aarch64" : "x86_64";
            var archOk = string.Equals(archText, expectedArch, StringComparison.OrdinalIgnoreCase)
                         || (expectedArch == "aarch64" && string.Equals(archText, "arm64", StringComparison.OrdinalIgnoreCase));
            if (!archOk) return false;
            if (requireExactVersion && !string.Equals(ver.GetString(), _module.Version)) return false;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    /// <summary>轮询状态机：500ms 一次，等就绪 / 异常 / 超时（host 20s / guest 20s）</summary>
    private async Task<MultiplayerSnapshot> PollUntilReadyAsync(bool isHost, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(StartupTimeoutMs);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            ControllerState state;
            try
            {
                state = await CallStateAsync("/state", ct);
            }
            catch (MultiplayerLobbyException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                ct.ThrowIfCancellationRequested(); // 用户取消直接上抛，别当网络抖动
                // 网络抖动：继续轮询直到超时
                if (DateTime.UtcNow >= deadline)
                    throw new MultiplayerLobbyException(
                        isHost ? MultiplayerLobbyFailure.StartupFailed : MultiplayerLobbyFailure.RoomConnectionFailed,
                        "联机模块无响应");
                await Task.Delay(PollIntervalMs, ct);
                continue;
            }

            if (state.State == "exception")
                throw ForExceptionType(state.Type, isHost, unexpected: false);

            var ready = isHost ? state.State == "host-ok" : state.State == "guest-ok";
            if (ready)
            {
                if (string.IsNullOrWhiteSpace(state.Room))
                    throw new MultiplayerLobbyException(MultiplayerLobbyFailure.ProtocolFailed, "联机模块未返回房间码");
                MultiplayerLog.Log($"就绪: {state.State}, room={state.Room}, profiles={state.Profiles.Count}");
                var snapshot = MakeSnapshot(MultiplayerSessionState.Active, state);
                Publish(snapshot);
                StartMonitor();
                return snapshot;
            }

            // 客机：connecting/starting 阶段服务端已回填规范房间码 → 提前展示
            if (!isHost && state.Room is not null)
            {
                Publish(new MultiplayerSnapshot(state.Room, MultiplayerSessionState.Joining,
                    MapPlayers(state)));
            }

            // 其他非期望态：host 侧 waiting / guest 态 → 协议错误（BHL 行为）
            var expectScanning = isHost
                ? state.State is "host-scanning" or "host-starting"
                : state.State is "guest-connecting" or "guest-starting";
            if (!expectScanning && state.State != "waiting")
            {
                MultiplayerLog.Log($"状态异常: {state.State}");
                throw new MultiplayerLobbyException(
                    MultiplayerLobbyFailure.ProtocolFailed, $"联机模块状态异常：{state.State}");
            }

            if (DateTime.UtcNow >= deadline)
            {
                // 超时：host 停在扫描 = 没开局域网世界；其他 = 启动/连接失败
                throw new MultiplayerLobbyException(
                    isHost
                        ? (state.State == "host-scanning"
                            ? MultiplayerLobbyFailure.WorldUnavailable
                            : MultiplayerLobbyFailure.StartupFailed)
                        : MultiplayerLobbyFailure.RoomConnectionFailed,
                    isHost ? "未检测到局域网世界（20 秒超时）" : "加入房间超时（20 秒）");
            }
            await Task.Delay(PollIntervalMs, ct);
        }
    }

    // ---------- 监控（会话就绪后 1s 轮询） ----------

    private void StartMonitor()
    {
        lock (_gate)
        {
            _monitorCts?.Cancel();
            _monitorCts = new CancellationTokenSource();
        }
        _ = Task.Run(() => MonitorLoopAsync(_monitorCts.Token));
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var consecutiveFailures = 0; // REVIEW-D 高4：连续失败计数——复用实例无进程句柄时的死亡判定
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                IProcessHandle? process;
                lock (_gate) process = _ownedProcess;
                if (process?.HasExited == true)
                {
                    MultiplayerLog.Log("监控: 陶瓦进程已退出，会话停止");
                    await StopUnexpectedlyAsync(MultiplayerStopReason.BackendExited);
                    return;
                }
                ControllerState state;
                try
                {
                    state = await CallStateAsync("/state", ct);
                }
                catch (Exception) when (ct.IsCancellationRequested) { return; }
                catch (Exception)
                {
                    // REVIEW-D 高4：复用现役实例（_ownedProcess==null）时旧代码无进程死亡检测——
                    // 连接拒绝被当「网络抖动」无限忽略，陶瓦进程死后 UI 永远卡「房间已就绪」。
                    // 连续 10 秒（10 次）连不上控制器即判定进程死亡（网络抖动 1~2 秒级不会误杀）
                    if (++consecutiveFailures >= 10)
                    {
                        MultiplayerLog.Log("监控: 控制器连续无响应（进程可能已退出），会话停止");
                        await StopUnexpectedlyAsync(MultiplayerStopReason.BackendExited);
                        return;
                    }
                    continue;
                }
                consecutiveFailures = 0; // 一次成功即复位

                if (state.State == "exception")
                {
                    await StopUnexpectedlyAsync(ReasonForType(state.Type));
                    return;
                }
                var expected = _mode == SessionMode.Host ? "host-ok" : "guest-ok";
                if (state.State != expected)
                {
                    await StopUnexpectedlyAsync(MultiplayerStopReason.ServiceFailed);
                    return;
                }
                // 玩家变更才发布（签名比对：machine_id\x1fname\x1fkind，'\n' 拼接）
                if (state.Signature != _lastState.Signature)
                {
                    _lastState = state;
                    Publish(MakeSnapshot(MultiplayerSessionState.Active, state));
                }
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
    }

    // ---------- 收尾 ----------

    private async Task StopRuntimeAsync(CancellationToken ct)
    {
        // 1. /state/ide → 回 waiting（5s 超时、100ms 轮询）
        IProcessHandle? process;
        int port;
        lock (_gate)
        {
            process = _ownedProcess;
            port = _controllerPort;
        }
        // 端口已建立即走 ide 序列——主动离开时 mode 已置 Stopping，不能拿 mode 判
        var started = port > 0;
        if (started)
        {
            try
            {
                using var resp = await _http.GetAsync(Url("/state/ide"), ct);
                if (resp.IsSuccessStatusCode)
                {
                    var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(WaitForWaitingTimeoutMs);
                    while (DateTime.UtcNow < deadline)
                    {
                        ct.ThrowIfCancellationRequested();
                        var s = await CallStateAsync("/state", ct);
                        if (s.State == "waiting") break;
                        await Task.Delay(100, ct);
                    }
                }
            }
            catch { /* ide 失败不阻塞收尾 */ }
        }

        // 2. 本会话拥有的进程 → /panic 优雅关闭 → 3s 内不退则 Kill 进程树
        if (process is not null && !process.HasExited)
        {
            try { await _http.GetAsync(Url("/panic?peaceful=true"), CancellationToken.None); } catch { }
            if (!await AwaitExitWithinAsync(process, 3000, CancellationToken.None))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                await AwaitExitWithinAsync(process, 3000, CancellationToken.None);
            }
            process.Dispose();
        }
        lock (_gate) _ownedProcess = null;
    }

    /// <summary>异常终止：停进程 + 清状态 + 触发 Stopped（加锁防重入）</summary>
    private async Task StopUnexpectedlyAsync(MultiplayerStopReason reason)
    {
        MultiplayerLog.Log($"异常停止: {reason}");
        lock (_gate)
        {
            if (_mode == SessionMode.None || _mode == SessionMode.Stopping) return;
            _mode = SessionMode.Stopping;
        }
        await StopRuntimeAsync(CancellationToken.None);
        lock (_gate)
        {
            _mode = SessionMode.None;
            Current = null;
        }
        Stopped?.Invoke(reason);
    }

    /// <summary>创建失败路径：停进程但**不发 Stopped**（UI 在异常分支自己复位）</summary>
    private async Task CleanupFailedCreationAsync()
    {
        MultiplayerLog.Log("创建失败清理: 停止陶瓦进程");
        IProcessHandle? process;
        lock (_gate)
        {
            process = _ownedProcess;
            _ownedProcess = null;
            _controllerPort = 0;
            _mode = SessionMode.None;
        }
        if (process is not null && !process.HasExited)
        {
            try { await _http.GetAsync(Url("/panic?peaceful=true"), CancellationToken.None); } catch { }
            if (!await AwaitExitWithinAsync(process, 3000, CancellationToken.None))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            process.Dispose();
        }
    }

    // ---------- 工具 ----------

    private string Url(string path) => $"http://127.0.0.1:{_controllerPort}{path}";

    private MultiplayerSnapshot MakeSnapshot(MultiplayerSessionState state, ControllerState? s = null)
    {
        s ??= _lastState;
        var snapshot = new MultiplayerSnapshot(s.Room, state, MapPlayers(s));
        Current = snapshot;
        return snapshot;
    }

    private void Publish(MultiplayerSnapshot snapshot)
        => SnapshotChanged?.Invoke(snapshot);

    private static List<MultiplayerPlayer> MapPlayers(ControllerState s)
    {
        var list = new List<MultiplayerPlayer>();
        var seen = new HashSet<string>();
        var isHostOk = s.State == "host-ok";
        foreach (var p in s.Profiles)
        {
            if (!seen.Add(p.MachineId)) continue; // machine_id 去重
            var isHost = p.Kind == "HOST";
            list.Add(new MultiplayerPlayer(
                p.Name, p.MachineId, isHost,
                IsLocal: p.Kind == "LOCAL" || (isHostOk && isHost),
                p.LatencyMs));
        }
        return list;
    }

    private static MultiplayerLobbyException ForExceptionType(int? type, bool isHost, bool unexpected)
    {
        if (isHost)
        {
            return type switch
            {
                3 => new MultiplayerLobbyException(MultiplayerLobbyFailure.StartupFailed, "联机模块退出（异常码 3）"),
                4 => new MultiplayerLobbyException(MultiplayerLobbyFailure.WorldUnavailable, "局域网世界关闭（异常码 4）"),
                _ => new MultiplayerLobbyException(MultiplayerLobbyFailure.ProtocolFailed, $"联机模块异常（码 {type}）"),
            };
        }
        return type is 0 or 1 or 2
            ? new MultiplayerLobbyException(MultiplayerLobbyFailure.RoomConnectionFailed, "连不上房主")
            : new MultiplayerLobbyException(MultiplayerLobbyFailure.ProtocolFailed, $"联机模块异常（码 {type}）");
    }

    private static MultiplayerStopReason ReasonForType(int? type) => type switch
    {
        3 => MultiplayerStopReason.BackendExited,
        4 => MultiplayerStopReason.WorldClosed,
        _ => MultiplayerStopReason.ServiceFailed,
    };

    private static int? ReadLockPort(string lockPath)
    {
        try
        {
            using var fs = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < 2) return null;
            Span<byte> buf = stackalloc byte[2];
            var n = fs.Read(buf);
            if (n < 2) return null;
            var port = (buf[0] << 8) | buf[1];
            return port is > 0 and <= 65535 ? port : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _http.Dispose();
        lock (_gate) _ownedProcess?.Dispose();
    }

    // ---------- 内部模型 ----------

    private enum SessionMode { None, Host, Guest, Stopping }

    private sealed class ControllerState
    {
        public string State { get; set; } = "waiting";
        public string? Room { get; set; }
        public int? Type { get; set; }
        public List<ProfileEntry> Profiles { get; set; } = new();
        public string Signature { get; set; } = "";
    }

    private sealed record ProfileEntry(string MachineId, string Name, string Vendor, string Kind, int? LatencyMs);

    private static async Task<ControllerState> ParseStateAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var contentLength = resp.Content.Headers.ContentLength ?? 0;
        if (contentLength > MaxResponseBytes)
            throw new MultiplayerLobbyException(MultiplayerLobbyFailure.ProtocolFailed, "联机模块响应过大");
        using var doc = await ParseJsonAsync(resp, ct);
        var root = doc.RootElement;
        var state = new ControllerState
        {
            State = Sanitize(root, "state", 32) ?? "other",
            Room = Sanitize(root, "room", 64),
        };
        // type 可能显式为 null（服务端序列化差异）→ 必须按 Number 判型，否则 TryGetInt32 抛 InvalidOperationException
        if (root.TryGetProperty("type", out var typeEl)
            && typeEl.ValueKind == JsonValueKind.Number && typeEl.TryGetInt32(out var type)) state.Type = type;
        if (root.TryGetProperty("profiles", out var profilesEl) && profilesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in profilesEl.EnumerateArray())
            {
                var kind = Sanitize(p, "kind", 16) ?? "";
                state.Profiles.Add(new ProfileEntry(
                    MachineId: Sanitize(p, "machine_id", 128) ?? Guid.NewGuid().ToString("N"),
                    Name: Sanitize(p, "name", 64) ?? "Player",
                    Vendor: Sanitize(p, "vendor", 128) ?? "Terracotta",
                    Kind: kind,
                    LatencyMs: TryInt(p, "latency_ms")));
            }
            // 玩家变更签名：machine_id\x1fname\x1fkind 按 '\n' 拼接
            state.Signature = string.Join("\n", state.Profiles.Select(x => $"{x.MachineId}\x1f{x.Name}\x1f{x.Kind}"));
        }
        return state;
    }

    /// <summary>解析 JSON 响应；非 JSON（空 body / HTML）时留证据再抛，方便真机排查。</summary>
    private static async Task<JsonDocument> ParseJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 16 }, ct);
        }
        catch (JsonException)
        {
            MultiplayerLog.Log($"响应非 JSON: HTTP={(int)resp.StatusCode}, len={resp.Content.Headers.ContentLength ?? -1}");
            throw;
        }
    }

    private static string? Sanitize(JsonElement el, string prop, int maxLen)
    {
        if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var text = v.GetString()?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var cleaned = new string(text.Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Length <= maxLen ? cleaned : cleaned[..maxLen];
    }

    private static int? TryInt(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        return null;
    }
}

/// <summary>进程句柄抽象（生产包装 Process，测试注入 fake）</summary>
public interface IProcessHandle : IDisposable
{
    bool HasExited { get; }
    void Kill(bool entireProcessTree);
    Task<bool> WaitForExitAsync(int milliseconds, CancellationToken ct);
    Task<string?> ReadLineAsync();
}

internal sealed class ProcessHandle : IProcessHandle
{
    private readonly Process _process;

    public ProcessHandle(Process process) => _process = process;
    public bool HasExited => _process.HasExited;
    public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);
    public async Task<bool> WaitForExitAsync(int milliseconds, CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMilliseconds(milliseconds), ct);
        return true;
    }
    public Task<string?> ReadLineAsync() => _process.StandardOutput.ReadLineAsync();
    public void Dispose() => _process.Dispose();
}
