<#
.SYNOPSIS
    Mints a throwaway code-signing certificate so the signing pipeline can be
    exercised without a purchased certificate.

.DESCRIPTION
    A real release needs a Windows OV/EV certificate or an Apple Developer ID.
    This script produces a self-signed PKCS#12 file with the Code Signing
    extended key usage instead, which is enough to prove that:

      * the certificate is accepted by Set-SigningSecrets.ps1,
      * the base64 secret round-trips,
      * signtool / codesign can use it.

    Signatures made with this certificate are structurally valid but carry no
    public trust, so Windows SmartScreen and macOS Gatekeeper will still warn.
    Never ship a build signed this way.

.EXAMPLE
    ./New-TestCertificate.ps1 -OutputPath ./tmp/test-codesign.pfx -Password test
#>
[CmdletBinding()]
param(
    [string] $OutputPath = './test-codesign.pfx',
    [string] $Password = 'test',
    [string] $Subject = '/CN=AirPDF Test Signing (not for release)',
    [int] $Days = 7,
    [string] $OpenSslPath
)

$ErrorActionPreference = 'Stop'

function Find-OpenSsl {
    param([string] $Override)

    if ($Override) {
        if (-not (Test-Path -LiteralPath $Override)) { throw "openssl not found at $Override" }
        return (Resolve-Path -LiteralPath $Override).Path
    }

    $command = Get-Command openssl -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    foreach ($candidate in @(
            'C:\Program Files\Git\usr\bin\openssl.exe',
            'C:\Program Files\Git\mingw64\bin\openssl.exe',
            'C:\Program Files (x86)\Git\usr\bin\openssl.exe',
            '/usr/bin/openssl',
            '/usr/local/bin/openssl')) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }

    throw 'openssl was not found. Install Git for Windows (which ships it) or pass -OpenSslPath.'
}

# openssl writes key-generation noise to stderr; under $ErrorActionPreference=Stop,
# Windows PowerShell turns that into a terminating error. Capture it instead.
function Invoke-Native {
    param([string] $FilePath, [string[]] $Arguments)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $FilePath @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($code -ne 0) {
        $text = $output | Out-String
        throw ("{0} failed with exit code {1}: {2}" -f $FilePath, $code, $text)
    }

    return $output
}

$openssl = Find-OpenSsl -Override $OpenSslPath

$directory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
if (-not (Test-Path -LiteralPath $directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$keyPath = Join-Path $directory ("code-sign-{0}.key" -f [Guid]::NewGuid().ToString('N'))
$certPath = Join-Path $directory ("code-sign-{0}.crt" -f [Guid]::NewGuid().ToString('N'))

try {
    Write-Host "==> Generating a self-signed code-signing certificate" -ForegroundColor Cyan
    Invoke-Native -FilePath $openssl -Arguments @(
        'req', '-x509', '-newkey', 'rsa:2048', '-sha256', '-nodes',
        '-keyout', $keyPath, '-out', $certPath, '-days', "$Days",
        '-subj', $Subject,
        '-addext', 'keyUsage=critical,digitalSignature',
        '-addext', 'extendedKeyUsage=codeSigning') | Out-Null

    Write-Host "==> Packaging as PKCS#12" -ForegroundColor Cyan
    Invoke-Native -FilePath $openssl -Arguments @(
        'pkcs12', '-export', '-out', $OutputPath,
        '-inkey', $keyPath, '-in', $certPath,
        '-passout', "pass:$Password",
        '-name', 'AirPDF Test Signing') | Out-Null

    $resolved = (Resolve-Path -LiteralPath $OutputPath).Path
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
        $resolved, $Password,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable)

    Write-Host ''
    Write-Host "Certificate: $resolved" -ForegroundColor Green
    Write-Host "  Subject:  $($certificate.Subject)"
    Write-Host "  Expires:  $($certificate.NotAfter.ToLocalTime().ToString('yyyy-MM-dd'))"
    Write-Host "  Password: $Password"
    Write-Host "  Base64:   $([Convert]::ToBase64String([IO.File]::ReadAllBytes($resolved)).Length) characters"
    Write-Host ''
    Write-Host 'This certificate is self-signed. Do not publish builds signed with it.' -ForegroundColor Yellow
}
finally {
    foreach ($temporary in @($keyPath, $certPath)) {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}
