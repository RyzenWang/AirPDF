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

> A feather-light PDF reader for Windows. Opens fast, annotates without ceremony,
> and stays out of the way.

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

打开文档通常不到一秒，然后做一件少见的事:它让开。不需要账号，不上传云端，没有订阅，
没有新手引导，也没有塞满推荐内容的首页。只有页面本身。

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

渲染与批注写入都使用 PDFium,文本与搜索同一引擎,所以高亮位置准确,写出的文件在其他
PDF 应用里也能正常打开。应用以 MIT 许可证开源,源代码托管在 GitHub。支持 Windows 10
2004 及以上版本的 x64 设备。
```

## What's new in this version (Partner Center field)

Limit is about 1500 characters. Both languages below fit.

English:

```
AirPDF 0.6 is a faster, tidier reader.

• Page rendering now runs through one priority queue: the page you are looking at is
  decoded before thumbnails, and the reader stays responsive on very large documents.
• Battery and memory: page bitmaps are capped, thumbnails release their images once
  they leave the rail, and closing a tab frees its PDF engine session off the UI thread.
• Search reports progress while it scans and says when the result list was capped.
• Zoom keeps your place inside the page instead of re-centring it on every step.
• Tabs can be dragged to reorder, with a smooth slide instead of a jump, and the strip
  scrolls horizontally when there are more tabs than fit.
• A new workspace bar puts the brand, Open, and your document tabs on one line, with
  Windows 11 Snap Layouts on the maximise button.
• A tray icon keeps AirPDF reachable while it is minimised.
• Closing a tab with unsaved highlights, ink, or notes asks first: Save, Discard, or
  Cancel. If saving cannot replace the original file, the saved copy is kept and opened
  so nothing is lost.
```

简体中文：

```
AirPDF 0.6 更快、更整齐。

• 页面渲染改走单一优先级队列:正在看的页面先于缩略图解码,超大文档下界面依然跟手。
• 更省内存:页面位图有上限,缩略图离开侧栏就释放位图,关闭标签时在 UI 线程之外释放
  PDF 引擎会话。
• 搜索扫描时显示进度,结果超过上限会明确提示。
• 缩放时保持页内位置,不再每步重新居中。
• 标签可拖动排序,带平滑滑动而不是瞬移;标签超过一屏时横向滚动。
• 新的工作区栏把品牌、打开按钮和文档标签放在同一行,最大化按钮支持 Windows 11 吸附布局。
• 最小化后可通过托盘图标回到应用。
• 有未保存的高亮、墨迹或便签时关闭标签会先询问:保存、放弃或取消。若无法覆盖原文件,
  保存的副本会被保留并打开,不会丢失。
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
