# Changelog

All notable changes to AirPDF are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
semantic versioning once it reaches 1.0.

## [Unreleased]

### Added

- Simplified Chinese interface with live switching from the toolbar or
  `Ctrl+Shift+L`. English remains the default on a fresh install, and the
  choice is persisted.
- Word-level text selection across pages, with `Ctrl+C` copy, `Ctrl+A` select
  all, double-click word selection, and a status-bar copy action.
- Per-document reading position memory, storing the page, within-page offset,
  and a file fingerprint so changed files do not restore a stale position.

### Fixed

- Zooming in and out could terminate the process. Cancellation tokens are now
  retired safely, and replaced page bitmaps are released after the compositor
  has had a chance to finish the frame.

### Planned

- Native printing.
- Per-document bookmarks and remembered reading position.
- Rotated-page search highlights.

## [0.1.0] - 2026-10-27

### Added

- Cross-platform Avalonia desktop shell with a custom light and dark palette.
- PDFium page rendering with high-DPI sizing and a virtualized continuous page
  list.
- Thumbnail rail, document outline, page navigation, and zoom controls.
- Full-text search with page-grouped results and in-page match highlights.
- Password-protected PDF support.
- Drag and drop, command-line opening, recent files, and persistent settings.
- Atomic JSON settings storage in the platform application-data folder.
- MIT license, third-party notices, architecture notes, and CI workflows.

### Known limitations

- Pages are rasterized, so text is not selectable.
- No annotations, form filling, printing, or editing.
- Search highlights are omitted on rotated pages.
- Release binaries are unsigned.
