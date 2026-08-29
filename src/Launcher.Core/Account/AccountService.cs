using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Core.Download;

namespace Launcher.Core.Account;

/// <summary>
/// 账号服务：离线账号（UUID v3）+ 多账号持久化 + 当前账号切换。
/// 微软正版登录为后续扩展（F4b，需 ClientId）。
/// </summary>
public sealed class AccountService
{
    /// <summary>进程级共享实例（账号页/主页/启动链路统一读同一状态，跨页面实时同步）</summary>
    public static AccountService Shared { get; } = new();

    /// <summary>账号状态变化（登录/切换/删除/退出）——主页玩家区等订阅实时刷新</summary>
    public event Action? Changed;

    private readonly string _storePath;

    public AccountInfo? Current { get; private set; }

    /// <summary>微软会话（当前账号为正版时；含 access/refresh token）</summary>
    public MicrosoftAuth.MicrosoftSession? MicrosoftSession { get; private set; }

    /// <summary>全部已保存账号（离线多账号；正版接入后并入）</summary>
    public IReadOnlyList<AccountInfo> Accounts => _accounts;

    private List<AccountInfo> _accounts = [];

    public AccountService(string? storePath = null)
    {
        _storePath = storePath ?? Path.Combine(
            Launcher.Core.Utils.AppPaths.DataRoot, "accounts.json");
    }

    public AccountInfo LoginOffline(string name)
    {
        var uuid = OfflineUuid(name);
        var acc = new AccountInfo(name, uuid, "offline");
        // 重名覆盖（同账号不重复存）；新名追加
        var existing = _accounts.FindIndex(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) _accounts[existing] = acc;
        else _accounts.Add(acc);
        Current = acc;
        Save();
        Changed?.Invoke();
        return acc;
    }

    /// <summary>8-13 Littleskin 第三方登录：按皮肤站角色保存（type=littleskin；
    /// 皮肤 PNG 由 LittleSkinSkinSync 登录时同步到 skins/{name}.png——SkinPack 注入条件）</summary>
    public AccountInfo LoginLittleskin(string name, string uuid)
    {
        var acc = new AccountInfo(name, FormatUuid(uuid), "littleskin");
        var existing = _accounts.FindIndex(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) _accounts[existing] = acc;
        else _accounts.Add(acc);
        Current = acc;
        Save();
        Changed?.Invoke();
        return acc;
    }

    /// <summary>正版登录：保存微软会话为当前账号（type=microsoft；refresh token 持久化供静默刷新）</summary>
    public AccountInfo LoginMicrosoft(MicrosoftAuth.MicrosoftSession session)
    {
        // 8-13：profile id 是 32 位无横线 hex，游戏 --uuid 要带横线（离线 UUID 本来就带横线，统一）
        var acc = new AccountInfo(session.MinecraftName, FormatUuid(session.MinecraftUuid), "microsoft",
            session.AccessToken, session.RefreshToken, session.ExpiresAtUtc);
        var existing = _accounts.FindIndex(a => a.Name.Equals(session.MinecraftName, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) _accounts[existing] = acc;
        else _accounts.Add(acc);
        Current = acc;
        MicrosoftSession = session;
        Save();
        Changed?.Invoke();
        return acc;
    }

    /// <summary>静默刷新正版会话（access token 过期时调用；失败抛异常 → UI 引导重新登录）</summary>
    public async Task<MicrosoftAuth.MicrosoftSession> RefreshMicrosoftAsync(CancellationToken ct = default)
    {
        if (Current?.Type != "microsoft" || Current.RefreshToken is not { } rt)
            throw new InvalidOperationException("当前不是正版账号");
        var http = HttpClientPool.CreateSharedClient();
        // 8-13 刷新前解析 clientId（远程下发/缓存/兜底三层——新进程首刷时生效值就绪）
        await ClientIdRemote.ResolveAsync(http, ct);
        var session = await MicrosoftAuth.RefreshAsync(http, rt, ct);
        MicrosoftSession = session;
        // 轮换保存新 refresh token + 新 Minecraft token 过期时间
        Current = new AccountInfo(session.MinecraftName, FormatUuid(session.MinecraftUuid), "microsoft",
            session.AccessToken, session.RefreshToken, session.ExpiresAtUtc);
        var idx = _accounts.FindIndex(a => a.Name.Equals(session.MinecraftName, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) _accounts[idx] = Current;
        Save();
        Changed?.Invoke();
        return session;
    }

    /// <summary>切换当前账号（按名称；不存在则忽略）</summary>
    public bool SwitchTo(string name)
    {
        var acc = _accounts.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (acc is null) return false;
        Current = acc;
        Save();
        Changed?.Invoke();
        return true;
    }

    /// <summary>删除账号（当前账号被删则退出登录）</summary>
    public bool Delete(string name)
    {
        var removed = _accounts.RemoveAll(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed && Current?.Name.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
            Current = null;
        if (removed)
        {
            Save();
            Changed?.Invoke();
        }
        return removed;
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var json = File.ReadAllText(_storePath);
            var saved = JsonSerializer.Deserialize<StoredState>(json);
            if (saved is null) return;
            _accounts = (saved.Accounts ?? [])
                // 8-13 token DPAPI 解密（无前缀旧明文原样返回——迁移兼容，下次保存自动转密）
                .Select(a => new AccountInfo(a.Name, a.Uuid, a.Type,
                    Launcher.Core.Utils.Secrets.Read(a.AccessToken),
                    Launcher.Core.Utils.Secrets.Read(a.RefreshToken),
                    a.MsExpiresAtUtc))
                .ToList();
            Current = saved.CurrentName is { } cur
                ? _accounts.FirstOrDefault(a => a.Name == cur)
                : null;
            if (Current?.Type == "microsoft" && Current.RefreshToken is { } rt)
            {
                MicrosoftSession = new MicrosoftAuth.MicrosoftSession(
                    Current.AccessToken ?? "", rt, Current.Uuid, Current.Name,
                    Current.MsExpiresAtUtc ?? default);
            }
        }
        catch (Exception) { /* 存储损坏则忽略 */ }
    }

    public void Logout()
    {
        Current = null;
        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            var stored = new StoredState(
                Current?.Name,
                // 8-13 token DPAPI 加密落盘（不再明文——accounts.json 拷走也解不开）
                _accounts.Select(a => new StoredAccount(a.Name, a.Uuid, a.Type,
                    Launcher.Core.Utils.Secrets.Protect(a.AccessToken),
                    Launcher.Core.Utils.Secrets.Protect(a.RefreshToken),
                    a.MsExpiresAtUtc)).ToList());
            File.WriteAllText(_storePath, JsonSerializer.Serialize(stored));
        }
        catch (Exception) { /* 存储失败不阻塞登录 */ }
    }

    /// <summary>离线 UUID v3：MD5("OfflinePlayer:" + name) 按 Java UUID.nameUUIDFromBytes 格式</summary>
    public static string OfflineUuid(string name)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30); // version 3
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant
        var hex = Convert.ToHexStringLower(bytes);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    /// <summary>8-13 32 位无横线 profile id → 8-4-4-4-12 带横线（游戏 --uuid 要求；已带横线原样返回）</summary>
    public static string FormatUuid(string profileId)
    {
        if (profileId.Length == 36) return profileId;
        if (profileId.Length == 32)
            return $"{profileId[..8]}-{profileId[8..12]}-{profileId[12..16]}-{profileId[16..20]}-{profileId[20..]}";
        return profileId;
    }

    public sealed record AccountInfo(string Name, string Uuid, string Type,
        string? AccessToken = null, string? RefreshToken = null, DateTime? MsExpiresAtUtc = null);

    private sealed record StoredState(string? CurrentName, List<StoredAccount> Accounts);
    private sealed record StoredAccount(string Name, string Uuid, string Type,
        string? AccessToken = null, string? RefreshToken = null, DateTime? MsExpiresAtUtc = null);
}
