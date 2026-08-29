using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace Launcher.Core.Multiplayer;

/// <summary>
/// EasyTier 联机（8-14 第二联机方案）：虚拟组网——房间码 = 网络名#密钥#房主地址:端口。
/// 房主启动节点（静态虚拟 IP，监听 11010），加入者凭房间码直连房主；互通后游戏内开局域网世界，
/// 朋友「直接连接」输 主机虚拟IP:端口（或一键进服用 --server 链路）。同网段开箱即用。
///
/// Windows 关键约束（实测 8-14）：TUN 虚拟网卡创建需要管理员权限（wintun 驱动）——启动器
/// 以普通权限运行时须 UAC 提权启动 core（-Verb RunAs）；虚拟 IP 静态分配（房间码+玩家名 hash，
/// 10.144.144.x），peer 表格显示 ipv4/hostname。模式对齐 TerracottaLobbyService（进程 seam 注入）。
/// </summary>
public sealed class EasyTierLobbyService : IMultiplayerLobbyService
{
    /// <summary>easytier-core 默认监听端口（TCP/UDP 同端口）</summary>
    public const int DefaultPeerPort = 11010;

    /// <summary>静态虚拟 IP 网段（EasyTier 默认 10.144.144.0/24；x=2..254）</summary>
    public const string VnetPrefix = "10.144.144.";

    private const int ReadyTimeoutMs = 20_000;
    private const int PollIntervalMs = 500;
    private const int CliTimeoutMs = 3000;

    /// <summary>房间码分隔符（网络名/密钥/房主地址）</summary>
    public const char RoomSeparator = '#';

    public event Action<MultiplayerSnapshot>? SnapshotChanged;
    public event Action<MultiplayerStopReason>? Stopped;

    public MultiplayerSnapshot? Current { get; private set; }

    /// <summary>主机虚拟 IP（静态分配，本机已知）</summary>
    public string? HostVirtualIp => _localIp;

    /// <summary>主机填的游戏端口（一键进服用；默认 25565）</summary>
    public int HostGamePort { get; set; } = 25565;

    private readonly string _moduleDir;
    private readonly Func<ProcessStartInfo, IProcessHandle> _processFactory;
    private readonly Func<string[], string> _runCli;
    private readonly string _playerName;
    private IProcessHandle? _process;
    private string? _networkName;
    private string? _secret;
    private string? _localIp;
    private int _rpcPort;
    private bool _isHost;
    private bool _stopping;

    /// <summary>runCli：执行 easytier-cli 并返回 stdout（测试注入 fake；args[0]=cli 路径）</summary>
    public EasyTierLobbyService(string moduleDir, string playerName,
        Func<ProcessStartInfo, IProcessHandle>? processFactory = null,
        Func<string[], string>? runCli = null)
    {
        _moduleDir = moduleDir;
        _playerName = playerName;
        _processFactory = processFactory ?? (psi =>
        {
            var p = new Process { StartInfo = psi };
            p.Start();
            return new ProcessHandle(p);
        });
        _runCli = runCli ?? (args => RunCliProcess(args));
    }

    // ---------- 接口实现 ----------

    public async Task<MultiplayerSnapshot> CreateHostAsync(string playerName, CancellationToken ct)
    {
        _isHost = true;
        var net = GenerateNetworkName();
        _networkName = net;
        _secret = GenerateSecret();
        _localIp = AssignVirtualIp(net, playerName);
        Publish(new MultiplayerSnapshot(null, MultiplayerSessionState.Creating, []));
        await StartCoreAsync(CoreArgs(net, _secret, playerName, hostAddress: null), ct);
        var localIp = FirstLanIpv4() ?? "?";
        var roomCode = $"{net}{RoomSeparator}{_secret}{RoomSeparator}{localIp}:{DefaultPeerPort}";
        var snap = new MultiplayerSnapshot(roomCode, MultiplayerSessionState.Active,
            [new MultiplayerPlayer(playerName, net, IsHost: true, IsLocal: true, null)]);
        Publish(snap);
        _ = MonitorLoop(ct);
        return snap;
    }

    public async Task<MultiplayerSnapshot> JoinAsync(string roomCode, string playerName, CancellationToken ct)
    {
        _isHost = false;
        var parts = roomCode.Split(RoomSeparator);
        if (parts.Length < 3 || parts[0].Length == 0 || parts[1].Length == 0)
            throw new MultiplayerLobbyException(MultiplayerLobbyFailure.InvalidRoomCode,
                $"房间码格式不对（应为 网络名#密钥#房主地址:端口）：{roomCode}");
        _networkName = parts[0];
        _secret = parts[1];
        var hostAddr = parts[2];
        _localIp = AssignVirtualIp(_networkName, playerName);
        Publish(new MultiplayerSnapshot(roomCode, MultiplayerSessionState.Joining, []));
        await StartCoreAsync(CoreArgs(_networkName, _secret, playerName, hostAddr), ct);
        await WaitForPeerAsync(ct);
        var snap = new MultiplayerSnapshot(roomCode, MultiplayerSessionState.Active,
            [new MultiplayerPlayer(playerName, _networkName, IsHost: false, IsLocal: true, null)]);
        Publish(snap);
        _ = MonitorLoop(ct);
        return snap;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _stopping = true;
        Publish(new MultiplayerSnapshot(Current?.RoomCode, MultiplayerSessionState.Stopping, Current?.Players ?? []));
        try
        {
            if (_process is { } p)
            {
                p.Kill(entireProcessTree: true);
                await p.WaitForExitAsync(2000, ct);
                p.Dispose();
            }
        }
        catch { /* 进程已退出 */ }
        finally
        {
            _process = null;
            Publish(new MultiplayerSnapshot(null, MultiplayerSessionState.Idle, []));
            Stopped?.Invoke(MultiplayerStopReason.Manual);
        }
    }

    public void Dispose()
    {
        try { _process?.Kill(entireProcessTree: true); } catch { }
        _process?.Dispose();
        _process = null;
    }

    // ---------- 主机虚拟 IP / 一键进服 ----------

    /// <summary>加入者取房主虚拟 IP（peer 表 hostname 匹配房主玩家名；房主自己直接返回本机 IP）</summary>
    public string? FindHostVirtualIp()
    {
        if (_isHost) return _localIp;
        foreach (var line in PeerLines())
        {
            var m = PeerRegex().Match(line);
            if (m.Success && m.Groups["host"].Value == _playerName)
                return m.Groups["ip"].Value.TrimEnd('/').Split('/')[0];
        }
        return null;
    }

    // ---------- 内部 ----------

    /// <summary>
    /// core 启动参数：静态虚拟 IP + 独立监听/RPC 端口（同机多实例/残留进程不冲突）。
    /// hostAddress null = 房主（监听 0.0.0.0 等朋友连）；否则加入者（直连房主）。
    /// </summary>
    private string[] CoreArgs(string net, string secret, string playerName, string? hostAddress)
    {
        var listenPort = PickFreePort(21010);
        _rpcPort = PickFreePort(15888);
        var args = new List<string>
        {
            "-i", _localIp!,
            "--network-name", net,
            "--network-secret", secret,
            "--hostname", playerName,
            "-l", $"tcp://0.0.0.0:{listenPort}",
            "-r", $"127.0.0.1:{_rpcPort}",
        };
        if (hostAddress is not null)
            args.AddRange(["-p", $"tcp://{hostAddress}"]);
        return [.. args];
    }

    private async Task StartCoreAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(_moduleDir, OperatingSystem.IsWindows() ? "easytier-core.exe" : "easytier-core"),
            WorkingDirectory = _moduleDir,
            UseShellExecute = true, // 提权（runas）需要 UseShellExecute
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (OperatingSystem.IsWindows() && !IsAdministrator())
            psi.Verb = "runas"; // Windows：TUN 虚拟网卡创建需管理员（wintun 驱动）——UAC 弹窗一次；Linux 用 tun 权限组/sudo

        try
        {
            _process = _processFactory(psi);
        }
        catch (Exception ex)
        {
            // UAC 拒绝 / 启动失败
            throw new MultiplayerLobbyException(MultiplayerLobbyFailure.StartupFailed,
                IsAdministrator()
                    ? $"EasyTier 节点启动失败：{ex.Message}"
                    : "启动 EasyTier 需要管理员权限创建虚拟网卡（系统会弹 UAC 确认框），如果点了「否」请重试并允许。",
                ex);
        }

        // 等 RPC 就绪：cli 能查到 peer 输出（网络名出现在输出）
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ReadyTimeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var outText = _runCli([Path.Combine(_moduleDir, OperatingSystem.IsWindows() ? "easytier-cli.exe" : "easytier-cli"), "--rpc-portal", $"127.0.0.1:{_rpcPort}", "peer"]);
                if (outText.Contains(_networkName!, StringComparison.OrdinalIgnoreCase)
                    || outText.Contains(_playerName, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            catch { /* RPC 未就绪 */ }
            await Task.Delay(PollIntervalMs, ct);
        }
        throw new MultiplayerLobbyException(MultiplayerLobbyFailure.StartupFailed,
            "EasyTier 节点启动超时（RPC 端口未就绪）");
    }

    /// <summary>加入者等房主出现（组网建立信号：peer 表出现本机以外的节点）</summary>
    private async Task WaitForPeerAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ReadyTimeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            if (PeerLines().Any(l => PeerRegex().Match(l) is { Success: true } m
                && m.Groups["host"].Value != _playerName)) return;
            await Task.Delay(PollIntervalMs, ct);
        }
        throw new MultiplayerLobbyException(MultiplayerLobbyFailure.NetworkFailed,
            "连不上房主节点：检查房间码里的地址是否正确（同路由器/局域网最稳），或房主是否已开端口转发");
    }

    /// <summary>会话监控：core 退出 → 通知 UI 停止</summary>
    private async Task MonitorLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _process is { } p && !p.HasExited)
                await Task.Delay(1000, ct);
            if (!ct.IsCancellationRequested && !_stopping)
            {
                Publish(new MultiplayerSnapshot(null, MultiplayerSessionState.Idle, []));
                Stopped?.Invoke(MultiplayerStopReason.BackendExited);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Publish(MultiplayerSnapshot snap)
    {
        Current = snap;
        SnapshotChanged?.Invoke(snap);
    }

    private IEnumerable<string> PeerLines()
    {
        try
        {
            var outText = _runCli([Path.Combine(_moduleDir, OperatingSystem.IsWindows() ? "easytier-cli.exe" : "easytier-cli"),
                "--rpc-portal", $"127.0.0.1:{_rpcPort}", "peer"]);
            return outText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch { return []; }
    }

    /// <summary>easytier-cli peer 表格行：| ipv4 | hostname | cost | ... |（静态 IP 模式 ipv4 带 /24）</summary>
    private static Regex PeerRegex() => new(@"\|?\s*(?<ip>\d+\.\d+\.\d+\.\d+(/\d+)?)\s*\|\s*(?<host>\S+)\s*\|",
        RegexOptions.Compiled);

    /// <summary>真实 cli 执行（seam 外部调用）</summary>
    private static string RunCliProcess(string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = args[0],
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        for (var i = 1; i < args.Length; i++) psi.ArgumentList.Add(args[i]);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("easytier-cli 启动失败");
        var outTask = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(CliTimeoutMs))
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException("easytier-cli 超时");
        }
        return outTask.GetAwaiter().GetResult();
    }

    // ---------- 静态工具（纯函数可单测） ----------

    /// <summary>虚拟 IP 分配：房间码网络名 + 玩家名 hash → 10.144.144.{2..254}（同房间不同玩家不同 IP）</summary>
    public static string AssignVirtualIp(string networkName, string playerName)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes($"{networkName}#{playerName}"));
        var x = 2 + (h[0] | (h[1] << 8)) % 253;
        return $"{VnetPrefix}{x}";
    }

    /// <summary>是否管理员/root（TUN 创建前提——Windows wintun 需管理员，Linux 需 root 或 tun 权限组）</summary>
    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return IsRootLinux();
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Linux：euid==0 视为 root（EasyTier TUN 需 root 或 tun 组权限）</summary>
    private static bool IsRootLinux()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("id", "-u")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (p is null) return false;
            var uid = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(1000);
            return uid == "0";
        }
        catch { return false; }
    }

    /// <summary>取空闲端口（监听/RPC 隔离——同机残留进程不冲突）</summary>
    private static int PickFreePort(int preferred)
    {
        var listener = System.Net.Sockets.TcpListener.Create(preferred);
        try
        {
            listener.Start();
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        catch { return preferred; }
        finally { listener.Stop(); }
    }

    /// <summary>本机首个可达 IPv4（房主地址打包进房间码——EasyTier 隧道从虚拟网卡出发连 127.0.0.1 会 10049，必须物理 IP）</summary>
    private static string? FirstLanIpv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                var ip = addr.Address.ToString();
                if (ip.StartsWith("169.254") || ip.StartsWith("10.144.144")) continue; // APIPA/虚拟网段无意义
                return ip;
            }
        }
        return null;
    }

    /// <summary>房间网络名（易读短语 + 短随机）</summary>
    private static string GenerateNetworkName()
    {
        var words = new[] { "山", "海", "星", "月", "云", "风", "林", "川", "雪", "火" };
        var r = new Random();
        return $"mc-{words[r.Next(words.Length)]}{words[r.Next(words.Length)]}-{r.Next(1000, 9999)}";
    }

    /// <summary>房间密钥（6 位大写字母数字，去易混字符）</summary>
    private static string GenerateSecret()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var r = new Random();
        return new string(Enumerable.Range(0, 6).Select(_ => chars[r.Next(chars.Length)]).ToArray());
    }
}
