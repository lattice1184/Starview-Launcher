# win-capture.ps1 <outfile.png> : capture Starview window region
param([string]$OutFile)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct RCAPRECT { public int Left, Top, Right, Bottom; }
public class RCAPW32 {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RCAPRECT rect);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmdShow);
}
"@
$proc = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like 'Starview*' } | Select-Object -First 1
if (-not $proc) { Write-Host "NO_PROCESS"; exit 1 }
# 若最小化则还原
[RCAPW32]::ShowWindow([IntPtr]$proc.MainWindowHandle, 9) | Out-Null
Start-Sleep -Milliseconds 400
$r = New-Object RCAPRECT
[RCAPW32]::GetWindowRect([IntPtr]$proc.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
if ($w -lt 100 -or $h -lt 100) { Write-Host "BAD_RECT $w x $h"; exit 1 }
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "SAVED $OutFile ${w}x${h}"
