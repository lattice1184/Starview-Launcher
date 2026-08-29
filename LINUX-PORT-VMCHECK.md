# Starview Linux 移植 — VMware Ubuntu 验证清单

> 2026-08-29。产出：`src/Launcher.App/bin/Release/net10.0/linux-x64/publish/Launcher.App`（Linux x64 单文件，97MB）。

## 准备（VMware）

1. 装 Ubuntu 24.04 LTS（或 22.04）Desktop（GNOME，自带 keyring + libsecret）。
2. 把 `starview-linux-x64-20260829.tar.gz` 拷进 VM，解压：
   ```bash
   tar -xzf starview-linux-x64-20260829.tar.gz
   ```
3. 启动（包内已带执行位，直接跑；`start.sh` 是兜底，会自动 chmod +x 再启动）：
   ```bash
   ./Launcher.App            # 或 ./start.sh
   ```
   8-30 修复：原包 Windows 打的 tar 不带 Unix 执行位（644），解压后 Permission denied——已用 `tar --mode=755` 重打并附 start.sh 兜底。
4. 系统依赖（Avalonia 运行需要）：
   ```bash
   sudo apt install libsecret-tools  # secret-tool（凭据存储）
   # 字体：Avalonia 默认字体走 fontconfig，Ubuntu 自带 Noto CJK 即可显示中文
   ```
5. 联机 TUN 权限（EasyTier）：普通用户需 `CAP_NET_ADMIN`，两种方式：
   - 用 `sudo ./Launcher.App` 启动（root 直通），或
   - 建立 tun 权限组 + 给组加 CAP：`sudo setcap cap_net_admin+ep /usr/sbin/easytier-core`（launcher 下载的模块也可 setcap）
6. **EasyTier Linux SHA256 回填（必要！）**：本机 GitHub 直连故障，`KnownDigests["2.6.4/x86_64/linux"]` 暂为 `"pending"`（安全拒装，不会跑未校验二进制）。在 VM 内（网络正常）下载并计算后填入代码：
   ```bash
   curl -L -o et.zip "https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/easytier-linux-x86_64-v2.6.4.zip"
   sha256sum et.zip   # 填进 EasyTierProvisioningService.cs KnownDigests 的 "2.6.4/x86_64/linux" 值
   ```

## 验证项

| # | 项 | 通过标准 | 状态 |
|---|----|---------|------|
| 1 | GUI 启动 | 主窗口正常渲染，无字体乱码/方块 | |
| 2 | XDG 路径 | `~/.local/share/starview/` 生成 settings.json/logs；`~/.cache/starview/imgcache` 生成 | |
| 3 | 微软账号登录 | 登录框走 OAuth，token 存进 keyring（`secret-tool search` 可见），重启后仍登录态 | |
| 4 | LittleSkin 登录 | 同上，token 不丢 | |
| 5 | Java 探测 | 装 OpenJDK 21 后，Java 自动匹配到（`/usr/lib/jvm` 扫描） | |
| 6 | 下载并启动 MC | 装 1.21.6 Fabric → 启动成功，游戏进主菜单（Linux 用系统 Java 或 Mojang runtime） | |
| 7 | 内存自动分配 | 启动参数 -Xmx 按 `/proc/meminfo` MemAvailable 计算（非 0/非异常） | |
| 8 | 联机 EasyTier | 两 VM 各起一个 launcher → 房主/客机连通（root 或 setcap 后） | |
| 9 | 联机 Terracotta | 下载 linux 模块 → 建房间握手成功（target_os=linux 校验） | |
| 10 | 打开文件夹 | 设置页「打开文件夹」调起文件管理器（xdg-open） | |
| 11 | 崩溃报告复制 | 崩溃窗「复制」用 Avalonia 剪贴板，粘贴出内容 | |
| 12 | 卸载 | 设置页卸载走 sh 脚本，应用自删 | |
| 13 | 日志/崩溃分析 | 启动失败时 latest.log 提取 + 修复按钮工作 | |
| 14 | 反调试 | Release 构建 attach 调试器 → 日志记录 + 静默退出（ptrace 场景 Linux 由 .NET 托管检测覆盖） | |

## 已知边界 / 注意事项

- **Terracotta 锁文件**：已统一到 `$TMPDIR/terracotta/terracotta.lock`（原 Linux 版指向 `%LocalAppData%` 是错路径，8-29 修复）。
- **EasyTier 镜像**：GitHub 直连在当前网络超时，走 `gh-proxy.com` 等镜像；代码候选链已含多镜像。
- **secret-tool 缺失时**：凭据退化明文（代码注释已说明），建议装 libsecret-tools。
- **GB18030 编码**：Encodings.cs 惰性加载 + CodePagesEncodingProvider，Linux 有 UTF-8 兜底。
- **Linux 无 UAC**：提权语义 = sudo/tun 权限组，UI 文案未改（Windows 语义），不影响功能。
- **单文件不含 .so**：libSkiaSharp/libHarfBuzzSharp/libdtls_native 在 publish/ 目录并列，必须随 Launcher.App 一起拷走。
