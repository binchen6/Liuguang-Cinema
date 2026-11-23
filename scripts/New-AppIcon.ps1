Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root "src/TV/Assets"
New-Item -ItemType Directory -Force -Path $assets | Out-Null

function New-Pt([double]$x, [double]$y) {
    return [System.Drawing.PointF]::new([float]$x, [float]$y)
}

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = [float][Math]::Min($r * 2.0, [Math]::Min($w, $h))
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Render-ApertureIcon([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 512.0
    $pad = [float](24.0 * $s)
    $box = [float](512.0 * $s - 2.0 * $pad)
    $rad = [float](116.0 * $s)

    # 1. Architectural Sumi-Ink Monolith Base
    $outerPath = New-RoundedRectPath $pad $pad $box $box $rad
    $bgRect = [System.Drawing.RectangleF]::new($pad, $pad, $box, $box)
    $bgBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $bgRect,
        [System.Drawing.Color]::FromArgb(255, 26, 26, 30),
        [System.Drawing.Color]::FromArgb(255, 11, 11, 13),
        [float]135.0
    )
    $g.FillPath($bgBrush, $outerPath)

    # 2. Clipped Frosted Glass Layered Interior (Architectural Washi / Frosted Sheet)
    $state = $g.Save()
    $g.SetClip($outerPath)

    # Soft diffused top-left frosted luminance (no harsh glossy diagonal line)
    $diffPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diffPath.AddEllipse([float](-120.0 * $s), [float](-140.0 * $s), [float](560.0 * $s), [float](560.0 * $s))
    $diffBrush = [System.Drawing.Drawing2D.PathGradientBrush]::new($diffPath)
    $diffBrush.CenterColor = [System.Drawing.Color]::FromArgb(30, 255, 254, 248)
    $diffBrush.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.FillPath($diffBrush, $diffPath)
    $diffBrush.Dispose()
    $diffPath.Dispose()

    # Frosted inner glass card layer
    $innerPad = [float](64.0 * $s)
    $innerBox = [float](512.0 * $s - 2.0 * $innerPad)
    $innerPath = New-RoundedRectPath $innerPad $innerPad $innerBox $innerBox ([float](78.0 * $s))
    $innerRect = [System.Drawing.RectangleF]::new($innerPad, $innerPad, $innerBox, $innerBox)
    $innerFill = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $innerRect,
        [System.Drawing.Color]::FromArgb(20, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(6, 255, 255, 255),
        [float]135.0
    )
    $g.FillPath($innerFill, $innerPath)
    if ($size -ge 24) {
        $innerPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(34, 255, 255, 255), [float][Math]::Max(1.0, 2.0 * $s))
        $g.DrawPath($innerPen, $innerPath)
        $innerPen.Dispose()
    }
    $innerFill.Dispose()
    $innerPath.Dispose()

    # Subtle Vermilion optical aura around seal dot
    $glowPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $glowR = [float](116.0 * $s)
    $dotCx = [float](360.0 * $s)
    $dotCy = [float](152.0 * $s)
    $glowPath.AddEllipse([float]($dotCx - $glowR), [float]($dotCy - $glowR), [float]($glowR * 2.0), [float]($glowR * 2.0))
    $glowBrush = [System.Drawing.Drawing2D.PathGradientBrush]::new($glowPath)
    $glowBrush.CenterColor = [System.Drawing.Color]::FromArgb(44, 232, 80, 62)
    $glowBrush.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 232, 80, 62))
    $g.FillPath($glowBrush, $glowPath)
    $glowBrush.Dispose()
    $glowPath.Dispose()

    # 3. Kenya Hara "Light Slit / Aperture" Mark
    # Left architectural vertical light bar (Ma / Slit)
    $barX = [float](146.0 * $s)
    $barY = [float](146.0 * $s)
    $barW = [float][Math]::Max(2.0, 32.0 * $s)
    $barH = [float](220.0 * $s)
    $barPath = New-RoundedRectPath $barX $barY $barW $barH ([float](16.0 * $s))
    $paperBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 245, 244, 239))
    $g.FillPath($paperBrush, $barPath)

    # Right optical prism / abstract play beam with softly rounded corners
    $triPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $triPath.AddPolygon([System.Drawing.PointF[]]@(
        (New-Pt (216.0 * $s) (158.0 * $s)),
        (New-Pt (364.0 * $s) (256.0 * $s)),
        (New-Pt (216.0 * $s) (354.0 * $s))
    ))
    $prismBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        (New-Pt (212.0 * $s) (154.0 * $s)),
        (New-Pt (368.0 * $s) (358.0 * $s)),
        [System.Drawing.Color]::FromArgb(255, 247, 246, 241),
        [System.Drawing.Color]::FromArgb(236, 224, 222, 215)
    )
    $g.FillPath($prismBrush, $triPath)
    $prismSoftPen = [System.Drawing.Pen]::new($prismBrush, [float][Math]::Max(1.0, 14.0 * $s))
    $prismSoftPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($prismSoftPen, $triPath)
    $prismSoftPen.Dispose()

    # 4. Vermilion Optical Seal Dot (Kenya Hara Red Seal)
    $dotR = [float][Math]::Max(1.8, 22.0 * $s)
    $vermilionBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 232, 80, 62))
    $g.FillEllipse($vermilionBrush, [float]($dotCx - $dotR), [float]($dotCy - $dotR), [float]($dotR * 2.0), [float]($dotR * 2.0))

    $g.Restore($state)

    # 5. Crisp Specular Glass Rim around outer monolith
    $rimPenBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $bgRect,
        [System.Drawing.Color]::FromArgb(92, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(24, 255, 255, 255),
        [float]135.0
    )
    $rimPen = [System.Drawing.Pen]::new($rimPenBrush, [float][Math]::Max(1.0, 3.2 * $s))
    $g.DrawPath($rimPen, $outerPath)

    $rimPen.Dispose()
    $rimPenBrush.Dispose()
    $vermilionBrush.Dispose()
    $prismBrush.Dispose()
    $triPath.Dispose()
    $paperBrush.Dispose()
    $barPath.Dispose()
    $bgBrush.Dispose()
    $outerPath.Dispose()
    $g.Dispose()

    return $bmp
}

$master = Render-ApertureIcon 512
$pngPath = Join-Path $assets "App.png"
$master.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$master.Dispose()

$icoSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$entries = [System.Collections.Generic.List[object]]::new()

foreach ($sz in $icoSizes) {
    $bmp = Render-ApertureIcon $sz
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $entries.Add([PSCustomObject]@{ Size = $sz; Bytes = $ms.ToArray() })
    $ms.Dispose()
    $bmp.Dispose()
}

$icoStream = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($icoStream)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$entries.Count)

$offset = 6 + (16 * $entries.Count)
foreach ($entry in $entries) {
    $dim = if ($entry.Size -ge 256) { [byte]0 } else { [byte]$entry.Size }
    $writer.Write($dim)
    $writer.Write($dim)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$entry.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $entry.Bytes.Length
}
foreach ($entry in $entries) {
    $writer.Write($entry.Bytes)
}

$icoPath = Join-Path $assets "App.ico"
[System.IO.File]::WriteAllBytes($icoPath, $icoStream.ToArray())
$writer.Dispose()
$icoStream.Dispose()

Write-Host "Generated $pngPath and $icoPath ($($entries.Count) sizes, $((Get-Item $icoPath).Length) bytes)"
