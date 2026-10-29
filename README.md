<p align="center">
  <strong>English</strong> &nbsp;·&nbsp; <a href="README.zh-CN.md">简体中文</a>
</p>

<p align="center">
  <img src="docs/welcome.png" alt="AirPDF welcome screen" width="860" />
</p>

<h1 align="center">AirPDF</h1>

<p align="center">
  A feather-light, cross-platform PDF reader built with .NET and Avalonia.
</p>

<p align="center">
  <a href="https://github.com/RyzenWang/AirPDF/actions/workflows/ci.yml">
    <img alt="CI" src="https://github.com/RyzenWang/AirPDF/actions/workflows/ci.yml/badge.svg" />
  </a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square" />
  <img alt="Avalonia 11" src="https://img.shields.io/badge/Avalonia-11.3-8B44AC?style=flat-square" />
  <img alt="MIT license" src="https://img.shields.io/badge/license-MIT-2E6F63?style=flat-square" />
  <img alt="Platforms" src="https://img.shields.io/badge/platforms-Windows%20%7C%20macOS%20%7C%20Linux-E3563B?style=flat-square" />
</p>

AirPDF opens PDFs quickly, renders them natively, and stays out of the way. It
has no account, no cloud, no telemetry, and no web view. The whole reader is a
small .NET application with a hand-built Avalonia interface.

> **Status:** 0.2 preview. The reading core is stable and tested; annotations
> and printing are not implemented yet. See the [roadmap](#roadmap).

---

## Screenshots

| Reading, light | Reading, dark |
| --- | --- |
| ![AirPDF reading a document in light theme](docs/reading-light.png) | ![AirPDF reading a document in dark theme](docs/reading-dark.png) |

![Search with page highlights](docs/search.png)

## Features

- **Native PDFium rendering** at the exact display scale, with high-DPI support.
- **Continuous, virtualized pages** — only pages near the viewport are decoded.
- **Thumbnail rail and document outline** for fast navigation.
- **Full-text search** with page-grouped results and in-page match highlights.
- **Text selection and copy** — drag across words and pages, double-click a
  word, `Ctrl+A` selects the document, and `Ctrl+C` copies the result.
- **Remembers your place** in every document, including the offset within the
  page; the position is restored on the next open.
- **Remembers your zoom** too — fit width, fit page, or an exact factor, per
  document.
- **Zoom** with fit width, fit page, actual size, presets, and `Ctrl` + wheel.
- **Password-protected PDFs** with a proper unlock prompt.
- **Light, dark, and high-contrast themes**, following the system on first launch.
- **Status bar** with the page position and a reading-progress percentage.
- **Links** — internal destinations jump to their page, `http`/`https`/`mailto`
  links open in your browser or mail client, and the cursor turns into a hand.
- **Back navigation** with `Alt+Left` after any outline, thumbnail, search, or
  page jump.
- **Right-click menu** for copy, select all, and showing the file in the folder.
- **Accessible controls** — toolbar buttons, inputs, and lists expose names to
  screen readers.
- **English by default, Simplified Chinese on demand** — switch live from the
  toolbar or with `Ctrl+Shift+L`.
- **Drag and drop**, command-line opening, and a recent-files shelf.
- **Automatic updates** in installed Windows and macOS builds, including
  background download and a restart action.
- **Keyboard-first navigation** and a quiet, information-dense status bar.
- **No network access on the reading path**; installed builds only contact
  GitHub when checking for updates.

## Tech stack

| Layer | Choice | Why |
| --- | --- | --- |
| Runtime | .NET 10 (LTS) | One modern runtime for Windows, macOS, and Linux. |
| UI | Avalonia 11 | Native desktop windows from a single XAML codebase. |
| Rendering | PDFium | The same open-source engine used by browsers; one document handle stays open while reading. |
| Raster transport | SkiaSharp | Fast, predictable pixel handling. |
| Text layer | PdfPig | Managed PDF text extraction, search, and match rectangles. |
| MVVM | CommunityToolkit.Mvvm | Small, source-generated, no framework lock-in. |

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) for building.
- Windows 10/11, macOS 13+, or a modern Linux desktop (X11 or Wayland).
- On Linux, the usual Avalonia desktop dependencies (X11/Wayland, fontconfig,
  and OpenGL or a software renderer) must be installed.

The published builds are self-contained; end users do not need .NET installed.

## Run from source

```bash
git clone https://github.com/RyzenWang/AirPDF.git
cd AirPDF
dotnet run --project src/AirPdf.App
```

Open a PDF with the **Open** button, drag one onto the window, or pass a path:

```bash
dotnet run --project src/AirPdf.App -- /path/to/document.pdf
```

## Build and test

```bash
dotnet restore AirPdf.sln
dotnet build AirPdf.sln -c Release
dotnet test AirPdf.sln -c Release
```

The tests use `tests/Assets/sample.pdf`, an eight-page document that exercises
Latin and Chinese text, mixed typography, and search geometry. `AirPdf.App.Tests`
adds headless Avalonia coverage for opening, search, selection, zoom, language
switching, and reading-position restore.

## Publish

Framework-dependent build:

```bash
dotnet publish src/AirPdf.App -c Release -r linux-x64 --self-contained false
```

Self-contained single file:

```bash
dotnet publish src/AirPdf.App -c Release -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
```

Supported runtime identifiers include `win-x64`, `win-arm64`, `linux-x64`,
`linux-arm64`, `osx-x64`, and `osx-arm64`.

### Installers and automatic updates

Tagged releases are packaged with [Velopack](https://velopack.io/):

- Windows x64: `AirPDF-win-Setup.exe` (installer) and a portable zip
- macOS arm64 / x64: `AirPDF-osx-*.pkg` (installer) and a portable package
- Linux x64: portable `.tar.gz`

Installed Windows and macOS builds check GitHub for a newer stable release,
download it in the background, and show a **Restart to update** action. Portable
and source builds do not self-update.

The workflow also contains optional Windows code-signing and macOS
codesign/notarization steps. See [docs/DISTRIBUTION.md](docs/DISTRIBUTION.md)
for the required repository secrets and the exact release process.

> Release binaries are currently unsigned. Windows SmartScreen and macOS
> Gatekeeper may ask for confirmation until signing is added.

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+O` / `Cmd+O` | Open a PDF |
| `Ctrl+W` / `Cmd+W` | Close the document |
| `Ctrl+F` / `Cmd+F` | Find in document |
| `F3` / `Shift+F3` | Next / previous search match |
| `Alt+Left` | Return to the previous position |
| Drag on text | Select words |
| Double-click | Select a word |
| `Ctrl+C` / `Cmd+C` | Copy the selected text |
| `Ctrl+A` / `Cmd+A` | Select all document text |
| `Enter` | Jump to the typed page, or run a search |
| `Esc` | Close search, dismiss an error, or leave full screen |
| `Ctrl++` / `Ctrl+-` | Zoom in / out |
| `Ctrl+0` | Fit page |
| `Ctrl+1` | Actual size |
| `Ctrl+2` | Fit width |
| `Ctrl` + wheel | Zoom |
| `Ctrl+Shift+L` | Toggle English / 简体中文 |
| `F11` | Toggle full screen |
| `Page Up` / `Page Down` | Scroll the document |

Right-clicking the page canvas opens a menu for copying the selection, selecting
the whole document, and showing the file in its folder.

## Architecture

```
┌─────────────────────────────┐
│ AirPdf.App (Avalonia)       │
│  views · view models        │
│  bitmap conversion · input  │
└──────────────┬──────────────┘
               │
┌──────────────▼──────────────┐
│ AirPdf.Core                 │
│  PdfDocumentService         │  PDFium
│  PdfSearchService           │  PdfPig
│  SettingsService            │  JSON in the user profile
└─────────────────────────────┘
```

- `AirPdf.Core` has no Avalonia dependency and can be reused by a CLI, tests,
  or another front end.
- PDFium is single-threaded by design, so all render calls are serialized
  through one process-wide gate.
- Pages are rendered to `WriteableBitmap` objects and evicted when they move
  outside a small window around the current page.
- The page list is a virtualizing `ListBox` with a custom template; the scroll
  position drives current-page detection and thumbnail selection.
- Search reads the PDF text layer once, groups matches by page, and maps word
  bounding boxes back to normalized overlay rectangles.

More detail: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Project layout

```
src/
  AirPdf.Core/          PDF services, models, settings
  AirPdf.App/           Avalonia desktop application
tests/
  AirPdf.Core.Tests/    Open, render, search, and failure tests
  AirPdf.App.Tests/     Headless view-model and page-layout tests
  Assets/               Reproducible sample PDF and its HTML source
docs/                   Screenshots and architecture notes
```

## Limitations

- Text selection is word-granular rather than character-granular.
- Text selection is unavailable on pages with a non-zero `/Rotate` value.
- No annotations, form filling, printing, or PDF editing.
- Search highlights are hidden on pages with a non-zero `/Rotate` value; the
  results themselves still work.
- Links are not clickable on pages with a non-zero `/Rotate` value, and only
  `http`, `https`, and `mailto` targets are followed.
- One document per window; no tabs.
- Release binaries are signed only when the maintainer signing secrets are
  configured. Unsigned Windows/macOS builds may show platform warnings.
- Mobile and browser targets are out of scope for the desktop reader.

## Roadmap

- [x] PDF hyperlinks and internal link navigation
- [ ] Native printing and "Save a copy"
- [ ] Per-document bookmarks
- [ ] Rotated-page search highlights
- [ ] Annotations (highlight, ink, notes)
- [ ] Tabs and multi-window support
- [ ] Additional UI languages (English and Simplified Chinese are available)
- [ ] Configure production Windows signing and Apple notarization credentials
- [ ] UI/end-to-end coverage for zoom, search, language switching, and updates

Contributions and issue reports are welcome. If you want to pick up a roadmap
item, open an issue first so we can keep the design coherent.

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. The
project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). Security issues
should follow [SECURITY.md](SECURITY.md), not the public issue tracker.

## License

AirPDF is released under the [MIT License](LICENSE). Third-party components and
their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

<sub>Also available in [简体中文](README.zh-CN.md).</sub>
