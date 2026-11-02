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

    The certificate is created with System.Security.Cryptography rather than by
    shelling out to openssl. A PKCS#12 produced by openssl signs successfully
    with signtool but fails to verify with 0x80096019 (TRUST_E_BAD_DIGEST),
    which made the signing harness look broken when only the certificate
    encoding was at fault.

    Signatures made with this certificate are structurally valid but carry no
    public trust, so Windows SmartScreen and macOS Gatekeeper will still warn.
    Never ship a build signed this way.

.PARAMETER Subject
    Distinguished name. It must match the Publisher in an MSIX manifest when the
    package is signed with this certificate.

.EXAMPLE
    ./New-TestCertificate.ps1 -OutputPath ./tmp/test-codesign.pfx -Password test
#>
[CmdletBinding()]
param(
    [string] $OutputPath = './test-codesign.pfx',
    [string] $Password = 'test',
    [string] $Subject = 'CN=AirPDF Test Signing (not for release)',
    [int] $Days = 7
)

$ErrorActionPreference = 'Stop'

if ($Days -lt 1) { throw '-Days must be at least 1.' }

$directory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
if (-not (Test-Path -LiteralPath $directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

Write-Host '==> Generating a self-signed code-signing certificate' -ForegroundColor Cyan

$key = [System.Security.Cryptography.RSA]::Create(2048)
try {
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        $Subject,
        $key,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

    # 1.3.6.1.5.5.7.3.3 is the Code Signing extended key usage; Windows will not
    # use a certificate for Authenticode without it.
    $request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
            $true))

    $usage = [System.Security.Cryptography.OidCollection]::new()
    $usage.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3', 'Code Signing')) | Out-Null

    $request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
            $usage,
            $true))

    $request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new(
            $false, $false, 0, $true))

    $request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new(
            $request.PublicKey, $false))

    $notBefore = [DateTimeOffset]::UtcNow.AddMinutes(-5)
    $notAfter = [DateTimeOffset]::UtcNow.AddDays($Days)

    # The DateTimeOffset overload exists from .NET 9; it keeps the validity
    # window unambiguous regardless of the machine's time zone.
    try {
        $certificate = $request.CreateSelfSigned($notBefore, $notAfter)
    }
    catch [System.Management.Automation.MethodException] {
        $certificate = $request.CreateSelfSigned($notBefore.LocalDateTime, $notAfter.LocalDateTime)
    }
}
finally {
    $key.Dispose()
}

try {
    Write-Host '==> Packaging as PKCS#12' -ForegroundColor Cyan
    $bytes = $certificate.Export(
        [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx,
        $Password)

    $resolved = [IO.Path]::GetFullPath($OutputPath)
    [IO.File]::WriteAllBytes($resolved, $bytes)

    # Read it back the way signtool and the settings helper will, so a bad export
    # fails here instead of during a release.
    $roundTrip = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $resolved,
        $Password,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable)

    Write-Host ''
    Write-Host "Certificate: $resolved" -ForegroundColor Green
    Write-Host "  Subject:  $($roundTrip.Subject)"
    Write-Host "  Expires:  $($roundTrip.NotAfter.ToLocalTime().ToString('yyyy-MM-dd'))"
    Write-Host "  Password: $Password"
    Write-Host "  Base64:   $([Convert]::ToBase64String($bytes).Length) characters"
    Write-Host ''
    Write-Host 'This certificate is self-signed. Do not publish builds signed with it.' -ForegroundColor Yellow
}
finally {
    $certificate.Dispose()
}
