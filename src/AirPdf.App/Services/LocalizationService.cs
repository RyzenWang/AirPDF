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
public sealed class LocalizationService
{
    public const string English = "en";
    public const string ChineseSimplified = "zh-CN";

    private static readonly IReadOnlyDictionary<string, (string En, string Zh)> Catalog =
        new Dictionary<string, (string En, string Zh)>(StringComparer.Ordinal)
        {
            ["L.Open"] = ("Open", "打开"),
            ["L.OpenPdf"] = ("Open a PDF", "打开 PDF"),
            ["L.OpenTooltip"] = ("Open a PDF (Ctrl+O)", "打开 PDF（Ctrl+O）"),
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
            ["L.ToggleTheme"] = ("Toggle light and dark theme", "切换浅色和深色主题"),
            ["L.DocumentDetails"] = ("Document details", "文档详情"),
            ["L.Language"] = ("Language", "语言"),
            ["L.Navigate"] = ("NAVIGATE", "导航"),
            ["L.Pages"] = ("Pages", "页面"),
            ["L.Contents"] = ("Contents", "目录"),
            ["L.NoOutline"] = ("No outline in this PDF", "此 PDF 没有目录"),
            ["L.Tagline"] = ("A FEATHER-LIGHT PDF READER", "轻量 PDF 阅读器"),
            ["L.WelcomeTitle1"] = ("Your PDFs,", "你的 PDF，"),
            ["L.WelcomeTitle2"] = ("without the weight.", "轻一点。"),
            ["L.WelcomeSubtitle"] = (
                "Fast, quiet, and native on Windows, macOS, and Linux. No account, no clutter — just the page in front of you.",
                "在 Windows、macOS 和 Linux 上快速、安静、原生运行。无需账号，没有杂乱，只有眼前的页面。"),
            ["L.WelcomeMeta"] = ("PDFium  ·  .NET 10  ·  MIT licensed", "PDFium  ·  .NET 10  ·  MIT 许可"),
            ["L.Recent"] = ("RECENT", "最近打开"),
            ["L.Clear"] = ("Clear", "清除"),
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
            ["L.ErrorNotFound"] = ("The file does not exist.", "文件不存在。"),
            ["L.ErrorAccessDenied"] = ("You do not have permission to read this file.", "没有权限读取此文件。"),
            ["L.ErrorPasswordRequired"] = ("This document is password protected.", "此文档受密码保护。"),
            ["L.ErrorInvalidPassword"] = ("The password is incorrect.", "密码不正确。"),
            ["L.ErrorCorrupt"] = ("The PDF appears to be damaged.", "此 PDF 似乎已损坏。"),
            ["L.ErrorUnsupported"] = ("This document uses an unsupported security scheme.", "此文档使用了不支持的安全方案。"),
            ["L.ErrorUnknown"] = ("AirPDF could not open this document.", "AirPDF 无法打开此文档。")
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
}
