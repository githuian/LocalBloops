# Generates src\LocalBloops\Assets\LocalBloops.ico from the same design as the tray icon (dark disc,
# green/red delta bars, white centre line), drawn natively at each size so small sizes stay crisp.
#
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'src\LocalBloops\Assets\LocalBloops.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($size in $sizes) {
    $s = $size / 32.0
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 24, 26, 32))
    $g.FillEllipse($bg, [single](1 * $s), [single](1 * $s), [single](30 * $s), [single](30 * $s))
    $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 46, 204, 85))
    $g.FillRectangle($green, [single](6 * $s), [single](13 * $s), [single](10 * $s), [single](6 * $s))
    $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 232, 58, 58))
    $g.FillRectangle($red, [single](16 * $s), [single](13 * $s), [single](10 * $s), [single](6 * $s))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single][Math]::Max(1.0, 2 * $s))
    $g.DrawLine($pen, [single](16 * $s), [single](8 * $s), [single](16 * $s), [single](24 * $s))

    $ms = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        # 256px as PNG (standard, keeps the file small).
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    else {
        # Smaller sizes as classic 32-bit DIBs, which every icon reader understands:
        # BITMAPINFOHEADER (height doubled for the mask), bottom-up BGRA rows, then a 1-bit AND mask.
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([UInt32]40); $bw.Write([Int32]$size); $bw.Write([Int32]($size * 2))
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
        $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
        $bw.Write([UInt32]($size * $size * 4 + $maskStride * $size))
        $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $bw.Write((New-Object byte[] ($maskStride * $size))) # all zero: alpha channel decides
        $bw.Flush()
    }
    $pngs += , $ms.ToArray()
    $g.Dispose(); $bmp.Dispose(); $bg.Dispose(); $green.Dispose(); $red.Dispose(); $pen.Dispose()
}

# ICO container with PNG-compressed entries (supported since Windows Vista).
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Dispose()
Write-Host "Wrote $out ($((Get-Item $out).Length) bytes, sizes: $($sizes -join ', '))"
