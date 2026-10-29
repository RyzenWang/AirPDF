# Changelog

All notable changes to AirPDF are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
semantic versioning once it reaches 1.0.

## [Unreleased]

### Added

- Windows installer and portable package built with Velopack.
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

### Changed

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

- Cross-platform Avalonia desktop shell with a custom light and dark palette.
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
