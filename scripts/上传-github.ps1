# ============================================================
#  Starview 上传辅助：确保 Watt(Steam++) 加速生效后再操作 GitHub
#  背景：github.com 间歇连不上（忘开 Watt / 运营商干扰）。本脚本
#  先保证 Watt 加速在跑，等 github 可达，再 git push / 替换 release 资产。
#  用法：
#    powershell -ExecutionPolicy Bypass -File 上传-github.ps1              # 仅 push 当前分支
#    powershell -ExecutionPolicy Bypass -File 上传-github.ps1 -ReplaceV112 # 额外替换 v1.1.2 资产
# ============================================================
param(
    [string]$Branch = "",
    [switch]$ReplaceV112
)
$ErrorActionPreference = "Continue"
Set-Location (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "..")

function Test-GitHub {
    try { $r = Invoke-WebRequest "https://github.com" -UseBasicParsing -TimeoutSec 8; return $r.StatusCode -eq 200 }
    catch { return $false }
}

function Ensure-Watt {
    if (Get-Process -Name "Steam++.Accelerator" -ErrorAction SilentlyContinue) {
        Write-Host "[watt] 加速进程已在运行" -ForegroundColor DarkGray; return
    }
    Write-Host "[watt] 未检测到 Steam++ 加速，尝试启动…" -ForegroundColor Yellow
    $candidates = @(
        "C:\Program Files\Steam++\Steam++.exe",
        "C:\Program Files (x86)\Steam++\Steam++.exe",
        "$env:LOCALAPPDATA\Programs\Steam++\Steam++.exe",
        "$env:ProgramFiles\Watt Toolkit\Steam++.exe"
    )
    $exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $exe) {
        $install = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*","HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*" -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -match "Watt|Steam\+\+" -and $_.InstallLocation } |
            Select-Object -First 1).InstallLocation
        if ($install) { $exe = Join-Path $install "Steam++.exe" }
    }
    if (-not $exe -or -not (Test-Path $exe)) {
        Write-Host "[watt] 找不到 Steam++ 安装路径，请手动打开 Watt 后重试" -ForegroundColor Red
        return
    }
    Start-Process $exe
    Write-Host "[watt] 已启动 Steam++，等待加速生效…" -ForegroundColor DarkYellow
}

# --- 等 github 可达（最多 ~100s）---
Ensure-Watt
$ok = $false
for ($i = 0; $i -lt 20; $i++) {
    if (Test-GitHub) { Write-Host "[net] github.com 可达" -ForegroundColor Green; $ok = $true; break }
    Start-Sleep -Seconds 5
}
if (-not $ok) { Write-Host "[net] github.com 仍不可达，放弃上传" -ForegroundColor Red; exit 1 }

# --- git push ---
Write-Host "[git] 推送中…" -ForegroundColor DarkYellow
if ($Branch) { git push origin $Branch } else { git push }
if ($LASTEXITCODE -ne 0) { exit 1 }

# --- 可选：替换 v1.1.2 release 资产 ---
if ($ReplaceV112) {
    $gh = "C:\Program Files\GitHub CLI\gh.exe"
    $base = Join-Path (Get-Location) "发布"
    if (-not (Test-Path $gh)) { Write-Host "[gh] 未安装 GitHub CLI，跳过资产替换" -ForegroundColor Red }
    elseif (-not (Test-Path (Join-Path $base "望星.exe"))) { Write-Host "[gh] 发布目录没有 exe，先跑 发布.ps1" -ForegroundColor Red }
    else {
        # 复制成 ASCII 名再传（gh 传中文路径会乱码）
        Copy-Item (Join-Path $base "望星.exe") (Join-Path $base "Starview-Launcher.exe") -Force
        Copy-Item (Join-Path $base "望星-轻量版.exe") (Join-Path $base "Starview-Launcher-Lite.exe") -Force
        & $gh release upload v1.1.2 --clobber (Join-Path $base "Starview-Launcher.exe") (Join-Path $base "Starview-Launcher-Lite.exe")
        Remove-Item (Join-Path $base "Starview-Launcher.exe"),(Join-Path $base "Starview-Launcher-Lite.exe") -Force -ErrorAction SilentlyContinue
    }
}
Write-Host "[done] 上传完成" -ForegroundColor Green
