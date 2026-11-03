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
from the `Microsoft.Windows.SDK.BuildTools` NuGet package 鈥?no elevation and no
SDK install needed.

Sign with a certificate whose subject matches the manifest `Publisher`:

```powershell
./packaging/msix/Build-Msix.ps1 -Version 0.3.3.0 `
  -CertificatePath ./test.pfx -CertificatePassword test
```

The publish is **ahead of time** by default, because that is what the releases ship: the
package comes out at 25.2 MB against 51.9 MB for the JIT build, and the first window appears in
under a second instead of several. AOT needs the C++ toolchain (`clang` and the MSVC linker) on
the build machine; pass `-NoAot` to publish the JIT build when it is not available.

Use `-SkipPublish` to repack an existing publish folder.

Two switches shape the package:

- `-Architecture arm64` builds the ARM64 package instead of x64. Upload both to the same
  Partner Center submission; a bundle is not needed, since the Store serves the right one.
- `-Symbols` publishes with debug symbols and packs them into `AirPDF-<version>-<arch>.appxsym`
  next to the package. Inside the `.msixupload` it gives Partner Center stack traces for crash
  reports; the application itself ships without symbols, so this asks for them explicitly.

The publish folder is per architecture (`artifacts/msix/app-<arch>`), so both can be built in a
row without clobbering each other.

For a Store submission, pass the identity Partner Center assigned and add
`-ForStore`. That mode refuses the placeholder identity, refuses a signing
certificate, checks the identity against the manifest schema, verifies the packed
manifest, and emits a `.msixupload` next to the `.msix`:

```powershell
./packaging/msix/Build-Msix.ps1 -Version 1.0.0.0 -ForStore `
  -IdentityName '<Package/Identity/Name>' `
  -Publisher 'CN=<Package/Identity/Publisher>'
```

The rest of the process — account setup, listing material, certification
expectations, and the pre-submission smoke test — is in
[docs/STORE_SUBMISSION.md](../../docs/STORE_SUBMISSION.md).

## What the manifest declares

- `rescap:runFullTrust` 鈥?a PDF reader needs unconstrained file system access to
  the documents the user opens.
- `.pdf` file type association, so Windows can offer AirPDF as a handler. Windows
  always prompts before changing the default; the manifest cannot claim it.
- `Windows.Desktop`, minimum 10.0.19041.0.

The app must never write beside its executable. AirPDF keeps `settings.json` and
`crash.log` in `%APPDATA%\AirPdf`, which is exactly what MSIX requires.

## Verified locally

- Package builds and passes every structural check the script asserts.
- The folder-published app runs and opens a PDF.
- Signing with a test certificate produces a signature that `signtool verify /pa` accepts once the certificate is trusted.
- Sideloading needs the root certificate in `LocalMachine`, so installing locally requires an elevated shell; the `MSIX check` workflow does it on a runner.

## Verified on a runner

`.github/workflows/msix-check.yml` builds the package, signs it with a throwaway
certificate, trusts that certificate, installs the package, checks what Windows
registered, launches the app, opens a document the way the file association
does, and uninstalls.

The activation test launches the **installed** executable with a document path —
exactly what the shell does for `windows.fileTypeAssociation` — using a name with
a space and non-ASCII characters. Because the app records every opened document
in its settings file, that file proves the document loaded rather than the
argument being ignored.

## Behaviour that differs from the Velopack build

**%APPDATA% is redirected, but only for writes.** The workflow measures this
rather than assuming it. It seeds the real settings file with a language marker,
launches the packaged app with a document (which forces a save), and then reads
both copies.

Measured on a runner:

| copy | result |
| --- | --- |
| real `%APPDATA%\AirPdf\settings.json` | kept `language=zh-CN`, did **not** receive the opened document |
| `%LOCALAPPDATA%\Packages\AirPDF_<hash>\LocalCache\Roaming\AirPdf\settings.json` | kept `language=zh-CN` **and** received the opened document |

So reads fall back to the real file — the packaged app starts from the settings
the unpackaged build left behind — while writes landed in the package cache before
the `SettingsService` change described below.

Microsoft documents the cause: a new file under `AppData` is redirected, but *"if
the file is opened from the real `AppData` location, then no virtualization for
that file occurs."* AirPDF saves by writing `settings.json.tmp` and renaming it,
which always creates a new file, so the write is redirected even when the
destination already exists.

What follows:

- No migration code is needed. Existing users keep their bookmarks, recent files,
  and reading positions when they move to the Store build.
- `SettingsService` now overwrites the existing file instead of writing a temporary
  file and renaming it, which is what makes the "opened from the real location"
  rule apply. Measured after that change on the same runner:

  | copy | result |
  | --- | --- |
  | real `%APPDATA%\AirPdf\settings.json` | `language=zh-CN` **and** the opened document |
  | package cache | absent |

  The Velopack build and the Store build therefore share one settings file.
- A user whose first AirPDF install is the Store build still creates that file
  inside the package cache, and uninstalling the package removes it. That is the
  platform's behaviour for new files and cannot be avoided from inside the package.
- The `crash.log` follows the same rule, so a Store user's log lives in the cache.
- The in-place overwrite can truncate the file if the process dies mid-write, so the
  previous copy is kept as `settings.json.bak` and loaded when the live file is
  unreadable.

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

