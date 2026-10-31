# Code signing and notarization

AirPDF publishes installers that Windows SmartScreen and macOS Gatekeeper will
inspect. Without a signature, Windows shows "unknown publisher" and macOS refuses
to open the app at all. This page covers getting the credentials, storing them,
and proving they work.

Two scripts do the work:

| Script | Purpose |
| --- | --- |
| [`packaging/signing/Set-SigningSecrets.ps1`](../packaging/signing/Set-SigningSecrets.ps1) | Validates a certificate and uploads it as a repository secret |
| [`packaging/signing/New-TestCertificate.ps1`](../packaging/signing/New-TestCertificate.ps1) | Mints a throwaway certificate to exercise the pipeline |

---

## What each platform needs

### Windows: one code-signing certificate

| Item | Detail |
| --- | --- |
| Type | OV or EV code-signing certificate (Authenticode) |
| Format | `.pfx` exported with the private key |
| Key usage | Extended key usage must include **Code Signing** (1.3.6.1.5.5.7.3.3) |
| Validity | Must cover the release date |

Options, cheapest first:

1. **Azure Trusted Signing** — a Microsoft subscription that signs through an
   API instead of shipping a PFX. Instant SmartScreen reputation, no hardware
   token. Requires an Azure subscription and identity verification. The release
   workflow does not use it yet; `vpk` accepts `--azureTrustedSignFile`.
2. **OV certificate** from a CA such as Sectigo or DigiCert. Cheaper, but
   SmartScreen reputation builds up over the first weeks of downloads.
3. **EV certificate** — immediate reputation, but key material is usually locked
   in a hardware token or cloud HSM, which a GitHub-hosted runner cannot reach.
   Use a self-hosted runner, or an HSM-backed provider.

> Since June 2023, CA/Browser Forum rules require new code-signing private keys
> to live in FIPS 140-2 hardware. Many CAs therefore issue a token or a cloud
> signing service rather than a downloadable PFX. If your CA cannot export a
> PFX, plan for Azure Trusted Signing or an HSM-backed signer.

### macOS: two certificates and an Apple ID

| Item | Detail |
| --- | --- |
| Membership | Apple Developer Program (paid, yearly) |
| Certificate 1 | **Developer ID Application** — signs the `.app` |
| Certificate 2 | **Developer ID Installer** — signs the `.pkg` |
| Notarization | App-specific password, Apple ID, and team ID |

Both certificates are created at
[developer.apple.com/account/resources/certificates](https://developer.apple.com/account/resources/certificates)
and exported from Keychain Access as `.p12`.

Create the app-specific password at [appleid.apple.com](https://appleid.apple.com)
under **Sign-In and Security → App-Specific Passwords**. It looks like
`abcd-efgh-ijkl-mnop`; strip the dashes.

Notarization is not optional in practice: an unsigned or un-notarized `.app`
quarantined by a browser is refused with "the app is damaged and cannot be
opened".

---

## Configure the secrets

Run the script from the repository root. It checks the certificate before
uploading anything, so a bad export fails locally instead of mid-release.

### Windows

```powershell
./packaging/signing/Set-SigningSecrets.ps1 `
  -Target windows `
  -CertificatePath ./airpdf-codesign.pfx
```

It prompts for the password, then sets:

- `WINDOWS_CERTIFICATE_BASE64`
- `WINDOWS_CERTIFICATE_PASSWORD`

### macOS

```powershell
# Identity strings, copied from `security find-identity -v -p codesigning`
./packaging/signing/Set-SigningSecrets.ps1 -Target mac-identities `
  -SignIdentity      "Developer ID Application: Your Name (TEAMID)" `
  -InstallerIdentity "Developer ID Installer: Your Name (TEAMID)"

# The two certificates
./packaging/signing/Set-SigningSecrets.ps1 -Target mac-app       -CertificatePath ./developer-id-application.p12
./packaging/signing/Set-SigningSecrets.ps1 -Target mac-installer -CertificatePath ./developer-id-installer.p12

# Notarization account
./packaging/signing/Set-SigningSecrets.ps1 -Target apple-notary `
  -AppleId you@example.com -TeamId TEAMID -AppPassword abcd-efgh-ijkl-mnop

# Random password for the temporary CI keychain
./packaging/signing/Set-SigningSecrets.ps1 -Target keychain-password
```

> Velopack matches the identity by its **prefix**: pass
> `Developer ID Application: Your Name`, without the trailing `(TEAMID)`.

Add `-DryRun` to any of these to validate the input and print which secrets
would be written without touching the repository.

### If you prefer the web UI

Settings → Secrets and variables → Actions → **New repository secret**. The names
must match the table above exactly.

---

## Verify the configuration

The `Signing check` workflow (`workflow_dispatch`) inspects the secrets
**without publishing anything**:

```bash
gh workflow run signing-check.yml --repo RyzenWang/AirPDF
gh run watch --repo RyzenWang/AirPDF
```

It reports, in the job summary:

- which secrets are present;
- the certificate subject, thumbprint, and expiry date;
- a full signing pass over the real installer when `package` is enabled.

Two behaviours to know about:

- **Half-configured secrets fail the run.** A certificate without a password (or
  a macOS certificate without the Apple credentials) is treated as an error,
  because it would otherwise produce a silently unsigned release.
- **With no secrets at all, the run still passes** — but only because it mints a
  throwaway self-signed certificate, signs with it, and verifies the result. The
  summary then says that published builds would be unsigned.

Set `notarize: true` when dispatching the workflow to exercise the full Apple
submission path. That is the only step that needs to reach Apple's servers, and
it takes several minutes.

---

## What the release workflow does

On a `v*` tag:

| Platform | Signing |
| --- | --- |
| Windows | `signtool` through Velopack, SHA-256, timestamped by DigiCert |
| macOS | `codesign` on the `.app`, `productsign` on the `.pkg`, then `notarytool` and stapling |

Both jobs write a line into the run summary stating whether the artifacts were
signed, so an audit of a release never has to guess.

---

## Rotating or revoking

1. Replace the secret with the new certificate (`Set-SigningSecrets.ps1`
   overwrites it).
2. Keep the old certificate until the last release it signed is out of support;
   timestamped signatures stay valid after the certificate expires.
3. For macOS, run the check workflow with `package: true` and `notarize: true`
   before tagging — notarization rejections are easier to read in a run log than
   in a failed release.

---

## Costs

| Item | Typical cost |
| --- | --- |
| Windows OV certificate | ~US$200–400 / year |
| Windows EV certificate | ~US$300–500 / year, plus hardware |
| Azure Trusted Signing | monthly, no hardware |
| Apple Developer Program | US$99 / year |

There is no free option that produces a trusted signature. The scripts in this
directory let the pipeline be built and verified now, so buying a certificate
later is a single command per secret rather than a redesign.
