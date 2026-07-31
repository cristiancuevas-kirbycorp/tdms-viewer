# Generates a modern app icon (waveform on a rounded gradient tile) as a multi-size .ico.
Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded tile
    $pad = [double]$size * 0.06
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($size - 2*$pad), ($size - 2*$pad))
    $radius = [double]$size * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $c1 = [System.Drawing.Color]::FromArgb(255, 37, 99, 235)   # blue
    $c2 = [System.Drawing.Color]::FromArgb(255, 34, 197, 200)  # teal
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 55.0)
    $g.FillPath($grad, $path)

    # Baseline axis
    $axisPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(120, 255, 255, 255), [single]([double]$size*0.02))
    $ax0 = [single]($size*0.20); $ax1 = [single]($size*0.80); $ayb = [single]($size*0.74)
    $g.DrawLine($axisPen, $ax0, [single]($size*0.24), $ax0, $ayb)
    $g.DrawLine($axisPen, $ax0, $ayb, $ax1, $ayb)

    # Waveforms (two series)
    function Points($ys) {
        $n = $ys.Count
        $x0 = 0.20; $x1 = 0.80
        $pts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
        for ($i=0; $i -lt $n; $i++) {
            $x = $x0 + ($x1 - $x0) * ($i / [double]($n-1))
            $p = New-Object System.Drawing.PointF([single]($x*$size), [single]($ys[$i]*$size))
            $pts.Add($p)
        }
        return ,$pts.ToArray()
    }

    $pen2 = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150, 255, 255, 255), [single]([double]$size*0.045))
    $pen2.StartCap = 'Round'; $pen2.EndCap = 'Round'; $pen2.LineJoin = 'Round'
    $g.DrawCurve($pen2, (Points @(0.66, 0.62, 0.58, 0.60, 0.50, 0.52, 0.46, 0.44)), [single]0.5)

    $pen1 = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single]([double]$size*0.06))
    $pen1.StartCap = 'Round'; $pen1.EndCap = 'Round'; $pen1.LineJoin = 'Round'
    $g.DrawCurve($pen1, (Points @(0.62, 0.60, 0.48, 0.52, 0.34, 0.40, 0.26, 0.30)), [single]0.5)

    $g.Dispose()
    return $bmp
}

$sizes = @(256, 128, 64, 48, 32, 16)
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,($ms.ToArray())
    $bmp.Dispose(); $ms.Dispose()
}

$out = Join-Path $PSScriptRoot '..\TdmsViewer\app.ico'
$fs = [System.IO.File]::Create((Resolve-Path -LiteralPath (Split-Path $out)).Path + '\' + (Split-Path $out -Leaf))
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)  # ICONDIR
$offset = 6 + 16 * $sizes.Count
for ($i=0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([Byte]($(if ($s -ge 256) {0} else {$s})))
    $bw.Write([Byte]($(if ($s -ge 256) {0} else {$s})))
    $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush(); $bw.Close(); $fs.Close()
Write-Host "Wrote icon with $($sizes.Count) sizes."
