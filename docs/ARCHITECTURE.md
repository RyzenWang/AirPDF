# Architecture

AirPDF is split into a small, UI-free core and an Avalonia front end. The split
keeps PDF decoding testable and leaves room for a future CLI or alternative
front end.

## Layers

### AirPdf.Core

`PdfDocumentService`

- Validates and opens a file.
- Keeps one PDFium document handle open while the document is displayed, so rendering a
  page no longer reparses the whole file. Measured on a 9,277-page / 35 MB document, a page
  render went from 0.72 s to 0.023 s.
- Reads page count and page sizes from PDFium directly (see `AirPdf.Core/Pdfium`). The
  bindings are plain C ABI; the native library is the one PDFtoImage already ships for every
  target, so the platform matrix is unchanged.
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

- Opens the text layer with PDFium (`FPDFText_*`), the same handle pages render from.
- Builds an ordered token stream per page and searches it case-insensitively.
- Returns snippets and word rectangles normalized to the page (`0..1`, origin
  top-left).
- Caps results at 250 to keep the UI responsive on pathological documents.
- Leaves highlight rectangles empty for rotated pages, where the coordinate
  transform is not yet implemented.

`SettingsService`

- Stores a small JSON document under the platform's per-user application data
  folder (`%APPDATA%`, `~/Library/Application Support`, or `~/.config`).
- Overwrites an existing file in place and only falls back to writing a temporary
  file and renaming it when there is nothing to overwrite. This is not a
  performance choice: a packaged (MSIX) build runs with AppData redirection, where
  *creating* a file sends it to the package's private cache, but *opening* a file
  that already exists at its real location does not. Writing through a temporary
  file always created one, so a Store build silently forked the user's settings.
- Keeps the previous copy as `settings.json.bak` and loads it when the live file is
  missing, empty, or truncated, because an interrupted in-place write is now
  possible.
- Never lets a settings failure block reading.
- Serializes through a source-generated `JsonSerializerContext`. The reflection-based
  overloads are marked as requiring unreferenced and dynamic code, and a trimmed build
  silently stopped writing the file, so this is not a style choice.

### AirPdf.App

- `MainWindowViewModel` owns document state, zoom, navigation, search, and the
  recent-file shelf. It has no direct dependency on Avalonia windows, only on
  `IUserDialogService`.
- `PageViewModel` owns the rendered bitmap for one page and a cancellable
  render generation. A page is rendered only while its view is attached to the
  visual tree; stale renders are discarded.
- `ThumbnailViewModel` uses the same renderer at a fixed preview width.
- `TextLayerLoader` extracts PDFium word geometry lazily for the pages that are
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
  ├─ PDFium (one document handle per open document) ──► SKBitmap ──► WriteableBitmap ──► Image
  │        (serialized)                                  (copy pixels, no PNG round-trip)
  │
  └─ PDFium ──► characters + boxes ──► words ──► normalized search rectangles
```

### Why a resident document handle?

PDFtoImage's public API takes a path, byte array or stream per call, and its document type
is `internal`, so every render used to reload and reparse the file. On a 35 MB document that
cost 0.72 s per page, which caps scrolling at about 1.4 pages/second. `AirPdf.Core/Pdfium`
keeps one `FPDF_DOCUMENT` for the lifetime of the display, reads the file through a
`FPDF_FILEACCESS` callback over a `FileShare.ReadWrite` stream (so the file is never locked
against other programs), and renders pages straight into the page bitmap.

`PdfDocumentService` still references the PDFtoImage package, but only for its native
payload — `bblanchon.PDFium.*` and the SkiaSharp native assets for every published RID.

### Why render to a bitmap?

PDFium has no managed, cross-platform Avalonia integration that is both free
and maintained for this use case. Rasterizing pages into a virtualized list is
simple, predictable, and fast enough for reading. The trade-off is that text
selection and accessible text extraction from the visual layer are not
available yet; search and selection use the separate PDFium text layer.

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

### Idle cost, and how it was measured

The window is idle when nothing is being read: no layout passes, no scroll events, no page
renders. That was checked rather than assumed, with a counter per second and a CPU sample:

```powershell
$p = Start-Process .\AirPdf.exe -ArgumentList 'sample.pdf' -PassThru
Start-Sleep -Seconds 12
$t = $p.TotalProcessorTime
Start-Sleep -Seconds 8
$p.Refresh()
100 * ($p.TotalProcessorTime - $t).TotalSeconds / 8
```

**Measure this on hardware with a GPU.** The first run of this exercise reported 78-96% of one
core with a document open and concluded there was a rendering defect. It was measured on a
VMware VM with "Microsoft Basic Display Adapter", where every frame is composited in software;
minimising the window dropped it to 0.3%, which located the cost in presentation rather than in
application code. On real hardware the same build idles in the low single digits, and the
earlier reading was an artefact of the environment. Forcing a different composition mode in
`Win32PlatformOptions` did not help there either (DirectComposition 67%, RedirectionSurface
115%, LowLatencyDxgiSwapChain 121%), which is another sign the cost was the software adapter.

Setting `AIRPDF_PROBE=1` turns on the render diagnostic: every document open appends a block to
`%TEMP%\airpdf-render-probe.txt` with the page size, whether each page is active, whether it has a
bitmap, and the viewer extent; `Ctrl+Shift+D` appends one on demand. It is the fastest way to tell
"never activated" from "rendered blank" in a report from a machine you cannot look at.
One genuine inefficiency survived the investigation even though it is not worth a change on
its own: every visible thumbnail and page placeholder carries an `IsIndeterminate="True"`
`ProgressBar`, so each visible item runs its own animation clock. It is a candidate to replace
with a static indicator if a low-end machine ever shows idle cost.
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

### Idle cost, and how it was measured

The window is idle when nothing is being read: no layout passes, no scroll events, no page
renders. That was checked rather than assumed, with a counter per second and a CPU sample:

```powershell
$p = Start-Process .\AirPdf.exe -ArgumentList 'sample.pdf' -PassThru
Start-Sleep -Seconds 12
$t = $p.TotalProcessorTime
Start-Sleep -Seconds 8
$p.Refresh()
100 * ($p.TotalProcessorTime - $t).TotalSeconds / 8
```

**Measure this on hardware with a GPU.** The first run of this exercise reported 78-96% of one
core with a document open and concluded there was a rendering defect. It was measured on a
VMware VM with "Microsoft Basic Display Adapter", where every frame is composited in software;
minimising the window dropped it to 0.3%, which located the cost in presentation rather than in
application code. On real hardware the same build idles in the low single digits, and the
earlier reading was an artefact of the environment. Forcing a different composition mode in
`Win32PlatformOptions` did not help there either (DirectComposition 67%, RedirectionSurface
115%, LowLatencyDxgiSwapChain 121%), which is another sign the cost was the software adapter.

Setting `AIRPDF_PROBE=1` turns on the render diagnostic: every document open appends a block to
`%TEMP%\airpdf-render-probe.txt` with the page size, whether each page is active, whether it has a
bitmap, and the viewer extent; `Ctrl+Shift+D` appends one on demand. It is the fastest way to tell
"never activated" from "rendered blank" in a report from a machine you cannot look at.
One genuine inefficiency survived the investigation even though it is not worth a change on
its own: every visible thumbnail and page placeholder carries an `IsIndeterminate="True"`
`ProgressBar`, so each visible item runs its own animation clock. It is a candidate to replace
with a static indicator if a low-end machine ever shows idle cost.
## Navigation history

`MainWindowViewModel` keeps a bounded stack of `(page, offset)` points. Every
deliberate jump — outline, thumbnail, search result, page-number input, previous
and next page — pushes the position it is leaving. Plain scrolling never pushes,
so `Alt+Left` only walks back through jumps. The view model raises
`ReturnToPositionRequested`; the window reuses the same scroll path as reading
position restore, so the jump is clamped to the document and suppresses the
scroll-to-page feedback loop.

## Links

Link annotations are read per page with `FPDFLink_Enumerate` when the page view
attaches, and cached on the page view model. Bounds are normalized exactly like
search rectangles, so hit-testing is the same math at any zoom. Internal
destinations resolve through `FPDFDest_GetDestPageIndex` and are followed with the
normal page navigation, which means they join the back history.

Clicking is handled before text selection starts, so a click on a link follows it
instead of beginning a drag. Only `http`, `https` and `mailto` targets are handed to
the shell: `Launch` and remote `GoTo` actions are ignored on purpose, so a document
cannot start a program.

Rotated pages report no links, for the same reason search highlights are skipped
there — PDFium returns annotation bounds in unrotated page space.

## Text selection

`PdfTextService` extracts ordered word geometry from PDFium on demand. Each
`PdfTextPage` carries normalized word rectangles. Selection state is stored as
`(page, word index)` pairs in `TextSelectionController`; dragging maps
the pointer to a page through the realized `PdfPageView` controls and expands
the range across pages. `TextSelectionOverlay` paints the selected word boxes,
and the status bar exposes copy/select-all actions.

The selection is word-granular by design: it keeps hit-testing deterministic
and avoids a second character-level layout system. Rotated pages are skipped
for now, matching the search-highlight limitation.

## Annotations and saving

The reader is a reader: the file the user opened is never written to. Annotations are added to
the resident PDFium document in memory, and the only way they leave the process is
**Save a copy**, which writes a new file through `FPDF_SaveAsCopy`. The test for this hashes the
original before and after.

Two things make this less trivial than it sounds:

- **Page space versus display space.** Selections are stored normalized, with the origin at the
  top-left, because that is what the overlays draw. Annotations are written in page space: points,
  origin bottom-left, Y up. `PageGeometry.ToPageQuad` is the exact inverse of the mapping that
  produced the selection, including `/Rotate`, and its test round-trips through the forward
  function for every quarter turn. Getting the direction wrong flips every highlight vertically
  and looks like a rendering bug.
- **Rotation is read per page, on demand.** Opening a document deliberately does not read
  `/Rotate` for every page: doing so cost 45 s of a 55 s open on a 9,277-page file. The highlight
  path asks only for the pages the selection touches.

`PdfDocumentService.AddHighlightAsync` and `SaveCopyAsync` take the render gate like every other
PDFium call, but unlike rendering they refuse to fall back to a throwaway handle. A render can
reopen the file for one page; an edit cannot, because a change written to a copy that is then
discarded would look like it succeeded and silently do nothing.

Saving in place cannot simply overwrite the file: PDFium reads it through an open stream, and a
replacement needs that handle closed first. The order is write a temporary file beside the
original, close the session, move the temporary over the original, reopen it. Reopening also
means the annotation geometry stays live for undo, which would otherwise point at a document that
no longer exists.
Undo works on snapshots rather than on indices. Removing a highlight captures its quads and
colour first, so the undo entry is a complete description of what to put back; adding pushes the
same shape, and undoing it removes the annotation whose geometry matches. An annotation index
would be cheaper and wrong: any later edit shifts it.

A step is a reader action, not an annotation. Highlighting a selection that covers eight pages
produces eight annotations and **one** undo step, because that is what the reader did and what
they expect Ctrl+Z to take back.
Until a copy is written, `HasUnsavedAnnotations` is set and the window title carries a marker.
Annotations that vanish on close would be the kind of bug a reader never forgives.
## Windows and shared state

Each window owns its document, page view models, renderer, and dialog service, so closing
one window cannot disturb another. Everything that is process-wide lives in a single
`AppServices` instance created by `App` and handed to every window:

- one `SettingsService` and one `AppSettings`, so two windows cannot overwrite each
  other's changes when both save;
- one `LocalizationService`, because applying a language swaps an application-level
  resource dictionary;
- one `ThemeService`, because `RequestedThemeVariant` and the merged high-contrast
  dictionary are application-level; it raises `Changed` so each window refreshes its
  own icon and tooltip;
- one `UpdateService`, with a process-wide guard so only the first window checks the
  update feed and downloads.

`WindowService` owns the open windows. Only the first window restores the remembered
position and size; later windows cascade from the window they were opened from, and a
position that would land off every screen is dropped rather than applied.

## Tabs

A window hosts a `WindowViewModel` that owns an `ObservableCollection` of tabs, and each
tab is a complete `MainWindowViewModel` — its own document, page view models, renderer,
search state, and reading position. The window's `DataContext` is the *active tab*, so the
existing markup and input handling keep binding to a document view model. The tab list
gets its own `DataContext` and compiled data type from the host; the workspace bar around
it stays on the document so its brand and Open action use the active tab's commands.

Switching tabs swaps the `DataContext`, moves the event subscriptions in
`HookViewModel`/`UnhookViewModel`, and restores that tab's scroll position from
`CurrentPosition`. Nothing is reloaded. Work that a tab posts to the dispatcher (scroll to
a search result, apply the initial zoom) re-checks that the tab is still active, so a
switch in between cannot move another tab's document.

The last tab is never removed: closing it closes its document, which is exactly what
`Ctrl+W` did before tabs existed.

## Theming and typography

- `Colors.axaml` defines light and dark theme dictionaries.
- Avalonia 11 has no high-contrast theme variant, so `HighContrast.axaml` is a
  flat dictionary merged on top of `Colors.axaml` while that theme is selected.
  Merged dictionaries are resolved in reverse order, so it must define every
  brush key; a missing one falls through to the light/dark value and produces
  unreadable combinations.
- The theme button cycles System, Light, Dark, and High contrast. The state lives in
  `ThemeService` rather than in a view model, because it is process-wide: System sets
  `RequestedThemeVariant` to `Default` and listens to
  `PlatformSettings.ColorValuesChanged`, so the app follows the OS live instead of
  reading the theme once at startup.
- `Controls.axaml` contains the button, field, list, and page-list templates.
- Inter is embedded, with system fallbacks for CJK text.
- The document page always renders on white; only the application chrome changes
  with the theme.

## Accessibility

Icon-only buttons carry `AutomationProperties.Name` fed from the same localization
catalog as their tooltips, and the page, thumbnail, outline, search-result, and
recent-file lists expose names. The document text itself is a bitmap, so UI
Automation cannot read it; the PDFium text layer is the only source of readable
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

## Tests

Four layers, and a defect is usually caught by the lowest one that can see it:

| Layer | Project | Drives | Catches |
| --- | --- | --- | --- |
| Storage and PDF | `AirPdf.Core.Tests` | services directly | parsing, geometry, persistence |
| View models | `AirPdf.App.Tests` (`MainWindowViewModelTests`) | view models with a window | command wiring, state machines |
| XAML | `AirPdf.App.Tests` (`MainWindowXamlTests`) | a real `MainWindow` | bindings, templates, compiled-binding data types |
| End to end | `AirPdf.App.Tests` (`EndToEndTests`) | a real window plus real key events | anything that depends on routing, focus, or the visual tree |

The end-to-end tests are the newest layer precisely because the other three all missed the
defects they were supposed to catch: a bookmark jump that left the page blank, a tab switch that
crashed, and a scroll-triggered stack overflow were each invisible to 131 unit tests.

Two rules make them reliable in a headless process:

- **Send input, do not call commands.** `window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control)`
  goes through Avalonia's input pipeline, so a missing handler, a wrong gesture, or an
  unfocused text box fails the test. Calling `SearchCommand.Execute` directly would not.
- **Assert something the reader can observe.** The bitmap width after a zoom, the highlight
  rectangles after a search, a `TextBlock` showing `书签` after the language switch. A bare
  property change proves the view model works, not that the window does.

Headless has no real layout: the page list reports a zero-extent scroll viewer, so anything
derived from a scroll offset (the current page number, the reading position) is not reliable
there. Assertions should use state that does not depend on layout.

Pixel assertions are not used. The test app runs with `UseHeadlessDrawing`, which installs a stub
drawing backend, so a captured frame proves nothing; page bitmaps are asserted instead, which are
produced by our own SkiaSharp renderer and are therefore real.
## Adding a feature

1. Put PDF parsing and file work in `AirPdf.Core`, with a unit test against the
   sample PDF or a focused fixture.
2. Add view state to a view model, not to a control.
3. Keep UI-thread work in the view layer and long-running work cancellable.
4. Update `README.md`, `CHANGELOG.md`, and `THIRD-PARTY-NOTICES.md` when a
   dependency or user-facing behavior changes.
