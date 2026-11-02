# Changelog

All notable changes to AirPDF are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
semantic versioning once it reaches 1.0.

## [Unreleased]

### Added

- The welcome screen is now the first tab, named Home, so the tab strip exists from the
  first document and returning home no longer closes what you are reading. The logo and
  wordmark in the toolbar form one button back to it, and a document already open in another
  tab is focused rather than opened twice when you click one of its bookmarks from Home.
- Per-document bookmarks. `Ctrl+D` marks the current page and scroll offset, the sidebar
  gains a Bookmarks tab listing them with page numbers, clicking one jumps there and records
  a back step, and they are stored per file in the settings file next to the reading
  position, so they survive restarts. Pressing the shortcut twice on the same spot does not
  duplicate the entry, and a document is capped at 100 bookmarks. This is separate from a
  PDF's own outline, which still has its own tab.
- Bookmarks can be renamed (double-click, or the right-click menu) and reordered by dragging
  them, with the order stored so it survives a restart. The sidebar menu also offers move up
  and move down for keyboard and screen-reader use.
- The welcome screen lists bookmarks from every document, showing the file name and page, and
  clicking one opens that file at the bookmark.

## [0.3.2] - 2026-11-02

Every platform is now compiled ahead of time.

### Changed

- The Linux and macOS builds are published with NativeAOT too, so all three platforms start
  in well under a second instead of several. The Linux runner installs `clang` and
  `zlib1g-dev` for the native link; macOS uses the Xcode toolchain already on the runner.
- The release jobs and every pull request now fail on any AOT warning for each platform
  separately, because Windows says nothing about Linux or macOS: they have different
  toolchains and different native assets.

### Fixed

- The Linux build no longer demands `GLIBC_2.38`. NativeAOT links against the build machine's
  glibc, and building on `ubuntu-latest` (24.04) produced a binary that died on 22.04 with
  "version `GLIBC_2.38' not found". The Linux jobs are pinned to `ubuntu-22.04` (glibc 2.35),
  and both workflows read the required symbol version off the produced binary and fail if it
  creeps above 2.35, since the failure is invisible until somebody runs it on an older
  distribution. Measured on the shipped binary: it needs at most `GLIBC_2.34`, which covers
  Ubuntu 22.04+, Debian 12+, and RHEL 9+.
## [0.3.1] - 2026-11-02

The Windows build is compiled ahead of time, and starts in well under a second.

### Changed

- The Windows build is published with NativeAOT. Milestone tracing put the previous 5.0 s
  to first window almost entirely in JIT compilation, spread evenly across Avalonia
  initialisation, XAML loading and first layout rather than in one slow call, so no cheaper
  change recovers the bulk of it. Nothing is compiled at startup now: 0.7 s to first window.
  The download grows only from 33.4 MB to 35.0 MB because Velopack compresses the payload,
  even though the uncompressed output is 60 MB against 28 MB.

  The publish step and every pull request now fail on any AOT or trimming warning, since
  either can remove a reflection path that only breaks at runtime. Velopack is no longer
  rooted for trimming: rooting it made the AOT compiler compile a COM shortcut class whose
  IL cannot be compiled, which warned that it would always throw. The app never reaches that
  code, so leaving it trimmable removes the warning and 1.1 MB.

  The update path was verified on the AOT build rather than assumed: installed from the
  Setup package, it reports IsInstalled, reads the real release feed, and against a local
  feed advertising a newer package it downloads and unpacks it.
## [0.3.0] - 2026-11-02

Rotated pages work end to end, and the Windows download halves.

### Changed

- The Windows build is trimmed, taking the download from 53 MB to 28 MB. The release
  step fails on any IL warning rather than shipping a build whose reflection paths were
  removed; the same check runs on every pull request. macOS and Linux are unchanged for
  now.
- The text layer is read through PDFium's text API instead of PdfPig, using the document
  handle pages are already rendered from. PdfPig applies `/Rotate` to its letters before
  grouping them, so rotated pages came back as single letters or reversed text; PDFium
  reports characters in reading order with boxes in unrotated page space. PdfPig still
  supplies metadata and the outline, and CJK text keeps its own per-character selection
  units because the search text no longer invents spaces between them.

### Fixed

- Rotated pages are fully supported: links, search highlights, and text selection. Two
  halves were broken. Geometry: the document's page box already accounts for `/Rotate`
  while content rectangles do not, so they are mapped through a shared `PageGeometry`
  transform. Ordering: the text layer now supplies characters in reading order. Verified
  by rendering rotated fixtures and checking that every reported rectangle covers the ink
  that was drawn, rather than comparing a transform against itself.
## [0.2.1] - 2026-10-31

A packaging release. The application binaries are unchanged from 0.2.0; this
version publishes the signing tooling and makes the release pipeline state
explicit.

### Added

- The welcome screen is now the first tab, named Home, so the tab strip exists from the
  first document and returning home no longer closes what you are reading. The logo and
  wordmark in the toolbar form one button back to it, and a document already open in another
  tab is focused rather than opened twice when you click one of its bookmarks from Home.
- The AirPDF logo in the toolbar returns to the welcome screen. It closes the open document,- `packaging/signing/Set-SigningSecrets.ps1` validates a Windows or macOS
  code-signing certificate (private key present, Code Signing extended key usage,
  not expired) and uploads it as the repository secrets the release workflow
  reads.
- `packaging/signing/New-TestCertificate.ps1` mints a throwaway self-signed
  certificate so the signing pipeline can be exercised without a purchased
  certificate.
- A `Signing check` workflow that inspects the configured credentials without
  publishing anything, and otherwise proves the signing path end to end with a
  temporary certificate.
- `docs/SIGNING.md` covering certificate acquisition, secret names, verification,
  rotation, and cost.

### Changed

- The release workflow now fails when signing is half configured (a certificate
  without its password, or a macOS certificate without the Apple credentials)
  instead of silently publishing an unsigned build, and records the signing state
  in the run summary.
- Certificate and key files are ignored by git so private material cannot be
  committed by accident.

### Notes

- No code-signing credentials are configured in this repository yet, so the
  published installers are unsigned.

## [0.2.0] - 2026-10-29

### Added

- The welcome screen is now the first tab, named Home, so the tab strip exists from the
  first document and returning home no longer closes what you are reading. The logo and
  wordmark in the toolbar form one button back to it, and a document already open in another
  tab is focused rather than opened twice when you click one of its bookmarks from Home.
- The AirPDF logo in the toolbar returns to the welcome screen. It closes the open document,- Windows installer and portable package built with Velopack.
- macOS `.pkg` and portable package built with optional Developer ID signing
  and Apple notarization.
- In-app update checks, background downloads, and a restart-to-update action for
  installed Windows and macOS builds.
- Distribution guide covering release assets, required signing secrets, and
  end-to-end update testing.
- macOS `.icns` application icon.
- Back navigation: `Alt+Left` (and a toolbar button) returns to the position you
  were at before the last outline, thumbnail, search, or page-number jump.
- Clickable links: internal destinations jump to their page (and take part in back
  navigation), and `http`, `https`, and `mailto` links open in the system handler.
  A link shows a hand cursor and takes precedence over starting a text selection.
  `Launch` and remote-`GoTo` actions are deliberately ignored so a document cannot
  start programs.
- Per-document zoom memory: the zoom mode (fit width, fit page, or a manual
  factor) is restored the next time the document is opened.
- Reading progress percentage in the status bar.
- A high-contrast theme, selectable from the theme button.
- Right-click menu on the page canvas: copy selection, select all, and show the
  file in the system file manager.
- `F3` / `Shift+F3` for the next / previous search match.
- Accessible names for the toolbar buttons, inputs, and lists, so screen readers
  can announce them.
- Command line: `--help`, `--version`, a `--` path terminator, and a message
  when extra files were ignored.
- Multiple windows: `Ctrl+N` opens an empty one and the page menu can open the
  current document in a new window. New windows cascade instead of stacking on the
  first one, and only the first window restores the remembered position.
- Tabs: `Ctrl+T` adds a document to the current window, `Ctrl+Tab` cycles, and the
  page menu can open the current document in a new tab. Each tab keeps its own
  document handle, zoom, scroll position, search results, and reading position, so
  switching does not reload. The tab strip only appears once a second tab exists.

### Changed

- Windows now share one settings store, language, theme, and update channel
  instead of each loading and rewriting the settings file, where the last window
  to save discarded the others' changes. The automatic update check runs once per
  process rather than once per window.
- Release workflow now publishes Velopack installers and update manifests
  instead of plain archives for Windows and macOS.
- The theme button now cycles System → Light → Dark → High contrast instead of
  toggling two states, and "System" follows the OS while the app is running.
- Window position is remembered and validated against the current screens, so a
  window saved on a disconnected monitor no longer reopens off-screen.
- The recent-files shelf now shows all ten remembered documents; it used to show
  six while storing ten.
- Search-result navigation uses the result index instead of scanning the list.
- Text layers are loaded lazily as pages become visible instead of parsing the
  whole document when it opens.
- Page layout is precomputed so scrolling uses a binary search rather than
  walking every page.
- Selection and reading-position state moved into dedicated controllers.
- Release builds now run the full test suite before packaging, and CI runs the
  new headless Avalonia tests.

### Notes

- Windows Authenticode and Apple Developer ID credentials are supplied as
  repository secrets. Without them the workflow produces unsigned packages and
  skips notarization.

### Fixed

- Rendering keeps the PDFium document handle open while a document is displayed instead of
  reparsing the file for every page. Measured on a 9,277-page / 35 MB document: a page
  render dropped from 0.72 s to 0.023 s, and opening dropped from 7.1 s to 1.9 s. Page
  counts and rendering results are unchanged apart from a one-pixel rounding difference in
  the derived page height.
- The page list now actually virtualizes. Its control template was missing the
  `ItemsPanel` binding, so the `VirtualizingStackPanel` was never used and every page in
  the document was realized at once. A 4,215-page document allocated 3 GB within 30
  seconds and stopped responding; it now stays around 300 MB and remains interactive.
- Opening a long document no longer scans every page dictionary just to record its
  rotation, which nothing read. Opening a 9,277-page document dropped from 54.7 s
  to 7.1 s, and a 4,215-page one from 24.6 s to 3.6 s.
- Opening a document no longer queues a page render at 100% and then renders
  again for fit-width.
- Closing a maximized window no longer overwrites the remembered restore size.

## [0.1.0] - 2026-10-28

First public preview.

### Added

- The welcome screen is now the first tab, named Home, so the tab strip exists from the
  first document and returning home no longer closes what you are reading. The logo and
  wordmark in the toolbar form one button back to it, and a document already open in another
  tab is focused rather than opened twice when you click one of its bookmarks from Home.
- The AirPDF logo in the toolbar returns to the welcome screen. It closes the open document,- Cross-platform Avalonia desktop shell with a custom light and dark palette.
- PDFium page rendering with high-DPI sizing and a virtualized continuous page
  list.
- Thumbnail rail, document outline, page navigation, and zoom controls.
- Full-text search with page-grouped results and in-page match highlights.
- Word-level text selection across visible pages, with `Ctrl+C` copy, `Ctrl+A`
  select all, double-click word selection, and a status-bar copy action.
- Per-document reading position memory, storing the page, within-page offset,
  and a file fingerprint so changed files do not restore a stale position.
- English and Simplified Chinese interfaces with live switching from the toolbar
  or `Ctrl+Shift+L`; the choice is persisted.
- Password-protected PDF support.
- Drag and drop, command-line opening, recent files, and persistent settings.
- Atomic JSON settings storage in the platform application-data folder.
- MIT license, third-party notices, architecture notes, and CI workflows.

### Fixed

- Zooming in and out could terminate the process. Cancellation tokens are now
  retired safely, and replaced page bitmaps are released after the compositor
  has had a chance to finish the frame.

### Known limitations

- Text selection is word-granular rather than character-granular.
- Text selection and search highlights are unavailable on pages with a non-zero
  `/Rotate` value.
- No annotations, form filling, printing, or PDF editing.
- One document per window; no tabs.
- Release binaries are unsigned.
