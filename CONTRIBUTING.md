# Contributing to AirPDF

Thanks for taking the time to make AirPDF better. This project is intentionally
small, so a little care goes a long way.

## Before you start

- For a bug, open an issue with the PDF's producer, the platform, and a minimal
  reproduction if possible.
- For a larger feature, open an issue first. The reader has a narrow scope and
  a few deliberate non-goals (editing, cloud sync, embedded web views).
- Do not attach confidential PDFs to issues. Reproduce with a synthetic document
  or redact it.

## Development setup

Requirements:

- .NET 10 SDK
- A desktop environment (Windows, macOS, or Linux)
- Git

```bash
git clone https://github.com/RyzenWang/AirPDF.git
cd AirPDF
dotnet restore AirPdf.sln
dotnet build AirPdf.sln
dotnet test tests/AirPdf.Core.Tests/AirPdf.Core.Tests.csproj
dotnet run --project src/AirPdf.App
```

The sample document at `tests/Assets/sample.pdf` is generated from
`tests/Assets/sample.html`. It contains searchable Latin and Chinese text and is
small enough to commit.

## Code guidelines

- Keep `AirPdf.Core` free of Avalonia references.
- Put parsing, rendering, and file operations in the core; put only presentation
  state in view models.
- Long-running work must be cancellable and must not block the UI thread.
- Do not call PDFium concurrently. Use `PdfDocumentService` rather than calling
  PDFtoImage directly.
- Prefer source-generated MVVM properties and commands over hand-written
  `INotifyPropertyChanged` boilerplate.
- Keep user-facing text in one place when localization lands. For now, English
  is the source language.
- Use four spaces in C# and two in XAML, JSON, and YAML (see `.editorconfig`).

## Tests

At minimum, add a core test when changing:

- document opening or metadata
- page sizing or rotation
- rendering and caching behavior
- search tokenization, matching, or rectangles
- settings and recent-file handling

UI changes should be exercised manually on the platforms you have available.
Include a screenshot in the pull request when the change is visual.

## Pull requests

A good pull request:

- explains the user-visible problem and the chosen approach;
- keeps unrelated refactors out of the diff;
- includes tests or a clear reason why none are practical;
- updates `README.md` and `CHANGELOG.md` when behavior changes;
- builds without new warnings on the supported target frameworks.

By contributing, you agree that your contribution is licensed under the MIT
License used by this repository.

## Commit messages

Use short, imperative subjects:

```
Fix page jump when zooming with a search open
Add password prompt for encrypted PDFs
```

Prefixes such as `feat:`, `fix:`, and `docs:` are welcome but not required.

## Reporting security issues

Please do not open a public issue for a vulnerability. See [SECURITY.md](SECURITY.md).
