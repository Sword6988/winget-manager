# Capture any top-level window by title substring, using PrintWindow so the
# window does not need to be in the foreground (the sandbox rarely lets us
# steal focus from the IDE).
param(
    [string]$Title = '',
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$Zoom = 1,
    [switch]$Focus
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SW32 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
"@
[void][SW32]::SetProcessDPIAware()

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# WPF borderless dialogs (WindowStyle=None) are not always exposed as
# ControlType.Window, so scan every top-level child and match on the name.
$found = $null

if ($Focus) {
    # Walk up from the focused element to its owning window. This is the most
    # reliable way to find a modal dialog that has no UIA name.
    $el = [System.Windows.Automation.AutomationElement]::FocusedElement
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    while ($el -ne $null) {
        if ($el.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { $found = $el; break }
        $el = $walker.GetParent($el)
    }
}

if (-not $found) {
    foreach ($w in [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)) {
        $n = $w.Current.Name
        if ($n -and $n -like "*$Title*" -and -not $w.Current.IsOffscreen) { $found = $w; break }
    }
}

if (-not $found) { "window not found: $Title"; exit 1 }
$hwnd = [IntPtr]$found.Current.NativeWindowHandle
"found     : {0}  hwnd={1}  type={2}" -f $found.Current.Name, $hwnd, $found.Current.ControlType.ProgrammaticName
if ($hwnd -eq [IntPtr]::Zero) { "no native window handle (owned popup?)"; exit 1 }

$r = New-Object SW32+RECT
$hr = [SW32]::DwmGetWindowAttribute($hwnd, 9, [ref]$r, 16)
if ($hr -ne 0) { [void][SW32]::GetWindowRect($hwnd, [ref]$r) }
$w = $r.Right - $r.Left
$h = $r.Bottom - $r.Top
"size      : ${w}x${h}"

$bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [SW32]::PrintWindow($hwnd, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
if (-not $ok) { "PrintWindow failed"; $bmp.Dispose(); exit 1 }

$target = $bmp
if ($Zoom -ne 1) {
    $nw = $w * $Zoom
    $nh = $h * $Zoom
    $scaled = New-Object System.Drawing.Bitmap($nw, $nh)
    $g2 = [System.Drawing.Graphics]::FromImage($scaled)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $g2.DrawImage($bmp, 0, 0, $nw, $nh)
    $g2.Dispose()
    $target = $scaled
}

$target.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
"ok        : $Out"
if (-not [object]::ReferenceEquals($target, $bmp)) { $target.Dispose() }
$bmp.Dispose()
