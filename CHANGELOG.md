# Changelog

All notable changes to AirPDF are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
semantic versioning once it reaches 1.0.

## [Unreleased]

### Planned

- Text selection and copy.
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
