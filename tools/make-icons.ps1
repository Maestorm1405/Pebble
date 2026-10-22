# Generates Pebble's icon (.ico for the exe/tray) and the PNG logos the MSIX/Store package needs.
param([string]$Out = (Join-Path $PSScriptRoot '..\Assets'))
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null

function New-PebbleBitmap([int]$w, [int]$h, [double]$scale = 0.86) {
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
  $s = [Math]::Min($w, $h) * $scale
  $pw = $s; $ph = $s * 0.46
  $x = ($w - $pw) / 2; $y = ($h - $ph) / 2
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc($x, $y, $ph, $ph, 90, 180)
  $path.AddArc($x + $pw - $ph, $y, $ph, $ph, 270, 180)
  $path.CloseFigure()
  $rect = New-Object System.Drawing.RectangleF $x, $y, $pw, $ph
  $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 58, 58, 62)), ([System.Drawing.Color]::FromArgb(255, 6, 6, 8)), 90
  $g.FillPath($grad, $path)
  $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 255, 255, 255)), ([Math]::Max(1, $s / 48))
  $g.DrawPath($pen, $path)
  # activity dot + a small "time" bar, like the island itself
  $d = $ph * 0.30
  $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 48, 209, 88))), ($x + $pw - $ph / 2 - $d / 2), ($y + ($ph - $d) / 2), $d, $d)
  if ($s -ge 40) {
    $bw = $pw * 0.30; $bh = [Math]::Max(2, $ph * 0.12)
    $bar = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bx = $x + $ph * 0.55; $by = $y + ($ph - $bh) / 2
    $bar.AddArc($bx, $by, $bh, $bh, 90, 180); $bar.AddArc($bx + $bw - $bh, $by, $bh, $bh, 270, 180); $bar.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(220, 255, 255, 255))), $bar)
  }
  $g.Dispose()
  $bmp
}

function Save-Png($bmp, $file) { $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose() }

# .ico with PNG-compressed entries
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
  $b = New-PebbleBitmap $sz $sz 0.94
  $ms = New-Object System.IO.MemoryStream
  $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
  , $ms.ToArray()
}
$fs = [System.IO.File]::Create((Join-Path $Out 'Pebble.ico'))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $sz = $sizes[$i]; $len = $pngs[$i].Length
  $bw.Write([byte]($sz % 256)); $bw.Write([byte]($sz % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
  $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()

# MSIX / Store logos
Save-Png (New-PebbleBitmap 44 44 0.96) (Join-Path $Out 'Square44x44Logo.png')
Save-Png (New-PebbleBitmap 150 150 0.80) (Join-Path $Out 'Square150x150Logo.png')
Save-Png (New-PebbleBitmap 310 150 0.62) (Join-Path $Out 'Wide310x150Logo.png')
Save-Png (New-PebbleBitmap 50 50 0.96) (Join-Path $Out 'StoreLogo.png')
Save-Png (New-PebbleBitmap 300 300 0.86) (Join-Path $Out 'Pebble-300.png')
"icons written to $Out"
