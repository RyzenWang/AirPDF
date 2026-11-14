<#
.SYNOPSIS
    Builds a sideloadable MSIX package for AirPDF from a folder publish.

.DESCRIPTION
    Proof of concept for Microsoft Store distribution through the MSIX path.
    The script:

      1. resolves makeappx.exe (installed Windows SDK, or the
         Microsoft.Windows.SDK.BuildTools NuGet package downloaded on demand),
      2. publishes the app as a folder (MSIX cannot use PublishSingleFile), ahead of
         time by default: the AOT build is about half the size and starts in well
         under a second, so the package matches what the releases ship,
      3. generates the logo assets MSIX requires from the existing icon,
      4. expands the manifest template and packs the .msix,
      5. unpacks the result and asserts the parts Microsoft certifies.

    The package is unsigned by default. Pass -CertificatePath to sign it with a
    test certificate; a Store submission is re-signed by Microsoft, so a real
    certificate is not required for that path.

    This script does not touch the release workflow.

.PARAMETER Version
    Four-part package version, as MSIX requires (for example 0.3.3.0). The last
    part must stay 0 for Store submissions.

.PARAMETER Publisher
    Certificate subject. Must match the signing certificate exactly when signed.

.PARAMETER DisplayName
    Name shown in Start and in the Open with list. Defaults to AirPDF. A test package
    built next to an installed Store build should pass something else, or the two
    entries are indistinguishable in the shell.

.PARAMETER NoAot
    Publish the JIT build instead. Roughly twice the download and a few seconds to
    the first window, but it needs no C++ toolchain and packages faster.

.EXAMPLE
    ./Build-Msix.ps1 -Version 0.3.3.0

.EXAMPLE
    ./Build-Msix.ps1 -Version 0.3.3.0 -NoAot

.PARAMETER NoAot
    Publish the JIT build instead. Roughly twice the download and a few seconds to
    the first window, but it needs no C++ toolchain and packages faster.

.EXAMPLE
    ./Build-Msix.ps1 -Version 0.3.3.0

.EXAMPLE
    ./Build-Msix.ps1 -Version 0.3.3.0 -NoAot -CertificatePath ../../artifacts/test.pfx -CertificatePassword test
#>
[CmdletBinding()]
param(
    [string] $Version = '0.3.3.0',
    [string] $Publisher = 'CN=AirPDF Test Signing (not for release)',
    [string] $PublisherDisplayName = 'AirPDF contributors',
    [string] $IdentityName = 'AirPDF',
    [string] $DisplayName = 'AirPDF',
    [string] $Configuration = 'Release',
    [string] $OutputDirectory = 'artifacts/msix',
    [string] $CertificatePath,
    [string] $CertificatePassword,
    [switch] $SkipPublish,
    [switch] $ForStore,
    [switch] $NoAot,
    [ValidateSet('x64', 'arm64')]
    [string] $Architecture = 'x64',
    [switch] $Symbols
)

$ErrorActionPreference = 'Stop'

function Write-Step([string] $Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path (Join-Path $root 'AirPdf.sln'))) {
    throw "Could not find the repository root from $PSScriptRoot."
}

$outputDirectory = Join-Path $root $OutputDirectory
$publishDirectory = Join-Path $outputDirectory "app-$Architecture"
$layoutDirectory = Join-Path $outputDirectory "layout-$Architecture"

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "-Version must have four numeric parts, for example 0.3.3.0 (got '$Version')."
}

$versionParts = $Version.Split('.') | ForEach-Object { [int] $_ }
if ($versionParts | Where-Object { $_ -gt 65535 }) {
    throw "-Version parts must each be 65535 or less (got '$Version')."
}

if ($ForStore -and $versionParts[0] -lt 1) {
    throw "-Version must start at 1.0.0.0 or higher for a Store submission (got '$Version')."
}

if (-not $Version.EndsWith('.0')) {
    Write-Warning 'The fourth version part is reserved for the Store and must be 0 for submissions.'
}

# Identity rules come from the package manifest schema. Checking them here turns a
# Partner Center upload failure into a local error with a readable message.
function Assert-PackageIdentity {
    if ($IdentityName -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]*$') {
        throw "-IdentityName may only contain letters, digits, periods, and dashes, and must start with a letter or digit (got '$IdentityName')."
    }

    if ($IdentityName.Length -lt 3 -or $IdentityName.Length -gt 50) {
        throw "-IdentityName must be 3 to 50 characters long (got $($IdentityName.Length))."
    }

    if ([string]::IsNullOrWhiteSpace($Publisher) -or -not $Publisher.StartsWith('CN=')) {
        throw "-Publisher must be a distinguished name starting with 'CN=', for example 'CN=1A2B3C4D-0000-0000-0000-000000000000' (got '$Publisher')."
    }

    if ($Publisher -match '[\r\n]') {
        throw '-Publisher must be a single line.'
    }

    if ([string]::IsNullOrWhiteSpace($PublisherDisplayName)) {
        throw '-PublisherDisplayName is required.'
    }

    if ($PublisherDisplayName.Length -gt 256) {
        throw '-PublisherDisplayName must be 256 characters or fewer (got ' + $PublisherDisplayName.Length + ').'
    }
}

$defaultIdentityName = 'AirPDF'
$defaultPublisher = 'CN=AirPDF Test Signing (not for release)'

if ($ForStore) {
    if ($CertificatePath) {
        throw '-ForStore publishes without a signature: the Store re-signs the package. Omit -CertificatePath.'
    }

    # A submission with the placeholder identity is rejected, so refuse to build one
    # rather than produce a package that cannot be uploaded.
    if ($IdentityName -eq $defaultIdentityName -and $Publisher -eq $defaultPublisher) {
        throw @'
-ForStore needs the identity values Partner Center assigned to the product:
  -IdentityName <name from Product identity>
  -Publisher    "CN=<publisher id from Product identity>"
Open Partner Center > your app > Product management > Product identity.
'@
    }

    # Partner Center compares this against the account's publisher display name
    # verbatim. A mismatch is reported as a package validation error before
    # certification, so the default is never right for a submission.
    if (-not $PSBoundParameters.ContainsKey('PublisherDisplayName')) {
        throw @'
-ForStore needs -PublisherDisplayName: it must match the publisher display name of the
Partner Center account exactly, or the upload fails with "The PublisherDisplayName
element in the app manifest ... does not match the publisher display name ...".
AirPDF's account publishes as: -PublisherDisplayName RyzenWang
'@
    }
}

Assert-PackageIdentity

function Resolve-BuildTools {
    param([string] $ToolName)

    $command = Get-Command $ToolName -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    # A full Windows SDK install keeps the tools under Program Files.
    foreach ($kit in @(
            'C:\Program Files (x86)\Windows Kits\10\bin',
            'C:\Program Files\Windows Kits\10\bin')) {
        if (-not (Test-Path $kit)) { continue }
        $found = Get-ChildItem $kit -Recurse -Filter "$ToolName.exe" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }

    # Otherwise pull the build tools from NuGet, which needs no elevation.
    $toolsRoot = Join-Path $root 'artifacts/tools/sdkbuildtools'
    $candidate = Get-ChildItem $toolsRoot -Recurse -Filter "$ToolName.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Select-Object -First 1
    if ($candidate) { return $candidate.FullName }

    Write-Step "Downloading Microsoft.Windows.SDK.BuildTools (contains $ToolName.exe)"
    $version = '10.0.28000.2957'
    $package = Join-Path $env:TEMP "sdkbuildtools-$version.nupkg"
    $url = "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$version/microsoft.windows.sdk.buildtools.$version.nupkg"

    $previous = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest $url -OutFile $package -TimeoutSec 300
    }
    finally {
        $ProgressPreference = $previous
    }

    New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
    Expand-Archive $package -DestinationPath $toolsRoot -Force

    $installed = Get-ChildItem $toolsRoot -Recurse -Filter "$ToolName.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Select-Object -First 1
    if (-not $installed) {
        throw "$ToolName.exe was not found after extracting the build tools package."
    }

    return $installed.FullName
}

$makeAppx = Resolve-BuildTools -ToolName 'makeappx'
Write-Host "makeappx: $makeAppx"

if (-not $SkipPublish) {
    if ($NoAot) {
        Write-Step 'Publishing the application as a folder layout (JIT, -NoAot)'
    }
    else {
        Write-Step 'Publishing the application as a folder layout (AOT)'
    }
    if (Test-Path $publishDirectory) { Remove-Item $publishDirectory -Recurse -Force }

    $aotArguments = @()
    if (-not $NoAot) {
        $aotArguments = @('-p:PublishAot=true', '-p:StripSymbols=true')
    }

    # The application ships without debug symbols, so the symbol package used for the
    # Store crash reports has to ask for them explicitly.
    $symbolArguments = @()
    if ($Symbols) {
        $symbolArguments = @('-p:DebugType=portable', '-p:DebugSymbols=true', '-p:CopyOutputSymbolsToPublishDirectory=true')
    }

    & dotnet publish (Join-Path $root 'src/AirPdf.App/AirPdf.App.csproj') `
        -c $Configuration `
        -r "win-$Architecture" `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:Version=$Version `
        @aotArguments `
        @symbolArguments `
        -o $publishDirectory

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
}

if (-not (Test-Path (Join-Path $publishDirectory 'AirPdf.exe'))) {
    throw "No AirPdf.exe in $publishDirectory."
}

Write-Step 'Building the package layout'
if (Test-Path $layoutDirectory) { Remove-Item $layoutDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $layoutDirectory -Force | Out-Null

$symbolFiles = @(Get-ChildItem $publishDirectory -Recurse -Filter *.pdb -ErrorAction SilentlyContinue)

Get-ChildItem $publishDirectory -Force | Copy-Item -Destination $layoutDirectory -Recurse -Force

# MSIX refuses debug symbols and anything that is not part of the product.
$pdbs = Get-ChildItem $layoutDirectory -Recurse -Filter *.pdb -ErrorAction SilentlyContinue
if ($pdbs) { $pdbs | Remove-Item -Force }

Write-Step 'Generating logo assets'
$sourceIcon = Join-Path $root 'src/AirPdf.App/Assets/airpdf.png'
$assetsDirectory = Join-Path $layoutDirectory 'Assets'
New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null

# System.Drawing ships with Windows, so the assets can be produced without an
# image library or a Python dependency on the build agent.
Add-Type -AssemblyName System.Drawing

function New-PackageImage {
    param(
        [string] $SourcePath,
        [string] $Target,
        [int] $Width,
        [int] $Height,
        [double] $ContentScale = 1.0,
        [bool] $Paper = $false
    )

    $sourceImage = [System.Drawing.Image]::FromFile($SourcePath)
    try {
        $bitmap = New-Object System.Drawing.Bitmap($Width, $Height)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

                # App-list icons are drawn at their exact target size and Windows adds
                # no padding of its own, so the artwork has to fill the canvas edge to
                # edge. The tile and splash canvases are branded surfaces instead, and
                # inset the mark on the paper background on purpose.
                if ($Paper) {
                    $graphics.Clear([System.Drawing.Color]::FromArgb(255, 246, 244, 239))
                }
                else {
                    $graphics.Clear([System.Drawing.Color]::Transparent)
                }

                $scale = [Math]::Min(
                    ($Width * $ContentScale) / $sourceImage.Width,
                    ($Height * $ContentScale) / $sourceImage.Height)
                $drawWidth = [int]($sourceImage.Width * $scale)
                $drawHeight = [int]($sourceImage.Height * $scale)
                $x = [int](($Width - $drawWidth) / 2)
                $y = [int](($Height - $drawHeight) / 2)
                $graphics.DrawImage($sourceImage, $x, $y, $drawWidth, $drawHeight)
            }
            finally {
                $graphics.Dispose()
            }

            $bitmap.Save($Target, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $bitmap.Dispose()
        }
    }
    finally {
        $sourceImage.Dispose()
    }
}

# Two families of assets come out of the same source:
#
#   * App-list icons (the Square44x44Logo family) are drawn edge to edge. Windows
#     renders one of these at an exact target size in the Start menu, on the
#     taskbar, in search results, and as the icon of a .pdf in Explorer, and it
#     does not add padding of its own. Any padding baked into the file therefore
#     shows up as an icon that looks smaller than its neighbours.
#   * Branded canvases (medium tile, wide tile, splash screen) where the mark is
#     inset on the paper background on purpose.
$canvases = @(
    @{ Name = 'Square150x150Logo.png'; Width = 150; Height = 150; Scale = 0.72; Paper = $true },
    @{ Name = 'StoreLogo.png'; Width = 50; Height = 50; Scale = 1.0; Paper = $false },
    @{ Name = 'Wide310x150Logo.png'; Width = 310; Height = 150; Scale = 0.62; Paper = $true },
    @{ Name = 'SplashScreen.png'; Width = 620; Height = 300; Scale = 0.50; Paper = $true },
    # The file-type icon. uap:Logo is rendered as one bitmap, without the
    # targetsize/altform resolution the app-list family gets, so it has to be the
    # largest size Explorer can ask for and is scaled down from there.
    @{ Name = 'FileTypeLogo.png'; Width = 256; Height = 256; Scale = 1.0; Paper = $false }
)

foreach ($canvas in $canvases) {
    $target = Join-Path $assetsDirectory $canvas.Name
    New-PackageImage -SourcePath $sourceIcon -Target $target -Width $canvas.Width `
        -Height $canvas.Height -ContentScale $canvas.Scale -Paper $canvas.Paper
    Write-Host "  $($canvas.Name) $($canvas.Width)x$($canvas.Height)"
}

# Windows resolves target-size and altform variants through the manifest's base
# filename, so the names have to follow the qualifier syntax exactly.
#
# All three forms are required, not just the plain one. Without the two unplated
# forms Windows draws the icon a little smaller and puts a backplate behind it,
# which is what made the app look undersized in the Start menu and the .pdf icon
# look undersized on the desktop.
$appIconSizes = 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256
$appIconForms = @('', 'altform-unplated', 'altform-lightunplated')

function New-AppListIcon {
    param([int] $Size, [string] $Qualifier)

    $name = if ($Qualifier) { "Square44x44Logo.$Qualifier.png" } else { 'Square44x44Logo.png' }
    New-PackageImage -SourcePath $sourceIcon -Target (Join-Path $assetsDirectory $name) `
        -Width $Size -Height $Size -ContentScale 1.0 -Paper $false
}

New-AppListIcon -Size 44 -Qualifier ''

foreach ($size in $appIconSizes) {
    foreach ($form in $appIconForms) {
        $qualifier = if ($form) { "targetsize-${size}_${form}" } else { "targetsize-$size" }
        New-AppListIcon -Size $size -Qualifier $qualifier
    }
}

Write-Host ("  Square44x44Logo {0}x{0} + {1} target sizes x {2} forms" -f 44, $appIconSizes.Count, $appIconForms.Count)

Write-Step 'Expanding the manifest'
$manifest = Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
$manifest = $manifest.Replace('{{IDENTITY_NAME}}', $IdentityName)
$manifest = $manifest.Replace('{{DISPLAY_NAME}}', $DisplayName)
$manifest = $manifest.Replace('{{PUBLISHER_DISPLAY_NAME}}', $PublisherDisplayName)
$manifest = $manifest.Replace('{{PUBLISHER}}', $Publisher)
$manifest = $manifest.Replace('{{VERSION}}', $Version)
$manifest = $manifest.Replace('{{ARCH}}', $Architecture)
Set-Content -Path (Join-Path $layoutDirectory 'AppxManifest.xml') -Value $manifest -Encoding utf8

Write-Step 'Packing'
$packageName = "AirPDF-$Version-$Architecture.msix"
$packagePath = Join-Path $outputDirectory $packageName
if (Test-Path $packagePath) { Remove-Item $packagePath -Force }

& $makeAppx pack /d $layoutDirectory /p $packagePath /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed with exit code $LASTEXITCODE." }

if ($CertificatePath) {
    Write-Step 'Signing with the supplied certificate'
    $signTool = Resolve-BuildTools -ToolName 'signtool'
    $signArgs = @('sign', '/fd', 'SHA256', '/f', $CertificatePath, '/a')
    if ($CertificatePassword) { $signArgs += @('/p', $CertificatePassword) }
    $signArgs += $packagePath

    & $signTool @signArgs
    if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE." }
}

$symbolPath = $null
if ($symbolFiles.Count -gt 0) {
    Write-Step 'Building the symbol package'
    $symbolPath = Join-Path $outputDirectory "$([System.IO.Path]::GetFileNameWithoutExtension($packageName)).appxsym"
    if (Test-Path $symbolPath) { Remove-Item $symbolPath -Force }

    $symbolDirectory = Join-Path $outputDirectory "symbols-$Architecture"
    if (Test-Path $symbolDirectory) { Remove-Item $symbolDirectory -Recurse -Force }
    New-Item -ItemType Directory -Path $symbolDirectory -Force | Out-Null
    foreach ($symbol in $symbolFiles) {
        Copy-Item $symbol.FullName (Join-Path $symbolDirectory $symbol.Name) -Force
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $symbolDirectory, $symbolPath,
        [System.IO.Compression.CompressionLevel]::Optimal, $false)

    Write-Host ("  {0} ({1} files)" -f (Split-Path $symbolPath -Leaf), $symbolFiles.Count) -ForegroundColor Green
}
elseif ($Symbols) {
    throw '-Symbols was requested but the publish produced no .pdb files.'
}

if ($ForStore) {
    # Partner Center expects a .msixupload for packaged submissions: a zip holding the
    # .msix and, optionally, the public symbols used for its crash reports.
    Write-Step 'Building the .msixupload for Partner Center'
    $uploadName = "AirPDF-$Version-$Architecture.msixupload"
    $uploadPath = Join-Path $outputDirectory $uploadName
    if (Test-Path $uploadPath) { Remove-Item $uploadPath -Force }

    $uploadDirectory = Join-Path $outputDirectory 'upload'
    if (Test-Path $uploadDirectory) { Remove-Item $uploadDirectory -Recurse -Force }
    New-Item -ItemType Directory -Path $uploadDirectory -Force | Out-Null

    Copy-Item $packagePath (Join-Path $uploadDirectory $packageName)
    if ($symbolPath) {
        Copy-Item $symbolPath (Join-Path $uploadDirectory (Split-Path $symbolPath -Leaf))
    }

    # Compress-Archive only accepts a .zip destination, so the archive is written by
    # the zip API directly under the extension Partner Center expects.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $uploadDirectory, $uploadPath,
        [System.IO.Compression.CompressionLevel]::Optimal, $false)

    Write-Host "  $uploadName" -ForegroundColor Green
}

Write-Step 'Verifying the package'
$verifyDirectory = Join-Path $outputDirectory 'verify'
if (Test-Path $verifyDirectory) { Remove-Item $verifyDirectory -Recurse -Force }

& $makeAppx unpack /p $packagePath /d $verifyDirectory /o
if ($LASTEXITCODE -ne 0) { throw "The package could not be unpacked; it is likely corrupt." }

$generatedManifest = [xml](Get-Content (Join-Path $verifyDirectory 'AppxManifest.xml') -Raw)
$ns = New-Object System.Xml.XmlNamespaceManager($generatedManifest.NameTable)
$ns.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$ns.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
$ns.AddNamespace('rescap', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')

# An icon asset is only useful if the artwork actually reaches the edge of the
# canvas, so the checks below measure the opaque area instead of trusting the
# filename. LockBits is used rather than GetPixel because a 256x256 walk of
# GetPixel calls is slow enough to notice on every build.
function Get-IconBitmap {
    param([string] $Path)

    $source = [System.Drawing.Image]::FromFile($Path)
    try {
        $bitmap = New-Object System.Drawing.Bitmap($source.Width, $source.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.DrawImage($source, 0, 0, $source.Width, $source.Height)
        }
        finally {
            $graphics.Dispose()
        }

        return $bitmap
    }
    finally {
        $source.Dispose()
    }
}

function Get-MarkBounds {
    param(
        [string] $Path,
        [int[]] $Background,
        [int] $Tolerance = 10
    )

    $bitmap = Get-IconBitmap -Path $Path
    try {
        $width = $bitmap.Width
        $height = $bitmap.Height
        $rect = New-Object System.Drawing.Rectangle(0, 0, $width, $height)
        $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $buffer = New-Object byte[] ($data.Stride * $height)
            [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buffer, 0, $buffer.Length)

            $minX = $width; $minY = $height; $maxX = -1; $maxY = -1
            for ($y = 0; $y -lt $height; $y++) {
                $row = $y * $data.Stride
                for ($x = 0; $x -lt $width; $x++) {
                    $offset = $row + ($x * 4)
                    if ($buffer[$offset + 3] -le 8) { continue }

                    # Memory order is BGRA; a pixel that matches the background colour
                    # belongs to the canvas rather than to the mark.
                    if ($Background) {
                        $deltaBlue = [Math]::Abs([int]$buffer[$offset + 0] - $Background[2])
                        $deltaGreen = [Math]::Abs([int]$buffer[$offset + 1] - $Background[1])
                        $deltaRed = [Math]::Abs([int]$buffer[$offset + 2] - $Background[0])
                        if ($deltaBlue -le $Tolerance -and $deltaGreen -le $Tolerance -and $deltaRed -le $Tolerance) { continue }
                    }

                    if ($x -lt $minX) { $minX = $x }
                    if ($x -gt $maxX) { $maxX = $x }
                    if ($y -lt $minY) { $minY = $y }
                    if ($y -gt $maxY) { $maxY = $y }
                }
            }

            $markWidth = 0
            $markHeight = 0
            if ($maxX -ge 0) {
                $markWidth = $maxX - $minX + 1
                $markHeight = $maxY - $minY + 1
            }

            return [pscustomobject]@{
                Width  = $markWidth
                Height = $markHeight
            }
        }
        finally {
            $bitmap.UnlockBits($data)
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

$checks = @()

$identity = $generatedManifest.SelectSingleNode('/f:Package/f:Identity', $ns)
$checks += @{ Name = 'identity version'; Ok = ($identity.Version -eq $Version); Detail = $identity.Version }
$checks += @{ Name = 'identity name'; Ok = ($identity.Name -eq $IdentityName); Detail = $identity.Name }
$checks += @{ Name = 'identity publisher'; Ok = ($identity.Publisher -eq $Publisher); Detail = $identity.Publisher }
$checks += @{ Name = 'architecture'; Ok = ($identity.ProcessorArchitecture -eq $Architecture); Detail = $identity.ProcessorArchitecture }

$executable = $generatedManifest.SelectSingleNode('//f:Application/@Executable', $ns).Value
$checks += @{ Name = 'entry executable present'; Ok = (Test-Path (Join-Path $verifyDirectory $executable)); Detail = $executable }

$fullTrust = $generatedManifest.SelectSingleNode('//rescap:Capability[@Name="runFullTrust"]', $ns)
$checks += @{ Name = 'runFullTrust capability'; Ok = ($null -ne $fullTrust); Detail = 'declared' }

$fileTypes = $generatedManifest.SelectNodes('//uap:FileType', $ns) | ForEach-Object { $_.InnerText }
$checks += @{ Name = 'pdf file association'; Ok = ($fileTypes -contains '.pdf'); Detail = ($fileTypes -join ', ') }

foreach ($image in @('Square44x44Logo.png', 'Square150x150Logo.png', 'StoreLogo.png', 'FileTypeLogo.png')) {
    $checks += @{
        Name   = "asset $image"
        Ok     = (Test-Path (Join-Path $verifyDirectory "Assets/$image"))
        Detail = $image
    }
}

$expectedIcons = @('Assets/Square44x44Logo.png')
foreach ($size in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
    $expectedIcons += "Assets/Square44x44Logo.targetsize-$size.png"
    $expectedIcons += "Assets/Square44x44Logo.targetsize-${size}_altform-unplated.png"
    $expectedIcons += "Assets/Square44x44Logo.targetsize-${size}_altform-lightunplated.png"
}

$missingIcons = @($expectedIcons | Where-Object { -not (Test-Path (Join-Path $verifyDirectory $_)) })
$iconSetDetail = if ($missingIcons.Count -eq 0) {
    "$($expectedIcons.Count) assets"
}
else {
    "missing: $($missingIcons -join ', ')"
}

$checks += @{
    Name   = 'app list icon set'
    Ok     = ($missingIcons.Count -eq 0)
    Detail = $iconSetDetail
}

# A padded icon in the 44px family is exactly the bug this set prevents, so measure
# one: the artwork of a 48px asset has to reach at least 46px across.
$iconBounds = Get-MarkBounds -Path (Join-Path $verifyDirectory 'Assets/Square44x44Logo.targetsize-48_altform-unplated.png')
$checks += @{
    Name   = 'app list icon fills its canvas'
    Ok     = ($iconBounds.Width -ge 46 -and $iconBounds.Height -ge 46)
    Detail = "$($iconBounds.Width)x$($iconBounds.Height) of 48x48"
}

# The file-type icon is a single bitmap, so it has to fill a canvas big enough for
# the largest view Explorer offers; anything smaller is pasted in unscaled and shows
# up small and off-centre on the desktop.
$fileTypeBounds = Get-MarkBounds -Path (Join-Path $verifyDirectory 'Assets/FileTypeLogo.png')
$checks += @{
    Name   = 'file type icon fills its canvas'
    Ok     = ($fileTypeBounds.Width -ge 250 -and $fileTypeBounds.Height -ge 250)
    Detail = "$($fileTypeBounds.Width)x$($fileTypeBounds.Height) of 256x256"
}

# The medium tile is inset on purpose, but it still has to read as a mark rather
# than a speck: 0.72 of 150 is 108px, and the old 0.44 was 66px.
$tileBounds = Get-MarkBounds -Path (Join-Path $verifyDirectory 'Assets/Square150x150Logo.png') -Background @(246, 244, 239)
$checks += @{
    Name   = 'tile logo size'
    Ok     = ($tileBounds.Width -ge 90 -and $tileBounds.Width -le 130)
    Detail = "$($tileBounds.Width)x$($tileBounds.Height) of 150x150"
}

if ($Symbols) {
    $checks += @{
        Name   = 'symbol package'
        Ok     = ($null -ne $symbolPath -and (Test-Path $symbolPath))
        Detail = if ($symbolPath) { Split-Path $symbolPath -Leaf } else { 'missing' }
    }
}

if ($ForStore) {
    # Certification rejects a package that still carries a signature other than the
    # Store's own, so an unsigned submission package is the expected shape.
    $signature = Join-Path $verifyDirectory 'AppxSignature.p7x'
    $checks += @{ Name = 'unsigned for submission'; Ok = (-not (Test-Path $signature)); Detail = 'no signature' }
}

$failed = 0
foreach ($check in $checks) {
    if ($check.Ok) {
        Write-Host ("  PASS  {0} ({1})" -f $check.Name, $check.Detail) -ForegroundColor Green
    }
    else {
        Write-Host ("  FAIL  {0} ({1})" -f $check.Name, $check.Detail) -ForegroundColor Red
        $failed++
    }
}

$packageSize = [math]::Round((Get-Item $packagePath).Length / 1MB, 1)
$payloadSize = [math]::Round((Get-ChildItem $layoutDirectory -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)

Write-Host ''
Write-Host "Package: $packagePath" -ForegroundColor Green
Write-Host "  version   $Version"
Write-Host "  identity  $IdentityName"
Write-Host "  publisher $Publisher"
Write-Host "  architecture $Architecture"
Write-Host "  packaged  $packageSize MB from $payloadSize MB of payload"
Write-Host "  symbols   $(if ($symbolPath) { 'yes' } else { 'no' })"
Write-Host "  signed    $(if ($CertificatePath) { 'yes' } else { 'no' })"

if ($failed -gt 0) {
    throw "$failed package check(s) failed."
}

Write-Host ''
if ($ForStore) {
    Write-Host "Upload to Partner Center: $uploadPath" -ForegroundColor Yellow
    Write-Host 'The Store re-signs the package, so it is uploaded unsigned.' -ForegroundColor Yellow
    Write-Host 'See docs/STORE_SUBMISSION.md for the rest of the checklist.' -ForegroundColor Yellow
}
else {
    Write-Host 'Sideload with: Add-AppxPackage -Path <package>' -ForegroundColor Yellow
    Write-Host 'A sideloaded package must be signed with a certificate the machine trusts.' -ForegroundColor Yellow
}


