# Privacy policy

**AirPDF does not collect, transmit, or sell any personal data.**

Last updated: 2 November 2026

## What the app stores

AirPDF keeps a small settings file on your own computer:

- the list of documents you recently opened, and where you were reading in each
- bookmarks you created
- your window size and position, theme, zoom, and language

On Windows this is `%APPDATA%\AirPdf\settings.json`, with a backup copy beside it.
On macOS it is `~/Library/Application Support/AirPdf/`, and on Linux
`~/.config/AirPdf/`.

If the app is installed from the Microsoft Store, Windows may place this file
inside the app's own package folder instead.

Uninstalling the app does not always delete that file; you can delete the folder
above at any time and the app will start with default settings.

## What the app sends

Nothing. AirPDF has no analytics, no telemetry, no crash reporting service, no
advertising, and no account. Opening, searching, and reading a document makes no
network request.

If the app is unresponsive or crashes, it writes a `crash.log` next to the settings
file. That file stays on your computer and is never uploaded.

## The one exception: update checks in the standalone build

The version downloaded from GitHub (not the Microsoft Store version) checks for a
newer release when it starts, by requesting a file from GitHub's public release
pages. That request carries no identifier beyond what any HTTPS request to GitHub
includes. It can be avoided by using the Microsoft Store build, which never checks,
or by blocking the app's network access.

## Documents

Documents you open are read locally. Their contents are never uploaded.

## Children

The app collects no data from anyone, including children.

## Changes

If this policy changes, the updated text will be published at this URL and in the
repository's history.

## Contact

Open an issue at <https://github.com/RyzenWang/AirPDF/issues>.
