# Microsoft Store submission checklist

Everything needed to take the MSIX package from `packaging/msix/` to a published
Store listing. The packaging itself already works; what remains is account setup,
listing material, and the identity values.

## 0. Registration walkthrough

The account has to be created by you, on your own Microsoft account, because the
verification is tied to your identity documents. This is the exact path.

### Before you start

| Prepare | Why |
| --- | --- |
| A Microsoft account you intend to keep | The account owns the app listing permanently; moving it later is not supported |
| A government-issued photo ID | Verified by scanning plus a selfie |
| A phone with a camera | The ID capture happens on mobile, not on the desktop |
| 15 minutes, in good light | Verification usually completes during the flow |
| Your legal name exactly as printed on the ID | It becomes the publisher name; mismatches are the most common cause of a failed verification |

No credit card is required. **Start at `storedeveloper.microsoft.com` and nowhere
else** — entering through Partner Center, Visual Studio, or the Xbox program shows
the old flow, which charges a fee and lacks the new verification.

### The flow

1. Open [storedeveloper.microsoft.com](https://storedeveloper.microsoft.com) and
   choose **Get started for free**.
2. Select **Individual developer**.
   - Choose this unless you are publishing as a registered business. Individual is
     free, needs no tax or payout details, and is not a downgrade: you can publish
     paid apps from it.
   - **An individual account cannot be converted to a company account later.** If
     you expect to publish under a company name, create a company account now.
3. Sign in with the Microsoft account that will own the listing.
4. Follow the identity verification: capture the government ID, then a selfie. Use
   the original document, not a photo of a screen, and avoid glare.
5. Review the profile Partner Center fills in. The **publisher display name** here
   is what customers see under the app name; it can differ from your legal name.
6. Finish setup and choose **Go to Partner Center dashboard**.
7. If the dashboard looks empty, wait five minutes and refresh until the **Apps and
   games** tile appears. Being redirected to `partner.microsoft.com` is expected.

If verification fails, it is almost always the name or the image quality; support
tickets go through <https://aka.ms/windowsdevelopersupport>.

### Reserve the name and copy the identity

1. In Partner Center, go to **Apps and games** → **New product** → **MSIX or PWA
   app**.
2. Enter the name. `AirPDF` if it is free; otherwise the fallbacks in
   [store/listing.md](store/listing.md).
3. After the product is created, open **Product management** → **Product identity**.
   Two values matter:

   | Field | Looks like | Used as |
   | --- | --- | --- |
   | Package/Identity/Name | `AirPDF`, `Contoso.AirPDF` | `-IdentityName` |
   | Package/Identity/Publisher | `CN=1A2B3C4D-1111-2222-3333-444455556666` | `-Publisher` |

   Use the value verbatim, including the `CN=`. It is a generated identifier, not
   your name, and it must match the manifest exactly or the upload is rejected.

The reserved name fixes `Identity Name`; it never changes afterwards. Changing it
later means a new product.

## 1. Account and reservation

| Step | Where | Notes |
| --- | --- | --- |
| Register as an individual developer | [storedeveloper.microsoft.com](https://storedeveloper.microsoft.com) | Free; needs a government ID and a selfie. Company accounts also have no fee now, but require business verification and cannot be converted from individual. |
| Reserve the app name | Partner Center → **Apps and games** → **New product** → **MSIX or PWA app** | Reserve before building: the manifest identity comes from this step. |
| Copy the identity values | Product management → **Product identity** | `Package/Identity/Name` and `Package/Identity/Publisher`. |

The reserved name fixes `Identity Name`; it never changes afterwards. Changing it
later means a new product.

## 2. Build the submission package

```powershell
./packaging/msix/Build-Msix.ps1 `
  -Version 1.0.0.0 `
  -ForStore `
  -IdentityName '<Package/Identity/Name>' `
  -Publisher 'CN=<Package/Identity/Publisher>'
```

`-ForStore` refuses the placeholder identity, refuses a signing certificate, checks
the identity against the manifest schema rules, verifies the packed manifest, and
produces two files:

| File | Purpose |
| --- | --- |
| `artifacts/msix/AirPDF-<version>-x64.msix` | what was actually packed |
| `artifacts/msix/AirPDF-<version>-x64.msixupload` | what Partner Center accepts |

The publish is ahead of time, so the Store download is 25.2 MB rather than 51.9 MB and the
first window appears in under a second instead of several. Pass `-NoAot` only if the build
machine has no C++ toolchain.

The package is intentionally unsigned. Microsoft re-signs Store submissions, and a
second signature only creates room for confusion.

Version rules for every later submission: four parts, each ≤ 65535, major ≥ 1, and
the fourth part must stay `0`. Versions must strictly increase between submissions.

## 3. Listing material

Everything below is written and ready to paste; only the account-specific URLs
need filling in.

| Item | Requirement | Status |
| --- | --- | --- |
| Description | Plain text, no placeholder links, must match what the app does | Ready: [store/listing.md](store/listing.md), English and Chinese |
| Screenshots | At least one desktop screenshot, 1366×768 or larger, PNG | Ready: `docs/store/screenshot-*.png`, 1578×944 client area |
| Store logo | 300×300 PNG | Ready: `docs/store/logo-300.png` |
| App icon | Provided by the package | Done |
| Category | Books & reference | Decided in [store/listing.md](store/listing.md) |
| Supported languages | English, Simplified Chinese | Copy ready in both |
| Privacy policy URL | Required if any personal data is touched | Text ready: [store/PRIVACY.md](store/PRIVACY.md); still needs publishing |
| Support contact | A URL or email | GitHub issues page |
| Age rating questionnaire | Answer honestly; a PDF reader is unrestricted | Guidance in [store/listing.md](store/listing.md) |
| System requirements | Windows 10 version 2004 (build 19041) or later, x64 | Aligns with the manifest |

The `docs/store/` folder holds the screenshots, the logo, the listing copy (with a
ready-made "Notes for certification" block), and the privacy policy.

The manifest currently targets `Windows.Desktop` with `MinVersion 10.0.19041.0`, so
the listing must not claim support below Windows 10 version 2004.

## 4. Certification expectations

Known checks that apply to this app:

- **Runs without a network connection.** The reader is offline-first; the update
  check only runs in the Velopack build and degrades silently here.
- **Does not crash on a missing file.** `OpenPathAsync` reports a friendly error.
- **Uninstalls cleanly.** MSIX handles this; the package writes nothing to
  `Program Files` or `HKLM`.
- **Full trust is declared.** `rescap:runFullTrust` is justified by reading
  arbitrary user documents.
- **No bundled third-party installers, no ads, no telemetry.**

Certification typically takes a few hours and can take up to three business days.

## 5. After the first release

| Task | Note |
| --- | --- |
| Updates | Build a higher version, upload a new `.msixupload`, submit. Users get it from the Store; the in-app updater stays inert because `UpdateManager.IsInstalled` is false inside a package. |
| Rollback | Submit a new package with a higher version that reverts the change. Removing a package from a submission only stops new acquisitions. |
| Gradual rollout | Available per submission in Partner Center; use it for a release that touches rendering. |
| Symbols | Optional `.appxsym` in the `.msixupload` enables crash reports in Partner Center. AirPDF writes `crash.log` locally instead; adding symbols is a nice-to-have. |

## 6. Pre-submission smoke test

Before uploading, on a clean Windows machine or VM:

1. Install the package: `Add-AppxPackage -Path artifacts/msix/AirPDF-<version>-x64.msix`
   (requires the signing certificate to be trusted, or install a Store-built copy).
2. Open a PDF from Explorer → **Open with** → AirPDF, and confirm the document
   loads and the app is offered as a handler.
3. Open a second PDF while the first is running and confirm both windows work.
4. Check that settings survive a restart, and that `%APPDATA%\AirPdf\settings.json`
   is the file being updated (not the package cache) when that file already existed.
5. Uninstall and confirm the app disappears from the Start menu and from
   **Apps & features**.

The `MSIX check` workflow already covers 1, 2, and 4 on a runner; steps 3 and 5 need
a human, because they are about what a person sees.

## Checklist

- [ ] Developer account registered and verified
- [ ] App name reserved; identity values copied
- [ ] `Build-Msix.ps1 -ForStore` succeeds with the reserved identity
- [x] Screenshots captured at 1578×944 (`docs/store/`)
- [x] Store logo (300×300) exported
- [x] Description written in English and Simplified Chinese
- [ ] Privacy policy page published and linked
- [ ] Support contact set
- [ ] Age rating questionnaire completed
- [ ] `.msixupload` uploaded and certification passed
- [ ] Listing published and the Store install tested once
