# Dump UIA geometry of a few controls for coordinate calibration.
# ASCII only. Prints window rect, search box rect and current keyboard focus.
param(
    [string]$Id = 'SearchBox'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W32 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
"@
[void][W32]::SetProcessDPIAware()

$proc = Get-Process WingetManager -ErrorAction Stop | Select-Object -First 1
$hwnd = $proc.MainWindowHandle
"hwnd      : $hwnd"
"dpi       : " + [W32]::GetDpiForWindow($hwnd)

$r = New-Object W32+RECT
[void][W32]::DwmGetWindowAttribute($hwnd, 9, [ref]$r, 16)
"frame     : L=$($r.Left) T=$($r.Top) R=$($r.Right) B=$($r.Bottom)  $($r.Right-$r.Left)x$($r.Bottom-$r.Top)"

"foreground: " + [W32]::GetForegroundWindow()
$p = New-Object W32+POINT
[void][W32]::GetCursorPos([ref]$p)
"cursor    : $($p.X),$($p.Y)"

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
$el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if (-not $el) { "element   : NOT FOUND ($Id)"; exit 1 }

$b = $el.Current.BoundingRectangle
"element   : {0}  id={1}  class={2}" -f $el.Current.ControlType.ProgrammaticName, $el.Current.AutomationId, $el.Current.ClassName
"rect(px)  : L={0} T={1} W={2} H={3}" -f $b.X, $b.Y, $b.Width, $b.Height
$f = [W32]::GetDpiForWindow($hwnd) / 96.0
"rect(dip) : L={0:N1} T={1:N1} W={2:N1} H={3:N1}  (rel to frame: {4:N1},{5:N1})" -f `
    ($b.X / $f), ($b.Y / $f), ($b.Width / $f), ($b.Height / $f), `
    (($b.X - $r.Left) / $f), (($b.Y - $r.Top) / $f)

$focused = [System.Windows.Automation.AutomationElement]::FocusedElement
if ($focused) {
    "focus     : {0}  id={1}  class={2}" -f $focused.Current.ControlType.ProgrammaticName, $focused.Current.AutomationId, $focused.Current.ClassName
} else {
    "focus     : (none)"
}
