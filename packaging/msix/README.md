# MSIX packaging

Proof of concept for shipping AirPDF through the Microsoft Store as an MSIX
package. Nothing here is wired into `.github/workflows/release.yml`; the Velopack
installer remains the primary Windows distribution.

## Why MSIX

The Store accepts two kinds of Windows submission:

| | MSIX | MSI / EXE |
| --- | --- | --- |
| Code signing | Microsoft re-signs the package, free | You must sign every PE file with a CA-trusted certificate |
| Updates | Store delivers them | Your installer must handle them |
| Installer requirements | Manifest only | Silent, offline, immutable versioned URL per release |

MSIX avoids purchasing a certificate and avoids SmartScreen warnings, which is
why it is the recommended path. The trade-off is that **in-app updating must go
away**: an MSIX installs into a read-only `WindowsApps` folder, so Velopack's
`Update.exe` cannot replace files, and Store-managed apps are expected to update
through the Store.

## Build

```powershell
./packaging/msix/Build-Msix.ps1 -Version 0.3.3.0
```

The script publishes the app as a folder (MSIX cannot use `PublishSingleFile`),
generates the logo assets from `src/AirPdf.App/Assets/airpdf.png`, expands
`AppxManifest.xml`, packs with `makeappx`, then unpacks the result and asserts
the parts Microsoft certifies.

`makeappx` is resolved from an installed Windows SDK, or downloaded on demand
from the `Microsoft.Windows.SDK.BuildTools` NuGet package — no elevation and no
SDK install needed.

Sign with a certificate whose subject matches the manifest `Publisher`:

```powershell
./packaging/msix/Build-Msix.ps1 -Version 0.3.3.0 `
  -CertificatePath ./test.pfx -CertificatePassword test
```

Use `-SkipPublish` to repack an existing `artifacts/msix/app` folder.

## What the manifest declares

- `rescap:runFullTrust` — a PDF reader needs unconstrained file system access to
  the documents the user opens.
- `.pdf` file type association, so Windows can offer AirPDF as a handler. Windows
  always prompts before changing the default; the manifest cannot claim it.
- `Windows.Desktop`, minimum 10.0.19041.0.

The app must never write beside its executable. AirPDF keeps `settings.json` and
`crash.log` in `%APPDATA%\AirPdf`, which is exactly what MSIX requires.

## Verified locally

- Package builds and passes every structural check the script asserts.
- The folder-published app runs and opens a PDF.
- Signing with a test certificate produces a valid signature block.

## Verified on a runner

`Signing check` cannot install packages, so see `.github/workflows/msix-check.yml`:
it builds the package, signs it with a throwaway certificate, trusts that
certificate, installs the package, checks that Windows registered it, launches
the app, and uninstalls.

## Not done yet

- **Store submission.** Needs a Partner Center reservation, screenshots, a
  privacy policy URL, and the Store's identity values in the manifest
  (`Name`, `Publisher`) instead of the local placeholders.
- **Removing Velopack from the MSIX build.** `VelopackApp.Build().Run()` runs
  before the UI starts and `UpdateService` is guarded by `IsInstalled`, which is
  false inside a package, so the update UI never appears. That is safe but the
  code should eventually be compiled out for this channel.
- **Localization.** The package declares `en-us` and `zh-cn` resources but ships
  a single binary; Store listings will need both languages.
- **AOT.** Store packaging uses a folder publish. The NativeAOT build is still
  used for the Velopack channel; whether to switch the Store channel to AOT is a
  separate decision.
