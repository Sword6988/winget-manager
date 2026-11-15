<#
  capture-frame.ps1 -- pixel-accurate frames of the WingetManager WPF window.

  Why this exists: the agent sandbox blocks Add-Type / screen copy, so visual review
  needs a real (non-sandboxed) process. Run this once outside the sandbox; afterwards
  the PNGs in docs/ui-v3/shots can be inspected with normal file reads.

  Quick start
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-frame.ps1 -Name 01-default -Launch

  Sizing
    -Width / -Height are DEVICE-INDEPENDENT pixels (the numbers in Metrics.cs), not
    physical pixels. The script measures the DWM frame and iterates SetWindowPos until
    the *visible* frame is exactly that size, so a 1240x880 shot really is 1240x880.
    Every size is then expressed relative to that, which makes 960 / 1080 / 1240
    breakpoint shots directly comparable.

  Capture method
    print  (default) PrintWindow + PW_RENDERFULLCONTENT. Works even when the window is
           larger than the screen or covered by another window -- which matters here:
           at 125% scaling a 1240x880 DIP window is 1550x1100 physical pixels on a
           1080p panel, so a plain screen grab would clip the bottom 20px.
    screen  CopyFromScreen of the visible frame. Use when print returns a blank or
           stale surface (some drivers do); must be fully on-screen.

  Action DSL (-Actions or -ActionsFile, semicolon separated)
    shot:<name>        capture to <OutDir>\<name>.png
    click:<text>       find element by UIA name, Invoke() it, else mouse-click its center
    clickid:<id>       same, matched on AutomationId
    clickat:<x>,<y>    mouse click at window-relative DIP coordinates
    hover:<x>,<y>      move the pointer there (hover states), no click
    key:<spec>         System.Windows.Forms.SendKeys, e.g. key:^z for Ctrl+Z
    wait:<ms>          sleep
    front              bring the window to the foreground
    -ActionsFile is read as UTF-8, so Chinese labels survive (command-line args do not).

  -Watch harvests a frame every -Interval ms and keeps only frames that differ from the
  previous one. Drive the UI by hand and the interesting states get saved by themselves.
#>
[CmdletBinding()]
param(
    [string]$OutDir = '',
    [string]$Name = 'frame',
    [switch]$Launch,
    [int]$Delay = 1600,
    [switch]$Watch,
    [int]$Interval = 260,
    [int]$MaxFrames = 40,
    [int]$WatchSeconds = 60,
    [double]$Scale = 1.0,
    [string]$Actions = '',
    [string]$ActionsFile = '',
    [string]$ProcessName = 'WingetManager',
    [string]$Exe = '',
    [int]$Width = 1240,
    [int]$Height = 880,
    [int]$X = 40,
    [int]$Y = 20,
    [ValidateSet('print', 'screen')]
    [string]$Method = 'print',
    [switch]$NoResize,
    [switch]$KeepWindow
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- paths
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $root 'docs\ui-v3\shots' }
if ([string]::IsNullOrWhiteSpace($Exe)) { $Exe = Join-Path $root 'dist\WingetManager.exe' }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$OutDir = (Resolve-Path $OutDir).Path

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

if (-not ('WBW32' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WBW32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT r, int size);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
}
'@
}

[void][WBW32]::SetProcessDPIAware()
$MOUSE_LEFTDOWN = 0x0002
$MOUSE_LEFTUP = 0x0004
$SWP_NOZORDER = 0x0004
$SWP_NOACTIVATE = 0x0010
$DWMWA_EXTENDED_FRAME_BOUNDS = 9
$PW_RENDERFULLCONTENT = 2

# ---------------------------------------------------------------- geometry
function Get-OuterRect([IntPtr]$hwnd) {
    $r = New-Object WBW32+RECT
    [void][WBW32]::GetWindowRect($hwnd, [ref]$r)
    return $r
}

function Get-FrameRect([IntPtr]$hwnd) {
    $r = New-Object WBW32+RECT
    $hr = [WBW32]::DwmGetWindowAttribute($hwnd, $DWMWA_EXTENDED_FRAME_BOUNDS, [ref]$r, 16)
    if ($hr -ne 0 -or ($r.Right - $r.Left) -le 0) { return (Get-OuterRect $hwnd) }
    return $r
}

function Get-Dpi([IntPtr]$hwnd) {
    try { $d = [WBW32]::GetDpiForWindow($hwnd); if ($d -gt 0) { return [int]$d } } catch { }
    return 96
}

function Set-Front([IntPtr]$hwnd) {
    if ([WBW32]::IsIconic($hwnd)) { [void][WBW32]::ShowWindow($hwnd, 9) }

    [void][WBW32]::SetForegroundWindow($hwnd)
    if ([WBW32]::GetForegroundWindow() -ne $hwnd) {
        # Windows refuses SetForegroundWindow when the calling process does not own
        # the foreground. A harmless ALT press makes this thread the "last input"
        # owner, which lifts the restriction for the retry.
        [WBW32]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
        [WBW32]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 90
        [void][WBW32]::SetForegroundWindow($hwnd)
    }

    if ([WBW32]::GetForegroundWindow() -ne $hwnd) {
        # Still refused: borrow the foreground thread's input queue and retry.
        $fg = [WBW32]::GetForegroundWindow()
        if ($fg -ne [IntPtr]::Zero) {
            $tidFg = [WBW32]::GetWindowThreadProcessId($fg, [IntPtr]::Zero)
            $tidMe = [WBW32]::GetCurrentThreadId()
            if ($tidFg -ne 0 -and $tidFg -ne $tidMe) {
                [void][WBW32]::AttachThreadInput($tidMe, $tidFg, $true)
                [void][WBW32]::SetForegroundWindow($hwnd)
                [void][WBW32]::AttachThreadInput($tidMe, $tidFg, $false)
            }
        }
    }

    if ([WBW32]::GetForegroundWindow() -ne $hwnd) {
        Write-Host "[warn] could not bring the window to the foreground"
    }
    Start-Sleep -Milliseconds 140
}

function Set-VisibleSize([IntPtr]$hwnd, [int]$dipW, [int]$dipH) {
    $dpi = Get-Dpi $hwnd
    $f = $dpi / 96.0
    $wantW = [int][Math]::Round($dipW * $f)
    $wantH = [int][Math]::Round($dipH * $f)
    $outerW = $wantW
    $outerH = $wantH
    $pxX = [int][Math]::Round($X * $f)
    $pxY = [int][Math]::Round($Y * $f)

    for ($i = 0; $i -lt 3; $i++) {
        [void][WBW32]::SetWindowPos($hwnd, [IntPtr]::Zero, $pxX, $pxY, $outerW, $outerH, ($SWP_NOZORDER -bor $SWP_NOACTIVATE))
        Start-Sleep -Milliseconds 180
        $r = Get-FrameRect $hwnd
        $vw = $r.Right - $r.Left
        $vh = $r.Bottom - $r.Top
        if ([Math]::Abs($vw - $wantW) -le 1 -and [Math]::Abs($vh - $wantH) -le 1) { break }
        $outerW += ($wantW - $vw)
        $outerH += ($wantH - $vh)
    }

    if ($outerW -ne $wantW -or $outerH -ne $wantH) {
        [void][WBW32]::SetWindowPos($hwnd, [IntPtr]::Zero, $pxX, $pxY, $outerW, $outerH, ($SWP_NOZORDER -bor $SWP_NOACTIVATE))
        Start-Sleep -Milliseconds 180
    }

    $r = Get-FrameRect $hwnd
    Write-Host ("[..] dpi={0}  target={1}x{2} dip  visible={3}x{4} px" -f $dpi, $dipW, $dipH, ($r.Right - $r.Left), ($r.Bottom - $r.Top))
}

# ---------------------------------------------------------------- process
function Start-App {
    if (-not (Test-Path $Exe)) { throw "Executable not found: $Exe" }
    Write-Host "[..] launching $Exe"
    # WMI creation detaches the child from this console's job object, so the window
    # survives after this script exits.
    $null = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = ('"' + $Exe + '"') }
}

function Get-AppWindow {
    $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($proc) { return $proc.MainWindowHandle }

    if (-not ($Launch -or $Watch)) { throw "Process '$ProcessName' is not running (pass -Launch)." }
    Start-App
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
        $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
                Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if ($proc) { return $proc.MainWindowHandle }
    }
    throw "The window did not appear within 25s."
}

# ---------------------------------------------------------------- capture
function New-ShotScreen([IntPtr]$hwnd) {
    $r = Get-FrameRect $hwnd
    $w = $r.Right - $r.Left
    $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { throw "Invalid frame rect: ${w}x${h}" }
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)), [System.Drawing.CopyPixelOperation]::SourceCopy)
    $g.Dispose()
    return $bmp
}

function New-ShotPrint([IntPtr]$hwnd) {
    $outer = Get-OuterRect $hwnd
    $vis = Get-FrameRect $hwnd
    $ow = $outer.Right - $outer.Left
    $oh = $outer.Bottom - $outer.Top
    if ($ow -le 0 -or $oh -le 0) { throw "Invalid window rect: ${ow}x${oh}" }

    $bmp = New-Object System.Drawing.Bitmap($ow, $oh, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [WBW32]::PrintWindow($hwnd, $hdc, $PW_RENDERFULLCONTENT)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    if (-not $ok) { $bmp.Dispose(); throw "PrintWindow failed" }

    $offX = $vis.Left - $outer.Left
    $offY = $vis.Top - $outer.Top
    $vw = $vis.Right - $vis.Left
    $vh = $vis.Bottom - $vis.Top
    if ($offX -eq 0 -and $offY -eq 0 -and $vw -eq $ow -and $vh -eq $oh) { return $bmp }

    $crop = New-Object System.Drawing.Rectangle($offX, $offY, $vw, $vh)
    $cut = $bmp.Clone($crop, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bmp.Dispose()
    return $cut
}

function Test-Blank($bmp) {
    $step = [Math]::Max(16, [int]($bmp.Width / 24))
    $first = $bmp.GetPixel(1, 1).ToArgb()
    for ($x = 1; $x -lt $bmp.Width; $x += $step) {
        for ($y = 1; $y -lt $bmp.Height; $y += $step) {
            if ($bmp.GetPixel($x, $y).ToArgb() -ne $first) { return $false }
        }
    }
    return $true
}

function New-Shot([IntPtr]$hwnd, [string]$mode) {
    if ($mode -eq 'screen') { return (New-ShotScreen $hwnd) }
    try {
        $bmp = New-ShotPrint $hwnd
        if (Test-Blank $bmp) {
            Write-Host "[warn] PrintWindow returned a blank surface, falling back to screen grab"
            $bmp.Dispose()
            return (New-ShotScreen $hwnd)
        }
        return $bmp
    } catch {
        Write-Host ("[warn] {0}; falling back to screen grab" -f $_.Exception.Message)
        return (New-ShotScreen $hwnd)
    }
}

function Save-Shot($bmp, [string]$file, [double]$factor) {
    $target = $bmp
    if ($factor -ne 1.0) {
        $nw = [int]([Math]::Round($bmp.Width * $factor))
        $nh = [int]([Math]::Round($bmp.Height * $factor))
        $scaled = New-Object System.Drawing.Bitmap($nw, $nh)
        $g = [System.Drawing.Graphics]::FromImage($scaled)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.DrawImage($bmp, 0, 0, $nw, $nh)
        $g.Dispose()
        $target = $scaled
    }
    $target.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    if (-not [object]::ReferenceEquals($target, $bmp)) { $target.Dispose() }
}

function Get-ShotHash($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $hash = [BitConverter]::ToString($md5.ComputeHash($ms.ToArray()))
    $md5.Dispose(); $ms.Dispose()
    return $hash
}

function Invoke-Capture([IntPtr]$hwnd, [string]$label) {
    $bmp = New-Shot $hwnd $Method
    $file = Join-Path $OutDir ($label + '.png')
    Save-Shot $bmp $file $Scale
    $rect = Get-FrameRect $hwnd
    $meta = [ordered]@{
        name     = $label
        file     = $file
        width    = $bmp.Width
        height   = $bmp.Height
        scale    = $Scale
        dpi      = (Get-Dpi $hwnd)
        method   = $Method
        screenX  = $rect.Left
        screenY  = $rect.Top
        captured = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
    }
    ($meta | ConvertTo-Json -Compress) | Set-Content -Path (Join-Path $OutDir ($label + '.json')) -Encoding UTF8
    Write-Host ("[ok] {0}  {1}x{2}  ({3})" -f $file, $bmp.Width, $bmp.Height, $Method)
    $bmp.Dispose()
}

# ---------------------------------------------------------------- ui automation
function Ensure-Uia {
    if ('System.Windows.Automation.AutomationElement' -as [type]) { return $true }
    foreach ($n in @('UIAutomationTypes', 'UIAutomationClient')) {
        try { Add-Type -AssemblyName $n } catch { Write-Host ("[warn] Add-Type {0}: {1}" -f $n, $_.Exception.Message) }
    }
    if ('System.Windows.Automation.AutomationElement' -as [type]) { return $true }
    $base = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    foreach ($n in @('UIAutomationTypes', 'UIAutomationClient')) {
        $p = Join-Path $base ($n + '.dll')
        if (Test-Path $p) {
            try { [void][System.Reflection.Assembly]::LoadFrom($p) }
            catch { Write-Host ("[warn] LoadFrom {0}: {1}" -f $n, $_.Exception.Message) }
        }
    }
    return [bool]('System.Windows.Automation.AutomationElement' -as [type])
}

function Get-UiaRoot([IntPtr]$hwnd) {
    if (-not (Ensure-Uia)) { throw "UI Automation is unavailable; use clickat:<x>,<y> instead of click:<text>." }
    return [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
}

function Invoke-MouseAt([int]$x, [int]$y) {
    [void][WBW32]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 70
    [WBW32]::mouse_event($MOUSE_LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 50
    [WBW32]::mouse_event($MOUSE_LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
}

function Find-ByProp([IntPtr]$hwnd, $property, [string]$value) {
    $root = Get-UiaRoot $hwnd
    $cond = New-Object System.Windows.Automation.PropertyCondition($property, $value)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-Element([IntPtr]$hwnd, $element, [string]$what) {
    if (-not $element) { throw "Element not found: $what" }
    $pattern = $null
    if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        $pattern.Invoke(); Write-Host "[ui] invoke $what"; return
    }
    if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        $pattern.Select(); Write-Host "[ui] select $what"; return
    }
    if ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
        $pattern.Toggle(); Write-Host "[ui] toggle $what"; return
    }
    $b = $element.Current.BoundingRectangle
    Set-Front $hwnd
    Invoke-MouseAt ([int]($b.X + $b.Width / 2)) ([int]($b.Y + $b.Height / 2))
    Write-Host "[ui] click $what"
}

# ---------------------------------------------------------------- actions
function Invoke-Action([IntPtr]$hwnd, [string]$token) {
    $token = $token.Trim()
    if ($token.Length -eq 0) { return }
    $idx = $token.IndexOf(':')
    if ($idx -lt 0) { $verb = $token; $arg = '' } else { $verb = $token.Substring(0, $idx); $arg = $token.Substring($idx + 1) }
    $rect = Get-FrameRect $hwnd
    $f = (Get-Dpi $hwnd) / 96.0

    switch ($verb.Trim().ToLowerInvariant()) {
        'shot'    { if ($arg) { Invoke-Capture $hwnd $arg } else { Invoke-Capture $hwnd $Name } }
        'wait'    { Start-Sleep -Milliseconds ([int]$arg) }
        'front'   { Set-Front $hwnd }
        'click'   {
            $el = Find-ByProp $hwnd ([System.Windows.Automation.AutomationElement]::NameProperty) $arg
            Invoke-Element $hwnd $el "name='$arg'"; Start-Sleep -Milliseconds 240
        }
        'clickid' {
            $el = Find-ByProp $hwnd ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) $arg
            Invoke-Element $hwnd $el "id='$arg'"; Start-Sleep -Milliseconds 240
        }
        'clickat' {
            $p = $arg.Split(',')
            Set-Front $hwnd
            Invoke-MouseAt ($rect.Left + [int]([double]$p[0].Trim() * $f)) ($rect.Top + [int]([double]$p[1].Trim() * $f))
            Start-Sleep -Milliseconds 240
        }
        'hover'   {
            $p = $arg.Split(',')
            Set-Front $hwnd
            [void][WBW32]::SetCursorPos(($rect.Left + [int]([double]$p[0].Trim() * $f)), ($rect.Top + [int]([double]$p[1].Trim() * $f)))
            Start-Sleep -Milliseconds 360
        }
        'key'     {
            Set-Front $hwnd
            [System.Windows.Forms.SendKeys]::SendWait($arg)
            Start-Sleep -Milliseconds 240
        }
        default   { Write-Host "[warn] unknown action: $token" }
    }
}

# ---------------------------------------------------------------- main
# UIA must be loaded before any statement that uses a [System.Windows.Automation.*]
# type literal: those literals are resolved while the arguments are evaluated, which
# is earlier than any lazy load inside the helper functions.
if (-not (Ensure-Uia)) { Write-Host "[warn] UI Automation unavailable; only clickat/hover/key actions will work." }

$hwnd = Get-AppWindow
Set-Front $hwnd
if (-not $NoResize) { Set-VisibleSize $hwnd $Width $Height }
Set-Front $hwnd

if ($Watch) {
    Write-Host "[..] watch mode: interact with the app now; differing frames are kept."
    $lastHash = ''
    $saved = 0
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($saved -lt $MaxFrames -and $sw.Elapsed.TotalSeconds -lt $WatchSeconds) {
        $bmp = New-Shot $hwnd $Method
        $hash = Get-ShotHash $bmp
        if ($hash -ne $lastHash) {
            $saved++
            $label = ('{0}-{1:00}' -f $Name, $saved)
            $file = Join-Path $OutDir ($label + '.png')
            Save-Shot $bmp $file $Scale
            Write-Host ("[ok] {0}  {1}" -f $file, (Get-Date).ToString('HH:mm:ss.fff'))
            $lastHash = $hash
        }
        $bmp.Dispose()
        Start-Sleep -Milliseconds $Interval
    }
    Write-Host "[ok] watch finished, $saved frame(s) -> $OutDir"
    return
}

if ($ActionsFile) {
    if (-not (Test-Path $ActionsFile)) { throw "ActionsFile not found: $ActionsFile" }
    $Actions = [System.IO.File]::ReadAllText((Resolve-Path $ActionsFile).Path, [System.Text.Encoding]::UTF8)
}

Start-Sleep -Milliseconds $Delay
if ($Actions) {
    foreach ($token in $Actions -split ';') { Invoke-Action $hwnd $token }
} else {
    Invoke-Capture $hwnd $Name
}

Write-Host "[done] output: $OutDir"
