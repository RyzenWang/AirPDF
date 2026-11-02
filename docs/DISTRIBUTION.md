# Distribution, signing, and updates

AirPDF uses [Velopack](https://velopack.io/) for Windows installers, macOS
`.pkg` bundles, and in-app updates. The release workflow builds and publishes
those packages from a version tag.

## Release assets

For a tag such as `v0.2.0`, the workflow publishes:

| Platform | Assets |
| --- | --- |
| Windows x64 | `AirPDF-win-Setup.exe`, `AirPDF-win-Portable.zip`, full `.nupkg`, `releases.win.json` |
| macOS arm64 | `AirPDF-osx-arm64-Setup.pkg`, portable package, full `.nupkg`, `releases.osx-arm64.json` |
| macOS x64 | `AirPDF-osx-x64-Setup.pkg`, portable package, full `.nupkg`, `releases.osx-x64.json` |
| Linux x64 | `AirPDF-<version>-linux-x64.tar.gz` |

The `releases.*.json` files are required by the in-app updater. Portable and
source builds do not self-update; install with the Windows Setup or macOS pkg
to enable automatic updates.
The Windows and Linux builds are published with NativeAOT. Everything is compiled ahead of
time, so the app starts in well under a second instead of several: measured to first window
on Windows, 0.7 s against 5.0 s for the trimmed JIT build, with the rest of the time spread
across Avalonia initialisation, XAML loading and first layout rather than any single slow
call. The Windows output is larger uncompressed (60 MB against 28 MB), but Velopack
compresses the payload, so the installer grows only from 33.4 MB to 35.0 MB. Linux ships a
tar.gz of the AOT output; macOS still builds JIT.

AOT and trimming fail silently when a reflection path loses its metadata: a trimmed build
once stopped writing the settings file while otherwise behaving normally. The Windows
publish step therefore fails on any IL warning and on an output that is unexpectedly large,
and `ci.yml` runs the same check for both the Windows and Linux AOT builds on every pull
request so a regression is caught long before a tag. The Linux runner installs `clang` and
`zlib1g-dev` first, since NativeAOT links natively and the image does not ship them.
The AOT build's update path is verified rather than assumed. Installed from the Setup
package, the app reports `IsInstalled`, reads the release feed and compares versions; against
a local feed advertising a newer package it downloads and unpacks it. This is worth checking
by hand because an AOT break here is silent: the update check catches its own exceptions, so
users would simply never be offered an update.

Note that Velopack is deliberately not rooted for trimming. Rooting it made the AOT compiler
compile a COM shortcut class whose IL cannot be compiled, which warned that it "will always
throw"; the app never reaches that code, so leaving it trimmable removes the warning and
1.1 MB.

## Local packaging

```bash
dotnet publish src/AirPdf.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:Version=0.2.0 \
  -o publish/win-x64

dotnet tool install vpk --version 1.2.161 --tool-path .vpk

./.vpk/vpk pack \
  --packId AirPDF \
  --packVersion 0.2.0 \
  --packDir publish/win-x64 \
  --mainExe AirPdf.exe \
  --packTitle AirPDF \
  --packAuthors "AirPDF contributors" \
  --icon src/AirPdf.App/Assets/airpdf.ico \
  --runtime win-x64 \
  --channel win \
  --outputDir releases/win
```

On macOS, use `--mainExe AirPdf`, `--icon src/AirPdf.App/Assets/airpdf.icns`,
and `--runtime osx-arm64` or `osx-x64`. On macOS, set the channel to
`osx-arm64` / `osx-x64` and use the matching `--packId`
(`AirPDF-osx-arm64` / `AirPDF-osx-x64`) so the full packages have unique asset
names in one GitHub release.

## Windows signing

The workflow signs with a PFX certificate when the following repository secrets
exist:

- `WINDOWS_CERTIFICATE_BASE64` — base64 of the `.pfx`
- `WINDOWS_CERTIFICATE_PASSWORD` — PFX password

`packaging/signing/Set-SigningSecrets.ps1 -Target windows` validates the
certificate and sets both secrets in one step. Setting only one of the two now
fails the job instead of silently shipping an unsigned installer.

When absent, the workflow still produces an unsigned installer and says so in the
run summary. Unsigned builds may trigger SmartScreen. For production, prefer
Azure Trusted Signing (or an EV/OV certificate stored in an HSM) and add the
appropriate `vpk` signing parameters; modern OV certificates often cannot be
exported as a portable PFX.

The `Signing check` workflow verifies the configuration without publishing
anything. See [SIGNING.md](SIGNING.md) for the full credential guide.

## macOS signing and notarization

Notarization requires an Apple Developer Program membership and **both** a
Developer ID Application certificate and a Developer ID Installer certificate.

Add these repository secrets:

| Secret | Meaning |
| --- | --- |
| `MACOS_CERTIFICATE_BASE64` | base64 of the Developer ID Application `.p12` |
| `MACOS_INSTALLER_CERTIFICATE_BASE64` | base64 of the Developer ID Installer `.p12` |
| `MACOS_CERTIFICATE_PASSWORD` | password for both `.p12` files |
| `MACOS_APP_SIGN_IDENTITY` | e.g. `Developer ID Application: Your Name` |
| `MACOS_INSTALLER_SIGN_IDENTITY` | e.g. `Developer ID Installer: Your Name` |
| `APPLE_ID` | Apple account email |
| `APPLE_APP_PASSWORD` | app-specific password for notarytool |
| `APPLE_TEAM_ID` | Apple Developer team ID |
| `MACOS_KEYCHAIN_PASSWORD` | random password for the temporary CI keychain |

The workflow creates a temporary keychain, installs both certificates, creates
the notary profile, and passes `--signAppIdentity`, `--signInstallIdentity`,
`--notaryProfile`, and `--keychain` to `vpk pack`. Velopack then signs and
notarizes the `.app` and `.pkg`, and staples the result.

Without those secrets, the macOS jobs still create unsigned packages. Gatekeeper
will block them on a normal user machine until they are signed and notarized.
Configuring a certificate without the Apple credentials now fails the job rather
than producing a package that cannot be opened.

See [SIGNING.md](SIGNING.md) for how to obtain the certificates, set every
secret, and verify the result.

## In-app updates

`VelopackApp.Build().Run()` runs before any Avalonia initialization in
`Program.Main`. `UpdateService` checks the GitHub release channel for the
installed platform, downloads an update in the background, and exposes a status
bar action:

1. Check the latest release two seconds after startup.
2. Download the update automatically, showing progress.
3. Show **Restart to update X.Y.Z** when ready.
4. Apply and restart through Velopack.

`GithubSource` only reads stable releases. A pre-release tag does not advertise
itself as an update. The check is silent when the app runs from source or from a
portable archive because `UpdateManager.IsInstalled` is false.

To test updates end to end:

1. Install `v0.2.0` using the Windows Setup or macOS pkg.
2. Publish a newer tag, for example `v0.2.1`, and let the release workflow
   attach the new `releases.*.json` and `.nupkg` files.
3. Start the installed v0.2.0 app and wait for the update button.

## GitHub release workflow

`.github/workflows/release.yml` triggers on `v*` tags and:

- builds/packages Windows, macOS arm64, macOS x64, and a Linux portable archive;
- optionally signs and notarizes when the secrets above are present;
- creates a GitHub Release with all installer, portable, full-package, and
  `releases.*.json` assets.

The release body is the annotated tag message, read back by the workflow. Write the
user-facing summary into the tag (`git tag -a v0.3.1 -F notes.txt`), not into the release
page by hand: leaving the body to be generated produced an empty page for v0.3.1 and only a
compare link for v0.3.0.

The workflow does not run on pull requests. Use a temporary tag on a fork or a
manual test workflow if you need to validate packaging changes without
publishing a real release.
