## Starview v1.1.3

### 新增

- **Linux 全平台支持**：XDG 路径服务、Java 自动探测、联机模块（EasyTier / Terracotta）平台化、凭据存储（libsecret）
- **模组搜索关键词匹配**：不用输全名，「机械动」也能搜出「机械动力」
- **启动链路日志**：点启动无反应时可查 `~/.local/share/starview/logs/launch-*.log` 直接定位断点
- **启动拦截点显性化**：无版本 / 未登录账号点启动会弹提示，按钮悬停有说明

### 变更

- 移除开服模块（全平台彻底删）
- 发布附 Linux x64 验证清单（`LINUX-PORT-VMCHECK.md`）

### Linux 使用

```bash
tar -xzf starview-linux-x64-20260830.tar.gz
sudo apt install libsecret-tools   # 凭据存储（secret-tool）
./Launcher.App                     # 或 ./start.sh
```

系统依赖：Avalonia 需要 X11 库（`libICE.so.6` / `libSM` / `libX11`）；EasyTier 联机需 `root` 或 tun 权限组。
