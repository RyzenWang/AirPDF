# Store listing copy

Ready-to-paste text for the Partner Center listing. Character counts are noted
where the field has a limit.

## App name

Reserved in Partner Center and then fixed for the life of the product.

Suggested order if the plain name is taken:

1. `AirPDF`
2. `AirPDF Reader`
3. `AirPDF — Quiet PDF Reader`

The reserved name also sets `Package/Identity/Name`, which is the value passed to
`Build-Msix.ps1 -IdentityName`.

## Short description (English)

> A feather-light PDF reader for Windows. Opens fast, remembers where you were,
> and stays out of the way.

## Description (English)

```
AirPDF is a small, fast PDF reader for Windows.

It opens a document in under a second and then does something unusual: it gets out
of the way. No account, no cloud, no subscription, no onboarding tour, no start
screen full of recommendations. Just the page, the way it was typeset.

WHAT IT DOES

• Opens PDF and remembers where you stopped, including how far down the page
• Highlight annotations: select text and press Ctrl+Shift+H, with undo and redo
• Saves your highlights back into the PDF itself with Ctrl+S
• Tabs and multiple windows, so a manual and a paper can sit side by side
• Full-text search with highlights, and F3 / Shift+F3 to step through matches
• Select and copy text, with Ctrl+A for the whole document
• Bookmarks you can rename and reorder, plus a list of bookmarks across files
• Thumbnails, the document outline, and links that work
• Fit width, fit page, actual size, and Ctrl + wheel zoom
• Light, dark, and high-contrast themes
• English and 简体中文, switchable without restarting
• Password-protected PDFs

WHAT IT DOES NOT DO

• No account, no telemetry, no network access while you read
• No ads, no upsells, no bundled software

TECHNICAL

Rendering is PDFium; text and search use the same engine, so highlights land on the
right words. The app is open source under the MIT license, and the source is on
GitHub. It targets Windows 10 version 2004 and later on x64.
```

## Description (简体中文)

```
AirPDF 是一个小巧、快速的 Windows PDF 阅读器。

打开文档通常不到一秒，然后做一件少见的事:它让开。不需要账号，不上传云端，没有订阅，
没有新手引导，也没有塞满推荐内容的首页。只有页面本身。

主要功能

• 自动记住阅读位置，包括页内滚动到哪儿
• 高亮批注:选中文字按 Ctrl+Shift+H,可撤销可重做
• Ctrl+S 把高亮写回 PDF 本身
• 标签页与多窗口,技术手册和论文可以并排看
• 全文搜索并高亮,F3 / Shift+F3 在结果间跳转
• 选中并复制文本,Ctrl+A 全选整篇
• 书签可重命名、可拖动排序,还能跨文档浏览
• 缩略图、文档目录、可点击的超链接
• 适应宽度、适应整页、实际大小、Ctrl + 滚轮缩放
• 浅色、深色、高对比度主题
• 中文与 English,免重启切换
• 支持加密 PDF

它不做的事

• 不需要账号,不收集使用数据,阅读时不联网
• 没有广告、没有内购、不捆绑任何软件

技术说明

渲染使用 PDFium,文本与搜索使用同一引擎,所以高亮位置准确。应用以 MIT 许可证开源,
源代码托管在 GitHub。支持 Windows 10 2004 及以上版本的 x64 设备。
```

## Screenshots

Captured at 1578×944 from the shipping build, English and light theme.

| Screenshot | Caption (English) | Caption (简体中文) |
| --- | --- | --- |
| `screenshot-welcome.png` | Opens straight to the page, not a start screen | 打开就是页面,不是首页 |
| `screenshot-reading.png` | Fit-width reading with the page in front of you | 适应宽度,专注页面本身 |

## Category and audience

| Field | Value |
| --- | --- |
| Category | Books & reference |
| Secondary category (optional) | Productivity |
| Age rating | 3+ / unrestricted; the app displays whatever the user opens, so answer the questionnaire honestly rather than claiming content filtering |
| Supported languages | English (United States), Chinese (Simplified) |
| System requirements | Windows 10 version 2004 (build 19041) or later, x64 |

## Support and policies

| Field | Value |
| --- | --- |
| Support contact | `https://github.com/RyzenWang/AirPDF/issues` |
| Website | `https://github.com/RyzenWang/AirPDF` |
| Privacy policy URL | `https://ryzenwang.github.io/AirPDF/store/PRIVACY.html` |
| License terms | MIT, link to the repository's `LICENSE` |

## Search keywords (optional)

`pdf`, `pdf reader`, `pdf viewer`, `lightweight`, `offline`, `no ads`, `open source`,
`pdf 阅读器`, `轻量`

## Notes for certification

Paste this into the "Notes for certification" box so reviewers do not have to guess:

```
AirPDF is a desktop PDF reader. It needs no account and no test credentials.

To test: open the app, press Ctrl+O, and choose any PDF. The repository includes a
sample at tests/Assets/sample.pdf.

Highlighting: select text on a page and press Ctrl+Shift+H. Ctrl+Z undoes it. Ctrl+S
writes the highlight into a copy beside the original file and asks before replacing
anything, so testing with a read-only document is also fine.

Notifications, location, camera, and microphone are not used. The app reads files
the user picks and stores preferences in %APPDATA%\AirPdf. The packaged build makes
no network request at all; only the standalone installer from GitHub offers an
update check, which the Store build never reaches.
```
