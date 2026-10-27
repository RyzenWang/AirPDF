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

Both files are original to this repository and are licensed under the MIT
License. See `THIRD-PARTY-NOTICES.md` for the fonts referenced by the HTML.
