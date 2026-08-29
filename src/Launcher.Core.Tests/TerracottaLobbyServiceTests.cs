using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Launcher.Core.Multiplayer;

namespace Launcher.Core.Tests;

/// <summary>
/// 陶瓦联机会话测试：进程 seam（IProcessHandle fake）+ HTTP stub，不真起进程。
/// 与 Provisioning 测试同 collection（串行）：都会碰 %TEMP%\terracotta\terracotta.lock，
/// 且避免真实陶瓦实例（如果开着）被测试干扰。
/// </summary>
[CollectionDefinition("terracotta", DisableParallelization = true)]
public class TerracottaSerialCollection;

[Collection("terracotta")]
public class TerracottaLobbyServiceTests : IDisposable
{
    private const int LockPort = 7001;

    // ---------- fakes ----------

    private sealed class FakeProcess : IProcessHandle
    {
        public bool HasExited { get; set; }
        public bool WaitForExitResult { get; set; } // AwaitExitWithinAsync 的返回值（false = 存活）
        public List<string> Killed { get; } = [];
        public bool Disposed { get; private set; }

        public void Kill(bool entireProcessTree) => Killed.Add(entireProcessTree.ToString());
        public Task<bool> WaitForExitAsync(int milliseconds, CancellationToken ct) => Task.FromResult(WaitForExitResult);
        public Task<string?> ReadLineAsync() => Task.FromResult<string?>(null);
        public void Dispose() => Disposed = true;
    }

    /// <summary>捕获 handoff 路径并预写 handoff JSON；记录创建次数</summary>
    private sealed class FakeProcessFactory
    {
        public string? HandoffJson = """{"port": 7000}""";
        public FakeProcess? Process;
        public int Called;
        public string? LastHandoffPath;

        public IProcessHandle Create(ProcessStartInfo psi)
        {
            Called++;
            LastHandoffPath = psi.ArgumentList[1];
            if (HandoffJson is not null && LastHandoffPath is not null)
                File.WriteAllText(LastHandoffPath, HandoffJson);
            Process ??= new FakeProcess();
            return Process;
        }
    }

    /// <summary>HTTP stub：路由表 + /state 序列（队列空时重复 DefaultState；null → 500）</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        public readonly Queue<string> StateQueue = new();
        public string? DefaultState;
        public int MetaStatus = 200;
        public string MetaJson = $$"""{"version":"0.4.2","target_os":"{{TerracottaProvisioningService.OsKey}}","target_arch":"{{TerracottaProvisioningService.Arch}}"}""";
        public int GuestingStatus = 200;
        public int IdeStatus = 200;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            lock (Requests) Requests.Add(path + query);
            return Task.FromResult(path switch
            {
                "/meta" => Json(MetaStatus, MetaJson),
                "/state" => StateQueue.Count > 0 ? Json(200, StateQueue.Dequeue()) : DefaultState is null
                        ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                        : Json(200, DefaultState),
                // 真实 terracotta：动作端点返回 200 + 空 body，状态只靠 /state 轮询（不能当 JSON 解析）
                "/state/scanning" => new HttpResponseMessage(HttpStatusCode.OK),
                "/state/guesting" => GuestingStatus == 200
                        ? new HttpResponseMessage(HttpStatusCode.OK)
                        : Json(GuestingStatus, "{}"),
                "/state/ide" => Json(IdeStatus, "{}"),
                "/panic" => Json(200, "{}"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }

        private static HttpResponseMessage Json(int status, string body) => new((HttpStatusCode)status)
        {
            Content = new StringContent(body),
        };
    }

    // ---------- fixture ----------

    private readonly string _lockPath = Path.Combine(Path.GetTempPath(), "terracotta", "terracotta.lock");
    private readonly byte[]? _lockBackup;

    public TerracottaLobbyServiceTests()
    {
        // 备份并移除真实 lock：防真机陶瓦实例干扰测试（或反之）
        Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
        if (File.Exists(_lockPath)) _lockBackup = File.ReadAllBytes(_lockPath);
        File.Delete(_lockPath);
    }

    public void Dispose()
    {
        if (_lockBackup is not null) File.WriteAllBytes(_lockPath, _lockBackup);
        else File.Delete(_lockPath);
    }

    private static TerracottaModule Module() => new(
        "0.4.2", TerracottaProvisioningService.Arch,
        @"C:\fake\terracotta", @"C:\fake\terracotta\terracotta.exe");

    /// <summary>玩家元组；重载拆三个（无 room / 有 room / 纯异常码），避免位置参数歧义。
    /// 字段按需写入（贴近真实服务端：非 exception 不写 type，缺省字段省略），避免 type:null 等异常形态。</summary>
    private static string State(string state,
        params (string Mid, string Name, string Kind, int? Latency)[] players) => StateCore(state, null, null, players);

    private static string State(string state, string room,
        params (string Mid, string Name, string Kind, int? Latency)[] players) => StateCore(state, room, null, players);

    private static string State(string state, int type) => StateCore(state, null, type, []);

    private static string StateCore(string state, string? room, int? type,
        params (string Mid, string Name, string Kind, int? Latency)[] players)
    {
        var obj = new Dictionary<string, object?> { ["state"] = state };
        if (room is not null) obj["room"] = room;
        if (type is not null) obj["type"] = type;
        if (players.Length > 0)
        {
            obj["profiles"] = players.Select(p => new
            {
                machine_id = p.Mid,
                name = p.Name,
                vendor = "Terracotta",
                kind = p.Kind,
                latency_ms = p.Latency,
            }).ToArray();
        }
        return JsonSerializer.Serialize(obj);
    }

    private static (string Mid, string Name, string Kind, int? Latency) P(string mid, string name, string kind, int? latency = 12)
        => (mid, name, kind, latency);

    // ---------- 创建房间 ----------

    [Fact]
    public async Task CreateHost_Ok_ReturnsSnapshotAndEvents()
    {
        var handler = new StubHandler();
        handler.StateQueue.Enqueue(State("host-ok", "ABCD-1234",
            P("m1", "Alice", "HOST"), P("m2", "Bob", "LOCAL"), P("m3", "Carol", "GUEST")));
        var factory = new FakeProcessFactory();
        var events = new List<MultiplayerSnapshot>();
        using var svc = new TerracottaLobbyService(Module(), factory.Create, handler);
        svc.SnapshotChanged += events.Add;

        var snap = await svc.CreateHostAsync("Alice", CancellationToken.None);

        Assert.Equal(MultiplayerSessionState.Active, snap.State);
        Assert.Equal("ABCD-1234", snap.RoomCode);
        Assert.Equal(3, snap.Players.Count);
        Assert.True(snap.Players[0].IsHost && snap.Players[0].IsLocal); // host-ok 下 HOST 也算本地
        Assert.True(snap.Players[1].IsLocal); // LOCAL
        Assert.False(snap.Players[2].IsHost);
        Assert.Equal(12, snap.Players[2].LatencyMs);
        Assert.Single(events); // 就绪时发布一次
        Assert.Equal(MultiplayerSessionState.Active, events[0].State);
        Assert.Equal(1, factory.Called);
        Assert.NotNull(factory.LastHandoffPath);
        Assert.False(File.Exists(factory.LastHandoffPath)); // handoff 用完已清理
        Assert.Empty(factory.Process!.Killed);
        Assert.False(factory.Process.Disposed); // 存活进程归服务所有
    }

    [Fact]
    public async Task CreateHost_LockFileReuse_NoNewProcess()
    {
        File.WriteAllBytes(_lockPath, [(byte)(LockPort >> 8), (byte)(LockPort & 0xFF)]);
        var handler = new StubHandler();
        handler.StateQueue.Enqueue(State("host-ok", "ABCD-1234", P("m1", "Alice", "HOST")));
        var factory = new FakeProcessFactory();
        using var svc = new TerracottaLobbyService(Module(), factory.Create, handler);

        var snap = await svc.CreateHostAsync("Alice", CancellationToken.None);

        Assert.Equal("ABCD-1234", snap.RoomCode);
        Assert.Equal(0, factory.Called); // 复用现役实例，不拉起新进程
        Assert.Null(factory.LastHandoffPath);
    }

    [Fact]
    public async Task CreateHost_InvalidLockPort_StartsNewProcess()
    {
        File.WriteAllBytes(_lockPath, [0, 0]); // port 0 → 非法，走新进程
        var handler = new StubHandler();
        handler.StateQueue.Enqueue(State("host-ok", "ABCD-1234", P("m1", "Alice", "HOST")));
        var factory = new FakeProcessFactory();
        using var svc = new TerracottaLobbyService(Module(), factory.Create, handler);

        await svc.CreateHostAsync("Alice", CancellationToken.None);

        Assert.Equal(1, factory.Called);
    }

    [Fact]
    public async Task CreateHost_HandoffPortInvalid_ProtocolFailed()
    {
        var factory = new FakeProcessFactory { HandoffJson = """{"port": 0}""" };
        var handler = new StubHandler();
        using var svc = new TerracottaLobbyService(Module(), factory.Create, handler);

        var ex = await Assert.ThrowsAsync<MultiplayerLobbyException>(
            () => svc.CreateHostAsync("Alice", CancellationToken.None));

        Assert.Equal(MultiplayerLobbyFailure.ProtocolFailed, ex.Failure);
        Assert.Empty(handler.Requests); // handoff 阶段失败 → 尚无 HTTP 交互
        Assert.Single(factory.Process!.Killed); // 进程存活且端口未知 → 直接 Kill 防僵尸
        Assert.True(factory.Process.Disposed);
    }

    [Fact]
    public async Task CreateHost_ProcessExitsEarly_BackendBusy()
    {
        var factory = new FakeProcessFactory { Process = new FakeProcess { HasExited = true } };
        var handler = new StubHandler();
        using var svc = new TerracottaLobbyService(Module(), factory.Create, handler);

        var ex = await Assert.ThrowsAsync<MultiplayerLobbyException>(
            () => svc.CreateHostAsync("Alice", CancellationToken.None));

        Assert.Equal(MultiplayerLobbyFailure.BackendBusy, ex.Failure);
        Assert.Empty(factory.Process.Killed); // 未拥有进程，不收尾
    }

    [Fact]
    public async Task CreateHost_ExceptionType3_StartupFailed()
    {
        var handler = new StubHandler();
        handler.StateQueue.Enqueue(State("exception", type: 3));
        using var svc = new TerracottaLobbyService(Module(), new FakeProcessFactory().Create, handler);

        var ex = await Assert.ThrowsAsync<MultiplayerLobbyException>(
            () => svc.CreateHostAsync("Alice", CancellationToken.None));

        Assert.Equal(MultiplayerLobbyFailure.StartupFailed, ex.Failure);
    }

    [Fact]
    public async Task CreateHost_ExceptionType4_WorldUnavailable()
    {
        var handler = new StubHandler();
        handler.StateQueue.Enqueue(State("exception", type: 4));
        using var svc = new TerracottaLobbyService(Module(), new FakeProcessFactory().Create, handler);

        var ex = await Assert.ThrowsAsync<MultiplayerLobbyException>(
            () => svc.CreateHostAsync("Alice", CancellationToken.None));

        Assert.Equal(MultiplayerLobbyFailure.WorldUnavailable, ex.Failure);
    }

    [Fact]
    public async Task CreateHost_UnexpectedState_ProtocolFailed()
    {
        var handler = new StubHandler();
        handler.StateQueue.Enqueue(State("waiting")); // host 侧 waiting = 协议错误
        using var svc = new TerracottaLobbyService(Module(), new FakeProcessFactory().Create, handler);

        var ex = await Assert.ThrowsAsync<MultiplayerLobbyException>(
            () => svc.CreateHostAsync("Alice", CancellationToken.None));

        Assert.Equal(MultiplayerLobbyFailure.ProtocolFailed, ex.Failure);
    }

    [Fact]
    public async Task CreateHost_ScanTimeout_WorldUnavailable()
    {
        // 20 秒轮询（500ms × 40 次）后仍停在扫描 = 没开局域网世界
        var handler = new StubHandler { DefaultState = State("host-scanning") };
        using var svc = new TerracottaLobbyService(Module(), new FakeProcessFactory().Create, handler);

        var ex = await Assert.ThrowsAsync<MultiplayerLobbyException>(
            () => svc.CreateHostAsync("Alice", CancellationToken.None));

        Assert.Equal(MultiplayerLobbyFailure.WorldUnavailable, ex.Failure);
    }

    [Fact]
    public async Task CreateHost_MetaIncompatible_BackendBusy()
    {
        var handler = new StubHandler { MetaJson = """{"version":"9.9.9","target_os":"windows","target_arch":"x86_64"}""" };
        using var svc = new TerracottaLobbyService(Module(), new FakeProcessFactory().Create, handler);

        var ex = await Assert.ThrowsAsync<MultiplayerLobbyException>(
            () => svc.CreateHostAsync("Alice", CancellationToken.None));

        Assert.Equal(MultiplayerLobbyFailure.BackendBusy, ex.Failure);
    }

    // ---------- 加入房间 ----------

    [Fact]
    public async Task Join_Ok_ReturnsSnapshot()
    {
        var handler = new StubHandler();
        handler.StateQueue.Enqueue(State("guest-ok", "ABCD-1234", P("m1", "Host", "HOST"), P("m2", "Bob", "LOCAL")));
        using var svc = new TerracottaLobbyService(Module(), new FakeProcessFactory().Create, handler);

        var snap = await svc.JoinAsync("ABCD-1234", "Bob", CancellationToken.None);

        Assert.Equal(MultiplayerSessionState.Active, snap.State);
        Assert.Equal("ABCD-1234", snap.RoomCode);
        Assert.Equal(2, snap.Players.Count);
        Assert.False(snap.Players[0].IsLocal); // guest-ok 下 HOST 不算本地
        Assert.True(snap.Players[1].IsLocal); // 只有 LOCAL 才算
    }

    [Fact]
    public async Task Join_InvalidRoomCode_Throws400()
    {
        var handler = new StubHandler { GuestingStatus = 400 };
        var factory = new FakeProcessFactory();
        using var svc = new TerracottaLobbyService(Module(), factory.Create, handler);

        var ex = await Assert.ThrowsAsync<MultiplayerLobbyException>(
            () => svc.JoinAsync("BAD-CODE", "Bob", CancellationToken.None));

        Assert.Equal(MultiplayerLobbyFailure.InvalidRoomCode, ex.Failure);
        Assert.Contains(handler.Requests, r => r.StartsWith("/panic")); // 失败 → 收尾
        Assert.Single(factory.Process!.Killed);
    }

    [Fact]
    public async Task Join_ExceptionType0_RoomConnectionFailed()
    {
        var handler = new StubHandler();
        handler.StateQueue.Enqueue(State("exception", type: 0));
        using var svc = new TerracottaLobbyService(Module(), new FakeProcessFactory().Create, handler);

        var ex = await Assert.ThrowsAsync<MultiplayerLobbyException>(
            () => svc.JoinAsync("ABCD-1234", "Bob", CancellationToken.None));

        Assert.Equal(MultiplayerLobbyFailure.RoomConnectionFailed, ex.Failure);
    }

    [Fact]
    public async Task Join_ConnectingWithRoom_PublishesJoiningSnapshot()
    {
        var handler = new StubHandler();
        handler.StateQueue.Enqueue(State("guest-connecting", "ABCD-1234", P("m1", "Host", "HOST")));
        handler.StateQueue.Enqueue(State("guest-ok", "ABCD-1234", P("m1", "Host", "HOST"), P("m2", "Bob", "LOCAL")));
        var events = new List<MultiplayerSnapshot>();
        using var svc = new TerracottaLobbyService(Module(), new FakeProcessFactory().Create, handler);
        svc.SnapshotChanged += events.Add;

        var snap = await svc.JoinAsync("abcd-1234", "Bob", CancellationToken.None);

        Assert.Equal("ABCD-1234", snap.RoomCode); // 以服务端回填为准
        Assert.Equal(2, events.Count);
        Assert.Equal(MultiplayerSessionState.Joining, events[0].State);
        Assert.Equal("ABCD-1234", events[0].RoomCode); // 连接中提前展示房间码
        Assert.Equal(MultiplayerSessionState.Active, events[1].State);
    }

    [Fact]
    public async Task Join_UrlEncodesRoomAndPlayer()
    {
        var handler = new StubHandler();
        handler.StateQueue.Enqueue(State("guest-ok", "ABCD-1234", P("m1", "Host", "HOST")));
        using var svc = new TerracottaLobbyService(Module(), new FakeProcessFactory().Create, handler);

        await svc.JoinAsync("中文 房间&X", "玩家 Y", CancellationToken.None);

        var guesting = Assert.Single(handler.Requests, r => r.StartsWith("/state/guesting"));
        Assert.Contains($"room={Uri.EscapeDataString("中文 房间&X")}", guesting);
        Assert.Contains($"player={Uri.EscapeDataString("玩家 Y")}", guesting);
    }

    // ---------- 玩家列表 ----------

    [Fact]
    public async Task Players_DedupAndFallback()
    {
        var handler = new StubHandler();
        handler.StateQueue.Enqueue(State("host-ok", "ABCD-1234",
            P("dup", "", "HOST"), P("dup", "Other", "GUEST"), // 同 machine_id 去重，空名 → Player
            P("no-kind", "Bob", ""),                            // kind 缺省 → 非房主
            P("m3", "Carol", "GUEST")));
        using var svc = new TerracottaLobbyService(Module(), new FakeProcessFactory().Create, handler);

        var snap = await svc.CreateHostAsync("Alice", CancellationToken.None);

        Assert.Equal(3, snap.Players.Count);
        Assert.Equal("Player", snap.Players[0].Name);
        Assert.False(snap.Players[1].IsHost);
        Assert.Equal(12, snap.Players[2].LatencyMs);
    }

    // ---------- 离开 / 异常停止 ----------

    [Fact]
    public async Task StopAsync_IdeThenPanic_NoStoppedEvent()
    {
        var handler = new StubHandler { DefaultState = State("waiting") };
        // 队列里多塞几个 host-ok：给 monitor 兜底，防「停在前就打 /state」竞态
        for (var i = 0; i < 20; i++) handler.StateQueue.Enqueue(State("host-ok", "ABCD-1234", P("m1", "Alice", "HOST")));
        var factory = new FakeProcessFactory();
        var stopped = new List<MultiplayerStopReason>();
        using var svc = new TerracottaLobbyService(Module(), factory.Create, handler);
        svc.Stopped += r => stopped.Add(r);

        await svc.CreateHostAsync("Alice", CancellationToken.None);
        await svc.StopAsync(CancellationToken.None);

        Assert.Contains("/state/ide", handler.Requests);
        Assert.Contains("/panic?peaceful=true", handler.Requests);
        Assert.Single(factory.Process!.Killed); // panic 后 3s 内未退 → Kill
        Assert.True(factory.Process.Disposed);
        Assert.Empty(stopped); // 主动离开不发 Stopped
    }

    [Fact]
    public async Task Monitor_ProcessExits_BackendExited()
    {
        var handler = new StubHandler();
        handler.StateQueue.Enqueue(State("host-ok", "ABCD-1234", P("m1", "Alice", "HOST")));
        var factory = new FakeProcessFactory();
        using var svc = new TerracottaLobbyService(Module(), factory.Create, handler);
        var tcs = new TaskCompletionSource<MultiplayerStopReason>();
        svc.Stopped += r => tcs.TrySetResult(r);

        await svc.CreateHostAsync("Alice", CancellationToken.None);
        factory.Process!.HasExited = true; // 进程退出 → monitor 1s 内发现
        var reason = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(MultiplayerStopReason.BackendExited, reason);
        Assert.Empty(factory.Process.Killed); // 进程已退，无需 Kill
    }

    [Fact]
    public async Task Monitor_ExceptionType4_WorldClosed()
    {
        var handler = new StubHandler();
        handler.StateQueue.Enqueue(State("host-ok", "ABCD-1234", P("m1", "Alice", "HOST")));
        handler.StateQueue.Enqueue(State("exception", type: 4));
        using var svc = new TerracottaLobbyService(Module(), new FakeProcessFactory().Create, handler);
        var tcs = new TaskCompletionSource<MultiplayerStopReason>();
        svc.Stopped += r => tcs.TrySetResult(r);

        await svc.CreateHostAsync("Alice", CancellationToken.None);
        var reason = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(MultiplayerStopReason.WorldClosed, reason);
    }
}
