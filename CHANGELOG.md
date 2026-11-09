# Changelog

All notable changes to AirPDF are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
semantic versioning once it reaches 1.0.

## [0.8.0] - 2026-11-09

### Added

- White is now available in the shared markup palette, including the ink colour picker.

### Changed

- The selected ink colour in the palette now has a thick accent ring and a check mark, with
  a contrasting dark/light check so it stays visible on every swatch including white.
- Document metadata and the outline now load on demand from the resident PDFium handle,
  only when the reader first opens the contents or document-information panel. Opening a
  document no longer pays for either parse, and the separate PdfPig dependency is gone.
- PDFium's native gate is priority-aware: a visible page render is handed the gate before
  queued text extraction, link loading, or search work. A long search now yields between
  pages instead of making the visible page wait for the whole scan.
- Scrolling and zooming now render new pages at 65% width and use medium-quality bitmap
  interpolation; once the reader pauses for 220ms, affected pages refine to the full
  target size and high-quality interpolation.
- Retired page bitmaps go through a small generation-scoped pool instead of being
  reallocated from scratch on every render. When the source and target strides match, the
  pixel copy is one bulk block instead of one copy per row.
- Page trims now walk a set of pages that actually hold bitmaps, thumbnails track their own
  populated set, and text trimming walks the loader's loaded pages. Zoom updates only the
  realised page views; scroll layout is computed from page points and the zoom factor.
  Long documents no longer scan every page for each turn or zoom tick.
- Thumbnails reuse the page's decoded bitmap when it is already available and downscale it
  instead of asking PDFium to render the page a second time at thumbnail size.
- Large-document searches use PDFium's native text search to reject non-matching pages
  before their words and character boxes are materialized. Search progress updates are
  throttled to 100ms instead of posting once per page.
- Queued page renders are coalesced by document/page/kind, so a newer request supersedes
  an older queued render instead of making the worker execute stale pages first.

### Fixed

- Indeterminate progress bars now only animate while their operation is active. The busy
  overlay's spinner had continued repainting the hidden welcome screen after a document
  finished opening, keeping one CPU core busy until the window was minimised. Page,
  thumbnail, and search spinners are gated the same way.
- Toolbar clicks are no longer mistaken for text-selection presses when a page is scrolled
  partly behind the toolbar. Position-based page hit-testing is now clipped to the page
  list bounds, and a selection press must originate inside a page view.
- Search previous/next controls stay hidden until a search returns results and while a
  search is running, so the loading bar no longer overlaps them.

## [0.7.0] - 2026-11-08

### Added

- The Windows custom titlebar now opens the native window system menu on right-click or
  `Alt+Space`.
- Caption buttons dim while the window is inactive.
- A system tray icon can restore, hide, or open a new AirPDF window, and exit the app.
  Closing a window still exits; use **Hide to tray** to keep reading sessions alive.
- Tabs can be dragged into a new order. Dragging near either edge auto-scrolls the strip,
  and tabs slide into their new positions instead of snapping. Right-clicking a tab offers
  **Close tab** and **Close other tabs**; both use the existing unsaved-changes confirmation.

### Changed

- Text selection is character-granular: dragging selects the characters between the two
  points instead of snapping out to the words they fall in, so a copy takes exactly what was
  highlighted. Double-click still takes a whole word. Each word keeps the box of every
  character PDFium reported, and adjacent characters are merged into runs before they are
  painted, so a highlight is still a few rectangles rather than one per glyph.
- The arrow keys now scroll the page canvas by a small step, and `Home` / `End` jump to the
  start and end of the document. The canvas is a list of pages, so its own keyboard handling
  answered every arrow press by selecting the next item - a whole page - which threw the reader
  off the line they were following. `Page Up` / `Page Down` still scroll one screen.
- Page navigation now sits at the far left of the toolbar, aligned with the thumbnail rail;
  the remaining document tools stay centred.
- The app icon now uses the orange brand tile and white paper plane from the titlebar
  instead of the dark navy tile. Windows ICO, macOS ICNS, Linux PNG and Store artwork were
  regenerated from the same mark.

## [0.6.0] - 2026-11-05

### Changed

- Page renders now go through one process-wide priority lane: visible pages run before
  thumbnails, and one worker keeps PDFium calls serialized without a FIFO queue.
- Search reports progress while it scans and says when the result list was cut off at 250
  matches.
- Closing a tab or window now releases its PDFium session off the UI thread. Opening a
  several-thousand-page document measures page sizes in chunks instead of holding the
  process-wide PDFium gate for the whole open.
- Page bitmaps are capped at 128 MB in addition to the page-distance rule, and the first
  render of a page no longer waits 90 ms before showing anything.
- Zooming keeps the reader's position within the current page instead of re-centring the
  page on every step.
- The tab strip scrolls horizontally when there are more tabs than fit, and `Ctrl+N` now
  opens a new window as documented.
- The brand, Open button, and document tabs now share one workspace bar above the
  toolbar. The selected tab is a warm pill with light/dark/high-contrast tokens; at the
  minimum window width the two fit shortcuts fold into the existing zoom menu.
- The new-tab button now follows the last tab, then pins to the visible edge of the tab
  strip once the tabs start scrolling, so it never scrolls out of reach.
- Windows now uses a managed custom titlebar: the 44px workspace bar also carries the
  minimise, maximise/restore and close controls, with a drag region and double-click
  maximise. Windows 11 Snap Layouts are enabled on the maximize control. The titlebar
  system menu is not wired yet. macOS and Linux keep their native decorations.
- The info panel's update button is hidden in Store and portable builds, where Velopack
  has no installed app to update.

### Fixed

- Windows 11 Snap Layouts work for the custom titlebar: the maximize/restore button is
  marked as `HTMAXBUTTON` and keeps its custom hover state.
- Microsoft Store/MSIX square logos are generated with the standard inset and include
  app-list `targetsize-*` assets. The old full-bleed 150×150 and 44×44 artwork made the
  installed app's Start icon look oversized.
- The theme button shows a monitor glyph while following the system theme. Theme cycling
  now skips the explicit light/dark mode that matches the current system appearance, so
  every click changes the palette: a light system cycles through Dark → Light → high
  contrast → System, and a dark system through Light → Dark → high contrast → System.
- High contrast now has its own yin-yang contrast glyph instead of reusing the dark-mode
  sun.
- In fullscreen, the top edge of the custom titlebar now starts a window drag and leaves
  fullscreen instead of being consumed by the resize border.
- The maximize control becomes an exit-fullscreen button in fullscreen. Exiting via that
  button, `Esc`, or `F11` restores the previous normal or maximized state.
- Entering fullscreen shows a top-centre, click-through hint for 3.5 seconds with the exit
  shortcuts and the exit button, on every entry.
- Closing a tab or window, or replacing the open file from a bookmark, now asks before
  discarding unsaved highlights, ink or notes. **Save** must succeed before the document is
  closed; **Discard** and **Cancel** are explicit choices.
- When an in-place save cannot replace the original file, the saved copy is kept and opened
  as the recovery session instead of being deleted. The error message names the recovery
  file, and a retry after the other handle is gone writes the changes through.
- Closing a tab now flushes its reading position before the stream is disposed, and closed
  tabs are removed from the window's tab list instead of being retained until exit.
- A scroll that was still being applied no longer reports an older page after the reader has
  moved on. Closing a tab right after opening a document used to save page one and reopen
  there, discarding the page the reader had actually reached.
- The text layer is no longer parsed for every page that scrolls past. Parsed words are kept
  only around the current page (or pages in the active selection), and `Ctrl+A` refuses
  documents above 1,000 pages rather than loading every page at once.
- Thumbnail rail items release their bitmaps once they are far from the current page, and a
  reused rail container deactivates its previous thumbnail.
- A superseded scroll callback can no longer clear the scroll-sync guard or write its old
  position after a newer navigation.
- The tab strip's horizontal scrollbar is capped at a 4px hairline instead of expanding
  over the tabs while scrolling.
- The fit-width toolbar icon is now vertically centred like the other toolbar glyphs.

## [0.5.0] - 2026-11-04

### Added

- Erasing: a button beside the pen rubs out whatever markup the pointer passes over, and a whole
  drag becomes one undo step.
- Removing markup: **Remove annotation** in the page menu takes away whatever sits under the
  pointer - a highlight, a stroke or a note - as one action and one undo step.
- Clicking a sticky note shows its text in a small popup. The text is an annotation, so
  there was nothing on the page to click before this.
- Ink annotations: `Ctrl+Shift+K` (or the pen button) turns on drawing, and a drag on the page
  becomes a freehand stroke stored as an ink annotation. The stroke is drawn under the pointer in
  the colour it will keep, and both ink and notes can be undone and redone like any other markup.
- Sticky notes: **Add note** in the page menu asks for the text and pins it to the point you
  right-clicked. The text is stored in the annotation itself, so it travels with the file.
- A colour palette for markup, with the highlight colour and the ink colour chosen separately.
- The undo model records an action as a pair of closures rather than as highlight geometry, which
  is what let ink and notes join it without special cases.

### Changed

- Undo keeps the last 100 markup edits instead of growing for the whole session. Each step holds
  a snapshot of what it changed - an ink step holds all of that stroke's points - so an unbounded
  history kept data that could never be reached again. Older steps are dropped from the history;
  they stay in the document.
- One file is one tab: opening a document that a window already has focuses its tab instead of
  opening a second one, and **Open in new tab** is gone from the page menu. Two tabs over the
  same file meant two document sessions, and saving in either failed because the other held the
  file open.
- The pen and the arrow that opens its palette are one segmented control now: the group carries
  the hover and the active background, and a bar under the nib shows the colour the next stroke
  will have. The arrow used to be a smaller button with its own hover, which made the pair look
  unrelated.
- The ink palette previews the chosen thickness as a stroke sample in the current colour, so the
  slider shows what 3 means instead of only naming it.
- The drawing and erasing cursors are real bitmap cursors built from the tool icons, set once
  when the pointer enters a page. Drawing a glyph on every pointer move is what made those
  modes feel slow, and the glyph itself was painted behind the page, which is why no cursor was
  visible at all.
- The eraser sits directly beside the pen, before the zoom controls, rather than after them.
- Highlighting the same text twice stacks again, which is what a reader expects from pressing the
  same thing twice; the skip policy is still one property away.
- While a drawing or erasing tool is active, the pointer carries that tool over a page and
  stays an arrow everywhere else, so the tool is visible without guessing where it works.
- The status bar texts share one vertical alignment instead of sitting at different heights.
- **Highlight** in the page menu is a submenu now, so the colours appear on hover instead of after
  a click, and choosing one highlights the selection in it.
- The test suite no longer writes into its own fixture: several tests saved edits into the copied
  sample, and the next run inherited them. Each test now works on a private copy.
- Undo and redo sit next to each other in the toolbar; the pen moved after them.
- The page menu only offers what applies to what was clicked: a highlight can be highlighted, a
  note read, and any markup removed only when the pointer is actually on one.

### Fixed

- `Esc` now leaves eraser mode as well as drawing mode, so either tool can be dismissed from the
  keyboard. It used to clear drawing only, leaving the eraser active until its button was pressed
  again.
- Switching to the pen or the eraser left the selected word highlighted, which made selection
  and drawing look like modes that could be on at the same time. Entering either tool now clears
  the text selection; ink and eraser already turned each other off, so exactly one of select,
  draw, and erase owns the pointer.
- Opening a file that already had a tab created a second tab and a second document session over
  the same file. Saving in the new tab then failed with "the change could not be applied",
  because the first session still held the file open while the save tried to replace it. Opening
  a file now selects the tab that already has it - including a tab whose document is still
  loading - and drag and drop take the same path. A save that still finds the file locked by
  another window says so instead of showing a generic failure.
- Closing a tab could leave the window pointed at the tab it had just closed. Removing the
  selected item cleared the tab strip's two-way selection before the close handler looked at it,
  so Home was never selected; the welcome screen still appeared because the disposed view model
  reported no document, but opening the file from its recent list ran on that dead view model.
  The document loaded with a disposed renderer behind it - every page blank at the right reading
  position - and the next click crashed on the closed document service. Closing a tab now
  selects Home before the item is removed, the window falls back to Home if the selection is
  ever null, and a disposed view model or document service refuses to open a document again.
- Erasing a highlight or a stroke and then undoing it used to bring the markup back black.
  Rendering a page makes PDFium generate the annotation's appearance stream, and it discards the
  standard `/C` and `/IC` colour entries while doing so; the eraser's snapshot then had no colour
  left to restore. The colour is now also written to a private `AirPdfColor` key that survives the
  render and the save, and read from there first. Annotations from other applications still read
  through the standard entries, and a saved copy keeps this application's colours even after its
  pages have been viewed.
- The stroke under the pointer was always drawn two units thick, whatever the palette said, and
  jumped to the chosen width when the page was re-rendered. It is now sized in points scaled by the
  page's zoom, with the same round caps as the committed annotation, so releasing the pointer
  changes nothing.
- Choosing a pen colour while the eraser was on left both modes active: the pen looked selected
  while the pointer still erased, and clicking the pen seemed to do nothing. Picking a colour now
  leaves erasing, and the pen toggle always turns the eraser off.
- Highlighting the same text twice no longer stacks a second annotation on top of the first, which
  made it darker every time. The behaviour is a property (Skip for now, Stack available), so
  stacking later is a setting rather than a rewrite.
- A press that started on the toolbar could be taken for the start of a stroke, which swallowed the
  click: the whole toolbar went dead while drawing was on, including the pen that would turn it off.
  A stroke now only begins when the press starts inside the page list.
- The pen button is a plain toggle again. It no longer opens the palette from its own press handler,
  so it works both before and after a stroke; the ink colours moved to **Ink colour** in the page menu.
- Ink written by this app was read back with the wrong colour slot in some files, so removing a
  stroke and undoing it brought it back black. Ink is written to both slots and read from either,
  and its width is read back instead of being replaced with a default.
- A context menu still would not close when clicking elsewhere. Popups are their own visual root,
  so walking the window for open menus found nothing; the menus are now looked up from the
  controls that own them, and the note popup is dismissed on a press too.
- A stroke was drawn in the theme colour rather than the chosen ink colour, and it disappeared for a
  moment between the pointer being released and its own render. It now uses the ink brush while
  being drawn and stays on screen until the page has been redrawn with it.
- The pen button now leaves drawing mode when pressed again, instead of only Escape doing it.
- Clicking away from a selection, or pressing Escape, clears it: a select-all had no way back.
  Escape also leaves drawing mode, and right-clicking away from a selection clears it while keeping
  it when the click lands on the selection itself.
- A context menu stayed open when clicking elsewhere in the window. The page pointer handler
  marked the press as handled before the popup could see it, so the popup never dismissed itself.
## [0.4.0] - 2026-11-04

### Added

- Windows builds are available from the
  [Microsoft Store](https://apps.microsoft.com/detail/9pnsx3223qh1), which is now the recommended way
  to install on Windows: updates arrive through the Store and no unsigned installer has to be downloaded.
- Highlight annotations. Select text and press `Ctrl+Shift+H`; the selection becomes a highlight in
  the document and is drawn immediately. Right-click a highlight to remove it, `Ctrl+Z` and
  `Ctrl+Y` take either back, and the toolbar carries undo and redo buttons next to the navigation
  icons. One reader action is one undo step, so a highlight spanning eight pages goes back in one
  press.
- Saving. `Ctrl+S` writes into the document you opened: it saves to a temporary file beside it,
  swaps that in, and reopens, because PDFium reads through an open stream that has to be closed
  first. `Ctrl+Shift+S` is **Save as** and leaves the original alone.
- Per-document bookmarks: `Ctrl+D` marks the current page and scroll offset, the sidebar gains a
  Bookmarks tab, a bookmark can be renamed by double-clicking it, dragged into a different order,
  and the welcome screen lists bookmarks from every document.
- The welcome screen is the first tab, so the tab strip exists from the first document, and going
  home no longer closes what you were reading. The logo and wordmark are one button back to it, and
  a document already open in another tab is focused rather than opened twice.
- A crash log: unhandled exceptions on any thread are appended to `crash.log` next to
  `settings.json`.
- End-to-end tests that drive a real window with real key events, covering zoom, search, language
  switching, undo/redo, and the update banner.

### Changed

- The release page no longer offers the Windows installer or the macOS `.pkg` while the builds are
  unsigned: SmartScreen and Gatekeeper would stop the first launch and it would read as a fault in
  the reader. Portable archives stay, and Windows users have the Store.
- The toolbar's zoom controls share one size, and the new undo/redo buttons sit with the navigation
  icons rather than inside the zoom group.

### Fixed

- A highlight stayed invisible until the document was saved and reopened: the page already had a
  bitmap at that width, so the re-render that should have shown it returned early.
- Switching tabs left the previous document's pages active, keeping their bitmaps and re-rendering
  them in the background as memory crept up.
- Scrolling after a tab switch could overflow the stack, because the thumbnail list was scrolled
  from inside a layout pass.
- A save that could not replace the file used to strand the document: the session was closed before
  the file was swapped, so editing failed and rendering fell back to a transient handle per page. A
  read-only or busy file is now detected before the session is released.

## [0.3.2] - 2026-11-02

Every platform is now compiled ahead of time.

### Changed

- The Linux and macOS builds are published with NativeAOT too, so all three platforms start
  in well under a second instead of several. The Linux runner installs `clang` and
  `zlib1g-dev` for the native link; macOS uses the Xcode toolchain already on the runner.
- The release jobs and every pull request now fail on any AOT warning for each platform
  separately, because Windows says nothing about Linux or macOS: they have different
  toolchains and different native assets.

### Changed

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

### Changed

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

- `packaging/signing/Set-SigningSecrets.ps1` validates a Windows or macOS
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

### Changed

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

### Changed

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
