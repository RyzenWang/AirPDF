# Architecture

AirPDF is split into a small, UI-free core and an Avalonia front end. The split
keeps PDF decoding testable and leaves room for a future CLI or alternative
front end.

## Layers

### AirPdf.Core

`PdfDocumentService`

- Validates and opens a file.
- Reads page count and page sizes from PDFium through PDFtoImage.
- Reads metadata, page rotation, and the outline through PdfPig.
- Serializes all PDFium rendering behind a static `SemaphoreSlim`, because
  PDFium is not thread-safe.
- Maps native and parsing failures to `PdfDocumentException` with a stable
  `PdfFailure` value.

`PdfSearchService`

- Opens the text layer with PdfPig.
- Builds an ordered token stream per page and searches it case-insensitively.
- Returns snippets and word rectangles normalized to the page (`0..1`, origin
  top-left).
- Caps results at 250 to keep the UI responsive on pathological documents.
- Leaves highlight rectangles empty for rotated pages, where the coordinate
  transform is not yet implemented.

`SettingsService`

- Stores a small JSON document under the platform's per-user application data
  folder (`%APPDATA%`, `~/Library/Application Support`, or `~/.config`).
- Saves atomically through a temporary file and never lets a settings failure
  block reading.

### AirPdf.App

- `MainWindowViewModel` owns document state, zoom, navigation, search, and the
  recent-file shelf. It has no direct dependency on Avalonia windows, only on
  `IUserDialogService`.
- `PageViewModel` owns the rendered bitmap for one page and a cancellable
  render generation. A page is rendered only while its view is attached to the
  visual tree; stale renders are discarded.
- `ThumbnailViewModel` uses the same renderer at a fixed preview width.
- `PdfPageList` is a `ListBox` with a custom template that exposes the internal
  `ScrollViewer`. The list virtualizes page containers; scrolling drives the
  current-page calculation.
- `SearchHighlightOverlay` paints normalized rectangles over the bitmap. The
  active match gets a border and a lighter fill.

## Rendering pipeline

```
file
  │
  ├─ PDFtoImage / PDFium ──► SKBitmap ──► WriteableBitmap ──► Image
  │        (serialized)          (copy pixels, no PNG round-trip)
  │
  └─ PdfPig ──► words + bounding boxes ──► normalized search rectangles
```

### Why render to a bitmap?

PDFium has no managed, cross-platform Avalonia integration that is both free
and maintained for this use case. Rasterizing pages into a virtualized list is
simple, predictable, and fast enough for reading. The trade-off is that text
selection and accessible text extraction from the visual layer are not
available yet; search uses the separate PdfPig text layer.

### Render sizing

- A page is displayed at `points × (96 / 72) × zoom`.
- The bitmap is rendered at display size multiplied by the monitor scale, capped
  by width (2400 px) and area (7 MP).
- This keeps text crisp at 100–200% and prevents a single zoomed page from
  exhausting memory.
- The page bitmap cache is trimmed to ±4 pages around the current page.

### Threading

- PDF calls run on the thread pool through `Task.Run`.
- One process-wide semaphore serializes PDFium calls.
- Bitmap conversion happens on the UI thread after the native render completes.
- Cancellation is cooperative: queued work is cancelled, and an in-flight native
  render is discarded when it returns.

## Navigation

Vertical page positions are deterministic: a 24 px top padding, a 28 px gap, and
known display heights. This lets the window calculate the current page from the
scroll offset without walking realized visuals, and lets it jump to a page
before that page has been virtualized into existence.

Search result navigation adds the active match's normalized `Y` coordinate to
the page offset, placing the highlight near the upper third of the viewport.

## Text selection

`PdfTextService` extracts ordered word geometry from PdfPig after a document is
opened. Each `PdfTextPage` carries normalized word rectangles. Selection state
is stored as `(page, word index)` pairs in `MainWindowViewModel`; dragging maps
the pointer to a page through the realized `PdfPageView` controls and expands
the range across pages. `TextSelectionOverlay` paints the selected word boxes,
and the status bar exposes copy/select-all actions.

The selection is word-granular by design: it keeps hit-testing deterministic
and avoids a second character-level layout system. Rotated pages are skipped
for now, matching the search-highlight limitation.

## Theming and typography

- `Colors.axaml` defines light and dark theme dictionaries.
- `Controls.axaml` contains the button, field, list, and page-list templates.
- Inter is embedded, with system fallbacks for CJK text.
- The document page always renders on white; only the application chrome changes
  with the theme.

## Localization

`LocalizationService` holds the English and Simplified Chinese catalogs. Applying
a language swaps a runtime `ResourceDictionary` in the application resources, so
XAML text bound with `DynamicResource` keys (`L.Open`, `L.SearchTitle`, …)
updates without recreating the window. Strings that include counts, dates, or
failure details go through `T` / `Format` in the view model. The selection is
stored as `language` in the settings file; English is the default when the key
is missing.

## Deliberate limits

- No PDF editing. AirPDF is a reader.
- No cloud sync or telemetry.
- No web view. The UI and renderer are native to the process.
- No plugin system yet. Keep changes small and composable.

## Adding a feature

1. Put PDF parsing and file work in `AirPdf.Core`, with a unit test against the
   sample PDF or a focused fixture.
2. Add view state to a view model, not to a control.
3. Keep UI-thread work in the view layer and long-running work cancellable.
4. Update `README.md`, `CHANGELOG.md`, and `THIRD-PARTY-NOTICES.md` when a
   dependency or user-facing behavior changes.
