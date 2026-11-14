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

> PDF reader | opens instantly · free · no ads · no sign-in

## Short description (简体中文)

> PDF 阅读器｜秒开 · 免费 · 无广告 · 免登录

## Description (English)

```
AirPDF is a small, fast PDF reader for Windows.

It opens a document in under a second and then does something unusual: it gets out
of the way. No account, no cloud, no subscription, no onboarding tour, no start
screen full of recommendations. Just the page, the way it was typeset.

READ AND ANNOTATE

• Opens PDF and remembers where you stopped, including how far down the page
• Highlight text with Ctrl+Shift+H, using a palette of colours
• Draw with the pen (Ctrl+Shift+K) to add ink annotations
• Pin sticky notes anywhere on a page; click one to read it again
• Rub out any markup with the eraser, or remove it from the page menu
• Undo and redo every markup change, up to the last 100 edits
• Ctrl+S writes your annotations back into the PDF, so they travel with the file
• Everything is stored as standard PDF annotations - no proprietary layer

READING

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
right words. Annotations are written through the same engine, which keeps the file
readable in any other PDF app. The app is open source under the MIT license, and the
source is on GitHub. It targets Windows 10 version 2004 and later on x64.
```

## Description (简体中文)

```
AirPDF 是一个小巧、快速的 Windows PDF 阅读器。

一秒打开，完全免费。没有开屏广告，没有推荐首页；不需要账号，不连接云端，没有订阅，
也没有新手引导。屏幕上只剩你正在读的那一页，和它原本的排版。

主要功能

阅读与批注

• 自动记住阅读位置，包括页内滚动到哪儿
• 选中文字按 Ctrl+Shift+H 加高亮，颜色可选
• 按 Ctrl+Shift+K 或点画笔按钮进入绘制，一段拖动就是一条墨迹批注
• 便签批注可钉在页面任意位置，点击便签即可再看内容
• 橡皮擦擦掉指针经过的任何标记，也可以在页面菜单里移除
• 所有批注修改都可撤销重做，保留最近 100 步
• Ctrl+S 把批注写回 PDF，随文件一起走
• 全部使用标准 PDF 批注格式，不引入私有图层

阅读

• 标签页与多窗口，技术手册和论文可以并排看
• 全文搜索并高亮，F3 / Shift+F3 在结果间跳转
• 选中并复制文本，Ctrl+A 全选整篇
• 书签可重命名、可拖动排序，还能跨文档浏览
• 缩略图、文档目录、可点击的超链接
• 适应宽度、适应整页、实际大小、Ctrl + 滚轮缩放
• 浅色、深色、高对比度主题
• 中文与 English，免重启切换
• 支持加密 PDF

它不做的事

• 不需要账号，不收集使用数据，阅读时不联网
• 没有广告、没有内购、不捆绑任何软件

技术说明

渲染与批注写入都使用 PDFium，文本与搜索同一引擎，所以高亮位置准确，写出的文件在其他
PDF 应用里也能正常打开。应用以 MIT 许可证开源，源代码托管在 GitHub。支持 Windows 10
2004 及以上版本的 x64 设备。
```

## What's new in this version (Partner Center field)

Limit is about 1500 characters. Both languages below fit.

English:

```
AirPDF 0.9 is a quieter reading surface.

• F11 is now a real reading mode: the tab strip, toolbar, status bar and thumbnail rail
  all step aside, the page refits itself to the wider canvas, and leaving fullscreen
  restores the zoom and zoom mode you had before.
• Move the pointer to the top edge of the screen and the titlebar and toolbar reappear —
  just long enough to drag the window or exit fullscreen. Move back into the page and they
  hide themselves again.
• A new button on the toolbar puts two pages side by side. Single and spread views share
  one scrolling canvas, so switching between them keeps your place in the document.
• The app icon no longer looks smaller than its neighbours in the Start menu, and a .pdf
  file on the desktop now shows the AirPDF icon at full size and centred, instead of small
  and pushed into a corner.
```

简体中文：

```
AirPDF 0.9 让页面本身成为主体。

• F11 变成真正的阅读模式：标签栏、工具栏、状态栏和缩略图栏一起收起，页面自动适配变宽
  的画布；退出全屏时恢复进入前的缩放比例和缩放方式。
• 鼠标移到屏幕顶边，标题栏和工具栏会临时出现，正好够拖动窗口或退出全屏；移回页面后又
  自动隐藏。
• 工具栏新增视图按钮，把相邻两页并排显示。单页与双页共用同一个滚动画布，切换时不会丢失
  当前阅读位置。
• 应用图标在开始菜单里不再比别的应用小一圈；把 .pdf 设为默认打开后，桌面上的 PDF 图标
  也会以完整尺寸居中显示，不再又小又偏。
```

## Screenshots

Captured at 2188×1308 from the 0.6 build, English and light theme. The display runs
at 150% scaling, so the pixels are real ones rather than an upscale, and the rounded
window corners are trimmed.

| File | Caption (English) | Caption (简体中文) |
| --- | --- | --- |
| `screenshot-welcome.png` | Opens straight to the page, not a start screen | 打开就是页面,不是首页 |
| `screenshot-reading.png` | Fit-width reading with the page in front of you | 适应宽度,专注页面本身 |
| `screenshot-markup.png` | Highlight, ink, and the pen in the toolbar | 高亮、墨迹,以及工具栏上的画笔 |

The markup shot shows a real highlight over a paragraph plus an ink underline drawn
with the pen, with the status bar reading "Drawing off" after Esc. The welcome and
reading shots show the 0.6 workspace bar with the brand, Open, and document tabs on
one line.

Recapture with `packaging/store/capture.ps1 -Scene welcome|reading|markup` after any
UI change; the scenes drive the app by fractions of its client area, so they survive
a different window size or DPI. Seed the reading position to page 3 first, otherwise
the document scenes open on the cover.

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

`pdf`, `pdf reader`, `pdf viewer`, `pdf annotator`, `highlight`, `lightweight`,
`offline`, `no ads`, `open source`, `pdf 阅读器`, `pdf 批注`, `轻量`

## Notes for certification

Paste this into the "Notes for certification" box so reviewers do not have to guess:

```
AirPDF is a desktop PDF reader. It needs no account and no test credentials.

To test: open the app, press Ctrl+O, and choose any PDF. The repository includes a
sample at tests/Assets/sample.pdf.

Annotating:
- Highlight: select text, press Ctrl+Shift+H. Right-click a highlight for its colours.
- Ink: press Ctrl+Shift+K or the pen button, then drag on the page. Esc leaves the tool.
- Sticky note: right-click a page and choose Add note.
- Eraser: the button beside the pen rubs out markup under the pointer.
- Ctrl+Z undoes, Ctrl+Y redoes.
- Ctrl+S writes the annotations into the PDF. It works on a copy beside the original
  and reports a clear message if the file cannot be replaced, so testing on a
  read-only document is safe.

Annotations are standard PDF highlight, ink and text annotations, so the saved file
opens in any other PDF reader.

Notifications, location, camera, and microphone are not used. The app reads files
the user picks and stores preferences in %APPDATA%\AirPdf. The packaged build makes
no network request at all; only the standalone installer from GitHub offers an
update check, which the Store build never reaches.
```
