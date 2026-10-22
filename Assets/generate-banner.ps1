# Generates docs/banner.png — the GitHub social/README banner for MauseRecorder.
# Requires Windows PowerShell (System.Drawing). Run from the repo root:
#   powershell -ExecutionPolicy Bypass -File Assets/generate-banner.ps1

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root "docs"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$outFile = Join-Path $outDir "banner.png"

$W = 1280; $H = 420
$bmp = New-Object System.Drawing.Bitmap($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

# Base gradient
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0, 0)),
    (New-Object System.Drawing.Point($W, $H)),
    [System.Drawing.Color]::FromArgb(255, 0x11, 0x0D, 0x1C),
    [System.Drawing.Color]::FromArgb(255, 0x08, 0x09, 0x0D))
$g.FillRectangle($bg, 0, 0, $W, $H)

# Accent glow on the right (smooth radial gradient via PathGradientBrush)
$glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$gc = New-Object System.Drawing.Point(1010, 210)
$gr = 430
$glowPath.AddEllipse(($gc.X - $gr), ($gc.Y - $gr), ($gr * 2), ($gr * 2))
$pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
$pgb.CenterPoint = $gc
$pgb.CenterColor = [System.Drawing.Color]::FromArgb(150, 0x8B, 0x5C, 0xF6)
$pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0x8B, 0x5C, 0xF6))
$pgb.SetSigmaBellShape(0.42)
$g.FillPath($pgb, $glowPath)
$pgb.Dispose(); $glowPath.Dispose()

# Orbit rings + satellite dots
$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, 0x8B, 0x5C, 0xF6), 1.4)
foreach ($rr in 58, 104, 150) {
    $g.DrawEllipse($pen, (985 - $rr), (210 - $rr), ($rr * 2), ($rr * 2))
}
$pen.Dispose()
$dotColors = @(
    [System.Drawing.Color]::FromArgb(255, 0xFF, 0x56, 0x68),
    [System.Drawing.Color]::FromArgb(255, 0x22, 0xD3, 0xEE),
    [System.Drawing.Color]::FromArgb(255, 0xA3, 0xE6, 0x35),
    [System.Drawing.Color]::FromArgb(255, 0xFB, 0xB0, 0x40))
$angle = 0.4
foreach ($c in $dotColors) {
    $bx = 985 + [Math]::Cos($angle) * 150
    $by = 210 + [Math]::Sin($angle) * 150
    $b = New-Object System.Drawing.SolidBrush($c)
    $g.FillEllipse($b, ($bx - 5), ($by - 5), 10, 10)
    $b.Dispose()
    $angle += 1.5
}

# Cursor arrow in the middle of the rings
$pts = @(
    (New-Object System.Drawing.PointF(975, 182)),
    (New-Object System.Drawing.PointF(975, 246)),
    (New-Object System.Drawing.PointF(991, 231)),
    (New-Object System.Drawing.PointF(1002, 254)),
    (New-Object System.Drawing.PointF(1013, 248)),
    (New-Object System.Drawing.PointF(1002, 226)),
    (New-Object System.Drawing.PointF(1021, 225)))
$shadow = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(80, 0, 0, 0))
$g.FillPolygon($shadow, ($pts | ForEach-Object { New-Object System.Drawing.PointF(($_.X + 4), ($_.Y + 5)) }))
$white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
$g.FillPolygon($white, $pts)
$white.Dispose(); $shadow.Dispose()

# Text
$titleFont = New-Object System.Drawing.Font("Segoe UI", 62, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$subFont = New-Object System.Drawing.Font("Segoe UI", 30, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$tagFont = New-Object System.Drawing.Font("Segoe UI", 18, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$monoFont = New-Object System.Drawing.Font("Consolas", 15, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)

$fg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0xF4, 0xF5, 0xF8))
$accent = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0xA9, 0x7B, 0xFF))
$muted = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0x94, 0x9C, 0xAE))
$chipBg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0x16, 0x11, 0x26))

$g.DrawString("MauseRecorder", $titleFont, $fg, 74, 96)
$g.DrawString("Record. Rewind. Repeat.", $subFont, $accent, 78, 182)
$g.DrawString("A beautiful, portable mouse recorder for Windows.", $tagFont, $muted, 80, 232)

# Hotkey chips
$chips = @("Ctrl + Alt + R", "Ctrl + Alt + P")
$cx = 80
foreach ($c in $chips) {
    $sz = $g.MeasureString($c, $monoFont)
    $pw = [int]$sz.Width + 24
    $g.FillRectangle($chipBg, $cx, 290, $pw, 34)
    $g.DrawString($c, $monoFont, $muted, ($cx + 12), 298)
    $cx += $pw + 12
}

# Accent underline
$g.FillRectangle($accent, 78, 172, 118, 5)

$bmp.Save($outFile, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
$titleFont.Dispose(); $subFont.Dispose(); $tagFont.Dispose(); $monoFont.Dispose()
$bg.Dispose(); $fg.Dispose(); $accent.Dispose(); $muted.Dispose(); $chipBg.Dispose()

Write-Host "Wrote $outFile"
