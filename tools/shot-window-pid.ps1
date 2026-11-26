# Capture the WingetManager main window by process id (avoids matching
# browser tabs whose title also contains "Winget 软件管家").
param([string]$Out = 'v4-win11-list.png')

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class SW32pid {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
}
"@
[void][SW32pid]::SetProcessDPIAware()

$proc = Get-Process WingetManager -ErrorAction Stop
$targetPid = $proc.Id
$hwndFound = [IntPtr]::Zero
$cb = [SW32pid+EnumWindowsProc]{
    param($h, $l)
    $ownerPid = 0
    [void][SW32pid]::GetWindowThreadProcessId($h, [ref]$ownerPid)
    if ($ownerPid -eq $targetPid -and [SW32pid]::IsWindowVisible($h)) {
        $script:hwndFound = $h
        return $false
    }
    return $true
}
[void][SW32pid]::EnumWindows($cb, [IntPtr]::Zero)

if ($hwndFound -eq [IntPtr]::Zero) { Write-Output "window not found for pid $targetPid"; exit 1 }

$r = New-Object SW32pid+RECT
[void][SW32pid]::DwmGetWindowAttribute($hwndFound, 9, [ref]$r, 16)
$w = $r.Right - $r.Left
$h = $r.Bottom - $r.Top

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [SW32pid]::PrintWindow($hwndFound, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
if (-not $ok) { $bmp.Dispose(); Write-Output "PrintWindow failed"; exit 1 }
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "captured ${w}x${h} -> $Out"
