using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using AirPdf.Core.Models;

namespace AirPdf.App.Services;

/// <summary>
/// Lightweight runtime localization. Static XAML text uses DynamicResource keys
/// such as <c>L.Open</c>; code-generated text goes through <see cref="T"/> and
/// <see cref="Format"/>.
/// </summary>
public sealed class LocalizationService : IDisposable
{
    public const string English = "en";
    public const string ChineseSimplified = "zh-CN";

    private static readonly IReadOnlyDictionary<string, (string En, string Zh)> Catalog =
        new Dictionary<string, (string En, string Zh)>(StringComparer.Ordinal)
        {
            ["L.Open"] = ("Open", "打开"),
            ["L.OpenPdf"] = ("Open a PDF", "打开 PDF"),
            ["L.OpenTooltip"] = ("Open a PDF (Ctrl+O)", "打开 PDF（Ctrl+O）"),
            ["L.Home"] = ("Home", "主页"),
            ["L.HomeTooltip"] = ("Back to home", "回到主页"),
            ["L.PreviousPage"] = ("Previous page", "上一页"),
            ["L.NextPage"] = ("Next page", "下一页"),
            ["L.ZoomOut"] = ("Zoom out", "缩小"),
            ["L.ZoomIn"] = ("Zoom in", "放大"),
            ["L.ZoomPreset"] = ("Zoom preset", "缩放比例"),
            ["L.FitWidth"] = ("Fit width", "适合宽度"),
            ["L.FitPage"] = ("Fit page", "适合页面"),
            ["L.ActualSize"] = ("Actual size — 100%", "实际大小 — 100%"),
            ["L.FindInDocument"] = ("Find in document (Ctrl+F)", "在文档中查找（Ctrl+F）"),
            ["L.ToggleThumbnails"] = ("Toggle thumbnails", "显示或隐藏缩略图"),
            ["L.HideSidebar"] = ("Hide sidebar", "隐藏侧栏"),
            ["L.DocumentDetails"] = ("Document details", "文档详情"),
            ["L.Language"] = ("Language", "语言"),
            ["L.Navigate"] = ("NAVIGATE", "导航"),
            ["L.Pages"] = ("Pages", "页面"),
            ["L.Contents"] = ("Outline", "目录"),
            ["L.NoOutline"] = ("No outline in this PDF", "此 PDF 没有目录"),
            ["L.Bookmarks"] = ("Bookmarks", "书签"),
            ["L.HighlightSelection"] = ("Highlight selection", "高亮所选"),
            ["L.ShowNote"] = ("Show note", "查看便签"),
            ["L.RemoveMarkup"] = ("Remove annotation", "移除标注"),
            ["L.MarkupRemoved"] = ("Annotation removed.", "已移除标注。"),
            ["L.NoMarkupHere"] = ("Nothing to remove here.", "这里没有可移除的标注。"),
            ["L.RemoveHighlight"] = ("Remove highlight", "移除高亮"),
            ["L.MarkupColors"] = ("Markup colours", "标注颜色"),
            ["L.HighlightColorLabel"] = ("Highlight", "高亮"),
            ["L.InkColorLabel"] = ("Ink", "墨迹"),
            ["L.StartDrawing"] = ("Start drawing", "开始绘制"),
            ["L.StopDrawing"] = ("Stop drawing", "停止绘制"),
            ["L.InkMode"] = ("Draw (Ctrl+Shift+K)", "自由绘制（Ctrl+Shift+K）"),
            ["L.InkModeOn"] = ("Drawing: drag on the page to draw. Press again to select.", "自由绘制已开启：在页面上拖动即可绘制，再次按下回到选择。"),
            ["L.InkModeOff"] = ("Drawing off.", "自由绘制已关闭。"),
            ["L.InkAdded"] = ("Ink added.", "已添加墨迹。"),
            ["L.AddNote"] = ("Add note", "添加便签"),
            ["L.NoteTextLabel"] = ("Note", "便签内容"),
            ["L.NoteAdded"] = ("Note added.", "已添加便签。"),

            ["L.Undo"] = ("Undo (Ctrl+Z)", "撤销（Ctrl+Z）"),
            ["L.Redo"] = ("Redo (Ctrl+Y)", "重做（Ctrl+Y）"),
            ["L.Redone"] = ("Redone.", "已重做。"),
            ["L.SaveDocument"] = ("Save (Ctrl+S)", "保存（Ctrl+S）"),
            ["L.SaveAs"] = ("Save as…", "另存为…"),
            ["L.Saved"] = ("Saved.", "已保存。"),
            ["L.NothingToRedo"] = ("Nothing to redo.", "没有可重做的操作。"),
            ["L.Undone"] = ("Undone.", "已撤销。"),
            ["L.NothingToUndo"] = ("Nothing to undo.", "没有可撤销的操作。"),

            ["L.HighlightRemoved"] = ("Highlight removed.", "已移除高亮。"),
            ["L.NoHighlightHere"] = ("No highlight under the pointer.", "该位置没有高亮。"),

            ["L.HighlightAdded"] = ("Highlight added. Save a copy to keep it.", "已添加高亮。保存副本才能保留。"),

            ["L.SavedAs"] = ("Saved as {0}", "已另存为 {0}"),
            ["L.UnsavedAnnotations"] = ("Unsaved highlights", "有未保存的高亮"),
            ["L.Markup"] = ("Markup", "标注"),

            ["L.AddBookmark"] = ("Add bookmark (Ctrl+D)", "添加书签（Ctrl+D）"),
            ["L.NoBookmarks"] = ("No bookmarks in this document yet", "此文档还没有书签"),
            ["L.RemoveBookmark"] = ("Remove bookmark", "删除书签"),
            ["L.BookmarksList"] = ("Document bookmarks", "文档书签"),
            ["L.BookmarkPageFormat"] = ("Page {0}", "第 {0} 页"),
            ["L.RenameBookmark"] = ("Rename bookmark", "重命名书签"),
            ["L.BookmarkNameLabel"] = ("Name", "名称"),
            ["L.Save"] = ("Save", "保存"),
            ["L.AllBookmarks"] = ("All bookmarks", "全部书签"),
            ["L.NoBookmarksAnywhere"] = ("No bookmarks yet", "还没有任何书签"),
            ["L.BookmarkInFile"] = ("{0} · page {1}", "{0} · 第 {1} 页"),
            ["L.MoveBookmarkUp"] = ("Move up", "上移"),
            ["L.MoveBookmarkDown"] = ("Move down", "下移"),
            ["L.Tagline"] = ("A FEATHER-LIGHT PDF READER", "轻量 PDF 阅读器"),
            ["L.WelcomeTitle1"] = ("Your PDFs,", "你的 PDF，"),
            ["L.WelcomeTitle2"] = ("without the weight.", "轻一点。"),
            ["L.WelcomeSubtitle"] = (
                "Fast, quiet, and native on Windows, macOS, and Linux. No account, no clutter — just the page in front of you.",
                "在 Windows、macOS 和 Linux 上快速、安静、原生运行。无需账号，没有杂乱，只有眼前的页面。"),
            ["L.WelcomeMeta"] = ("PDFium  ·  .NET 10  ·  MIT licensed", "PDFium  ·  .NET 10  ·  MIT 许可"),
            ["L.Recent"] = ("RECENT", "最近打开"),
            ["L.Clear"] = ("Clear", "清除"),
            ["L.Copy"] = ("Copy", "复制"),
            ["L.SelectionStatusFormat"] = ("{0} characters selected", "已选择 {0} 个字符"),
            ["L.UpdateAvailableFormat"] = ("Update {0}", "更新到 {0}"),
            ["L.RestartToUpdateFormat"] = ("Restart to update {0}", "重启以更新 {0}"),
            ["L.UpdatingFormat"] = ("Updating… {0}%", "正在更新… {0}%"),
            ["L.UpdateNotInstalled"] = ("Updates are available in installed builds.", "自动更新仅适用于安装版。"),
            ["L.UpdateError"] = ("Could not check for updates.", "检查更新失败。"),
            ["L.CheckForUpdates"] = ("Check for updates", "检查更新"),
            ["L.SearchTitle"] = ("Find in document", "在文档中查找"),
            ["L.SearchSubtitle"] = ("Searches the PDF text layer", "搜索 PDF 文本层"),
            ["L.SearchPlaceholder"] = ("Search this PDF…", "搜索此 PDF…"),
            ["L.Find"] = ("Find", "查找"),
            ["L.Close"] = ("Close", "关闭"),
            ["L.CloseSearch"] = ("Close search", "关闭搜索"),
            ["L.ClearSearch"] = ("Clear search", "清除搜索"),
            ["L.PreviousMatch"] = ("Previous match", "上一个匹配"),
            ["L.NextMatch"] = ("Next match", "下一个匹配"),
            ["L.NoMatchesTitle"] = ("No matches reached the page", "没有找到匹配内容"),
            ["L.NoMatchesHint"] = ("Try a shorter word or a different spelling.", "试试更短的词或不同的拼写。"),
            ["L.SearchFooter"] = ("Enter to search  ·  Esc to close", "Enter 搜索  ·  Esc 关闭"),
            ["L.Dismiss"] = ("Dismiss", "关闭提示"),
            ["L.DropToOpen"] = ("Drop to open", "松开以打开"),
            ["L.DropHint"] = ("Release the PDF anywhere in this window", "在窗口任意位置松开 PDF"),
            ["L.Ready"] = ("Ready", "就绪"),
            ["L.NoDocument"] = ("No document", "未打开文档"),
            ["L.OpenDialogTitle"] = ("Open a PDF", "打开 PDF"),
            ["L.PdfDocuments"] = ("PDF documents", "PDF 文档"),
            ["L.PasswordTitle"] = ("Password required", "需要密码"),
            ["L.PasswordHeading"] = ("This PDF is locked", "此 PDF 已加密"),
            ["L.PasswordLabel"] = ("Enter the document password", "输入文档密码"),
            ["L.PasswordRetry"] = ("That password did not work. Try again.", "密码不正确，请重试。"),
            ["L.PasswordEmpty"] = ("Enter a password to continue.", "请输入密码后继续。"),
            ["L.Unlock"] = ("Unlock", "解锁"),
            ["L.Cancel"] = ("Cancel", "取消"),
            ["L.OK"] = ("OK", "确定"),
            ["L.CouldNotOpen"] = ("Could not open PDF", "无法打开 PDF"),
            ["L.Page"] = ("Page", "页"),
            ["L.PageStatusFormat"] = ("Page {0} of {1}", "第 {0} / {1} 页"),
            ["L.PageLabelFormat"] = ("Page {0}", "第 {0} 页"),
            ["L.PageUpperFormat"] = ("PAGE {0}", "第 {0} 页"),
            ["L.PageCountOne"] = ("1 page", "1 页"),
            ["L.PageCountMany"] = ("{0} pages", "{0} 页"),
            ["L.Searching"] = ("Searching…", "正在搜索…"),
            ["L.NoMatches"] = ("No matches found", "没有找到匹配"),
            ["L.MatchOne"] = ("1 match", "1 个匹配"),
            ["L.MatchMany"] = ("{0} matches", "{0} 个匹配"),
            ["L.SearchFailed"] = ("Search failed", "搜索失败"),
            ["L.SearchError"] = ("AirPDF could not search this document.", "AirPDF 无法搜索此文档。"),
            ["L.OpenError"] = ("AirPDF could not open this document.", "AirPDF 无法打开此文档。"),
            ["L.RenderError"] = ("This page could not be rendered.", "此页面无法渲染。"),
            ["L.BusyOpening"] = ("Opening {0}…", "正在打开 {0}…"),
            ["L.RecentJustNow"] = ("just now", "刚刚"),
            ["L.RecentMinutes"] = ("{0} min ago", "{0} 分钟前"),
            ["L.RecentHours"] = ("{0} hr ago", "{0} 小时前"),
            ["L.RecentDays"] = ("{0} d ago", "{0} 天前"),
            ["L.MetaTitle"] = ("Title", "标题"),
            ["L.MetaAuthor"] = ("Author", "作者"),
            ["L.MetaSubject"] = ("Subject", "主题"),
            ["L.MetaCreated"] = ("Created", "创建时间"),
            ["L.MetaCreator"] = ("Creator", "创建工具"),
            ["L.MetaProducer"] = ("Producer", "生成工具"),
            ["L.MetaPages"] = ("Pages", "页数"),
            ["L.MetaPdfVersion"] = ("PDF version", "PDF 版本"),
            ["L.MetaFileSize"] = ("File size", "文件大小"),
            ["L.MetaKeywords"] = ("Keywords", "关键词"),
            ["L.ErrorSaveDenied"] = ("The file could not be written. It may be read-only, or open in another program.", "无法写入文件：它可能是只读的，或正被其他程序打开。"),
            ["L.ErrorEditFailed"] = ("The change could not be applied.", "无法应用该修改。"),
            ["L.ErrorSaveFailed"] = ("The copy could not be written. Check the location and try again.", "无法写入副本，请检查保存位置后重试。"),
            ["L.ErrorNotFound"] = ("The file does not exist.", "文件不存在。"),
            ["L.ErrorAccessDenied"] = ("You do not have permission to read this file.", "没有权限读取此文件。"),
            ["L.ErrorPasswordRequired"] = ("This document is password protected.", "此文档受密码保护。"),
            ["L.ErrorInvalidPassword"] = ("The password is incorrect.", "密码不正确。"),
            ["L.ErrorCorrupt"] = ("The PDF appears to be damaged.", "此 PDF 似乎已损坏。"),
            ["L.ErrorUnsupported"] = ("This document uses an unsupported security scheme.", "此文档使用了不支持的安全方案。"),
            ["L.ErrorUnknown"] = ("AirPDF could not open this document.", "AirPDF 无法打开此文档。"),
            ["L.CopySelection"] = ("Copy selection", "复制选中内容"),
            ["L.SelectAll"] = ("Select all", "全选"),
            ["L.OpenContainingFolder"] = ("Show in folder", "打开所在文件夹"),
            ["L.LinkError"] = ("AirPDF could not open this link.", "无法打开此链接。"),
            ["L.OpenInNewWindow"] = ("Open in new window", "在新窗口中打开"),
            ["L.NewWindow"] = ("New window (Ctrl+N)", "新建窗口（Ctrl+N）"),
            ["L.NewTab"] = ("New tab", "新标签页"),
            ["L.NewTabHint"] = ("New tab (Ctrl+T)", "新建标签页（Ctrl+T）"),
            ["L.CloseTab"] = ("Close tab", "关闭标签页"),
            ["L.OpenInNewTab"] = ("Open in new tab", "在新标签页中打开"),
            ["L.ExplorerError"] = ("AirPDF could not open the file's folder.", "无法打开文件所在文件夹。"),
            ["L.NavigateBack"] = ("Back to previous position (Alt+Left)", "返回上一个位置（Alt+Left）"),
            ["L.PageNumberLabel"] = ("Page number", "页码"),
            ["L.ThumbnailsList"] = ("Page thumbnails", "页面缩略图"),
            ["L.OutlineList"] = ("Document outline", "文档目录"),
            ["L.SearchResultsList"] = ("Search results", "搜索结果"),
            ["L.RecentFilesList"] = ("Recently opened files", "最近打开的文件"),
            ["L.SearchBoxLabel"] = ("Search this PDF", "搜索此 PDF"),
            ["L.ZoomLevelLabel"] = ("Zoom level", "缩放比例"),
            ["L.ReadingProgressLabel"] = ("Reading progress", "阅读进度"),
            ["L.ThemeSystem"] = ("System", "跟随系统"),
            ["L.ThemeLight"] = ("Light", "浅色"),
            ["L.ThemeDark"] = ("Dark", "深色"),
            ["L.ThemeHighContrast"] = ("High contrast", "高对比度"),
            ["L.ThemeTooltipFormat"] = ("Theme: {0} — click to change", "主题：{0}（点击切换）"),
            ["L.CommandLineHelpTitle"] = ("AirPDF — command line", "AirPDF — 命令行"),
            ["L.CommandLineHelpBody"] = (
                "Usage: AirPDF [options] [file.pdf]\n\n" +
                "  -h, --help      Show this help\n" +
                "      --version   Show the application version\n" +
                "  --              Treat every following argument as a file path\n\n" +
                "AirPDF opens one document per window. When several files are given, " +
                "the first existing one is opened.",
                "用法：AirPDF [选项] [文件.pdf]\n\n" +
                "  -h, --help      显示此帮助\n" +
                "      --version   显示版本号\n" +
                "  --               其后所有参数都按文件路径处理\n\n" +
                "AirPDF 每个窗口只打开一个文档；给出多个文件时，只打开第一个存在的文件。"),
            ["L.CommandLineVersionFormat"] = ("AirPDF {0}", "AirPDF {0}"),
            ["L.ExtraFilesTitle"] = ("Only one document per window", "每个窗口只打开一个文档"),
            ["L.UnknownOptionsFormat"] = (
                "Ignored unrecognised arguments: {0}",
                "已忽略无法识别的参数：{0}"),
            ["L.ExtraFilesFormat"] = (
                "Opened: {0}\n\nIgnored: {1}",
                "已打开：{0}\n\n已忽略：{1}")
        };

    private ResourceDictionary? _dictionary;

    public string Language { get; private set; } = English;

    public bool IsChinese => string.Equals(Language, ChineseSimplified, StringComparison.OrdinalIgnoreCase);

    public string LanguageShortLabel => IsChinese ? "中" : "EN";

    public CultureInfo Culture => IsChinese
        ? CultureInfo.GetCultureInfo("zh-CN")
        : CultureInfo.GetCultureInfo("en-US");

    public event EventHandler? LanguageChanged;

    public void Apply(string? language)
    {
        var normalized = string.Equals(language, ChineseSimplified, StringComparison.OrdinalIgnoreCase) ||
                         (language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ?? false)
            ? ChineseSimplified
            : English;

        var changed = !string.Equals(Language, normalized, StringComparison.Ordinal) || _dictionary is null;
        Language = normalized;

        if (Application.Current is { } application)
        {
            var dictionary = new ResourceDictionary();
            foreach (var pair in Catalog)
            {
                dictionary[pair.Key] = IsChinese ? pair.Value.Zh : pair.Value.En;
            }

            if (_dictionary is not null)
            {
                application.Resources.MergedDictionaries.Remove(_dictionary);
            }

            application.Resources.MergedDictionaries.Add(dictionary);
            _dictionary = dictionary;
        }

        if (changed)
        {
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string T(string key) =>
        Catalog.TryGetValue(key, out var value)
            ? (IsChinese ? value.Zh : value.En)
            : key;

    public string Format(string key, params object[] args) =>
        string.Format(Culture, T(key), args);

    public string GetPdfFailureMessage(PdfFailure failure) => failure switch
    {
        PdfFailure.NotFound => T("L.ErrorNotFound"),
        PdfFailure.AccessDenied => T("L.ErrorAccessDenied"),
        PdfFailure.PasswordRequired => T("L.ErrorPasswordRequired"),
        PdfFailure.InvalidPassword => T("L.ErrorInvalidPassword"),
        PdfFailure.Corrupt => T("L.ErrorCorrupt"),
        PdfFailure.Unsupported => T("L.ErrorUnsupported"),
        _ => T("L.ErrorUnknown")
    };

    /// <summary>Removes this instance's resource dictionary from the application.</summary>
    public void Dispose()
    {
        if (_dictionary is not null && Application.Current is { } application)
        {
            application.Resources.MergedDictionaries.Remove(_dictionary);
        }

        _dictionary = null;
    }
}
