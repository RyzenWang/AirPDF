# Microsoft Store submission checklist

Everything needed to take the MSIX package from `packaging/msix/` to a published
Store listing. The packaging itself already works; what remains is account setup,
listing material, and the identity values.

## 1. Account and reservation

| Step | Where | Notes |
| --- | --- | --- |
| Register as an individual developer | [storedeveloper.microsoft.com](https://storedeveloper.microsoft.com) | Free since September 2025; needs a government ID and a selfie. A company account costs more and needs business verification. |
| Reserve the app name | Partner Center → **Apps and games** → **New product** → **MSIX or PWA app** | Reserve before building: the manifest identity comes from this step. |
| Copy the identity values | Product management → **Product identity** | `Package/Identity/Name` and `Package/Identity/Publisher`. The publisher is a `CN=<id>` distinguished name, not a person's name. |

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

The package is intentionally unsigned. Microsoft re-signs Store submissions, and a
second signature only creates room for confusion.

Version rules for every later submission: four parts, each ≤ 65535, major ≥ 1, and
the fourth part must stay `0`. Versions must strictly increase between submissions.

## 3. Listing material

| Item | Requirement | Status |
| --- | --- | --- |
| Description | Plain text, no placeholder links, must match what the app does | To write |
| Screenshots | At least one desktop screenshot, 1366×768 or larger, PNG | `docs/reading-light.png`, `docs/search.png`, `docs/welcome.png` exist but are 1280-wide-ish; re-export at ≥1366 wide |
| Store logo | 300×300 PNG | Generate from `src/AirPdf.App/Assets/airpdf.png` |
| App icon | Provided by the package | Done |
| Category | Books & reference, or Productivity | Pick one |
| Supported languages | English, Simplified Chinese | Listing text needed in both |
| Privacy policy URL | Required if any personal data is touched | AirPDF sends nothing; publish a short "collects no data" page and link it |
| Support contact | A URL or email | Use the GitHub issues page |
| Age rating questionnaire | Answer honestly; a PDF reader is unrestricted | To fill in |
| System requirements | Windows 10 1809 (build 17763) or later, x64 | Manifest declares 10.0.19041; align the listing |

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
- [ ] Screenshots re-exported at 1366×768 or larger
- [ ] Store logo (300×300) exported
- [ ] Description written in English and Simplified Chinese
- [ ] Privacy policy page published and linked
- [ ] Support contact set
- [ ] Age rating questionnaire completed
- [ ] `.msixupload` uploaded and certification passed
- [ ] Listing published and the Store install tested once
