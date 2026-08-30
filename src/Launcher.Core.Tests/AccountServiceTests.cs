using System.Text.Json;
using Launcher.Core.Account;

namespace Launcher.Core.Tests;

/// <summary>账号服务：离线登录 / 多账号持久化 / 切换 / 删除</summary>
public class AccountServiceTests
{
    private static string TempStore() => Path.Combine(Path.GetTempPath(), $"accounts-{Guid.NewGuid():N}.json");

    [Fact]
    public void LoginOffline_MultipleAccounts_Persisted()
    {
        var path = TempStore();
        try
        {
            var svc = new AccountService(path);
            var a = svc.LoginOffline("Steve");
            var b = svc.LoginOffline("Alex");
            Assert.Equal(2, svc.Accounts.Count);
            Assert.Equal(b.Name, svc.Current!.Name);

            // 重载：列表与当前账号保持
            var reloaded = new AccountService(path);
            reloaded.Load();
            Assert.Equal(2, reloaded.Accounts.Count);
            Assert.Equal("Alex", reloaded.Current!.Name);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void LoginOffline_SameName_Deduplicates()
    {
        var path = TempStore();
        try
        {
            var svc = new AccountService(path);
            svc.LoginOffline("Steve");
            svc.LoginOffline("steve"); // 大小写不敏感 → 覆盖
            Assert.Single(svc.Accounts);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SwitchTo_ChangesCurrent()
    {
        var path = TempStore();
        try
        {
            var svc = new AccountService(path);
            svc.LoginOffline("Steve");
            svc.LoginOffline("Alex");
            Assert.True(svc.SwitchTo("Steve"));
            Assert.Equal("Steve", svc.Current!.Name);
            Assert.False(svc.SwitchTo("Nobody")); // 不存在 → false
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Delete_CurrentAccount_LogsOut()
    {
        var path = TempStore();
        try
        {
            var svc = new AccountService(path);
            svc.LoginOffline("Steve");
            svc.LoginOffline("Alex");
            Assert.True(svc.Delete("Alex")); // 当前账号
            Assert.Null(svc.Current);
            Assert.Single(svc.Accounts);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void OfflineUuid_Stable_V3Format()
    {
        var uuid = AccountService.OfflineUuid("Steve");
        Assert.Equal(36, uuid.Length);
        Assert.Equal(uuid, AccountService.OfflineUuid("Steve")); // 稳定
        Assert.NotEqual(uuid, AccountService.OfflineUuid("Alex"));
        Assert.Equal('3', uuid[14]); // UUID v3
    }

    [Fact]
    public void Changed_Event_FiresOnLoginAndLogout()
    {
        var path = TempStore();
        try
        {
            var svc = new AccountService(path);
            var count = 0;
            svc.Changed += () => count++;
            svc.LoginOffline("Steve");
            Assert.Equal(1, count);
            svc.SwitchTo("Steve");
            Assert.Equal(2, count);
            svc.Logout();
            Assert.Equal(3, count);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ---------- 8-31 默认离线账号：无账号自动建 Player，保证启动永远有账号 ----------

    [Fact]
    public void Load_NoStoreFile_AutoCreatesOfflinePlayer()
    {
        // 新装：accounts.json 不存在 → Load 自动建离线 Player + 设为当前
        var path = TempStore();
        try
        {
            var svc = new AccountService(path);
            svc.Load();
            Assert.Single(svc.Accounts);
            Assert.Equal("Player", svc.Current!.Name);
            Assert.Equal("offline", svc.Current.Type);
            Assert.True(File.Exists(path), "自动创建的账号应持久化");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_ExistingAccounts_DoesNotAddDefault()
    {
        var path = TempStore();
        try
        {
            var svc = new AccountService(path);
            svc.LoginOffline("Steve");
            var reloaded = new AccountService(path);
            reloaded.Load();
            Assert.Single(reloaded.Accounts); // 只有 Steve，不自动加 Player
            Assert.Equal("Steve", reloaded.Current!.Name);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_EmptyStoreFile_AutoCreatesPlayer()
    {
        // 用户删光所有账号（存了空列表）→ 下轮启动自动补回 Player
        var path = TempStore();
        try
        {
            File.WriteAllText(path, """{"CurrentName":null,"Accounts":[]}""");
            var svc = new AccountService(path);
            svc.Load();
            Assert.Equal("Player", svc.Current!.Name);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ---------- 8-13 正版账号：token DPAPI 加密落盘 + 旧明文迁移 ----------

    [Fact]
    public void LoginMicrosoft_TokensEncryptedOnDisk()
    {
        var path = TempStore();
        try
        {
            var svc = new AccountService(path);
            var acc = svc.LoginMicrosoft(new MicrosoftAuth.MicrosoftSession(
                "live-at-secret", "live-rt-secret", "uuid-1", "Steve"));

            Assert.Equal("microsoft", acc.Type);
            // 落盘断言：accounts.json 不含明文 token，含 dpapi: 前缀
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("live-at-secret", json);
            Assert.DoesNotContain("live-rt-secret", json);
            Assert.Contains("dpapi:", json);

            // 重载：token 解密还原
            var reloaded = new AccountService(path);
            reloaded.Load();
            var loaded = reloaded.Accounts.Single();
            Assert.Equal("live-at-secret", loaded.AccessToken);
            Assert.Equal("live-rt-secret", loaded.RefreshToken);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ---------- 8-13 正版 UUID 横线 + token 过期时间持久化 ----------

    [Fact]
    public void LoginLittleskin_Persisted_WithDashedUuid()
    {
        // 8-13 Littleskin 第三方登录：类型 + UUID 横线落盘，重载还原
        var path = TempStore();
        try
        {
            var svc = new AccountService(path);
            var acc = svc.LoginLittleskin("Steve", "069a79f444e94726a5befca90e38aaf5");
            Assert.Equal("littleskin", acc.Type);
            Assert.Equal("069a79f4-44e9-4726-a5be-fca90e38aaf5", acc.Uuid);

            var reloaded = new AccountService(path);
            reloaded.Load();
            var loaded = reloaded.Accounts.Single();
            Assert.Equal("littleskin", loaded.Type);
            Assert.Equal("Steve", loaded.Name);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void FormatUuid_32To36_Idempotent()
    {
        Assert.Equal("069a79f4-44e9-4726-a5be-fca90e38aaf5",
            AccountService.FormatUuid("069a79f444e94726a5befca90e38aaf5"));
        Assert.Equal("069a79f4-44e9-4726-a5be-fca90e38aaf5",
            AccountService.FormatUuid("069a79f4-44e9-4726-a5be-fca90e38aaf5")); // 已带横线原样
        Assert.Equal("weird", AccountService.FormatUuid("weird")); // 异常输入原样
    }

    [Fact]
    public void LoginMicrosoft_Uuid_FormattedWithDashes()
    {
        var path = TempStore();
        try
        {
            var svc = new AccountService(path);
            var acc = svc.LoginMicrosoft(new MicrosoftAuth.MicrosoftSession(
                "mc-token", "rt", "069a79f444e94726a5befca90e38aaf5", "Steve"));
            // profile id 是 32 位无横线，游戏 --uuid 要带横线
            Assert.Equal(36, acc.Uuid.Length);
            Assert.Equal("069a79f4-44e9-4726-a5be-fca90e38aaf5", acc.Uuid);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void LoginMicrosoft_ExpiresAt_PersistedAndReloaded()
    {
        var path = TempStore();
        try
        {
            var expires = DateTime.UtcNow.AddHours(20);
            var svc = new AccountService(path);
            svc.LoginMicrosoft(new MicrosoftAuth.MicrosoftSession(
                "mc-token", "rt", "069a79f444e94726a5befca90e38aaf5", "Steve", expires));

            // 落盘 + 重载：过期时间还原（未过期启动直接复用 token，跳过刷新链）
            var reloaded = new AccountService(path);
            reloaded.Load();
            var acc = reloaded.Accounts.Single();
            Assert.NotNull(acc.MsExpiresAtUtc);
            Assert.Equal(expires, acc.MsExpiresAtUtc!.Value);
            Assert.NotNull(reloaded.MicrosoftSession);
            Assert.Equal(expires, reloaded.MicrosoftSession!.ExpiresAtUtc);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ---------- 8-13 Live 设备码流 ----------

    [Fact]
    public async Task StartDeviceCodeAsync_ParsesUserCode()
    {
        var handler = new SequenceHandler().WithJson(
            """{"user_code":"KS7LPEM3","device_code":"dev-1","verification_uri":"https://www.microsoft.com/link","interval":5,"expires_in":900}""");
        var http = new HttpClient(handler);
        var s = await MicrosoftAuth.StartDeviceCodeAsync(http, CancellationToken.None);
        Assert.Equal("KS7LPEM3", s.UserCode);
        Assert.Equal("dev-1", s.DeviceCode);
        Assert.Equal("https://www.microsoft.com/link", s.VerificationUri);
        Assert.Equal(5, s.IntervalSec);
        Assert.Equal(900, s.ExpiresInSec);
        // 发起参数：设备码端点 + MBI_SSL scope + response_type=device_code
        Assert.Contains("oauth20_connect.srf", handler.RequestUris.Single());
        var form = Uri.UnescapeDataString(handler.RequestBodies.Single());
        Assert.Contains("service::user.auth.xboxlive.com::MBI_SSL", form);
        Assert.Contains("response_type=device_code", form);
        Assert.Contains("00000000402b5328", form); // 内置 Java title client_id
    }

    [Fact]
    public async Task StartDeviceCodeAsync_InvalidClient_Throws()
    {
        var handler = new SequenceHandler().WithJson(
            """{"error":"invalid_client","error_description":"The client does not exist"}""");
        var http = new HttpClient(handler);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MicrosoftAuth.StartDeviceCodeAsync(http, CancellationToken.None));
        Assert.Contains("invalid_client", ex.Message);
    }

    [Fact]
    public async Task PollDeviceCodeAsync_Pending_Then_Token()
    {
        var handler = new SequenceHandler()
            .WithJson("""{"error":"authorization_pending","error_description":"wait"}""")
            .WithJson("""{"access_token":"live-at","refresh_token":"live-rt","expires_in":3600}""");
        var http = new HttpClient(handler);
        var session = new MicrosoftAuth.DeviceCodeSession("CODE1", "dev-1", "https://www.microsoft.com/link", 0, 900);
        var (at, rt) = await MicrosoftAuth.PollDeviceCodeAsync(http, session, null, CancellationToken.None);
        Assert.Equal("live-at", at);
        Assert.Equal("live-rt", rt);
        // 轮询参数：device_code grant（RFC 8628 固定 grant type）
        var form = Uri.UnescapeDataString(handler.RequestBodies[0]);
        Assert.Contains("grant_type=urn:ietf:params:oauth:grant-type:device_code", form);
        Assert.Contains("device_code=dev-1", form);
    }

    [Fact]
    public async Task PollDeviceCodeAsync_AccessDenied_Throws()
    {
        var handler = new SequenceHandler().WithJson("""{"error":"access_denied","error_description":"nope"}""");
        var http = new HttpClient(handler);
        var session = new MicrosoftAuth.DeviceCodeSession("CODE1", "dev-1", "", 0, 900);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MicrosoftAuth.PollDeviceCodeAsync(http, session, null, CancellationToken.None));
        Assert.Contains("access_denied", ex.Message);
    }

    [Fact]
    public async Task PollDeviceCodeAsync_Timeout_Throws()
    {
        var handler = new SequenceHandler(); // 默认无限 pending
        var http = new HttpClient(handler);
        var session = new MicrosoftAuth.DeviceCodeSession("CODE1", "dev-1", "", 0, 1); // 1 秒过期
        await Assert.ThrowsAsync<TimeoutException>(() =>
            MicrosoftAuth.PollDeviceCodeAsync(http, session, null, CancellationToken.None));
    }

    [Fact]
    public async Task PollDeviceCodeAsync_Cancel_ThrowsOperationCanceled()
    {
        var handler = new SequenceHandler(); // 默认无限 pending
        var http = new HttpClient(handler);
        var session = new MicrosoftAuth.DeviceCodeSession("CODE1", "dev-1", "", 0, 900);
        using var cts = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MicrosoftAuth.PollDeviceCodeAsync(http, session, null, cts.Token));
    }

    [Fact]
    public async Task AuthenticateMinecraftAsync_RpsTicket_UsesT_Prefix()
    {
        // 完整认证链：user.auth（RPS 交换）→ xsts → login_with_xbox → profile
        var handler = new SequenceHandler()
            .WithJson("""{"Token":"xbl-token"}""")
            .WithJson("""{"Token":"xsts-token","DisplayClaims":{"xui":[{"uhs":"uhs-1"}]}}""")
            .WithJson("""{"access_token":"mc-token","expires_in":7200}""")
            .WithJson("""{"id":"uuid-1","name":"Steve"}""");
        var http = new HttpClient(handler);
        var session = await MicrosoftAuth.AuthenticateMinecraftAsync(http, "live-at", "live-rt", CancellationToken.None);
        Assert.Equal("Steve", session.MinecraftName);
        Assert.Equal("uuid-1", session.MinecraftUuid);
        Assert.Equal("live-rt", session.RefreshToken);
        // expires_in 解析：7200 秒 → 过期时间约 +2h（启动前据此跳过刷新）
        Assert.True(session.ExpiresAtUtc > DateTime.UtcNow.AddHours(1.9));
        Assert.True(session.ExpiresAtUtc < DateTime.UtcNow.AddHours(2.1));
        // MBI_SSL token 的 RPS ticket 必须 t= 前缀（d= 是 AAD access token 的）
        Assert.Contains("\"RpsTicket\":\"t=live-at\"", handler.RequestBodies[0]);
        // Minecraft login 的 identityToken：XBL3.0 x=<uhs>;<xstsToken>
        Assert.Contains("XBL3.0 x=uhs-1;xsts-token", handler.RequestBodies[2]);
    }

    /// <summary>按顺序回放 JSON 响应的 stub（队列空 → 默认 authorization_pending）</summary>
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<Func<string>> _responses = new();

        /// <summary>记录每次请求的 URI（断言端点用）</summary>
        public List<string> RequestUris { get; } = [];

        /// <summary>记录每次请求的 body（断言参数用）</summary>
        public List<string> RequestBodies { get; } = [];

        public SequenceHandler WithJson(string json) { _responses.Enqueue(() => json); return this; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestUris.Add(request.RequestUri?.ToString() ?? "");
            RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            var json = _responses.Count > 0 ? _responses.Dequeue()() : """{"error":"authorization_pending"}""";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            };
        }
    }

    [Fact]
    public void Load_LegacyPlaintextTokens_MigratedOnRead()
    {
        // 8-13 迁移兼容：旧版明文 accounts.json（无 dpapi: 前缀）→ Load 原样读出，下次 Save 自动转密
        var path = TempStore();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                CurrentName = "Steve",
                Accounts = new[]
                {
                    new { Name = "Steve", Uuid = "uuid-1", Type = "microsoft",
                          AccessToken = "legacy-at", RefreshToken = "legacy-rt" },
                },
            }));

            var svc = new AccountService(path);
            svc.Load();
            var acc = svc.Accounts.Single();
            Assert.Equal("legacy-at", acc.AccessToken);   // 旧明文可读（迁移）
            Assert.Equal("legacy-rt", acc.RefreshToken);

            svc.Logout(); // 触发 Save → 转密
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("legacy-at", json);     // 落盘已无明文
            Assert.Contains("dpapi:", json);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
