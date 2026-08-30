using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Services;
using Launcher.Core.Account;
using PCL.Core.Logging;

namespace Launcher.App.ViewModels;

/// <summary>账号行（列表展示 + 切换/删除）</summary>
public sealed record AccountRowVM(string Name, string TypeText, bool IsCurrent);

/// <summary>
/// 账号页：离线登录 + 多账号列表（切换/删除）+ 当前账号卡片。微软正版登录入口预留。
/// </summary>
public partial class AccountViewModel : ViewModelBase
{
    private readonly AccountService _accounts = AccountService.Shared;

    [ObservableProperty]
    public partial string NameInput { get; set; } = "";

    [ObservableProperty]
    public partial string CurrentName { get; set; } = "未登录";

    [ObservableProperty]
    public partial string CurrentUuid { get; set; } = "";

    [ObservableProperty]
    public partial string AccountType { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLoggedIn { get; set; }

    /// <summary>8-13 当前账号是正版（显示「正版账号管理」入口用）</summary>
    [ObservableProperty]
    public partial bool IsMicrosoftAccount { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial Bitmap? Avatar { get; set; }

    /// <summary>8-13 头像未就绪时的首字母占位（弹窗面板头像不露白）</summary>
    [ObservableProperty]
    public partial string AvatarFallback { get; set; } = "";

    /// <summary>8-31 当前账号是离线类型（显示「改名」按钮——正版/LittleSkin 名字是平台身份不改）</summary>
    [ObservableProperty]
    public partial bool IsOfflineAccount { get; set; }

    /// <summary>8-31 离线账号改名：是否处于内联改名态（当前账号行变输入框）</summary>
    [ObservableProperty]
    public partial bool IsRenamingOffline { get; set; }

    /// <summary>8-31 离线账号改名输入框内容（进入改名态时预填当前名）</summary>
    [ObservableProperty]
    public partial string RenameInput { get; set; } = "";

    /// <summary>已保存账号列表（当前账号标记）</summary>
    public ObservableCollection<AccountRowVM> Accounts { get; } = [];

    public AccountViewModel()
    {
        _accounts.Load();
        Refresh();
    }

    private void Refresh()
    {
        var acc = _accounts.Current;
        IsLoggedIn = acc is not null;
        IsMicrosoftAccount = acc?.Type == "microsoft";
        IsOfflineAccount = acc?.Type == "offline";
        IsRenamingOffline = false; // 刷新时退出改名态（切号/删除等）
        CurrentName = acc?.Name ?? "未登录";
        CurrentUuid = acc?.Uuid ?? "";
        AccountType = TypeTextOf(acc);
        if (acc is not null) NameInput = acc.Name;
        // 8-13 账号页改版：无账号时登录表单直接展开；有账号默认收起（点「添加账号」再展开）
        if (acc is null) IsLoginFormVisible = true;

        Accounts.Clear();
        foreach (var a in _accounts.Accounts)
            Accounts.Add(new AccountRowVM(a.Name,
                a.Type == "microsoft" ? "正版" : a.Type == "littleskin" ? "Littleskin" : "离线",
                a.Name == acc?.Name));

        // 玩家头像（8-19：正版/离线走 minotar 渲染；LittleSkin 走 yggdrasil 纹理——minotar 对非
        // Mojang 名返回 Steve 默认图，头像永不更新）。8-13：不置空——加载期间保留旧头像，首字母块兜底
        AvatarFallback = acc is null || string.IsNullOrEmpty(acc.Name) ? "" : acc.Name[..1].ToUpperInvariant();
        if (acc is not null)
        {
            if (acc.Type == "littleskin")
            {
                // 8-19 LittleSkin 弹窗头像：/skin/{name}.png 实测 404，走 profile 解析真纹理 URL
                var lsUuid = acc.Uuid ?? "";
                _ = Task.Run(async () =>
                {
                    using var http = Launcher.Core.Download.HttpClientPool.CreateSharedClient(TimeSpan.FromSeconds(8));
                    var url = await Launcher.Core.Account.LittleSkinSkinSync.ResolveTextureUrlAsync(http, lsUuid);
                    if (string.IsNullOrEmpty(url)) return;
                    ImageLoader.LoadAsync(url, bmp => Avatar = bmp);
                });
            }
            else
            {
                _ = ImageLoader.LoadAsync($"https://minotar.net/helm/{Uri.EscapeDataString(acc.Name)}/64.png",
                    bmp => Avatar = bmp);
            }
        }
    }

    /// <summary>8-13 账号类型文本（微软正版 / Littleskin / 离线 / 空）</summary>
    private static string TypeTextOf(AccountService.AccountInfo? acc)
        => acc?.Type == "microsoft" ? "正版账号"
            : acc?.Type == "littleskin" ? "Littleskin"
            : acc?.Type == "offline" ? "离线账号" : "";

    [RelayCommand]
    private void LoginOffline()
    {
        var name = NameInput.Trim();
        if (string.IsNullOrEmpty(name)) { Status = "你还没填用户名"; return; }
        _accounts.LoginOffline(name);
        IsLoginFormVisible = false; // 登录成功收起表单
        Status = $"已登录 {name}";
        Refresh();
    }

    // ---------- 8-31 离线账号改名（自动 Player 允许改成自己的名字）----------

    /// <summary>进入改名态：输入框预填当前名（改名 = 换离线身份，UUID 由名字派生）</summary>
    [RelayCommand]
    private void StartRenameOffline()
    {
        RenameInput = CurrentName;
        IsRenamingOffline = true;
    }

    /// <summary>确认改名：校验 + 重建账号；失败原因进 Status（面板下方展示）</summary>
    [RelayCommand]
    private void CommitRenameOffline()
    {
        var oldName = CurrentName;
        var error = _accounts.RenameOffline(oldName, RenameInput);
        if (error is not null) { Status = error; return; }
        Status = $"已改名为 {RenameInput.Trim()}";
        Refresh();
    }

    /// <summary>取消改名（退出内联输入态）</summary>
    [RelayCommand]
    private void CancelRenameOffline() => IsRenamingOffline = false;

    /// <summary>切换账号（点击列表行）</summary>
    [RelayCommand]
    private void SwitchAccount(AccountRowVM row)
    {
        if (_accounts.SwitchTo(row.Name))
        {
            Status = $"已切换到 {row.Name}";
            Refresh();
        }
    }

    /// <summary>删除账号（DialogService 确认；当前账号被删则退出）</summary>
    [RelayCommand]
    private async Task DeleteAccount(AccountRowVM row)
    {
        var owner = DialogService.MainWindow();
        if (owner is null || !await DialogService.Confirm(owner,
                $"删除账号「{row.Name}」？删了就找不回来了。", "删除账号", "删除", "取消"))
        {
            return;
        }
        if (_accounts.Delete(row.Name))
        {
            Status = $"已删除 {row.Name}";
            Refresh();
        }
    }

    [RelayCommand]
    private async Task Logout()
    {
        if (IsLoggedIn && DialogService.MainWindow() is { } owner)
        {
            if (!await DialogService.Confirm(owner,
                    "退出当前账号？", "退出登录", "退出", "取消"))
            {
                return;
            }
        }
        _accounts.Logout();
        Status = "已退出登录";
        Refresh();
    }

    [ObservableProperty]
    public partial bool IsMsAuthBusy { get; set; }

    /// <summary>微软登录进度（等待浏览器授权 / 认证中）</summary>
    [ObservableProperty]
    public partial string MsAuthStatus { get; set; } = "";

    /// <summary>8-13 设备码登录：配对码（微软服务器生成，8 位）大字显示，用户在浏览器里输入</summary>
    [ObservableProperty]
    public partial string DeviceCodeText { get; set; } = "";

    /// <summary>浏览器输码页地址（重新打开网页按钮用；默认 microsoft.com/link）</summary>
    [ObservableProperty]
    public partial string DeviceCodeVerifyUri { get; set; } = "";

    /// <summary>是否处于设备码等待状态（显示配对码 + 重开网页/取消按钮）</summary>
    [ObservableProperty]
    public partial bool IsDeviceCodeMode { get; set; }

    // ---------- 8-13 账号页改版：登录方式分割（PCL 式）+ Littleskin 登录 + 正版管理入口 ----------

    /// <summary>登录方式（正版 / 离线 / Littleskin 三态分割）</summary>
    public enum LoginModeKind { Microsoft, Offline, Littleskin }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMicrosoftMode))]
    [NotifyPropertyChangedFor(nameof(IsOfflineMode))]
    [NotifyPropertyChangedFor(nameof(IsLittleskinMode))]
    // 8-31 默认离线登录：新用户首次添加账号落在「离线登录」tab（不再是正版登录，找不到入口以为没法登录）
    public partial LoginModeKind LoginMode { get; set; } = LoginModeKind.Offline;

    public bool IsMicrosoftMode => LoginMode == LoginModeKind.Microsoft;
    public bool IsOfflineMode => LoginMode == LoginModeKind.Offline;
    public bool IsLittleskinMode => LoginMode == LoginModeKind.Littleskin;

    /// <summary>登录表单区是否展开（未登录默认展开；登录成功后收起；「添加账号」再展开）</summary>
    [ObservableProperty]
    public partial bool IsLoginFormVisible { get; set; } = true;

    /// <summary>8-13 添加账号按钮：展开/收起登录表单（按钮永不禁用——「点不到」修复）</summary>
    [RelayCommand]
    private void ToggleLoginForm() => IsLoginFormVisible = !IsLoginFormVisible;

    /// <summary>切换登录方式（按钮 CommandParameter 传 offline/littleskin/microsoft）</summary>
    [RelayCommand]
    private void SetLoginMode(string mode) =>
        LoginMode = mode switch
        {
            "offline" => LoginModeKind.Offline,
            "littleskin" => LoginModeKind.Littleskin,
            _ => LoginModeKind.Microsoft,
        };

    /// <summary>8-19 重设计：LittleSkin 登录 = 一次浏览器授权搞定一切。
    /// client_id 内置默认 1504（设置页可改）——不用填；授权后 token 存入 LittleSkinTokenStore
    /// （登录即连接：皮肤库窗口直接可用，不再二次配对）。</summary>
    [RelayCommand]
    private async Task LoginLittleskin()
    {
        if (IsMsAuthBusy) return;
        // 8-19 client_id 已内部化（Load 空值回填内置默认）——此防御理论不可达，纯保险
        var clientId = Launcher.Core.Utils.LauncherSettings.Current.LittleSkinClientId;
        if (string.IsNullOrWhiteSpace(clientId))
            clientId = Launcher.Core.Account.LittleSkinOAuth.DefaultClientId;
        IsMsAuthBusy = true;
        Status = "";
        try
        {
            using var http = Launcher.Core.Download.HttpClientPool.CreateSharedClient(TimeSpan.FromSeconds(30));
            Status = "正在发起 LittleSkin 授权…";
            var session = await Launcher.Core.Account.LittleSkinOAuth.StartDeviceCodeAsync(
                http, clientId, CancellationToken.None);
            DeviceCodeText = session.UserCode;
            DeviceCodeVerifyUri = string.IsNullOrEmpty(session.VerificationUriComplete)
                ? session.VerificationUri : session.VerificationUriComplete;
            IsDeviceCodeMode = true; // 复用设备码 UI（配对码大字 + 自动复制 + 重开网页/取消）
            Status = "在打开的网页里输入配对码并授权（配对码已自动复制）";
            try { Process.Start(new ProcessStartInfo(DeviceCodeVerifyUri) { UseShellExecute = true }); }
            catch { /* 无法自动打开则手动访问 */ }

            // 轮询授权（设备码 UI 的「取消登录」按钮随时可取消）
            _deviceCts = new CancellationTokenSource();
            var tokens = await Launcher.Core.Account.LittleSkinOAuth.PollDeviceCodeAsync(
                http, clientId, session, s => Status = s, _deviceCts.Token);

            // 授权成功：先存 token（登录即连接——皮肤库窗口读同一 store 直接可用，免二次配对）
            Launcher.Core.Account.LittleSkinTokenStore.Shared.Save(tokens);
            // 取角色 → 登录为游戏账号（与皮肤库「连接即登录」同一链路）
            Status = "授权成功，正在同步账号…";
            var api = new Launcher.Core.Account.LittleSkinApi(http, () => tokens.AccessToken);
            var players = await api.GetPlayersAsync(CancellationToken.None);
            var name = players.FirstOrDefault()?.Name;
            if (string.IsNullOrEmpty(name))
            {
                // 账号没角色：引导创建（LittleSkin 平台规则：登录角色由平台管理）
                IsDeviceCodeMode = false;
                DeviceCodeText = "";
                Status = "LittleSkin 账号还没有游戏角色，先去创建";
                NotificationService.Error("这个账号没有游戏角色，去 littleskin.cn 创建");
                try { Process.Start(new ProcessStartInfo("https://littleskin.cn/user/player") { UseShellExecute = true }); }
                catch { /* 打不开则用户手动访问 */ }
                return;
            }
            var uuid = await api.GetUuidByNameAsync(name, CancellationToken.None) ?? "";
            if (uuid.Length == 0)
            {
                // 8-19 不再回退假 uuid（MD5 离线式会污染进服身份/皮肤链路）——失败明示重试
                IsDeviceCodeMode = false;
                DeviceCodeText = "";
                Status = "获取角色 UUID 失败（LittleSkin 接口异常），稍后重试";
                NotificationService.Error("获取角色 UUID 失败，登录未完成。稍后重新点一次登录即可");
                return;
            }
            _accounts.LoginLittleskin(name, uuid);
            // 8-19 回归修复：登录即同步皮肤到本地（SkinPack 注入条件 = 本地文件存在——
            // 旧邮箱流程有下载、OAuth 重构丢失，不下载则游戏内永远是默认 Steve/Alex）
            try
            {
                if (!await Launcher.Core.Account.LittleSkinSkinSync.DownloadToLocalAsync(http, name, uuid))
                    Status = $"已登录 Littleskin {name}（皮肤同步失败，游戏内暂时是默认皮肤）";
            }
            catch { /* 皮肤同步失败不阻塞登录 */ }
            IsDeviceCodeMode = false;
            DeviceCodeText = "";
            IsLoginFormVisible = false;
            Status = $"已登录 Littleskin {name}";
            NotificationService.Success($"LittleSkin 账号 {name} 登录成功");
            Refresh();
        }
        catch (OperationCanceledException)
        {
            IsDeviceCodeMode = false;
            DeviceCodeText = "";
            Status = _deviceCts?.IsCancellationRequested == true ? "已取消登录" : "登录超时，请重新发起";
        }
        catch (Exception ex)
        {
            IsDeviceCodeMode = false;
            DeviceCodeText = "";
            Status = ex.Message;
            NotificationService.Error($"LittleSkin 登录失败: {ex.Message}");
        }
        finally
        {
            _deviceCts?.Dispose();
            _deviceCts = null;
            IsMsAuthBusy = false;
        }
    }

    /// <summary>8-13 注册 Littleskin 账号（没账号的一条龙入口）</summary>
    [RelayCommand]
    private void OpenLittleskinRegister()
    {
        try { Process.Start(new ProcessStartInfo("https://littleskin.cn/auth/register") { UseShellExecute = true }); }
        catch { NotificationService.Error("无法打开浏览器，请手动访问 littleskin.cn/auth/register"); }
    }

    /// <summary>8-16 批次 51：皮肤库改为内置窗口（SkinLibraryWindow，HomeView.axaml code-behind 打开）——此命令废弃</summary>

    /// <summary>8-13 正版账号管理：跳 Minecraft 官网（皮肤/披风/用户名——Mojang 不开放 API，只能官网改）</summary>
    [RelayCommand]
    private void OpenMojangProfile()
    {
        try { Process.Start(new ProcessStartInfo("https://www.minecraft.net/msaprofile") { UseShellExecute = true }); }
        catch { NotificationService.Error("无法打开浏览器，请手动访问 minecraft.net/msaprofile"); }
    }

    /// <summary>8-19 设备码登录统一取消源（微软正版 + LittleSkin 共用——设备码 UI 的「取消登录」按钮）</summary>
    private CancellationTokenSource? _deviceCts;

    /// <summary>微软正版登录（8-13 Live 设备码流）：配对码 → 浏览器输码登录 → 轮询拿 token → 认证链</summary>
    [RelayCommand]
    private async Task LoginMicrosoft()
    {
        if (IsMsAuthBusy) return;
        IsMsAuthBusy = true;
        Status = "";
        // 8-14：点击后立即给反馈 + 全链路日志——此前设备码会话建立前 MsAuthStatus 空白，
        // 微软服务器连接慢/超时时用户看到「点了没反应」（实为网络请求无反馈），日志也查不到
        MsAuthStatus = "正在连接微软服务器…";
        LogWrapper.Info("[账号] 发起正版登录（设备码流）");
        try
        {
            // 8-13 连接复用：SharedHandler 池化 TCP/TLS（每次登录新建 HttpClient 会白付握手 ~几百 ms）
            using var http = Launcher.Core.Download.HttpClientPool.CreateSharedClient(TimeSpan.FromSeconds(30));

            // 0. 解析 clientId（远程下发/缓存/兜底三层）——登录前保证生效值就绪
            await ClientIdRemote.ResolveAsync(http, CancellationToken.None);

            // 1. 发起设备码会话 → 显示配对码 + 打开浏览器输码页
            LogWrapper.Info("[账号] clientId 就绪，请求设备码会话");
            var session = await MicrosoftAuth.StartDeviceCodeAsync(http, CancellationToken.None);
            LogWrapper.Info($"[账号] 设备码已获取 {session.UserCode}，已打开浏览器等待授权");
            DeviceCodeText = session.UserCode;
            DeviceCodeVerifyUri = session.VerificationUri.Length > 0 ? session.VerificationUri : "https://www.microsoft.com/link";
            IsDeviceCodeMode = true;
            // 8-13：配对码由视图层监听 DeviceCodeText 自动复制到剪贴板（浏览器弹太快来不及看/复制）
            MsAuthStatus = "在打开的网页里输入配对码并登录（配对码已自动复制）";
            try { Process.Start(new ProcessStartInfo(DeviceCodeVerifyUri) { UseShellExecute = true }); }
            catch { /* 无法自动打开则手动访问 microsoft.com/link */ }

            // 2. 轮询等授权（可取消）→ 认证链（分步状态反馈，缓解同步慢的体感）
            _deviceCts = new CancellationTokenSource();
            LogWrapper.Info("[账号] 设备码轮询等待授权…");
            var (oauthToken, refreshToken) = await MicrosoftAuth.PollDeviceCodeAsync(
                http, session, status => MsAuthStatus = status, _deviceCts.Token);
            LogWrapper.Info("[账号] 授权完成，走认证链（Xbox→XSTS→Minecraft）");
            var msSession = await MicrosoftAuth.AuthenticateMinecraftAsync(
                http, oauthToken, refreshToken, _deviceCts.Token,
                stage => MsAuthStatus = stage);
            _accounts.LoginMicrosoft(msSession);
            LogWrapper.Info($"[账号] 正版登录成功：{msSession.MinecraftName}");
            MsAuthStatus = "";
            IsDeviceCodeMode = false;
            DeviceCodeText = "";
            IsLoginFormVisible = false; // 登录成功收起表单
            Status = $"已以正版账号 {msSession.MinecraftName} 登录";
            NotificationService.Success($"正版账号 {msSession.MinecraftName} 登录成功");
            Refresh();
        }
        catch (OperationCanceledException)
        {
            MsAuthStatus = "";
            IsDeviceCodeMode = false;
            DeviceCodeText = "";
            Status = _deviceCts?.IsCancellationRequested == true
                ? "已取消登录"
                : "登录超时，请重新发起";
        }
        catch (Exception ex)
        {
            MsAuthStatus = "";
            IsDeviceCodeMode = false;
            DeviceCodeText = "";
            Status = $"登录失败: {ex.Message}";
            NotificationService.Error($"微软登录失败: {ex.Message}");
            LogMsError(ex.ToString());
        }
        finally
        {
            _deviceCts?.Dispose();
            _deviceCts = null;
            IsMsAuthBusy = false;
        }
    }

    /// <summary>8-13 重新打开输码网页（浏览器被关掉后不用重新发起登录；微软/LittleSkin 共用）</summary>
    [RelayCommand]
    private void ReopenLoginPage()
    {
        if (DeviceCodeVerifyUri.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(DeviceCodeVerifyUri) { UseShellExecute = true }); }
        catch { NotificationService.Error("无法打开浏览器，请手动访问链接页面"); }
    }

    /// <summary>8-13 取消设备码登录（停止轮询，收起等待区；微软/LittleSkin 共用）</summary>
    [RelayCommand]
    private void CancelMsLogin() => _deviceCts?.Cancel();

    /// <summary>微软登录错误落盘（AppData\Launcher\logs\microsoft-auth.log）——下次失败可回看原因</summary>
    private static void LogMsError(string detail)
    {
        try
        {
            var dir = Path.Combine(Launcher.Core.Utils.AppPaths.DataRoot, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "microsoft-auth.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {detail}{Environment.NewLine}");
        }
        catch { }
    }
}
