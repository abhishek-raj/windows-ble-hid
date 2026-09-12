# Regenerates src/BleHid.App/Assets/blehid.ico.
# Kept in the repo so the binary asset can be re-derived instead of being unmaintainable.
# Run with Windows PowerShell 5.1: .\tools\New-AppIcon.ps1

[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\BleHid.App\Assets\blehid.ico'),
    # Markdown renderers will not show an .ico, so the README needs a PNG of the same art.
    [string]$PngPath = (Join-Path $PSScriptRoot '..\src\BleHid.App\Assets\blehid.png')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

# All geometry is in fractions of the tile, so every size is drawn from one source of truth.
# The Bluetooth rune is one polyline, expressed in its own unit square.
$RunePoints = @(
    @(0.25, 0.30), @(0.75, 0.70), @(0.50, 0.95),
    @(0.50, 0.05), @(0.75, 0.30), @(0.25, 0.70)
)

function Add-RoundedRect {
    param(
        [System.Drawing.Drawing2D.GraphicsPath]$Path,
        [double]$X, [double]$Y, [double]$Width, [double]$Height,
        [double]$TopRadius, [double]$BottomRadius
    )

    $limit = [math]::Min($Width, $Height) / 2
    # A zero-sized arc is degenerate in GDI+, so keep a floor on the radius.
    $t = [math]::Max([math]::Min($TopRadius, $limit), 0.01)
    $b = [math]::Max([math]::Min($BottomRadius, $limit), 0.01)

    $Path.StartFigure()
    $Path.AddArc([single]$X, [single]$Y, [single]($t * 2), [single]($t * 2), [single]180, [single]90)
    $Path.AddArc([single]($X + $Width - $t * 2), [single]$Y, [single]($t * 2), [single]($t * 2), [single]270, [single]90)
    $Path.AddArc([single]($X + $Width - $b * 2), [single]($Y + $Height - $b * 2), [single]($b * 2), [single]($b * 2), [single]0, [single]90)
    $Path.AddArc([single]$X, [single]($Y + $Height - $b * 2), [single]($b * 2), [single]($b * 2), [single]90, [single]90)
    $Path.CloseFigure()
}

function New-TileBitmap {
    param([int]$Size)

    # Supersample: GDI+ anti-aliasing alone leaves the diagonals ragged at tray sizes.
    $scale = 4
    $s = $Size * $scale

    # Detail has to drop out as the tile shrinks or sub-pixel features render as grey mush.
    # The silhouettes never change, so the identity survives across sizes.
    $keycaps = $Size -ge 32
    $rows = $Size -ge 20

    $bitmap = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        $radius = $s * 0.22
        $d = $radius * 2
        $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
        $tile.AddArc(0, 0, $d, $d, 180, 90)
        $tile.AddArc($s - $d, 0, $d, $d, 270, 90)
        $tile.AddArc($s - $d, $s - $d, $d, $d, 0, 90)
        $tile.AddArc(0, $s - $d, $d, $d, 90, 90)
        $tile.CloseFigure()

        $top = New-Object System.Drawing.Point(0, 0)
        $bottom = New-Object System.Drawing.Point(0, $s)
        $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $top, $bottom,
            [System.Drawing.Color]::FromArgb(255, 64, 156, 255),
            [System.Drawing.Color]::FromArgb(255, 10, 74, 196))
        $g.FillPath($fill, $tile)
        $fill.Dispose()
        $tile.Dispose()

        $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)

        # Keyboard. Alternate fill turns the keycap subpaths into holes, so the tile gradient
        # shows through them instead of needing a second opaque colour.
        $keyboard = New-Object System.Drawing.Drawing2D.GraphicsPath
        $keyboard.FillMode = [System.Drawing.Drawing2D.FillMode]::Alternate
        Add-RoundedRect $keyboard (0.12 * $s) (0.56 * $s) (0.48 * $s) (0.28 * $s) (0.045 * $s) (0.045 * $s)
        if ($keycaps) {
            foreach ($row in 0, 1) {
                foreach ($col in 0, 1, 2, 3) {
                    Add-RoundedRect $keyboard `
                        ((0.165 + $col * 0.104) * $s) ((0.605 + $row * 0.07) * $s) `
                        (0.078 * $s) (0.05 * $s) (0.012 * $s) (0.012 * $s)
                }
            }
            Add-RoundedRect $keyboard (0.215 * $s) (0.745 * $s) (0.29 * $s) (0.05 * $s) (0.012 * $s) (0.012 * $s)
        }
        elseif ($rows) {
            # Individual keycaps are gone; two bands still say "keyboard" rather than "white bar".
            foreach ($y in 0.62, 0.72) {
                Add-RoundedRect $keyboard (0.165 * $s) ($y * $s) (0.39 * $s) (0.06 * $s) (0.02 * $s) (0.02 * $s)
            }
        }
        $g.FillPath($white, $keyboard)
        $keyboard.Dispose()

        # Mouse, sitting to the right of the keyboard at the same baseline. It is close to an
        # oval on purpose: a narrower capsule with a centred slot reads as a padlock.
        $mouse = New-Object System.Drawing.Drawing2D.GraphicsPath
        $mouse.FillMode = [System.Drawing.Drawing2D.FillMode]::Alternate
        Add-RoundedRect $mouse (0.68 * $s) (0.56 * $s) (0.18 * $s) (0.28 * $s) (0.09 * $s) (0.075 * $s)
        if ($keycaps) {
            Add-RoundedRect $mouse (0.756 * $s) (0.605 * $s) (0.028 * $s) (0.05 * $s) (0.014 * $s) (0.014 * $s)
        }
        $g.FillPath($white, $mouse)
        $mouse.Dispose()
        $white.Dispose()

        # Bluetooth rune above the pair, saying how they connect.
        $glyphWidth = $s * 0.19
        $glyphHeight = $s * 0.36
        $cx = $s * 0.5
        $cy = $s * 0.28
        $points = foreach ($p in $RunePoints) {
            New-Object System.Drawing.PointF(
                [single]($cx + ($p[0] - 0.5) * $glyphWidth * 2),
                [single]($cy + ($p[1] - 0.5) * $glyphHeight / 0.9))
        }

        # Thicken the stroke as the tile shrinks, or the rune fades to grey.
        $strokeWidth = if ($keycaps) { 0.055 } else { 0.075 }
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single]($s * $strokeWidth))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawLines($pen, [System.Drawing.PointF[]]$points)
        $pen.Dispose()
    }
    finally {
        $g.Dispose()
    }

    if ($Size -eq $s) { return $bitmap }

    $final = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $fg = [System.Drawing.Graphics]::FromImage($final)
    try {
        $fg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $fg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $fg.Clear([System.Drawing.Color]::Transparent)
        $fg.DrawImage($bitmap, (New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)))
    }
    finally {
        $fg.Dispose()
        $bitmap.Dispose()
    }

    return $final
}

function ConvertTo-DibFrame {
    param([System.Drawing.Bitmap]$Bitmap)

    $size = $Bitmap.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $pixels = New-Object byte[] ($data.Stride * $size)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    }
    finally {
        $Bitmap.UnlockBits($data)
    }

    $maskStride = [int]([math]::Floor(($size + 31) / 32) * 4)
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        # BITMAPINFOHEADER: double height covers the XOR image plus the legacy AND mask.
        $writer.Write([uint32]40)
        $writer.Write([int32]$size)
        $writer.Write([int32]($size * 2))
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]0)
        $writer.Write([uint32](($size * $size * 4) + ($maskStride * $size)))
        $writer.Write([int32]0); $writer.Write([int32]0)
        $writer.Write([uint32]0); $writer.Write([uint32]0)
        $writer.Flush()

        # DIB rows run bottom-up. Buffers go straight to the stream because BinaryWriter.Write
        # cannot disambiguate byte[] from char[] under PowerShell's overload resolution.
        $rowBytes = $size * 4
        $xor = New-Object byte[] ($size * $rowBytes)
        for ($y = 0; $y -lt $size; $y++) {
            [System.Buffer]::BlockCopy($pixels, ($size - 1 - $y) * $data.Stride, $xor, $y * $rowBytes, $rowBytes)
        }
        $stream.Write($xor, 0, $xor.Length)

        # The alpha channel does the masking; the AND mask stays empty but must be present.
        $mask = New-Object byte[] ($maskStride * $size)
        $stream.Write($mask, 0, $mask.Length)
    }
    finally {
        $writer.Dispose()
    }

    # The comma stops PowerShell unrolling the array into a stream of loose bytes.
    return , $stream.ToArray()
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $bitmap = New-TileBitmap -Size $size
    if ($size -ge 256) {
        # Only the 256 frame is PNG: it is where the size saving matters, and every decoder
        # that reads a 256 frame at all understands PNG.
        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = [byte[]]$stream.ToArray()
        $stream.Dispose()
    }
    else {
        $bytes = [byte[]](ConvertTo-DibFrame -Bitmap $bitmap)
    }

    $bitmap.Dispose()
    $frames += [pscustomobject]@{ Size = $size; Bytes = $bytes }
}

$directory = Split-Path -Parent $OutputPath
if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }

$file = [System.IO.File]::Create($OutputPath)
$writer = New-Object System.IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)

    # Vista and later accept a PNG payload per entry, which is what keeps the 256 frame affordable.
    $offset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }

    $writer.Flush()
    foreach ($frame in $frames) { $file.Write($frame.Bytes, 0, $frame.Bytes.Length) }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Host "Wrote $OutputPath ($($frames.Count) sizes, $((Get-Item $OutputPath).Length) bytes)"

# The 256 frame is already PNG-encoded, so the README asset is a straight copy of it.
$png = ($frames | Where-Object { $_.Size -eq 256 }).Bytes
[System.IO.File]::WriteAllBytes($PngPath, $png)
Write-Host "Wrote $PngPath ($($png.Length) bytes)"
