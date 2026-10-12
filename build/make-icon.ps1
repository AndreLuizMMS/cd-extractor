# Generates the application icon (multi-size .ico with PNG frames) and a 256 px PNG preview.
# Usage: powershell -File build\make-icon.ps1
param(
    [string]$OutIco = (Join-Path $PSScriptRoot '..\src\MediaRescue.App\Assets\app.ico'),
    [string]$OutPng = (Join-Path $PSScriptRoot '..\docs\icon-256.png')
)
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Background tile
    $bg = New-RoundedRect 0 0 $s $s ($s * 0.22)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF($s, $s)),
        [System.Drawing.Color]::FromArgb(255, 30, 58, 138), [System.Drawing.Color]::FromArgb(255, 14, 165, 233))
    $g.FillPath($grad, $bg)

    # Disc
    $cx = $s * 0.47; $cy = $s * 0.47; $r = $s * 0.36
    $disc = New-Object System.Drawing.RectangleF(($cx - $r), ($cy - $r), (2 * $r), (2 * $r))
    $g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 241, 245, 249))), $disc)
    # Sheen wedges
    $g.FillPie((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(110, 56, 189, 248))), $disc.X, $disc.Y, $disc.Width, $disc.Height, 200, 50)
    $g.FillPie((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(90, 192, 132, 252))), $disc.X, $disc.Y, $disc.Width, $disc.Height, 250, 30)
    $g.FillPie((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(110, 56, 189, 248))), $disc.X, $disc.Y, $disc.Width, $disc.Height, 20, 50)
    $g.FillPie((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(90, 192, 132, 252))), $disc.X, $disc.Y, $disc.Width, $disc.Height, 70, 30)
    # Hub ring and hole
    $r2 = $s * 0.14
    $g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 203, 213, 225))), ($cx - $r2), ($cy - $r2), (2 * $r2), (2 * $r2))
    $r3 = $s * 0.075
    $g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 30, 64, 150))), ($cx - $r3), ($cy - $r3), (2 * $r3), (2 * $r3))

    # "Extract" badge: orange circle with a white down arrow (omitted at tiny sizes)
    if ($s -ge 24) {
        $bx = $s * 0.73; $by = $s * 0.73; $br = $s * 0.215
        $g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)), ($bx - $br), ($by - $br), (2 * $br), (2 * $br))
        $bi = $br * 0.86
        $g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 249, 115, 22))), ($bx - $bi), ($by - $bi), (2 * $bi), (2 * $bi))
        $a = $bi * 0.55
        $arrow = New-Object System.Drawing.Drawing2D.GraphicsPath
        $pts = @(
            (New-Object System.Drawing.PointF(($bx - $a * 0.32), ($by - $a))),
            (New-Object System.Drawing.PointF(($bx + $a * 0.32), ($by - $a))),
            (New-Object System.Drawing.PointF(($bx + $a * 0.32), ($by - $a * 0.05))),
            (New-Object System.Drawing.PointF(($bx + $a * 0.95), ($by - $a * 0.05))),
            (New-Object System.Drawing.PointF($bx, ($by + $a))),
            (New-Object System.Drawing.PointF(($bx - $a * 0.95), ($by - $a * 0.05))),
            (New-Object System.Drawing.PointF(($bx - $a * 0.32), ($by - $a * 0.05)))
        )
        $arrow.AddPolygon([System.Drawing.PointF[]]$pts)
        $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)), $arrow)
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) {
        New-Item -ItemType Directory -Force (Split-Path $OutPng) | Out-Null
        $bmp.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $bmp.Dispose()
    $frames += , @($s, $ms.ToArray())
}

New-Item -ItemType Directory -Force (Split-Path $OutIco) | Out-Null
$fs = [System.IO.File]::Create($OutIco)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = $f[0]; $data = $f[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($f in $frames) { $bw.Write($f[1]) }
$bw.Dispose(); $fs.Dispose()
"{0} ({1:N0} bytes, {2} tamanhos)" -f (Resolve-Path $OutIco), (Get-Item $OutIco).Length, $frames.Count
