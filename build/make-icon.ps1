# make-icon.ps1 - generates icon.ico (a small globe with a green check badge).
# Produces a multi-size PNG-based .ico (works on Windows 7/10/11).
param([string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) "icon.ico"))
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = [double]$s * 0.08
    $d = [double]$s - 2 * $m            # globe diameter
    $cx = $s / 2.0; $cy = $s / 2.0; $r = $d / 2.0

    # globe fill (blue gradient)
    $rect = New-Object System.Drawing.RectangleF($m, $m, $d, $d)
    $c1 = [System.Drawing.Color]::FromArgb(255, 43, 155, 255)
    $c2 = [System.Drawing.Color]::FromArgb(255, 21, 103, 200)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 90)
    $g.FillEllipse($grad, $rect)

    # meridians (white, thin)
    $pw = [Math]::Max(1.0, $s / 22.0)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(230, 255, 255, 255), $pw)
    # equator
    $g.DrawLine($pen, [single]($cx - $r), [single]$cy, [single]($cx + $r), [single]$cy)
    # vertical meridian (thin ellipse)
    $g.DrawEllipse($pen, [single]($cx - $r * 0.45), [single]($cy - $r), [single]($r * 0.9), [single]($r * 2))
    # globe rim
    $g.DrawEllipse($pen, $rect)

    # green check badge (bottom-right)
    $br = $d * 0.42
    $bx = $s - $m - $br; $by = $s - $m - $br
    $badge = New-Object System.Drawing.RectangleF($bx, $by, $br, $br)
    $green = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 47, 191, 91))
    $g.FillEllipse($green, $badge)
    $wpen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [Math]::Max(1.2, $s / 16.0))
    $wpen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $wpen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $wpen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $p1 = New-Object System.Drawing.PointF([single]($bx + $br * 0.24), [single]($by + $br * 0.52))
    $p2 = New-Object System.Drawing.PointF([single]($bx + $br * 0.44), [single]($by + $br * 0.72))
    $p3 = New-Object System.Drawing.PointF([single]($bx + $br * 0.78), [single]($by + $br * 0.30))
    $g.DrawLines($wpen, @($p1, $p2, $p3))

    $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$blobs = @()
foreach ($s in $sizes) {
    $bmp = New-Frame $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $blobs += , ($ms.ToArray())
    $ms.Dispose(); $bmp.Dispose()
}

$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $blobs[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)
    $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($b in $blobs) { $bw.Write($b) }
$bw.Flush(); $fs.Close()
Write-Host "icon written: $Out ($((Get-Item $Out).Length) bytes)"
