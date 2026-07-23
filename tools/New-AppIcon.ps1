<#
.SYNOPSIS
    Generates Assets\AppIcon.ico (and the PNG logo assets) for MdPad.

.DESCRIPTION
    Draws the app mark — a rounded indigo tile carrying a white "M" and a down
    arrow, echoing the Markdown mark — at every size Windows asks for, then packs
    them into a multi-resolution .ico. Re-run after changing the design.
#>
[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent)
)

Add-Type -AssemblyName System.Drawing

$assets = Join-Path $ProjectRoot 'Assets'
if (-not (Test-Path $assets)) { New-Item -ItemType Directory -Path $assets | Out-Null }

function New-RoundedPath {
    param([single]$X, [single]$Y, [single]$W, [single]$H, [single]$R)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $R * 2
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $s = [single]$Size
    # Small sizes need to be nearly full-bleed or the glyph has no room to read.
    $inset = if ($Size -le 32) { $s * 0.02 } else { $s * 0.05 }
    $box = $s - ($inset * 2)
    $radius = $box * 0.22

    $tile = New-RoundedPath -X $inset -Y $inset -W $box -H $box -R $radius
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point($Size, $Size)),
        [System.Drawing.Color]::FromArgb(255, 99, 62, 226),
        [System.Drawing.Color]::FromArgb(255, 37, 129, 235))
    $g.FillPath($brush, $tile)

    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)

    # "M" drawn as strokes rather than text, so it stays crisp when scaled down.
    $stroke = $box * 0.115
    $mLeft = $inset + ($box * 0.17)
    $mTop = $inset + ($box * 0.30)
    $mBottom = $inset + ($box * 0.70)
    $mWidth = $box * 0.40
    $pen = New-Object System.Drawing.Pen($white, $stroke)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    # PowerShell folds "a + b" inside a constructor call into extra arguments,
    # so every coordinate is computed before the point is built.
    $mid = [single]($mLeft + ($mWidth / 2))
    $mRight = [single]($mLeft + $mWidth)
    $mValley = [single]($mTop + ($box * 0.20))

    $points = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF($mLeft, $mBottom)),
        (New-Object System.Drawing.PointF($mLeft, $mTop)),
        (New-Object System.Drawing.PointF($mid, $mValley)),
        (New-Object System.Drawing.PointF($mRight, $mTop)),
        (New-Object System.Drawing.PointF($mRight, $mBottom))
    )
    $g.DrawLines($pen, $points)

    # Down arrow: stem plus a solid head, the "↓" of the Markdown mark.
    $aX = $inset + ($box * 0.755)
    $aTop = $mTop
    $headHalf = $box * 0.115
    $headTop = $mBottom - ($headHalf * 1.7)
    $g.DrawLine($pen, $aX, $aTop, $aX, $headTop)
    $headLeft = [single]($aX - $headHalf)
    $headRight = [single]($aX + $headHalf)
    $head = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF($headLeft, $headTop)),
        (New-Object System.Drawing.PointF($headRight, $headTop)),
        (New-Object System.Drawing.PointF($aX, $mBottom))
    )
    $g.FillPolygon($white, $head)

    $pen.Dispose(); $white.Dispose(); $brush.Dispose(); $tile.Dispose(); $g.Dispose()
    return $bmp
}

function Save-Ico {
    param([int[]]$Sizes, [string]$Path)

    $streams = @()
    foreach ($size in $Sizes) {
        $bmp = New-IconBitmap -Size $size
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $streams += , @{ Size = $size; Bytes = $ms.ToArray() }
        $ms.Dispose()
    }

    $fs = [System.IO.File]::Create($Path)
    $bw = New-Object System.IO.BinaryWriter($fs)

    # ICONDIR
    $bw.Write([uint16]0)                    # reserved
    $bw.Write([uint16]1)                    # type: icon
    $bw.Write([uint16]$streams.Count)

    # Each entry stores PNG data, supported by Windows Vista and later.
    $offset = 6 + (16 * $streams.Count)
    foreach ($entry in $streams) {
        $dim = if ($entry.Size -ge 256) { 0 } else { $entry.Size }
        $bw.Write([byte]$dim)               # width
        $bw.Write([byte]$dim)               # height
        $bw.Write([byte]0)                  # palette
        $bw.Write([byte]0)                  # reserved
        $bw.Write([uint16]1)                # colour planes
        $bw.Write([uint16]32)               # bits per pixel
        $bw.Write([uint32]$entry.Bytes.Length)
        $bw.Write([uint32]$offset)
        $offset += $entry.Bytes.Length
    }
    foreach ($entry in $streams) { $bw.Write($entry.Bytes) }

    $bw.Flush(); $bw.Dispose(); $fs.Dispose()
}

$icoPath = Join-Path $assets 'AppIcon.ico'
Save-Ico -Sizes @(16, 20, 24, 32, 40, 48, 64, 128, 256) -Path $icoPath
Write-Host "wrote $icoPath ($((Get-Item $icoPath).Length) bytes)"

# PNG logo assets referenced by Package.appxmanifest.
$pngs = @{
    'Square44x44Logo.scale-200.png'                        = 88
    'Square44x44Logo.targetsize-24_altform-unplated.png'    = 24
    'Square44x44Logo.targetsize-48_altform-lightunplated.png' = 48
    'Square150x150Logo.scale-200.png'                      = 300
    'StoreLogo.png'                                        = 50
    'LockScreenLogo.scale-200.png'                         = 48
}
foreach ($name in $pngs.Keys) {
    $bmp = New-IconBitmap -Size $pngs[$name]
    $bmp.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
Write-Host "wrote $($pngs.Count) PNG logo assets"

# Preview sheet, handy when tweaking the design.
$preview = New-Object System.Drawing.Bitmap(560, 300)
$pg = [System.Drawing.Graphics]::FromImage($preview)
$pg.Clear([System.Drawing.Color]::FromArgb(255, 32, 32, 32))
$x = 20
foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
    $bmp = New-IconBitmap -Size $size
    $pg.DrawImage($bmp, $x, 20)
    $bmp.Dispose()
    $x += $size + 14
}
$pg.Dispose()
$previewPath = Join-Path $env:TEMP 'mdpad-icon-preview.png'
$preview.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()
Write-Host "preview: $previewPath"
