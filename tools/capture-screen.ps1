# Capture the primary screen to a PNG file.
#
# Must run under Windows PowerShell 5.1 (powershell.exe), which ships with
# System.Drawing / System.Windows.Forms. Keep this file ASCII-only: PS 5.1 reads
# .ps1 files as ANSI unless they carry a UTF-8 BOM, so non-ASCII comments break parsing.
param(
    [string]$Out = "$env:TEMP\screen.png"
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)

try {
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Output $Out
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}
