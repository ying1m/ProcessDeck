# Generate ProcessDeck's app.ico (256x256, PNG-compressed ICO, Vista+).
#
# Must run under Windows PowerShell 5.1 (powershell.exe), which ships with
# System.Drawing. Keep this file ASCII-only: PS 5.1 reads .ps1 files as ANSI
# unless they carry a UTF-8 BOM, so non-ASCII comments break parsing.
#
# The ICO container is written by hand: a 6-byte ICONDIR, one 16-byte
# ICONDIRENTRY, then the PNG payload. Vista and later accept PNG inside ICO,
# which is the only practical way to get a crisp 256x256 icon without
# hand-rolling BMP+AND-mask data.
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\src\ProcessDeck.App\app.ico')
)

Add-Type -AssemblyName System.Drawing

$size = 256
$bitmap = New-Object System.Drawing.Bitmap $size, $size
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

function New-RoundedPath {
    param([int]$X, [int]$Y, [int]$W, [int]$H, [int]$R)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $R * 2

    $path.AddArc($X, $Y, $d, $d, 180, 90) | Out-Null
    $path.AddArc($X + $W - $d, $Y, $d, $d, 270, 90) | Out-Null
    $path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90) | Out-Null
    $path.AddArc($X, $Y + $H - $d, $d, $d, 90, 90) | Out-Null
    $path.CloseFigure()

    return $path
}

try {
    # Background: blue -> purple diagonal gradient, matching the panel's brand mark.
    $bounds = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $background = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $bounds,
        [System.Drawing.Color]::FromArgb(255, 59, 130, 246),
        [System.Drawing.Color]::FromArgb(255, 139, 92, 246),
        45.0)

    $backgroundPath = New-RoundedPath -X 8 -Y 8 -W 240 -H 240 -R 56
    $graphics.FillPath($background, $backgroundPath)

    # Three rows = "a panel of apps".
    $rowBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(238, 255, 255, 255))

    foreach ($y in 82, 124, 166) {
        $rowPath = New-RoundedPath -X 52 -Y $y -W 152 -H 28 -R 14
        $graphics.FillPath($rowBrush, $rowPath)
        $rowPath.Dispose()
    }

    # A green "running" dot on the first row.
    $dotBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 46, 160, 67))
    $graphics.FillEllipse($dotBrush, 64, 89, 14, 14)

    # Encode as PNG, then wrap in an ICO container.
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $png = $stream.ToArray()

    $directory = Split-Path -Parent $Out
    if (-not (Test-Path $directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $file = [System.IO.File]::Create($Out)
    $writer = New-Object System.IO.BinaryWriter($file)

    $writer.Write([UInt16]0)             # reserved
    $writer.Write([UInt16]1)             # type: 1 = icon
    $writer.Write([UInt16]1)             # image count
    $writer.Write([Byte]0)               # width  0 means 256
    $writer.Write([Byte]0)               # height 0 means 256
    $writer.Write([Byte]0)               # palette size
    $writer.Write([Byte]0)               # reserved
    $writer.Write([UInt16]1)             # color planes
    $writer.Write([UInt16]32)            # bits per pixel
    $writer.Write([UInt32]$png.Length)   # payload size
    $writer.Write([UInt32]22)            # payload offset = 6 + 16
    $writer.Write($png)

    $writer.Close()
    $file.Close()

    Write-Output ("Wrote {0} ({1} bytes)" -f $Out, (Get-Item $Out).Length)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}
