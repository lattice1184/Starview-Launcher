# v1.0.9 release 创建：建 release + 上传两个 exe 资产
# ASCII-only 脚本（中文 body 从外部 UTF-8 文件读入，规避 PS 5.1 编码坑）
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$cred = "protocol=https`nhost=github.com`n" | git credential fill | Select-String "^password=" | ForEach-Object { $_.Line.Substring(9) }
$headers = @{ Authorization = "Bearer $cred"; "User-Agent" = "Starview-Release"; Accept = "application/vnd.github+json" }
$repo = "lattice1184/Starview-Launcher"

# 1) 检查 release 是否已存在（避免重复）
$exists = $null
try { $exists = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$repo/releases/tags/v1.0.9" -Headers $headers } catch { $exists = $null }
if ($exists) { Write-Host "release v1.0.9 already exists (id=$($exists.id))" } else {
  $body = [IO.File]::ReadAllText((Join-Path $root "body-v109.md"), [Text.Encoding]::UTF8)
  $json = @{ tag_name = "v1.0.9"; name = "Starview Launcher v1.0.9"; body = $body; draft = $false; prerelease = $false } | ConvertTo-Json -Depth 3
  $rel = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$repo/releases" -Headers $headers `
    -ContentType "application/json; charset=utf-8" -Body ([Text.Encoding]::UTF8.GetBytes($json))
  Write-Host "release created id=$($rel.id)"
}

# 2) 上传资产（二进制）
$rel = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$repo/releases/tags/v1.0.9" -Headers $headers
$upl = "https://uploads.github.com/repos/$repo/releases/$($rel.id)/assets"
$files = @(
  @{ Local = Join-Path $root "..\发布\望星.exe"; Name = "Starview-Launcher-Setup.exe" },
  @{ Local = Join-Path $root "..\发布\望星-轻量版.exe"; Name = "Starview-Launcher-Lite.exe" }
)
foreach ($f in $files) {
  Invoke-RestMethod -Method Post -Uri "$upl`?name=$($f.Name)" -Headers $headers `
    -InFile $f.Local -ContentType "application/octet-stream" | Out-Null
  Write-Host "uploaded: $($f.Name) ($((Get-Item $f.Local).Length) bytes)"
}
Write-Host "DONE"