<#
  crop.ps1 -- crop and (optionally) integer-zoom a PNG for pixel inspection.

    powershell -NoProfile -ExecutionPolicy Bypass -File tools\crop.ps1 `
        -In docs\ui-v3\shots\01-default.png -Out docs\ui-v3\shots\01-row.png `
        -X 0 -Y 110 -W 520 -H 130 -Zoom 3

  -X/-Y/-W/-H are in PIXELS of the source image (the captures are 1550x1100 at 125%
  scaling, i.e. 1.25x the design's device-independent pixels).
  -Zoom uses nearest-neighbour so individual pixels stay crisp.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$In,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$X = 0,
    [int]$Y = 0,
    [int]$W = 0,
    [int]$H = 0,
    [int]$Zoom = 1
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$traceFile = Join-Path $PSScriptRoot 'crop-trace.log'
function Trace([string]$m) { Add-Content -Path $traceFile -Value ((Get-Date).ToString('HH:mm:ss.fff') + ' ' + $m) }
Remove-Item -Path $traceFile -ErrorAction SilentlyContinue
Trace "start In=$In Out=$Out X=$X Y=$Y W=$W H=$H Zoom=$Zoom"

$outPath = $Out
if (-not [System.IO.Path]::IsPathRooted($outPath)) { $outPath = Join-Path (Get-Location).Path $outPath }

try {
    $src = [System.Drawing.Image]::FromFile((Resolve-Path $In).Path)
    Trace "loaded $($src.Width)x$($src.Height)"
    if ($W -le 0) { $W = $src.Width - $X }
    if ($H -le 0) { $H = $src.Height - $Y }
    $X = [Math]::Max(0, [Math]::Min($X, $src.Width - 1))
    $Y = [Math]::Max(0, [Math]::Min($Y, $src.Height - 1))
    $W = [Math]::Min($W, $src.Width - $X)
    $H = [Math]::Min($H, $src.Height - $Y)

    $outW = $W * $Zoom
    $outH = $H * $Zoom
    Trace "clamped X=$X Y=$Y W=$W H=$H -> out ${outW}x${outH}"
    $bmp = New-Object System.Drawing.Bitmap($outW, $outH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $dest = New-Object System.Drawing.Rectangle(0, 0, $outW, $outH)
    $srect = New-Object System.Drawing.Rectangle($X, $Y, $W, $H)
    $g.DrawImage($src, $dest, $srect, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    Trace "drawn"

    $dir = Split-Path -Parent $outPath
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    Trace "saved $outPath"
    Write-Host ("[ok] {0}  crop {1},{2} {3}x{4} x{5} -> {6}x{7}" -f $Out, $X, $Y, $W, $H, $Zoom, $outW, $outH)
    $bmp.Dispose()
}
catch {
    $msg = $_.Exception.ToString() + "`r`n--- script stack ---`r`n" + $_.ScriptStackTrace
    Set-Content -Path (Join-Path $PSScriptRoot 'crop-error.log') -Value $msg -Encoding UTF8
    Write-Host "[err] $($_.Exception.Message)"
}
finally {
    if ($src) { $src.Dispose() }
}
