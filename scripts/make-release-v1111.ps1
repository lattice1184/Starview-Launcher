# v1.1.11 release 创建：建 release + 上传 5 个资产（win 2 exe + linux/osx 3 tar.gz）
# ASCII-only 脚本（中文 body 从外部 UTF-8 文件读入，规避 PS 5.1 编码坑；
# 中文路径不用字面量，一律 glob 匹配 + 大小/文件名映射）
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$cred = "protocol=https`nhost=github.com`n" | git credential fill | Select-String "^password=" | ForEach-Object { $_.Line.Substring(9) }
$headers = @{ Authorization = "Bearer $cred"; "User-Agent" = "Starview-Release"; Accept = "application/vnd.github+json" }
$repo = "lattice1184/Starview-Launcher"
$tag = "v1.1.11"

# 1) 检查 release 是否已存在（避免重复）
$exists = $null
try { $exists = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$repo/releases/tags/$tag" -Headers $headers } catch { $exists = $null }
if ($exists) { Write-Host "release $tag already exists (id=$($exists.id))" }
else {
  $body = [IO.File]::ReadAllText((Join-Path $root "..\body-v1111.md"), [Text.Encoding]::UTF8)
  $json = @{ tag_name = $tag; name = "Starview Launcher v1.1.11"; body = $body; draft = $false; prerelease = $false } | ConvertTo-Json -Depth 3
  $rel = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$repo/releases" -Headers $headers `
    -ContentType "application/json; charset=utf-8" -Body ([Text.Encoding]::UTF8.GetBytes($json))
  Write-Host "release created id=$($rel.id)"
}

# 2) 上传资产（二进制）
$rel = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$repo/releases/tags/$tag" -Headers $headers
$upl = "https://uploads.github.com/repos/$repo/releases/$($rel.id)/assets"
$launcher = Join-Path $root ".."
$pub = Join-Path $launcher "发布"
$dest = "C:\Users\yanka\Desktop\Starview发布\v1.1.11"

$files = @()
# win exe：发布目录两个 exe，大的=标准版，小的=轻量版
$exes = @(Get-ChildItem $pub -Filter "*.exe" -ErrorAction SilentlyContinue | Sort-Object Length -Descending)
if ($exes.Count -ge 1) { $files += @{ Local = $exes[0].FullName; Name = "Starview-Launcher.exe" } }
if ($exes.Count -ge 2) { $files += @{ Local = $exes[1].FullName; Name = "Starview-Launcher-Lite.exe" } }
# 三平台 tar.gz：桌面 v1.1.11 目录 glob 映射 ASCII 名
foreach ($t in @(Get-ChildItem $dest -Filter "starview-*.tar.gz" -ErrorAction SilentlyContinue)) {
  $n = if ($t.Name -like "*linux*")      { "Starview-Launcher-Linux-x64.tar.gz" }
       elseif ($t.Name -like "*osx-arm64*") { "Starview-Launcher-macOS-arm64.tar.gz" }
       elseif ($t.Name -like "*osx-x64*")   { "Starview-Launcher-macOS-x64.tar.gz" }
       else { $t.Name }
  $files += @{ Local = $t.FullName; Name = $n }
}

foreach ($f in $files) {
  if (-not (Test-Path $f.Local)) { Write-Host "SKIP missing: $($f.Local)"; continue }
  Invoke-RestMethod -Method Post -Uri "$upl`?name=$($f.Name)" -Headers $headers `
    -InFile $f.Local -ContentType "application/octet-stream" | Out-Null
  Write-Host "uploaded: $($f.Name) ($((Get-Item $f.Local).Length) bytes)"
}
Write-Host "DONE"
