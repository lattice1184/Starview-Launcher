# ============================================================
#  Starview 三平台一键打包 + 桌面收集 + 残留清理 + 旧版自动删除
#  产物 → 桌面\Starview发布\v{版本}\（win 两个 exe + 使用说明 + linux/osx 三个 tar.gz）
#  用法：powershell -ExecutionPolicy Bypass -File 打3平台包.ps1 [版本号]
#        版本号缺省 = 读 src\Launcher.App\metadata.json（发布前先改这里）
#  自动行为：
#    · 调发布.ps1（win 自包含 + 轻量版 + 签名，自动杀运行中的启动器）
#    · 调 build-linux-osx-v118.sh（linux-x64 / osx-arm64 / osx-x64）
#    · 清构建残留（.release-* 暂存目录、发布\stage、根目录旧 tar.gz）
#    · 删桌面 Starview发布 下其它版本文件夹（有新版自动删旧版）
# ============================================================
param(
    [string]$Version = "",
    [string]$OutputRoot = "C:\Users\yanka\Desktop\Starview发布"   # 8-31 用户指定默认发布目录（可 -OutputRoot 覆盖）
)
$ErrorActionPreference = "Stop"
$root    = Split-Path -Parent $MyInvocation.MyCommand.Path   # scripts\
$launcher = Split-Path -Parent $root                          # launcher\
$version = if ($Version) {
    $Version
} else {
    (Get-Content (Join-Path $launcher "src\Launcher.App\metadata.json") -Raw | ConvertFrom-Json).version.base
}
$date   = Get-Date -Format "yyyyMMdd"
$pub    = $OutputRoot
$dest   = Join-Path $pub "v$version"

Write-Host ""
Write-Host "=== Starview 三平台打包 v$version（$date）===" -ForegroundColor Cyan

# 0) 先清桌面旧版本（有新版产物自动删旧版——在构建前就删，桌面始终只有最新一版）
if (Test-Path $pub) {
    $old = Get-ChildItem $pub -Directory | Where-Object { $_.Name -ne "v$version" }
    if ($old) { $old | ForEach-Object { Write-Host "删旧版: $($_.FullName)" -ForegroundColor Yellow; Remove-Item $_.FullName -Recurse -Force } }
} else { New-Item -ItemType Directory -Path $pub -Force | Out-Null }

# 1) win 构建（发布.ps1：自包含 + 轻量版 + 签名 + 自动杀进程）
Write-Host "[1/4] Windows 构建（发布.ps1，约 3-5 分钟）..." -ForegroundColor Cyan
Push-Location $launcher
try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $launcher "发布.ps1")
    if ($LASTEXITCODE -ne 0) { throw "发布.ps1 失败（exit $LASTEXITCODE）" }
} finally { Pop-Location }

# 2) linux / osx 构建（bash：linux-x64 + osx-arm64 + osx-x64 三个 tar.gz）
Write-Host "[2/4] Linux/macOS 构建（约 5-8 分钟）..." -ForegroundColor Cyan
Push-Location $launcher
try {
    bash scripts/build-linux-osx-v118.sh $version $date
    if ($LASTEXITCODE -ne 0) { throw "linux/osx 打包失败（exit $LASTEXITCODE）" }
} finally { Pop-Location }

# 3) 汇到桌面（win 产物 + 三个 tar.gz + 使用说明）
Write-Host "[3/4] 收集到 $dest ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $dest -Force | Out-Null
Copy-Item (Join-Path $launcher "发布\望星.exe") $dest
Copy-Item (Join-Path $launcher "发布\望星-轻量版.exe") $dest
Copy-Item (Join-Path $launcher "发布\使用说明.txt") $dest
Copy-Item (Join-Path $launcher "使用必看-Linux.txt") $dest
Copy-Item (Join-Path $launcher "使用必看-Mac.txt") $dest
# 本次 tar.gz 移入桌面；历史日期残留的（如有）连同暂存目录一起进第 4 步清理
# 8-31 修：同版本同天重打时目标已存在同名 tar → Move-Item 默认不覆盖报错，加 -Force
$tars = Get-ChildItem $launcher -File -Filter "starview-*.tar.gz" -ErrorAction SilentlyContinue
foreach ($t in $tars) { Move-Item $t.FullName (Join-Path $dest $t.Name) -Force }

# 4) 清构建残留（暂存目录 .release-* + 发布\stage）
Write-Host "[4/4] 清理残留..." -ForegroundColor Cyan
$stageDirs = Get-ChildItem $launcher -Directory -Filter ".release-*" -ErrorAction SilentlyContinue
foreach ($d in $stageDirs) { Write-Host "  删暂存: $($d.Name)" -ForegroundColor DarkGray; Remove-Item $d.FullName -Recurse -Force }
Remove-Item (Join-Path $launcher "发布\stage") -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "  清理完成" -ForegroundColor DarkGray

Write-Host ""
Write-Host "=== 完成，产物在：$dest ===" -ForegroundColor Green
Get-ChildItem $dest | Sort-Object Name | ForEach-Object {
    Write-Host "  $($_.Name)  ($([Math]::Round($_.Length / 1MB)) MB)" -ForegroundColor Gray
}
