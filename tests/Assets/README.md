# Test assets

`sample.pdf` is a self-contained eight-page PDF used by the core tests and the
README screenshots. It is generated from `sample.html` with Microsoft Edge's
headless print-to-PDF mode:

```powershell
& "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" `
  --headless=new `
  --disable-gpu `
  --no-pdf-header-footer `
  --user-data-dir="$env:TEMP\airpdf-edge" `
  --print-to-pdf="$PWD\sample.pdf" `
  "file:///$($PWD.Path.Replace('\','/'))/sample.html"
```

The document intentionally includes:

- vector and typography-heavy cover pages,
- Latin text for search and highlight tests,
- a Simplified Chinese page (`轻量`) for non-ASCII search,
- enough pages to exercise virtualization.

`links.pdf` is a hand-built two-page fixture for link annotations: page 1 carries
an external `https` link and page 2 an internal link back to page 1. It is only 883
bytes because it has no content stream; it is produced by writing the objects,
computing the cross-reference offsets, and appending the trailer:

```text
catalog → pages → two page objects (300x400 MediaBox)
                → link annot with /A << /S /URI /URI (https://example.com/airpdf-links) >>
                → link annot with /Dest [3 0 R /Fit]
```

Regenerate it if the fixture ever needs to change; keep the byte offsets in the
`xref` table correct or PDFium will refuse to open it.

Both files are original to this repository and are licensed under the MIT
License. See `THIRD-PARTY-NOTICES.md` for the fonts referenced by the HTML.
