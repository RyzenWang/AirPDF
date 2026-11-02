<#
.SYNOPSIS
    Builds a sideloadable MSIX package for AirPDF from a folder publish.

.DESCRIPTION
    Proof of concept for Microsoft Store distribution through the MSIX path.
    The script:

      1. resolves makeappx.exe (installed Windows SDK, or the
         Microsoft.Windows.SDK.BuildTools NuGet package downloaded on demand),
      2. publishes the app as a folder (MSIX cannot use PublishSingleFile),
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

.EXAMPLE
    ./Build-Msix.ps1 -Version 0.3.3.0

.EXAMPLE
    ./Build-Msix.ps1 -Version 0.3.3.0 -CertificatePath ../../artifacts/test.pfx -CertificatePassword test
#>
[CmdletBinding()]
param(
    [string] $Version = '0.3.3.0',
    [string] $Publisher = 'CN=AirPDF Test Signing (not for release)',
    [string] $PublisherDisplayName = 'AirPDF contributors',
    [string] $IdentityName = 'AirPDF',
    [string] $Configuration = 'Release',
    [string] $OutputDirectory = 'artifacts/msix',
    [string] $CertificatePath,
    [string] $CertificatePassword,
    [switch] $SkipPublish
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
$publishDirectory = Join-Path $outputDirectory 'app'
$layoutDirectory = Join-Path $outputDirectory 'layout'

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "-Version must have four numeric parts, for example 0.3.3.0 (got '$Version')."
}

if (-not $Version.EndsWith('.0')) {
    Write-Warning 'The fourth version part is reserved for the Store and must be 0 for submissions.'
}

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
    Write-Step 'Publishing the application as a folder layout'
    if (Test-Path $publishDirectory) { Remove-Item $publishDirectory -Recurse -Force }

    & dotnet publish (Join-Path $root 'src/AirPdf.App/AirPdf.App.csproj') `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:Version=$Version `
        -o $publishDirectory

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
}

if (-not (Test-Path (Join-Path $publishDirectory 'AirPdf.exe'))) {
    throw "No AirPdf.exe in $publishDirectory."
}

Write-Step 'Building the package layout'
if (Test-Path $layoutDirectory) { Remove-Item $layoutDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $layoutDirectory -Force | Out-Null

Get-ChildItem $publishDirectory -Force | Copy-Item -Destination $layoutDirectory -Recurse -Force

# MSIX refuses debug symbols and anything that is not part of the product.
$pdbs = Get-ChildItem $layoutDirectory -Recurse -Filter *.pdb -ErrorAction SilentlyContinue
if ($pdbs) { $pdbs | Remove-Item -Force }

Write-Step 'Generating logo assets'
$sourceIcon = Join-Path $root 'src/AirPdf.App/Assets/airpdf.png'
$assetsDirectory = Join-Path $layoutDirectory 'Assets'
New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null

$sizes = @{
    'Square44x44Logo.png'   = 44
    'Square150x150Logo.png' = 150
    'StoreLogo.png'         = 50
    'Wide310x150Logo.png'   = 0
    'SplashScreen.png'      = 0
}

$python = @'
import sys
from PIL import Image

source, target, width, height = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4])
image = Image.open(source).convert("RGBA")
if width == height:
    image = image.resize((width, height), Image.LANCZOS)
    image.save(target, "PNG")
else:
    canvas = Image.new("RGBA", (width, height), (246, 244, 239, 255))
    scaled = image.copy()
    scaled.thumbnail((height, height), Image.LANCZOS)
    canvas.paste(scaled, ((width - scaled.width) // 2, (height - scaled.height) // 2), scaled)
    canvas.save(target, "PNG")
'@

$pythonPath = Join-Path $env:TEMP 'airpdf-msix-assets.py'
Set-Content -Path $pythonPath -Value $python -Encoding utf8

foreach ($entry in $sizes.GetEnumerator()) {
    $width = if ($entry.Value -gt 0) { $entry.Value } else { 310 }
    $height = if ($entry.Value -gt 0) { $entry.Value } else { 150 }

    switch ($entry.Key) {
        'Wide310x150Logo.png' { $width = 310; $height = 150 }
        'SplashScreen.png'    { $width = 620; $height = 300 }
    }

    $target = Join-Path $assetsDirectory $entry.Key
    & python $pythonPath $sourceIcon $target $width $height
    if ($LASTEXITCODE -ne 0) { throw "Failed to generate $($entry.Key)." }
    Write-Host "  $($entry.Key) ${width}x${height}"
}

Write-Step 'Expanding the manifest'
$manifest = Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
$manifest = $manifest.Replace('{{IDENTITY_NAME}}', $IdentityName)
$manifest = $manifest.Replace('{{PUBLISHER_DISPLAY_NAME}}', $PublisherDisplayName)
$manifest = $manifest.Replace('{{PUBLISHER}}', $Publisher)
$manifest = $manifest.Replace('{{VERSION}}', $Version)
Set-Content -Path (Join-Path $layoutDirectory 'AppxManifest.xml') -Value $manifest -Encoding utf8

Write-Step 'Packing'
$packageName = "AirPDF-$Version-x64.msix"
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

$checks = @()

$identity = $generatedManifest.SelectSingleNode('/f:Package/f:Identity', $ns)
$checks += @{ Name = 'identity version'; Ok = ($identity.Version -eq $Version); Detail = $identity.Version }
$checks += @{ Name = 'identity name'; Ok = ($identity.Name -eq $IdentityName); Detail = $identity.Name }
$checks += @{ Name = 'architecture'; Ok = ($identity.ProcessorArchitecture -eq 'x64'); Detail = $identity.ProcessorArchitecture }

$executable = $generatedManifest.SelectSingleNode('//f:Application/@Executable', $ns).Value
$checks += @{ Name = 'entry executable present'; Ok = (Test-Path (Join-Path $verifyDirectory $executable)); Detail = $executable }

$fullTrust = $generatedManifest.SelectSingleNode('//rescap:Capability[@Name="runFullTrust"]', $ns)
$checks += @{ Name = 'runFullTrust capability'; Ok = ($null -ne $fullTrust); Detail = 'declared' }

$fileTypes = $generatedManifest.SelectNodes('//uap:FileType', $ns) | ForEach-Object { $_.InnerText }
$checks += @{ Name = 'pdf file association'; Ok = ($fileTypes -contains '.pdf'); Detail = ($fileTypes -join ', ') }

foreach ($image in @('Square44x44Logo.png', 'Square150x150Logo.png', 'StoreLogo.png')) {
    $checks += @{
        Name   = "asset $image"
        Ok     = (Test-Path (Join-Path $verifyDirectory "Assets/$image"))
        Detail = $image
    }
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
Write-Host "  publisher $Publisher"
Write-Host "  packaged  $packageSize MB from $payloadSize MB of payload"
Write-Host "  signed    $(if ($CertificatePath) { 'yes' } else { 'no' })"

if ($failed -gt 0) {
    throw "$failed package check(s) failed."
}

Write-Host ''
Write-Host 'Sideload with: Add-AppxPackage -Path <package>' -ForegroundColor Yellow
Write-Host 'A sideloaded package must be signed with a certificate the machine trusts.' -ForegroundColor Yellow
