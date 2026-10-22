Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$outDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$masterSize = 1024
$master = New-Object System.Drawing.Bitmap($masterSize, $masterSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($master)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.Clear([System.Drawing.Color]::Transparent)

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

$tile = New-RoundedPath 28 28 968 968 218
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(100, 80)),
    (New-Object System.Drawing.Point(930, 980)),
    [System.Drawing.Color]::FromArgb(255, 22, 17, 36),
    [System.Drawing.Color]::FromArgb(255, 7, 8, 12))
$g.FillPath($bg, $tile)

$border = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 52, 43, 76), 5)
$g.DrawPath($border, $tile)

# Accent ring
$ringRect = New-Object System.Drawing.RectangleF(250, 245, 524, 524)
$ringBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(250, 245)),
    (New-Object System.Drawing.Point(774, 769)),
    [System.Drawing.Color]::FromArgb(255, 139, 92, 246),
    [System.Drawing.Color]::FromArgb(255, 34, 211, 238))
$ringPen = New-Object System.Drawing.Pen($ringBrush, 34)
$ringPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$ringPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawEllipse($ringPen, $ringRect)

# Soft glow behind the cursor
$glow = New-Object System.Drawing.Drawing2D.GraphicsPath
$glow.AddEllipse(246, 228, 540, 540)
$glowBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow)
$glowBrush.CenterColor = [System.Drawing.Color]::FromArgb(40, 139, 92, 246)
$glowBrush.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 139, 92, 246))
$g.FillPath($glowBrush, $glow)

# Mouse cursor
$cursor = New-Object System.Drawing.Drawing2D.GraphicsPath
$cursor.AddPolygon(@(
    (New-Object System.Drawing.Point(332, 226)),
    (New-Object System.Drawing.Point(332, 716)),
    (New-Object System.Drawing.Point(438, 612)),
    (New-Object System.Drawing.Point(526, 824)),
    (New-Object System.Drawing.Point(620, 782)),
    (New-Object System.Drawing.Point(532, 572)),
    (New-Object System.Drawing.Point(686, 572))
))
$shadowPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150, 0, 0, 0), 30)
$shadowPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
$g.DrawPath($shadowPen, $cursor)
$white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 247, 247, 250))
$g.FillPath($white, $cursor)
$cursorPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 24, 19, 38), 14)
$cursorPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
$g.DrawPath($cursorPen, $cursor)

# Record dot
$g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(70, 255, 86, 104))), 704, 198, 212, 212)
$g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 77, 94))), 744, 238, 132, 132)
$g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 213, 218))), 790, 284, 40, 40)

# Save master PNG for README / social preview
$master.Save((Join-Path $outDir 'app.png'), [System.Drawing.Imaging.ImageFormat]::Png)

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $sg = [System.Drawing.Graphics]::FromImage($bmp)
    $sg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $sg.Clear([System.Drawing.Color]::Transparent)
    $sg.DrawImage($master, 0, 0, $size, $size)
    $sg.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,$ms.ToArray()
    $bmp.Dispose()
}

$icoPath = Join-Path $outDir 'app.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $bytes = $pngs[$i]
    $bw.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $bw.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $bytes.Length
}
foreach ($bytes in $pngs) { $bw.Write($bytes) }
$bw.Flush()
$bw.Dispose()
$fs.Dispose()

$g.Dispose()
$master.Dispose()
Write-Host "Icon written to $icoPath"
