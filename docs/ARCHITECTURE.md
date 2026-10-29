# Architecture

AirPDF is split into a small, UI-free core and an Avalonia front end. The split
keeps PDF decoding testable and leaves room for a future CLI or alternative
front end.

## Layers

### AirPdf.Core

`PdfDocumentService`

- Validates and opens a file.
- Reads page count and page sizes from PDFium through PDFtoImage.
- Reads metadata and the outline through PdfPig.
- Does **not** read page rotation. Doing so requires parsing every page dictionary, which
  dominated open time on long documents (measured at 45.1 s of a 54.7 s open on a
  9,277-page file, and 18.5 s of 24.6 s on a 4,215-page one) while nothing consumed the
  value. The services that need it read it per page, for the pages they actually touch.
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
- `TextLayerLoader` extracts PdfPig word geometry lazily for the pages that are
  visible or needed by a selection. Requests are batched and cancelled when the
  document closes.
- `PdfPageList` is a `ListBox` with a custom template that exposes the internal
  `ScrollViewer`. The list virtualizes page containers; scrolling drives the
  current-page calculation.
- `PageLayout` precomputes page tops for the current zoom. Scroll and
  navigation use binary search instead of walking every page.
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
- Text-layer extraction runs on the thread pool; page callbacks are marshaled to
  the UI thread before touching view models.
- Cancellation is cooperative: queued work is cancelled, and an in-flight native
  render is discarded when it returns.

## Navigation

Vertical page positions are deterministic: a 24 px top padding, a 28 px gap, and
known display heights. This lets the window calculate the current page from the
scroll offset without walking realized visuals, and lets it jump to a page
before that page has been virtualized into existence.

Search result navigation adds the active match's normalized `Y` coordinate to
the page offset, placing the highlight near the upper third of the viewport.

## Reading position

`ReadingPositionController` records `(page, within-page offset)` in the settings
file for each opened document, keyed by its absolute path. The entry also
stores the file size and last-write ticks; if either changes, the old position
is ignored. Scroll updates are debounced for 1.2 seconds, and closing a document
or the window flushes the latest position immediately.

On open, the saved position is held until the window has applied its initial zoom
and laid out the page heights, then the scroll viewer is placed at
`pageTop + offset × pageHeight`. The fraction is used rather than a raw pixel
offset so the position survives a different window size or zoom level.

The same entry also stores the zoom: `ZoomMode` is `fit-width`, `fit-page`, or
`manual`, and `Zoom` is only applied for `manual`. Storing the mode rather than a
frozen number matters because fit-width depends on the window width — reopening
the document maximized would otherwise keep a zoom that no longer fits.
`ApplyInitialZoom` resolves the mode once the viewport is known; older settings
files have no mode and fall back to fit-width.

## Navigation history

`MainWindowViewModel` keeps a bounded stack of `(page, offset)` points. Every
deliberate jump — outline, thumbnail, search result, page-number input, previous
and next page — pushes the position it is leaving. Plain scrolling never pushes,
so `Alt+Left` only walks back through jumps. The view model raises
`ReturnToPositionRequested`; the window reuses the same scroll path as reading
position restore, so the jump is clamped to the document and suppresses the
scroll-to-page feedback loop.

## Text selection

`PdfTextService` extracts ordered word geometry from PdfPig on demand. Each
`PdfTextPage` carries normalized word rectangles. Selection state is stored as
`(page, word index)` pairs in `TextSelectionController`; dragging maps
the pointer to a page through the realized `PdfPageView` controls and expands
the range across pages. `TextSelectionOverlay` paints the selected word boxes,
and the status bar exposes copy/select-all actions.

The selection is word-granular by design: it keeps hit-testing deterministic
and avoids a second character-level layout system. Rotated pages are skipped
for now, matching the search-highlight limitation.

## Theming and typography

- `Colors.axaml` defines light and dark theme dictionaries.
- Avalonia 11 has no high-contrast theme variant, so `HighContrast.axaml` is a
  flat dictionary merged on top of `Colors.axaml` while that theme is selected.
  Merged dictionaries are resolved in reverse order, so it must define every
  brush key; a missing one falls through to the light/dark value and produces
  unreadable combinations.
- The theme button cycles System, Light, Dark, and High contrast. System sets
  `RequestedThemeVariant` to `Default` and listens to
  `PlatformSettings.ColorValuesChanged`, so the app follows the OS live instead
  of reading the theme once at startup.
- `Controls.axaml` contains the button, field, list, and page-list templates.
- Inter is embedded, with system fallbacks for CJK text.
- The document page always renders on white; only the application chrome changes
  with the theme.

## Accessibility

Icon-only buttons carry `AutomationProperties.Name` fed from the same localization
catalog as their tooltips, and the page, thumbnail, outline, search-result, and
recent-file lists expose names. The document text itself is a bitmap, so UI
Automation cannot read it; the PdfPig text layer is the only source of readable
text today.

## Command line

`CommandLineOptions` parses the arguments before the window is created. A desktop
build is a `WinExe` with no console, so `--help`, `--version`, unknown options,
and ignored extra files are reported through the app's message dialog instead of
stdout. `--` terminates option parsing so paths that begin with `-` remain
usable. One document per window means only the first existing path is opened and
the rest are reported.

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
