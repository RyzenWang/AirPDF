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

### Changed

- Release workflow now publishes Velopack installers and update manifests
  instead of plain archives for Windows and macOS.

### Notes

- Windows Authenticode and Apple Developer ID credentials are supplied as
  repository secrets. Without them the workflow produces unsigned packages and
  skips notarization.

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
