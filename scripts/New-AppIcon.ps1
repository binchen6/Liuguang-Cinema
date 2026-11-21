Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root "src/TV/Assets"
New-Item -ItemType Directory -Force -Path $assets | Out-Null
$bitmap = New-Object Drawing.Bitmap 256,256
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([Drawing.Color]::FromArgb(13,27,30))
$brush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(101,212,186))
$path = New-Object Drawing.Drawing2D.GraphicsPath
$rect = New-Object Drawing.Rectangle 24,40,208,176
$radius = 42
$path.AddArc($rect.X,$rect.Y,$radius,$radius,180,90)
$path.AddArc($rect.Right-$radius,$rect.Y,$radius,$radius,270,90)
$path.AddArc($rect.Right-$radius,$rect.Bottom-$radius,$radius,$radius,0,90)
$path.AddArc($rect.X,$rect.Bottom-$radius,$radius,$radius,90,90)
$path.CloseFigure()
$graphics.FillPath($brush,$path)
$play = [Drawing.Point[]]@((New-Object Drawing.Point 104,87),(New-Object Drawing.Point 104,169),(New-Object Drawing.Point 174,128))
$dark = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(13,42,37))
$graphics.FillPolygon($dark,$play)
$png = Join-Path $assets "App.png"
$bitmap.Save($png,[Drawing.Imaging.ImageFormat]::Png)
$pngBytes = [IO.File]::ReadAllBytes($png)
$stream = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter $stream
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
$writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0)
$writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$pngBytes.Length); $writer.Write([uint32]22)
$writer.Write($pngBytes)
[IO.File]::WriteAllBytes((Join-Path $assets "App.ico"),$stream.ToArray())
$writer.Dispose(); $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $brush.Dispose(); $dark.Dispose(); $path.Dispose()
