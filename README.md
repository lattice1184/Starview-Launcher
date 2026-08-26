# Starview 启动器

> 本项目由 AI 协助开发（Claude Code 编写大量代码与本文档），作者校对与实测。

> **English TL;DR** — Starview is a self-written Minecraft launcher for Windows (PCL-style UX) that puts its effort into download speed and login simplicity. A file is fetched from **6 sources simultaneously** (official direct link + 2 mirrors + CDN signed link + mirrors of the signed link) — first to finish wins, ranked by per-source speed history. Measured: a 159.8 MB installer in **19.9 s**. Microsoft device-code login (pairing code auto-copied, no Client ID needed), Littleskin one-click auth, and offline skins visible in-game via an auto-injected resource pack (no mods needed). Single-file portable build (~84 MB), double-click to run, no .NET required. Built with Avalonia on .NET 10, Apache-2.0. Issues welcome — Chinese or English.

AI 辅助我斟酌写的 Minecraft 启动器。没想刻意超越谁或者对比谁，只想做个自己的启动器分享出来。

- 完整版自带 .NET 运行时，因此内存占用较大，好处就是双击就能用
- 适配Windows 10/11，遗憾的是这个架构和语言已经天然抛弃了win7用户

## 关于下载

### 六源竞速，一个第三方文件被下载时，将会从6个源中进行竞速并拉取最高者

GitHub 上的文件，启动器会同时从这些源中筛出胜利者并起跑：

- GitHub 官方直链
- ghproxy.net / gh-proxy.com 两个加速镜像源
- GitHub API 换链出的 CDN 签名直链（国内直连）
- 签名直链再套两个镜像

谁先下完用谁，其余取消。候选顺序按各源历史速度自动排列；每一轮的结果都会被记住，随后提高此源的排名优先级

**实测数据**（OBS 32.2.1 安装包，159.8MB，录屏环境下多次重测）：

| 轮次 | 赢家 | 耗时 | 均速 |
|---|---|---|---|
| 1 | CDN 签名直链 | 77.4s | 2.1MB/s |
| 2 | GitHub 官方直链 | 22.9s | 7.0MB/s |
| 3 | 第1轮全源失败自动重赛 → CDN 签名直链 | 163.4s（含 121s 网络抖动） | 3.7MB/s |
| 4 | CDN 签名直链 | 19.9s | 8.0MB/s |
| 5 | CDN 签名直链 | 21.9s | 7.3MB/s |
此为 286Mbps 网络下的测试，真实数据还得看各位的实际使用情况，欢迎反馈到 issues。

### 卡住不干等：三层兜底

- 分片断点续传：中断、换源、重试时已下分片复用，不会从零开始下
- 陪跑：当前赢家掉速到峰值一半，新源在后台提前开跑，超过并稳定三拍才接手，期间主源不会中断数据下载传输
- 卡死处理：低速自动换路（30 秒低于 100KB/s）、静默断流换路、唯一幸存源停滞兜底、下载全程 watchdog
- 完成后会自动清理竞速临时文件

## 登录与皮肤

- **正版（Microsoft）**：设备码配对，点登录自动复制配对码到剪贴板，浏览器粘贴即完成；多账户切换，登录后自动同步头像与皮肤
- **Littleskin**：一条龙登录，没创建过角色会引导去创建；登录就同步皮肤
- **离线**：自定义用户名
- **皮肤**：拖 PNG/JPG 进窗口即换（自动校验格式 64×64 / 64×32）；重置正版账号皮肤 = 强制同步官方皮肤（不是清空）；**离线也能在游戏里看到皮肤**——启动器自动打包资源包注入，不用装任何模组

## 版本与整合包

- 版本安装与启动：原版 / Fabric / Quilt / Forge / NeoForge，多实例隔离
- Java 自动检测，装哪个版本用哪个；想手动指定也行，版本设置里下拉选你要的JAVA版本就好了
- 整合包导入：拖入 .zip / .mrpack 会下载并且出现版本实例
- Forge 下载走国内镜像（BMCLAPI 等），官方断不裸奔

## 模组与资源

- Modrinth + CurseForge 双源搜索，一键装模组（含依赖），跟随实例版本和加载器
- 模组启停、检查更新（已装的模组版本落后会标出来）
- 资源包 / 光影管理

## 开服与联机

- 本地服务端可视化：一键开服、在线玩家列表、踢出 / 封禁 / 授予 OP 图形化操作（封禁名单和 OP 列表直接读写服务器文件，服务端停止时也生效）
- server.properties 图形化编辑（内存、视距、最大玩家数按机器自动推荐）
- 联机：Terracotta 免费 P2P 直连；也支持陶瓦 / EasyTier / 蓝盾这类虚拟局域网——开服页填个「对外地址」，朋友直接在虚拟网里复制连接
- 开服页可以弹成独立窗口，日志实时同步

## 外观

- 强调色预设（靛蓝等 8 色）、自定义背景图、界面密度（紧凑/标准/舒适）、窗口透明度
- 亚克力毛玻璃窗口，背景图片可透出
- 自定义你的启动器样式！

## 内存

- **常驻占用低**：启动器自身稳态工作集约 **70MB 级**（1.1.2 内存瘦身专项——设置页背景预览限宽解码、下载页只预载最常用 tab、关掉一套从未被使用过的空转动画引擎、截图/图标限宽解码 + 换图显式释放）
- 曾提供「一键释放内存 / 工作集修剪」按钮，实测是 `SetProcessWorkingSetSize` 的**假释放**（骗任务管理器、不真省内存），1.1.1 起移除——常驻占用压下来后不需要手动清理

## 工程与安全

- 自动化测试 780+ 项（下载引擎、登录、JSON 解析、版本匹配、模组兼容修复等）
- 发布脚本一键签名（自签名；SmartScreen 提示「更多信息 → 仍要运行」属正常，见下）
- CurseForge 下载：开箱即用，无需任何配置；想用自己的 Key 也可以在设置里填，离开输入框自动加密保护
- GitHub Token：可选配置，同样加密保护

## 安装

去 [Releases](../../releases) 下载，两个版本二选一：

| | 望星.exe | 轻量版 |
|---|---|---|
| 体积 | 约 84MB | 约 47MB |
| 依赖 | 无 | .NET 10 Desktop Runtime（没装会弹窗引导） |
| 适合 | 图省事 | 在意体积/更新快 |

**Windows 拦截说明**：自签名发布者，SmartScreen 提示「更多信息 → 仍要运行」属正常。Win11 新装机会被智能应用控制（SAC）无提示阻止——不信任此启动器的话，可以 Win+S 搜索「智能」，打开「智能应用控制」页面关闭该功能。
## 近期更新

- **v1.1.2** 启动前自动检查模组与游戏版本兼容性，不兼容的自动替换为兼容版（找不到适配版才停用，全自动不弹框、控制台逐步可见）；自动匹配下载严格按游戏版本过滤（fabric 实例也正确，不再装错版本）；崩溃自动修复读取游戏真实日志、换成正确模组；内存瘦身（稳态 ~70MB 级）
- **v1.1.1** 自动修复更透明：启动失败原因弹窗展示，修复自动读取当时日志执行下载
- 加载器覆盖原版：装 Fabric/Forge 后原版自动隐藏合并，删加载器连带清理
- 主页自动选中最新安装的版本：下载模组不再落错实例
- 安装直接选目录：点安装直接弹系统目录选择器，默认指向对应实例 mods，取消即不装
- 修复模组装进错误目录的嵌套路径问题
- 大文件下载「假成功」根治：不再出现「提示完成但文件没落地」（分片合并阶段误判已修复）
- 下载历史「位置」按钮支持目录：模组/整合包安装记录直接定位到 mods 文件夹
- 首次设置游戏目录后主页版本立即刷新，更换目录后版本页自动跟随（删除文件秒同步）
- 设置-关于新增赞助者名单，感谢每一位支持者
- 下载完成的文件直接装进当前版本的 mods 文件夹，不再落到缓存区找不到
- 模组中文搜索变快了：默认走镜像加速，搜「优化」「小地图」不再干等
- 日志中心：下载/启动/修复分三类整理，启动器里直接展开看，不用开记事本
- 下载完成弹 2 秒提示，点「查看日志」直接跳到那条记录
- 修复补全统一装到当前实例，删了文件启动能自动找回
- 官网顶部品牌开场动画 + 全面换装 Starview 品牌

## 构建

```bash
dotnet build            # Debug 构建
```

发布（Windows）：`dotnet publish` 后运行签名脚本，产物在 `发布\`：`望星.exe`（自包含，双击即用）与 `望星-轻量版.exe`（需 .NET 10 Runtime）。

## 目录结构

```
src/          # 源码（Launcher.App / Launcher.Core / Launcher.Animation / Tests）
PCL.Core/     # vendored PCL-CE 核心库（Apache-2.0，见 NOTICE）
```

## 许可

- `PCL.Core/`：Apache License 2.0（来自 PCL-CE，见 NOTICE）
- `src/`：本项目原创，Apache License 2.0
- 第三方依赖清单以及开源见设置页「关于」

## 反馈

目前仍在努力的找——修——测试，如果有任何问题或者功能建议，欢迎反馈到 issues，会尽力改以确保各位的舒适体验:)
