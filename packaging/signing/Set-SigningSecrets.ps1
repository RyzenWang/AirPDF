<#
.SYNOPSIS
    Validates a code-signing certificate and uploads it as a GitHub Actions secret.

.DESCRIPTION
    The release workflow signs Windows installers with Authenticode and macOS
    packages with Developer ID. Both consume base64-encoded PKCS#12 files from
    repository secrets, so this script exists to make that hand-off safe and
    repeatable:

      * the certificate must contain a private key,
      * it must carry the Code Signing extended key usage,
      * it must not be expired,
      * the password must actually open it.

    Validation happens before anything is uploaded, so a broken export is caught
    locally instead of in the middle of a release.

.PARAMETER Target
    Which set of secrets to configure:

      windows          WINDOWS_CERTIFICATE_BASE64, WINDOWS_CERTIFICATE_PASSWORD
      mac-app          MACOS_CERTIFICATE_BASE64, MACOS_CERTIFICATE_PASSWORD
      mac-installer    MACOS_INSTALLER_CERTIFICATE_BASE64
      mac-identities   MACOS_APP_SIGN_IDENTITY, MACOS_INSTALLER_SIGN_IDENTITY
      apple-notary     APPLE_ID, APPLE_TEAM_ID, APPLE_APP_PASSWORD
      keychain-password  MACOS_KEYCHAIN_PASSWORD (generated if omitted)

.EXAMPLE
    ./Set-SigningSecrets.ps1 -Target windows -CertificatePath ./airpdf-codesign.pfx

.EXAMPLE
    ./Set-SigningSecrets.ps1 -Target mac-app -CertificatePath ./developer-id-application.p12

.EXAMPLE
    ./Set-SigningSecrets.ps1 -Target windows -CertificatePath ./cert.pfx -DryRun
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('windows', 'mac-app', 'mac-installer', 'mac-identities', 'apple-notary', 'keychain-password')]
    [string] $Target,

    [string] $CertificatePath,
    [string] $CertificatePassword,
    [string] $SignIdentity,
    [string] $InstallerIdentity,
    [string] $AppleId,
    [string] $TeamId,
    [string] $AppPassword,
    [string] $KeychainPassword,

    [string] $Repository = 'RyzenWang/AirPDF',
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'

if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "-Repository must look like 'owner/name', got '$Repository'."
}

function Write-Step([string] $Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Fail([string] $Message) {
    Write-Host "ERROR: $Message" -ForegroundColor Red
}

function Resolve-GitHubCli {
    $command = Get-Command gh -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $fallback = Join-Path $env:USERPROFILE 'gh\bin\gh.exe'
    if (Test-Path $fallback) { return $fallback }

    return $null
}

function Resolve-CertificatePassword {
    param([string] $Provided, [string] $Name)

    if ($PSBoundParameters.ContainsKey('Provided') -and $Provided) { return $Provided }

    if ([Console]::IsInputRedirected) {
        throw "$Name is required in a non-interactive session. Pass -CertificatePassword."
    }

    $secure = Read-Host -Prompt "Password for the certificate" -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

function Read-Certificate {
    param([string] $Path, [string] $Password)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Certificate not found: $Path"
    }

    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
    try {
        $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
            $resolved, $Password, $flags)
    }
    catch {
        throw "Could not open '$resolved' with that password. Re-export the certificate as PKCS#12 (.pfx/.p12) and check the password."
    }

    return $certificate
}

function Get-ExtendedKeyUsageOids {
    param([System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate)

    # .NET Framework fills Oid.FriendlyName but leaves Oid.Value empty for
    # EnhancedKeyUsageList on some localized Windows builds, so the extension is
    # parsed from its DER bytes instead of trusting the friendly names.
    $oids = @()
    $extension = $Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' }
    if ($extension) {
        try {
            $parsed = New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($extension, $false)
            $oids = @($parsed.EnhancedKeyUsages | ForEach-Object { $_.Value } | Where-Object { $_ })
        }
        catch {
            $oids = @()
        }
    }

    if ($oids.Count -eq 0) {
        $oids = @($Certificate.EnhancedKeyUsageList |
            ForEach-Object { if ($_.Value) { $_.Value } else { $_.FriendlyName } } |
            Where-Object { $_ })
    }

    return $oids
}

function Assert-CodeSigningCertificate {
    param(
        [System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate,
        [string] $Origin
    )

    if (-not $Certificate.HasPrivateKey) {
        throw "$Origin does not contain a private key. Export it from the 'Personal' store with the private key included."
    }

    $usages = Get-ExtendedKeyUsageOids -Certificate $Certificate
    $isCodeSigning = ($usages -contains '1.3.6.1.5.5.7.3.3') -or
        ($usages | Where-Object { $_ -match 'Code Signing|代码签名' })
    if (-not $isCodeSigning) {
        $found = if ($usages.Count -eq 0) { '(none)' } else { $usages -join ', ' }
        throw "$Origin is not valid for code signing. Expected extended key usage 'Code Signing' (1.3.6.1.5.5.7.3.3), found: $found."
    }

    $now = Get-Date
    if ($Certificate.NotAfter.ToUniversalTime() -le $now.ToUniversalTime()) {
        throw "$Origin expired on $($Certificate.NotAfter.ToLocalTime().ToString('yyyy-MM-dd')). Renew the certificate before publishing."
    }

    if ($Certificate.NotBefore.ToUniversalTime() -gt $now.ToUniversalTime()) {
        throw "$Origin is not valid until $($Certificate.NotBefore.ToLocalTime().ToString('yyyy-MM-dd HH:mm'))."
    }

    if ($Certificate.NotAfter.ToUniversalTime() -lt $now.AddDays(30).ToUniversalTime()) {
        Write-Warning "Certificate expires on $($Certificate.NotAfter.ToLocalTime().ToString('yyyy-MM-dd')) - renew it soon."
    }
}

function Get-CertificateSummary {
    param([System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate)

    $lines = @(
        "Subject:    $($Certificate.Subject)",
        "Issuer:     $($Certificate.Issuer)",
        "Thumbprint: $($Certificate.Thumbprint)",
        "Valid:      $($Certificate.NotBefore.ToLocalTime().ToString('yyyy-MM-dd')) .. $($Certificate.NotAfter.ToLocalTime().ToString('yyyy-MM-dd'))"
    )

    return ($lines -join [Environment]::NewLine)
}

function Publish-Secret {
    param(
        [string] $Name,
        [string] $Value,
        [string] $GhCli
    )

    if ([string]::IsNullOrEmpty($Value)) {
        throw "Refusing to upload an empty value for $Name."
    }

    if ($DryRun) {
        Write-Host "    [dry run] would set $Name ($($Value.Length) characters)" -ForegroundColor DarkGray
        return
    }

    if (-not $GhCli) {
        throw @"
GitHub CLI not found. Install it (https://cli.github.com/) or set the secret by
hand in Settings > Secrets and variables > Actions.

  Secret name: $Name
  Secret value length: $($Value.Length)
"@
    }

    $temp = Join-Path ([IO.Path]::GetTempPath()) ("airpdf-secret-{0}.txt" -f [Guid]::NewGuid().ToString('N'))
    try {
        # The value goes through a file so it never appears in a process command
        # line, where other local users could read it. cmd.exe is used purely for
        # its stdin redirection: PowerShell has no '<' operator and piping adds a
        # trailing newline that would corrupt a password.
        [IO.File]::WriteAllText($temp, $Value, (New-Object Text.UTF8Encoding($false)))
        $command = '"{0}" secret set {1} --repo "{2}" < "{3}"' -f $GhCli, $Name, $Repository, $temp
        $result = & cmd.exe /c $command 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "gh secret set $Name failed: $($result | Out-String)"
        }

        Write-Host "    set $Name" -ForegroundColor Green
    }
    finally {
        if (Test-Path $temp) {
            Remove-Item -LiteralPath $temp -Force
        }
    }
}

$ghCli = Resolve-GitHubCli

switch ($Target) {
    'windows' {
        if (-not $CertificatePath) { throw "-CertificatePath is required for -Target windows." }

        $CertificatePassword = Resolve-CertificatePassword -Provided $CertificatePassword -Name '-CertificatePassword'

        Write-Step 'Validating the Windows code-signing certificate'
        $certificate = Read-Certificate -Path $CertificatePath -Password $CertificatePassword
        Assert-CodeSigningCertificate -Certificate $certificate -Origin $CertificatePath
        Write-Host (Get-CertificateSummary -Certificate $certificate)

        $base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $CertificatePath).Path))
        Write-Step "Uploading secrets to $Repository"
        Publish-Secret -Name 'WINDOWS_CERTIFICATE_BASE64' -Value $base64 -GhCli $ghCli
        Publish-Secret -Name 'WINDOWS_CERTIFICATE_PASSWORD' -Value $CertificatePassword -GhCli $ghCli
    }

    'mac-app' {
        if (-not $CertificatePath) { throw "-CertificatePath is required for -Target mac-app." }

        $CertificatePassword = Resolve-CertificatePassword -Provided $CertificatePassword -Name '-CertificatePassword'

        Write-Step 'Validating the Developer ID Application certificate'
        $certificate = Read-Certificate -Path $CertificatePath -Password $CertificatePassword
        Assert-CodeSigningCertificate -Certificate $certificate -Origin $CertificatePath
        Write-Host (Get-CertificateSummary -Certificate $certificate)

        if ($certificate.Subject -notlike 'CN=Developer ID Application*') {
            Write-Warning "Subject is not 'Developer ID Application: ...'. Gatekeeper will reject a package signed with any other identity."
        }

        $base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $CertificatePath).Path))
        Write-Step "Uploading secrets to $Repository"
        Publish-Secret -Name 'MACOS_CERTIFICATE_BASE64' -Value $base64 -GhCli $ghCli
        Publish-Secret -Name 'MACOS_CERTIFICATE_PASSWORD' -Value $CertificatePassword -GhCli $ghCli
    }

    'mac-installer' {
        if (-not $CertificatePath) { throw "-CertificatePath is required for -Target mac-installer." }

        $CertificatePassword = Resolve-CertificatePassword -Provided $CertificatePassword -Name '-CertificatePassword'

        Write-Step 'Validating the Developer ID Installer certificate'
        $certificate = Read-Certificate -Path $CertificatePath -Password $CertificatePassword
        Assert-CodeSigningCertificate -Certificate $certificate -Origin $CertificatePath
        Write-Host (Get-CertificateSummary -Certificate $certificate)

        if ($certificate.Subject -notlike 'CN=Developer ID Installer*') {
            Write-Warning "Subject is not 'Developer ID Installer: ...'. The .pkg signature will not be trusted."
        }

        $base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $CertificatePath).Path))
        Write-Step "Uploading secrets to $Repository"
        Publish-Secret -Name 'MACOS_INSTALLER_CERTIFICATE_BASE64' -Value $base64 -GhCli $ghCli
    }

    'mac-identities' {
        if (-not $SignIdentity -or -not $InstallerIdentity) {
            throw "-Target mac-identities needs -SignIdentity and -InstallerIdentity."
        }

        if ($SignIdentity -notlike 'Developer ID Application:*') {
            throw "-SignIdentity must start with 'Developer ID Application:'."
        }

        if ($InstallerIdentity -notlike 'Developer ID Installer:*') {
            throw "-InstallerIdentity must start with 'Developer ID Installer:'."
        }

        Write-Step 'Checking the identity strings'
        Write-Host "  app:       $SignIdentity"
        Write-Host "  installer: $InstallerIdentity"

        Write-Step "Uploading secrets to $Repository"
        Publish-Secret -Name 'MACOS_APP_SIGN_IDENTITY' -Value $SignIdentity -GhCli $ghCli
        Publish-Secret -Name 'MACOS_INSTALLER_SIGN_IDENTITY' -Value $InstallerIdentity -GhCli $ghCli
    }

    'apple-notary' {
        if (-not $AppleId -or -not $TeamId -or -not $AppPassword) {
            throw "-Target apple-notary needs -AppleId, -TeamId and -AppPassword."
        }

        if ($AppPassword -match '\s') {
            throw "The app-specific password contains whitespace. Apple prints it in groups of four; remove the dashes and spaces."
        }

        Write-Step "Uploading notarization credentials to $Repository"
        Write-Host "  Apple ID: $AppleId"
        Write-Host "  Team ID:  $TeamId"
        Publish-Secret -Name 'APPLE_ID' -Value $AppleId -GhCli $ghCli
        Publish-Secret -Name 'APPLE_TEAM_ID' -Value $TeamId -GhCli $ghCli
        Publish-Secret -Name 'APPLE_APP_PASSWORD' -Value $AppPassword -GhCli $ghCli
    }

    'keychain-password' {
        if (-not $KeychainPassword) {
            $bytes = New-Object byte[] 24
            $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
            try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
            $KeychainPassword = [Convert]::ToBase64String($bytes)
        }

        Write-Step "Uploading a temporary keychain password to $Repository"
        Publish-Secret -Name 'MACOS_KEYCHAIN_PASSWORD' -Value $KeychainPassword -GhCli $ghCli
    }
}

Write-Step 'Done'
if ($DryRun) {
    Write-Host 'Dry run: no secret was changed.' -ForegroundColor Yellow
}
else {
    Write-Host "Run 'gh workflow run signing-check.yml --repo $Repository' to verify what the workflow can see." -ForegroundColor Yellow
}
