# Capture helpers for the Microsoft Store screenshots.
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File packaging/store/capture.ps1 -Scene welcome
#
# The display runs at 150% scaling, so every interaction below is expressed as a
# fraction of the client area instead of hard-coded pixels; that keeps the clicks on
# the same controls at any DPI or window size.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('welcome', 'reading', 'markup', 'search')]
    [string] $Scene,

    [string] $Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string] $Sample = (Join-Path $Root 'tests\Assets\sample.pdf')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -Path (Join-Path $PSScriptRoot 'Shot.cs')

# Without this the process is DPI-unaware: window rectangles come back scaled, and
# the capture is cut at the virtualised screen edge instead of the real one.
[Shot]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

$exe = Join-Path $Root 'artifacts\msix\app-x64\AirPdf.exe'
$output = Join-Path $Root 'docs\store'
New-Item -ItemType Directory -Path $output -Force | Out-Null

function Get-ClientSize($handle) {
    $rect = New-Object Shot+RECT
    [Shot]::GetClientRect($handle, [ref] $rect) | Out-Null
    return @(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
}

function Get-Origin($handle) {
    $point = New-Object Shot+POINT
    [Shot]::ClientToScreen($handle, [ref] $point) | Out-Null
    return $point
}

function Click($handle, [double] $fx, [double] $fy) {
    $size = Get-ClientSize $handle
    $origin = Get-Origin $handle
    $x = [int] ($size[0] * $fx)
    $y = [int] ($size[1] * $fy)
    [Shot]::SetCursorPos(($origin.X + $x), ($origin.Y + $y)) | Out-Null
    Start-Sleep -Milliseconds 300
    [Shot]::mouse_event([Shot]::LeftDown, 0, 0, 0, [IntPtr]::Zero)
    [Shot]::mouse_event([Shot]::LeftUp, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 700
}

function Drag($handle, [double] $fx1, [double] $fy1, [double] $fx2, [double] $fy2, [int] $steps = 16) {
    $size = Get-ClientSize $handle
    $origin = Get-Origin $handle
    $x1 = [int] ($size[0] * $fx1); $y1 = [int] ($size[1] * $fy1)
    $x2 = [int] ($size[0] * $fx2); $y2 = [int] ($size[1] * $fy2)

    [Shot]::SetCursorPos(($origin.X + $x1), ($origin.Y + $y1)) | Out-Null
    Start-Sleep -Milliseconds 350
    [Shot]::mouse_event([Shot]::LeftDown, 0, 0, 0, [IntPtr]::Zero)
    for ($i = 1; $i -le $steps; $i++) {
        $x = [int] ($x1 + (($x2 - $x1) * $i / $steps))
        $y = [int] ($y1 + (($y2 - $y1) * $i / $steps))
        [Shot]::SetCursorPos(($origin.X + $x), ($origin.Y + $y)) | Out-Null
        Start-Sleep -Milliseconds 60
    }
    Start-Sleep -Milliseconds 150
    [Shot]::mouse_event([Shot]::LeftUp, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 900
}

function Save-Window($handle, $name) {
    $window = New-Object Shot+RECT
    [Shot]::GetWindowRect($handle, [ref] $window) | Out-Null

    # PrintWindow renders per-monitor-DPI-aware windows at the wrong scale, which
    # clips the right and bottom edges on this 150% display, so the client area is
    # copied straight off the screen instead. The window is raised first.
    $size = Get-ClientSize $handle
    $origin = Get-Origin $handle

    $bitmap = New-Object System.Drawing.Bitmap($size[0], $size[1])
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($origin.X, $origin.Y, 0, 0, $bitmap.Size)
    $graphics.Dispose()

    $shot = $bitmap
    $path = Join-Path $output $name
    $shot.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $shot.Dispose()
    $bitmap.Dispose()

    Write-Host "saved $name $($size[0])x$($size[1])"
}

if ($Scene -eq 'welcome') {
    $process = Start-Process -FilePath $exe -PassThru
}
else {
    $process = Start-Process -FilePath $exe -ArgumentList @($Sample) -PassThru
}

Start-Sleep -Seconds 8

$handle = $process.MainWindowHandle
[Shot]::ShowWindow($handle, 9) | Out-Null
[Shot]::SetWindowPos($handle, [IntPtr]::Zero, 80, 60, 2200, 1320, 0x0004) | Out-Null
[Shot]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 1600

try {
    switch ($Scene) {
        'welcome' {
            Save-Window $handle 'screenshot-welcome.png'
        }

        'reading' {
            [System.Windows.Forms.SendKeys]::SendWait('^2')      # fit width
            Start-Sleep -Milliseconds 900
            [System.Windows.Forms.SendKeys]::SendWait('{PGDN}')  # first text page
            Start-Sleep -Milliseconds 1200
            Save-Window $handle 'screenshot-reading.png'
        }

        'markup' {
            [System.Windows.Forms.SendKeys]::SendWait('^2')
            Start-Sleep -Milliseconds 1000

            # The reading position is seeded to page 3 before launch, so this is already
            # a text page; paging proved unreliable because focus lands on the tab strip.
            Drag $handle 0.30 0.525 0.88 0.525
            [System.Windows.Forms.SendKeys]::SendWait('^+h')
            Start-Sleep -Milliseconds 1400

            # Underline the same paragraph with ink.
            [System.Windows.Forms.SendKeys]::SendWait('^+k')
            Start-Sleep -Milliseconds 900
            Drag $handle 0.30 0.585 0.84 0.585 18
            Start-Sleep -Milliseconds 900
            [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
            Start-Sleep -Milliseconds 900
            Save-Window $handle 'screenshot-markup.png'
        }

        'search' {
            [System.Windows.Forms.SendKeys]::SendWait('^2')
            Start-Sleep -Milliseconds 700
            [System.Windows.Forms.SendKeys]::SendWait('^f')
            Start-Sleep -Milliseconds 1000
            [System.Windows.Forms.SendKeys]::SendWait('quiet')
            Start-Sleep -Milliseconds 700
            [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
            Start-Sleep -Seconds 5
            Save-Window $handle 'screenshot-search.png'
        }
    }
}
finally {
    if (-not $process.HasExited) { $process | Stop-Process -Force }
}







