#!/bin/bash
# 打包：linux-x64 / osx-arm64 / osx-x64 三个 tar.gz（复刻 20260830 结构）
# 用法：bash build-linux-osx-v118.sh [版本号] [日期]（默认 1.1.11 / 今天）
set -e
cd /c/Users/yanka/Desktop/launcher
VERSION="${1:-1.1.11}"
DATE="${2:-$(date +%Y%m%d)}"
ROOT="$(pwd)"
STAGE="$ROOT/.release-$DATE"
rm -rf "$STAGE"; mkdir -p "$STAGE"

# 辅助文件（进包的启动脚本/桌面项/说明）
cat > "$STAGE/start.sh" <<'SH'
#!/bin/sh
# Starview Linux/macOS 启动脚本：修复可执行权限后启动（tar 从 Windows 打出来不带执行位，这里兜底）
# 8-31 兼容 osx bundle：Mac 包应用整份在 Starview.app 里，顶层没有 Launcher.App——有 bundle 走 bundle，否则走散文件
cd "$(dirname "$0")" || exit 1
if [ -x ./Starview.app/Contents/MacOS/Launcher.App ]; then
    chmod +x ./Starview.app/Contents/MacOS/Launcher.App 2>/dev/null
    exec ./Starview.app/Contents/MacOS/Launcher.App "$@"
fi
chmod +x Launcher.App
exec ./Launcher.App "$@"
SH
cat > "$STAGE/启动.command" <<'SH'
#!/bin/sh
# Starview macOS 一键启动
# 自动解除 quarantine（未签名 App 被 macOS 拦） + 修复执行位 + 启动。
# 首次双击若提示「无法验证开发者」：右键 → 打开 → 仍要打开 即可。
cd "$(dirname "$0")" || exit 1
# 8-31 清整个目录 quarantine（含所有 dylib/native 库——此前只清 Launcher.App，库被拦也打不开）
xattr -dr com.apple.quarantine . 2>/dev/null
# 8-31 应用整份在 Starview.app bundle 里（砍掉顶层重复散文件后体积减半）——启动 bundle 内二进制
chmod +x ./Starview.app/Contents/MacOS/Launcher.App 2>/dev/null
echo "正在启动 Starview…"
exec ./Starview.app/Contents/MacOS/Launcher.App "$@"
SH

publish_rid() { # $1=rid → 只 echo 路径到 stdout（状态行进 stderr 防污染）
  echo "=== publish $1 ===" >&2
  dotnet publish src/Launcher.App -c Release -r "$1" --self-contained \
    -p:RollForward=LatestMajor -p:DebugType=None -p:DebugSymbols=false --nologo -v q > "$STAGE/pub-$1.log" 2>&1 || {
      echo "PUBLISH FAIL $1" >&2; tail -20 "$STAGE/pub-$1.log" >&2; exit 1; }
  echo "$ROOT/src/Launcher.App/bin/Release/net10.0/$1/publish"
}

# ---- linux ----
P="$(publish_rid linux-x64)"
cp "$STAGE/start.sh" "$ROOT/Starview.desktop" "$ROOT/使用必看-Linux.txt" "$P/"
tar czf "starview-linux-x64-$DATE.tar.gz" -C "$P" .
echo "linux tar.gz: $(du -h "starview-linux-x64-$DATE.tar.gz" | cut -f1)"

# ---- osx-arm64（完整 .app bundle：整个 publish 拷进 Contents/MacOS）----
# 8-31 修「Mac 打不开」：此前只拷 Launcher.App + lib*.dylib → 缺托管 dll + runtimeconfig/deps
# → 双击 .app 时 apphost 找不到程序集静默失败。改为整个 publish 目录进 MacOS。
P="$(publish_rid osx-arm64)"
mkdir -p "$P/Starview.app/Contents/MacOS"
sed "s/1\.1\.4/${VERSION}/g" "$ROOT/Starview.app.plist" > "$P/Starview.app/Contents/Info.plist"
# 8-31 修自复制：cp -R "$P"/. 会把 Starview.app 目录再拷进自己 → GNU cp 拒绝（cannot copy dir into itself）。
# tar --exclude=Starview.app 也无效（成员名带 ./ 前缀匹配不上）→ find 顶层逐项拷，绝对不拷自身
# （publish 目录的残留 bundle 也一并跳过）
find "$P" -maxdepth 1 -mindepth 1 ! -name 'Starview.app' -exec cp -R {} "$P/Starview.app/Contents/MacOS/" \;
cp "$STAGE/start.sh" "$P/Starview.app/Contents/MacOS/"
# 8-31 砍重复体积：应用已整份进 bundle，顶层再放一份 = osx 包翻倍（100MB vs linux 51MB）——删散文件只留 bundle+辅助
find "$P" -maxdepth 1 -mindepth 1 ! -name 'Starview.app' ! -name 'start.sh' ! -name '启动.command' ! -name '使用必看-Mac.txt' -exec rm -rf {} \;
cp "$STAGE/start.sh" "$STAGE/启动.command" "$ROOT/使用必看-Mac.txt" "$P/"
tar czf "starview-osx-arm64-$DATE.tar.gz" -C "$P" .
echo "osx-arm64 tar.gz: $(du -h "starview-osx-arm64-$DATE.tar.gz" | cut -f1)"

# ---- osx-x64（完整 .app bundle，同 arm64；libdtls_native.dylib 缺 = 已知联机限制，不影响启动）----
P="$(publish_rid osx-x64)"
mkdir -p "$P/Starview.app/Contents/MacOS"
sed "s/1\.1\.4/${VERSION}/g" "$ROOT/Starview.app.plist" > "$P/Starview.app/Contents/Info.plist"
find "$P" -maxdepth 1 -mindepth 1 ! -name 'Starview.app' -exec cp -R {} "$P/Starview.app/Contents/MacOS/" \;   # 同 arm64 修自复制
cp "$STAGE/start.sh" "$P/Starview.app/Contents/MacOS/"
find "$P" -maxdepth 1 -mindepth 1 ! -name 'Starview.app' ! -name 'start.sh' ! -name '启动.command' ! -name '使用必看-Mac.txt' -exec rm -rf {} \;   # 同 arm64 砍重复体积
cp "$STAGE/start.sh" "$STAGE/启动.command" "$ROOT/使用必看-Mac.txt" "$P/"
tar czf "starview-osx-x64-$DATE.tar.gz" -C "$P" .
echo "osx-x64 tar.gz: $(du -h "starview-osx-x64-$DATE.tar.gz" | cut -f1)"

echo "ALL DONE"
