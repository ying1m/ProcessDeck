# Focus a window by process name, force it topmost, then capture the screen.
#
# Two problems this script solves:
#
#   1. SetForegroundWindow is unreliable when called from a background process
#      (Windows foreground lock), so the window is raised with
#      SetWindowPos(HWND_TOPMOST) for the duration of the capture.
#
#   2. Windows PowerShell 5.1 is DPI-UNUWARE. On a scaled display it sees a
#      virtualized resolution (e.g. 1707x1067 for a 2560x1600 panel at 150%)
#      and CopyFromScreen then grabs only the top-left corner of the real
#      desktop, scaled. We must declare Per-Monitor-V2 awareness BEFORE any
#      GDI/Forms object exists, otherwise the capture is both cropped and soft.
#
# Keep this file ASCII-only: PS 5.1 reads .ps1 files as ANSI unless they carry a
# UTF-8 BOM, so non-ASCII comments break parsing.
param(
    [Parameter(Mandatory = $true)][string]$ProcessName,
    [string]$Out = "$env:TEMP\shot.png",
    [switch]$Maximize
)

# Step 1: P/Invoke surface + DPI awareness, before touching WinForms/Drawing.
Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class ProcessDeckShot
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_SHOWWINDOW = 0x0040;
}
"@

# DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 == -4
$awarenessSet = [ProcessDeckShot]::SetProcessDpiAwarenessContext([IntPtr](-4))
Write-Output "PerMonitorV2 awareness set: $awarenessSet"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$proc = Get-Process -Name $ProcessName -ErrorAction Stop |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Select-Object -First 1

if (-not $proc) {
    throw "No window found for process '$ProcessName'."
}

$handle = $proc.MainWindowHandle

if ($Maximize) {
    [ProcessDeckShot]::ShowWindow($handle, 3) | Out-Null   # SW_MAXIMIZE
}
elseif ([ProcessDeckShot]::IsIconic($handle)) {
    [ProcessDeckShot]::ShowWindow($handle, 9) | Out-Null   # SW_RESTORE
}

[ProcessDeckShot]::BringWindowToTop($handle) | Out-Null
[ProcessDeckShot]::SetForegroundWindow($handle) | Out-Null

$raiseFlags = [ProcessDeckShot]::SWP_NOMOVE -bor [ProcessDeckShot]::SWP_NOSIZE -bor [ProcessDeckShot]::SWP_SHOWWINDOW
[ProcessDeckShot]::SetWindowPos($handle, [ProcessDeckShot]::HWND_TOPMOST, 0, 0, 0, 0, $raiseFlags) | Out-Null

# Give WebView2 time to repaint after the resize / z-order change.
Start-Sleep -Milliseconds 2500

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Output "Capturing $($bounds.Width)x$($bounds.Height) (physical pixels)"

$bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)

try {
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
    [ProcessDeckShot]::SetWindowPos($handle, [ProcessDeckShot]::HWND_NOTOPMOST, 0, 0, 0, 0, $raiseFlags) | Out-Null
}

Write-Output $Out
