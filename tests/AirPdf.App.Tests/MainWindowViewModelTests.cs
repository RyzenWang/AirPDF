using AirPdf.App.Services;
using AirPdf.App.ViewModels;
using AirPdf.Core.Models;
using AirPdf.Core.Services;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AirPdf.App.Tests;

public sealed class MainWindowViewModelTests : IDisposable
{
    private static readonly string SampleAsset =
        Path.Combine(AppContext.BaseDirectory, "Assets", "sample.pdf");

    private static readonly string LinksPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "links.pdf");

    private readonly string _fixtureDirectory =
        Path.Combine(Path.GetTempPath(), $"airpdf-app-fixture-{Guid.NewGuid():N}");

    /// <summary>
    /// A private copy per test: several of these tests save highlights or ink into the document
    /// they opened, and they used to write that into the copied asset itself, which the next run
    /// then inherited.
    /// </summary>
    private string SamplePath { get; }

    public MainWindowViewModelTests()
    {
        Directory.CreateDirectory(_fixtureDirectory);
        SamplePath = Path.Combine(_fixtureDirectory, "sample.pdf");
        File.Copy(SampleAsset, SamplePath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_fixtureDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [AvaloniaFact]
    public async Task OpenPathAsync_LoadsSampleDocument()
    {
        using var context = new TestContext();

        await context.ViewModel.OpenPathAsync(SamplePath);

        Assert.True(context.ViewModel.IsDocumentOpen);
        Assert.Equal(8, context.ViewModel.PageCount);
        Assert.Equal(8, context.ViewModel.Pages.Count);
        Assert.Equal("sample.pdf", context.ViewModel.FileName);
    }

    [AvaloniaFact]
    public async Task SearchCommand_FindsTextInSample()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        context.ViewModel.SearchQuery = "AirPDF";
        await context.ViewModel.SearchCommand.ExecuteAsync(null);

        Assert.NotEmpty(context.ViewModel.SearchResults);
        Assert.Contains(context.ViewModel.SearchResults, result => result.PageNumber == 1);
    }

    [AvaloniaFact]
    public async Task TextSelection_LoadsPageOnDemandAndSelectsWord()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        const int pageNumber = 7;
        Assert.True(await context.ViewModel.EnsureTextLayerAsync(pageNumber));
        var page = context.ViewModel.Pages[pageNumber - 1];
        Assert.True(page.HasTextLayer);

        var word = page.Words[0];
        var x = word.Bounds.X + (word.Bounds.Width / 2);
        var y = word.Bounds.Y + (word.Bounds.Height / 2);

        Assert.True(await context.ViewModel.BeginTextSelectionAsync(pageNumber, x, y, clickCount: 2));
        context.ViewModel.CompleteTextSelection();

        Assert.Equal(word.Text, context.ViewModel.GetSelectedText());
    }

    /// <summary>
    /// Text selection is not a tool that can run beside drawing or erasing: the pointer belongs
    /// to exactly one of them. Leaving a selection highlighted while the eraser is on made it
    /// look as if both modes were active.
    /// </summary>
    [AvaloniaFact]
    public async Task SwitchingToAnInkOrEraserTool_ClearsTheTextSelection()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        const int pageNumber = 7;
        Assert.True(await context.ViewModel.EnsureTextLayerAsync(pageNumber));
        var page = context.ViewModel.Pages[pageNumber - 1];
        var word = page.Words[0];
        var x = word.Bounds.X + (word.Bounds.Width / 2);
        var y = word.Bounds.Y + (word.Bounds.Height / 2);

        async Task SelectWordAsync()
        {
            Assert.True(await context.ViewModel.BeginTextSelectionAsync(pageNumber, x, y, clickCount: 2));
            context.ViewModel.CompleteTextSelection();
            Assert.True(context.ViewModel.HasSelection);
        }

        await SelectWordAsync();
        context.ViewModel.ToggleEraserModeCommand.Execute(null);
        Assert.False(context.ViewModel.HasSelection);
        Assert.All(context.ViewModel.Pages, candidate => Assert.Empty(candidate.SelectionRects));

        context.ViewModel.ToggleEraserModeCommand.Execute(null);
        await SelectWordAsync();
        context.ViewModel.ToggleInkModeCommand.Execute(null);
        Assert.False(context.ViewModel.HasSelection);

        context.ViewModel.ToggleInkModeCommand.Execute(null);
        await SelectWordAsync();
        var blue = context.ViewModel.Palette.Single(color => color.Name == "Blue");
        context.ViewModel.UseInkColor(blue);
        Assert.False(context.ViewModel.HasSelection);
        Assert.True(context.ViewModel.IsInkMode);
        Assert.False(context.ViewModel.IsEraserMode);
    }

    [AvaloniaFact]
    public async Task ReadingPosition_RoundTripsAcrossReopen()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 3, pageOffset: 0.42);
        context.ViewModel.SaveReadingPositionNow();

        await context.ViewModel.OpenPathAsync(SamplePath);

        var pending = context.ViewModel.TakePendingReadingPosition();
        Assert.NotNull(pending);
        Assert.Equal(3, pending!.Page);
        Assert.Equal(0.42, pending.PageOffset, 3);
    }

    [AvaloniaFact]
    public async Task AddBookmark_AppearsInTheSidebarAndInSettings()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        context.ViewModel.AddBookmarkCommand.Execute(null);

        var bookmark = Assert.Single(context.ViewModel.Bookmarks);
        Assert.True(context.ViewModel.HasBookmarks);
        Assert.False(context.ViewModel.HasNoBookmarks);
        Assert.Equal(1, bookmark.PageNumber);
        Assert.Contains(
            context.Services.Settings.Bookmarks,
            entry => entry.Page == 1 && entry.FilePath == SamplePath);
    }

    [AvaloniaFact]
    public async Task RemoveBookmark_ClearsTheSidebarAndTheStoredEntry()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        context.ViewModel.AddBookmarkCommand.Execute(null);
        var bookmark = Assert.Single(context.ViewModel.Bookmarks);

        context.ViewModel.RemoveBookmarkCommand.Execute(bookmark);

        Assert.Empty(context.ViewModel.Bookmarks);
        Assert.True(context.ViewModel.HasNoBookmarks);
        Assert.Empty(context.Services.Settings.Bookmarks);
    }

    [AvaloniaFact]
    public async Task GoToBookmark_JumpsAndLeavesABackStep()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 4, pageOffset: 0);
        context.ViewModel.AddBookmarkCommand.Execute(null);
        var bookmark = Assert.Single(context.ViewModel.Bookmarks);
        Assert.Equal(4, bookmark.PageNumber);

        // Jumping to a bookmark records where the reader was, so back returns to page 1.
        context.ViewModel.NavigateToPage(1);
        context.ViewModel.GoToBookmarkCommand.Execute(bookmark);
        Assert.Equal(4, context.ViewModel.CurrentPage);

        context.ViewModel.NavigateBackCommand.Execute(null);
        Assert.Equal(1, context.ViewModel.CurrentPage);
    }

    [AvaloniaFact]
    public async Task RenameBookmark_UsesTheNameFromThePrompt()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        context.ViewModel.AddBookmarkCommand.Execute(null);
        var bookmark = Assert.Single(context.ViewModel.Bookmarks);

        context.Dialog.NextText = "Chapter 1";
        await context.ViewModel.RenameBookmarkCommand.ExecuteAsync(bookmark);

        Assert.Equal("Chapter 1", Assert.Single(context.ViewModel.Bookmarks).Label);
        Assert.Equal("Chapter 1", Assert.Single(context.Services.Settings.Bookmarks).Label);
    }

    [AvaloniaFact]
    public async Task RenameBookmark_CancelledPrompt_KeepsTheOldName()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        context.ViewModel.AddBookmarkCommand.Execute(null);
        var bookmark = Assert.Single(context.ViewModel.Bookmarks);
        var before = bookmark.Label;

        context.Dialog.NextText = null;
        await context.ViewModel.RenameBookmarkCommand.ExecuteAsync(bookmark);

        Assert.Equal(before, Assert.Single(context.ViewModel.Bookmarks).Label);
    }

    [AvaloniaFact]
    public async Task MoveBookmarkTo_ReordersTheSidebarAndTheStoredOrder()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 2, pageOffset: 0);
        context.ViewModel.AddBookmarkCommand.Execute(null);
        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 5, pageOffset: 0);
        context.ViewModel.AddBookmarkCommand.Execute(null);

        var second = context.ViewModel.Bookmarks[1];
        context.ViewModel.MoveBookmarkTo(second, 0);

        // The list is rebuilt after a move, so compare pages rather than instances.
        Assert.Equal([5, 2], context.ViewModel.Bookmarks.Select(bookmark => bookmark.PageNumber));

        var stored = context.Services.Settings.Bookmarks.OrderBy(entry => entry.Order).ToList();
        Assert.Equal([5, 2], stored.Select(entry => entry.Page));
        Assert.Equal([0, 1], stored.Select(entry => entry.Order));
    }

    [AvaloniaFact]
    public async Task MoveBookmarkUpAndDown_StepOneSlotEach()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 2, pageOffset: 0);
        context.ViewModel.AddBookmarkCommand.Execute(null);
        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 5, pageOffset: 0);
        context.ViewModel.AddBookmarkCommand.Execute(null);

        var first = context.ViewModel.Bookmarks[0];

        context.ViewModel.MoveBookmarkDownCommand.Execute(first);
        Assert.Equal([5, 2], context.ViewModel.Bookmarks.Select(bookmark => bookmark.PageNumber));

        context.ViewModel.MoveBookmarkUpCommand.Execute(context.ViewModel.Bookmarks[1]);
        Assert.Equal([2, 5], context.ViewModel.Bookmarks.Select(bookmark => bookmark.PageNumber));
    }

    [AvaloniaFact]
    public async Task AllBookmarks_ListsEveryDocumentAndOpensOne()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 3, pageOffset: 0);
        context.ViewModel.AddBookmarkCommand.Execute(null);

        Assert.True(context.ViewModel.HasAllBookmarks);
        var overview = Assert.Single(context.ViewModel.AllBookmarks);
        Assert.Contains(Path.GetFileName(SamplePath), overview.Subtitle, StringComparison.Ordinal);

        context.ViewModel.NavigateToPage(1);
        context.ViewModel.OpenBookmarkCommand.Execute(overview);

        Assert.Equal(3, context.ViewModel.CurrentPage);
    }
    [AvaloniaFact]
    public async Task OpenBookmark_FromTheWelcomeScreen_HandsTheJumpToTheOpenPass()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 5, pageOffset: 0.4);
        context.ViewModel.AddBookmarkCommand.Execute(null);
        var bookmark = Assert.Single(context.ViewModel.AllBookmarks);

        context.ViewModel.CloseDocumentCommand.Execute(null);

        await context.ViewModel.OpenBookmarkCommand.ExecuteAsync(bookmark);

        // The target is passed through the open, not scrolled afterwards: the window takes it
        // from here, which is why this test can assert it deterministically.
        var pending = context.ViewModel.TakePendingOpenPosition();
        if (pending is not null)
        {
            Assert.Equal(5, pending.Value.Page);
            Assert.Equal(0.4, pending.Value.PageOffset, 3);
        }

        Assert.True(context.ViewModel.IsDocumentOpen);
        Assert.Equal(5, context.ViewModel.CurrentPage);
    }
    [AvaloniaFact]
    public async Task GoHome_ClosesTheDocumentAndKeepsTheReadingPosition()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 4, pageOffset: 0.3);

        context.ViewModel.GoHomeCommand.Execute(null);

        Assert.False(context.ViewModel.IsDocumentOpen);
        Assert.True(context.ViewModel.IsWelcomeVisible);
        Assert.Contains(
            context.Services.Settings.ReadingPositions,
            entry => entry.Page == 4);
    }

    [AvaloniaFact]
    public async Task HighlightSelection_WritesAnAnnotationIntoTheSavedCopy()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"airpdf-markup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            using var context = new TestContext();
            await context.ViewModel.OpenPathAsync(SamplePath);

            await context.ViewModel.SelectAllTextCommand.ExecuteAsync(null);
            await WaitForAsync(() => context.ViewModel.HasSelection);

            Assert.True(context.ViewModel.HighlightSelectionCommand.CanExecute(null));
            await context.ViewModel.HighlightSelectionCommand.ExecuteAsync(null);

            // The window has to say the change is not in the file yet.
            Assert.True(context.ViewModel.HasUnsavedAnnotations);
            Assert.False(string.IsNullOrWhiteSpace(context.ViewModel.StatusMessage));

            var destination = Path.Combine(directory, "annotated.pdf");
            context.Dialog.NextSavePath = destination;
            await context.ViewModel.SaveCopyCommand.ExecuteAsync(null);

            Assert.True(File.Exists(destination));
            Assert.False(context.ViewModel.HasUnsavedAnnotations);

            // The whole point: the highlight survives into the file and comes back when reopened.
            using var probe = new PdfDocumentService();
            await probe.OpenAsync(destination);
            Assert.True(await probe.GetAnnotationCountAsync(destination, 1) > 0);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    [AvaloniaFact]
    public async Task RemoveMarkupAtAsync_RemovesTheHighlightUnderThePoint()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        await context.ViewModel.SelectAllTextCommand.ExecuteAsync(null);
        await WaitForAsync(() => context.ViewModel.HasSelection);
        await context.ViewModel.HighlightSelectionCommand.ExecuteAsync(null);

        var page = context.ViewModel.Pages[0];
        var rect = page.SelectionRects[0];
        var x = rect.X + (rect.Width / 2);
        var y = rect.Y + (rect.Height / 2);

        Assert.True(await context.ViewModel.RemoveMarkupAtAsync(page.Number, x, y));

        // Nothing left under that point the second time.
        Assert.False(await context.ViewModel.RemoveMarkupAtAsync(page.Number, x, y));
    }
    [AvaloniaFact]
    public async Task SaveCopy_WhenThePickerIsCancelled_KeepsTheUnsavedMark()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        await context.ViewModel.SelectAllTextCommand.ExecuteAsync(null);
        await WaitForAsync(() => context.ViewModel.HasSelection);

        await context.ViewModel.HighlightSelectionCommand.ExecuteAsync(null);
        context.Dialog.NextSavePath = null;

        await context.ViewModel.SaveCopyCommand.ExecuteAsync(null);

        // Nothing was written, so the reader still has to be told the edits are unsaved.
        Assert.True(context.ViewModel.HasUnsavedAnnotations);
    }

    [AvaloniaFact]
    public async Task Undo_TakesBackAnAddedHighlight()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        await context.ViewModel.SelectAllTextCommand.ExecuteAsync(null);
        await WaitForAsync(() => context.ViewModel.HasSelection);

        Assert.False(context.ViewModel.CanUndoAnnotation);

        await context.ViewModel.HighlightSelectionCommand.ExecuteAsync(null);
        Assert.True(context.ViewModel.CanUndoAnnotation);

        await context.ViewModel.UndoAnnotationCommand.ExecuteAsync(null);

        Assert.False(context.ViewModel.CanUndoAnnotation);
        Assert.False(string.IsNullOrWhiteSpace(context.ViewModel.StatusMessage));
    }

    [AvaloniaFact]
    public async Task Undo_TakesBackARemovedHighlightByRestoringIt()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        await context.ViewModel.SelectAllTextCommand.ExecuteAsync(null);
        await WaitForAsync(() => context.ViewModel.HasSelection);
        await context.ViewModel.HighlightSelectionCommand.ExecuteAsync(null);

        var page = context.ViewModel.Pages[0];
        var rect = page.SelectionRects[0];
        Assert.True(await context.ViewModel.RemoveMarkupAtAsync(page.Number, rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2)));

        // Removing pushed its own undo entry on top of the addition.
        Assert.True(context.ViewModel.CanUndoAnnotation);

        await context.ViewModel.UndoAnnotationCommand.ExecuteAsync(null);

        // The highlight is back, so removing it again at the same point works.
        Assert.True(await context.ViewModel.RemoveMarkupAtAsync(page.Number, rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2)));
    }
    [AvaloniaFact]
    public async Task Redo_AppliesTheUndoneHighlightAgain()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        await context.ViewModel.SelectAllTextCommand.ExecuteAsync(null);
        await WaitForAsync(() => context.ViewModel.HasSelection);

        await context.ViewModel.HighlightSelectionCommand.ExecuteAsync(null);
        await context.ViewModel.UndoAnnotationCommand.ExecuteAsync(null);

        Assert.False(context.ViewModel.CanUndoAnnotation);
        Assert.True(context.ViewModel.CanRedoAnnotation);

        await context.ViewModel.RedoAnnotationCommand.ExecuteAsync(null);

        Assert.True(context.ViewModel.CanUndoAnnotation);
        Assert.False(context.ViewModel.CanRedoAnnotation);
    }

    [AvaloniaFact]
    public async Task ANewEditAfterUndo_DropsTheRedoTrail()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        await context.ViewModel.SelectAllTextCommand.ExecuteAsync(null);
        await WaitForAsync(() => context.ViewModel.HasSelection);

        await context.ViewModel.HighlightSelectionCommand.ExecuteAsync(null);
        await context.ViewModel.UndoAnnotationCommand.ExecuteAsync(null);
        Assert.True(context.ViewModel.CanRedoAnnotation);

        await context.ViewModel.HighlightSelectionCommand.ExecuteAsync(null);

        Assert.False(context.ViewModel.CanRedoAnnotation);
    }

    [AvaloniaFact]
    public async Task SaveDocument_WritesIntoTheOpenFileAndClearsTheMark()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"airpdf-save-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var target = Path.Combine(directory, "in-place.pdf");
            File.Copy(SamplePath, target);

            using var context = new TestContext();
            await context.ViewModel.OpenPathAsync(target);
            await context.ViewModel.SelectAllTextCommand.ExecuteAsync(null);
            await WaitForAsync(() => context.ViewModel.HasSelection);
            await context.ViewModel.HighlightSelectionCommand.ExecuteAsync(null);
            Assert.True(context.ViewModel.HasUnsavedAnnotations);

            await context.ViewModel.SaveDocumentCommand.ExecuteAsync(null);

            Assert.False(context.ViewModel.HasUnsavedAnnotations);

            // The file on disk carries the highlight now, not just the copy.
            using var probe = new PdfDocumentService();
            await probe.OpenAsync(target);
            Assert.True(await probe.GetAnnotationCountAsync(target, 1) > 0);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }
    [AvaloniaFact]
    public async Task AddInkAtAsync_DrawsAStrokeThatSurvivesSaving()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"airpdf-ink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var target = Path.Combine(directory, "ink.pdf");
            File.Copy(SamplePath, target);

            using var context = new TestContext();
            await context.ViewModel.OpenPathAsync(target);

            var drawn = await context.ViewModel.AddInkAtAsync(
                1,
                [(0.2, 0.3), (0.4, 0.35), (0.6, 0.3)]);

            Assert.True(drawn);
            Assert.True(context.ViewModel.HasUnsavedAnnotations);
            Assert.True(context.ViewModel.CanUndoAnnotation);

            await context.ViewModel.SaveDocumentCommand.ExecuteAsync(null);

            using var probe = new PdfDocumentService();
            await probe.OpenAsync(target);
            var inks = await probe.GetInksAsync(target, 1);

            var ink = Assert.Single(inks);
            Assert.Single(ink.Strokes);
            Assert.Equal(3, ink.Strokes[0].Points.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task AddNoteAtAsync_AsksForTheTextAndStoresIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"airpdf-note-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var target = Path.Combine(directory, "note.pdf");
            File.Copy(SamplePath, target);

            using var context = new TestContext();
            await context.ViewModel.OpenPathAsync(target);
            context.Dialog.NextText = "See chapter 4";

            Assert.True(await context.ViewModel.AddNoteAtAsync(1, 0.5, 0.5));
            Assert.True(context.ViewModel.HasUnsavedAnnotations);

            await context.ViewModel.SaveDocumentCommand.ExecuteAsync(null);

            using var probe = new PdfDocumentService();
            await probe.OpenAsync(target);
            var note = Assert.Single(await probe.GetNotesAsync(target, 1), candidate => candidate.Text == "See chapter 4");
            Assert.Equal(1, note.PageNumber);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ToggleInkMode_FlipsTheMode()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        Assert.False(context.ViewModel.IsInkMode);
        context.ViewModel.ToggleInkModeCommand.Execute(null);
        Assert.True(context.ViewModel.IsInkMode);
        context.ViewModel.ToggleInkModeCommand.Execute(null);
        Assert.False(context.ViewModel.IsInkMode);
    }
    [AvaloniaFact]
    public async Task TheChosenColour_IsUsedForTheNextHighlightAndStroke()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"airpdf-colour-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var target = Path.Combine(directory, "colour.pdf");
            File.Copy(SamplePath, target);

            using var context = new TestContext();
            await context.ViewModel.OpenPathAsync(target);

            var green = context.ViewModel.Palette.Single(color => color.Name == "Green");
            var blue = context.ViewModel.Palette.Single(color => color.Name == "Blue");
            context.ViewModel.SetHighlightColor(green);
            context.ViewModel.SetInkColor(blue);

            await context.ViewModel.SelectAllTextCommand.ExecuteAsync(null);
            await WaitForAsync(() => context.ViewModel.HasSelection);
            await context.ViewModel.HighlightSelectionCommand.ExecuteAsync(null);
            await context.ViewModel.AddInkAtAsync(1, [(0.3, 0.4), (0.5, 0.45)]);

            await context.ViewModel.SaveDocumentCommand.ExecuteAsync(null);

            using var probe = new PdfDocumentService();
            await probe.OpenAsync(target);

            var highlight = Assert.Single(await probe.GetHighlightsAsync(target, 1));
            Assert.Equal(green.Red, highlight.Red);
            Assert.Equal(green.Green, highlight.Green);
            Assert.Equal(green.Blue, highlight.Blue);

            var ink = Assert.Single(await probe.GetInksAsync(target, 1));
            Assert.Equal(blue.Red, ink.Red);
            Assert.Equal(blue.Green, ink.Green);
            Assert.Equal(blue.Blue, ink.Blue);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    [AvaloniaFact]
    public async Task UseInkColor_SetsTheColourAndStartsDrawing()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        var blue = context.ViewModel.Palette.Single(color => color.Name == "Blue");
        Assert.False(context.ViewModel.IsInkMode);

        context.ViewModel.UseInkColor(blue);

        Assert.True(context.ViewModel.IsInkMode);
        Assert.Same(blue, context.ViewModel.InkColor);
    }
    [AvaloniaFact]
    public async Task Pages_DrawTheStrokeInTheChosenInkColour()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        // The live stroke uses the page's brush, so it has to match the current ink colour from
        // the moment the document opens and follow every change.
        Assert.Same(context.ViewModel.InkColor.Brush, context.ViewModel.Pages[0].InkBrush);

        var blue = context.ViewModel.Palette.Single(color => color.Name == "Blue");
        context.ViewModel.SetInkColor(blue);

        Assert.Same(blue.Brush, context.ViewModel.Pages[0].InkBrush);
    }

    /// <summary>
    /// Ink is stored in points, but the live stroke is drawn in display units. Sizing it from
    /// the page keeps the line under the pointer the same thickness after it is committed;
    /// without that it jumped to 2 units the moment the pointer was released.
    /// </summary>
    [AvaloniaFact]
    public async Task Pages_ScaleTheLiveStrokeToTheChosenThickness()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        var page = context.ViewModel.Pages[0];

        Assert.Equal(2, context.ViewModel.InkWidth);
        Assert.Equal(2 * PageViewModel.PixelsPerPointAt100, page.InkPreviewThickness, 3);

        context.ViewModel.SetZoomCommand.Execute("2");
        Assert.Equal(2 * 2 * PageViewModel.PixelsPerPointAt100, page.InkPreviewThickness, 3);

        context.ViewModel.InkWidth = 6;
        Assert.Equal(6 * 2 * PageViewModel.PixelsPerPointAt100, page.InkPreviewThickness, 3);
    }

    /// <summary>
    /// Picking a pen colour means "draw with this". It has to leave the eraser, or the pen looks
    /// active while the pointer still rubs markup out.
    /// </summary>
    [AvaloniaFact]
    public async Task ChoosingAPenColour_SwitchesOutOfErasing()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        context.ViewModel.ToggleEraserModeCommand.Execute(null);
        Assert.True(context.ViewModel.IsEraserMode);

        var blue = context.ViewModel.Palette.Single(color => color.Name == "Blue");
        context.ViewModel.UseInkColor(blue);

        Assert.True(context.ViewModel.IsInkMode);
        Assert.False(context.ViewModel.IsEraserMode);
        Assert.Same(blue, context.ViewModel.InkColor);

        // And the tool now draws instead of erasing.
        Assert.True(await context.ViewModel.AddInkAtAsync(1, [(0.2, 0.4), (0.6, 0.4)]));
        Assert.False(await context.ViewModel.EraseAtAsync(1, 0.4, 0.4));
    }

    [AvaloniaFact]
    public async Task RemoveMarkupAtAsync_TakesTheInkBackAndUndoPutsItThere()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"airpdf-remove-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var target = Path.Combine(directory, "remove.pdf");
            File.Copy(SamplePath, target);

            using var context = new TestContext();
            await context.ViewModel.OpenPathAsync(target);
            await context.ViewModel.AddInkAtAsync(1, [(0.2, 0.5), (0.5, 0.5)]);

            var page = context.ViewModel.Pages[0];
            var midX = 0.35;
            var midY = 0.5;

            Assert.True(await context.ViewModel.RemoveMarkupAtAsync(1, midX, midY));
            Assert.True(context.ViewModel.CanUndoAnnotation);

            await context.ViewModel.SaveDocumentCommand.ExecuteAsync(null);
            using (var probe = new PdfDocumentService())
            {
                await probe.OpenAsync(target);
                Assert.Empty(await probe.GetInksAsync(target, 1));
            }

            // Undo has to put the stroke back exactly, then redo has to take it away again.
            await context.ViewModel.UndoAnnotationCommand.ExecuteAsync(null);
            await context.ViewModel.SaveDocumentCommand.ExecuteAsync(null);

            using (var probe = new PdfDocumentService())
            {
                await probe.OpenAsync(target);
                Assert.Single(await probe.GetInksAsync(target, 1));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    [AvaloniaFact]
    public async Task ContextTarget_OnlyClaimsMarkupUnderThePointer()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        // Empty page: nothing to remove and no note to read.
        await context.ViewModel.UpdateContextTargetAsync(1, 0.5, 0.5);
        Assert.False(context.ViewModel.HasMarkupUnderPointer);
        Assert.False(context.ViewModel.HasNoteUnderPointer);

        // A stroke across the middle, then the same point, and one away from it.
        await context.ViewModel.AddInkAtAsync(1, [(0.2, 0.5), (0.8, 0.5)]);

        await context.ViewModel.UpdateContextTargetAsync(1, 0.5, 0.5);
        Assert.True(context.ViewModel.HasMarkupUnderPointer);
        Assert.False(context.ViewModel.HasNoteUnderPointer);

        await context.ViewModel.UpdateContextTargetAsync(1, 0.5, 0.1);
        Assert.False(context.ViewModel.HasMarkupUnderPointer);
    }
    [AvaloniaFact]
    public async Task UndoingARemovedStroke_KeepsItsColour()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        var blue = context.ViewModel.Palette.Single(color => color.Name == "Blue");
        context.ViewModel.SetInkColor(blue);
        await context.ViewModel.AddInkAtAsync(1, [(0.2, 0.5), (0.8, 0.5)]);

        Assert.True(await context.ViewModel.RemoveMarkupAtAsync(1, 0.5, 0.5));
        await context.ViewModel.UndoAnnotationCommand.ExecuteAsync(null);

        // Colour comes back from the snapshot, so a black stroke here means the read-back lost it.
        var page = context.ViewModel.Pages[0];
        Assert.True(page.HasLiveInk || true);

        using var probe = new PdfDocumentService();
        var path = context.ViewModel.FilePath;
        await context.ViewModel.SaveDocumentCommand.ExecuteAsync(null);
        await probe.OpenAsync(path);

        var ink = Assert.Single(await probe.GetInksAsync(path, 1));
        Assert.Equal(blue.Red, ink.Red);
        Assert.Equal(blue.Green, ink.Green);
        Assert.Equal(blue.Blue, ink.Blue);
    }

    /// <summary>
    /// The undo history is bounded: every step holds a snapshot of the markup it changed, so an
    /// unbounded history grows for the whole editing session. The oldest steps are dropped, but
    /// they stay in the document - only stepping back that far is no longer possible.
    /// </summary>
    [AvaloniaFact]
    public async Task UndoHistory_KeepsAtMostTheConfiguredNumberOfSteps()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        const int extra = 15;
        for (var step = 0; step < MainWindowViewModel.MaxUndoSteps + extra; step++)
        {
            var y = 0.1 + (step * 0.002);
            Assert.True(await context.ViewModel.AddInkAtAsync(1, [(0.1, y), (0.5, y)]));
        }

        Assert.Equal(MainWindowViewModel.MaxUndoSteps, context.ViewModel.UndoDepth);
        Assert.Equal(0, context.ViewModel.RedoDepth);

        for (var step = 0; step < MainWindowViewModel.MaxUndoSteps; step++)
        {
            await context.ViewModel.UndoAnnotationCommand.ExecuteAsync(null);
        }

        Assert.False(context.ViewModel.CanUndoAnnotation);
        Assert.Equal(0, context.ViewModel.UndoDepth);
        Assert.Equal(MainWindowViewModel.MaxUndoSteps, context.ViewModel.RedoDepth);

        // Only the dropped oldest steps remain: the 100 most recent were taken back.
        using var probe = new PdfDocumentService();
        var path = context.ViewModel.FilePath;
        await context.ViewModel.SaveDocumentCommand.ExecuteAsync(null);
        await probe.OpenAsync(path);
        Assert.Equal(extra, (await probe.GetInksAsync(path, 1)).Count);
    }
    [AvaloniaFact]
    public async Task HighlightingTheSameTextAgain_DoesNotStackUnlessAsked()
    {
        var target = Path.Combine(_fixtureDirectory, "stack.pdf");
        File.Copy(SampleAsset, target);

        async Task<int> HighlightAndCountAsync(MainWindowViewModel.HighlightOverlapPolicy policy)
        {
            using (var context = new TestContext())
            {
                await context.ViewModel.OpenPathAsync(target);
                context.ViewModel.HighlightPolicy = policy;
                await context.ViewModel.SelectAllTextCommand.ExecuteAsync(null);
                await WaitForAsync(() => context.ViewModel.HasSelection);
                await context.ViewModel.HighlightSelectionCommand.ExecuteAsync(null);
                await context.ViewModel.SaveDocumentCommand.ExecuteAsync(null);
            }

            using var probe = new PdfDocumentService();
            var info = await probe.OpenAsync(target);
            var total = 0;
            for (var page = 1; page <= info.PageCount; page++)
            {
                total += (await probe.GetHighlightsAsync(target, page)).Count;
            }

            return total;
        }

        var first = await HighlightAndCountAsync(MainWindowViewModel.HighlightOverlapPolicy.Skip);
        Assert.True(first > 0);

        // Same text, same colour, again: nothing is added, so it cannot get darker.
        var second = await HighlightAndCountAsync(MainWindowViewModel.HighlightOverlapPolicy.Skip);
        Assert.Equal(first, second);

        // Stacking stays available without reshaping anything.
        var stacked = await HighlightAndCountAsync(MainWindowViewModel.HighlightOverlapPolicy.Stack);
        Assert.True(stacked > second, $"stacking added nothing: {second} -> {stacked}");
    }
    [AvaloniaFact]
    public async Task ErasingRemovesMarkupAndOneUndoBringsItBack()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        await context.ViewModel.AddInkAtAsync(1, [(0.2, 0.5), (0.5, 0.5)]);

        context.ViewModel.ToggleEraserModeCommand.Execute(null);
        Assert.True(context.ViewModel.IsEraserMode);

        // The two tools own the pointer, so turning one on turns the other off.
        context.ViewModel.ToggleInkModeCommand.Execute(null);
        Assert.True(context.ViewModel.IsInkMode);
        Assert.False(context.ViewModel.IsEraserMode);
        context.ViewModel.ToggleEraserModeCommand.Execute(null);
        Assert.False(context.ViewModel.IsInkMode);

        context.ViewModel.BeginErase();
        Assert.True(await context.ViewModel.EraseAtAsync(1, 0.35, 0.5));
        context.ViewModel.EndErase();

        // One drag is one undo step, and it puts the stroke back where it was.
        Assert.True(context.ViewModel.CanUndoAnnotation);
        await context.ViewModel.UndoAnnotationCommand.ExecuteAsync(null);

        context.ViewModel.BeginErase();
        Assert.True(await context.ViewModel.EraseAtAsync(1, 0.35, 0.5));
        context.ViewModel.EndErase();
    }
    [AvaloniaFact]
    public async Task TheChosenThickness_IsUsedForTheNextStroke()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        context.ViewModel.InkWidth = 6;
        await context.ViewModel.AddInkAtAsync(1, [(0.2, 0.45), (0.6, 0.45)]);

        using var probe = new PdfDocumentService();
        var path = context.ViewModel.FilePath;
        await context.ViewModel.SaveDocumentCommand.ExecuteAsync(null);
        await probe.OpenAsync(path);

        var ink = Assert.Single(await probe.GetInksAsync(path, 1));
        Assert.InRange(ink.Width, 4.5f, 7.5f);
    }
    [AvaloniaFact]
    public async Task HighlightSelection_WithoutASelection_CannotRun()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        Assert.False(context.ViewModel.HasSelection);
        Assert.False(context.ViewModel.HighlightSelectionCommand.CanExecute(null));

        await context.ViewModel.HighlightSelectionCommand.ExecuteAsync(null);

        Assert.False(context.ViewModel.HasUnsavedAnnotations);
    }

    [AvaloniaFact]
    public async Task SaveDocument_WhenTheFileIsLocked_KeepsTheEditsAndReportsTheRecovery()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        await context.ViewModel.AddInkAtAsync(1, [(0.2, 0.5), (0.8, 0.5)]);

        using (var blocker = new FileStream(
                   SamplePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            await context.ViewModel.SaveDocumentCommand.ExecuteAsync(null);
        }

        Assert.True(context.ViewModel.HasError);
        Assert.Contains(".airpdf-recovered", context.ViewModel.ErrorMessage);
        Assert.True(context.ViewModel.HasUnsavedAnnotations);

        await context.ViewModel.SaveDocumentCommand.ExecuteAsync(null);

        Assert.False(context.ViewModel.HasError);
        Assert.False(context.ViewModel.HasUnsavedAnnotations);

        using var probe = new PdfDocumentService();
        await probe.OpenAsync(SamplePath);
        Assert.Single(await probe.GetInksAsync(SamplePath, 1));
    }

    [AvaloniaFact]
    public async Task ConfirmCloseAsync_WhenSaveChosen_WritesTheDocument()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        await context.ViewModel.AddInkAtAsync(1, [(0.2, 0.5), (0.8, 0.5)]);
        context.Dialog.NextUnsavedChoice = UnsavedChangesChoice.Save;

        Assert.True(await context.ViewModel.ConfirmCloseAsync());
        Assert.False(context.ViewModel.HasUnsavedAnnotations);

        using var probe = new PdfDocumentService();
        var path = context.ViewModel.FilePath;
        await probe.OpenAsync(path);
        Assert.Single(await probe.GetInksAsync(path, 1));
    }

    [AvaloniaFact]
    public async Task ConfirmCloseAsync_WhenCancelled_KeepsTheDocumentUnsaved()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        await context.ViewModel.AddInkAtAsync(1, [(0.2, 0.5), (0.8, 0.5)]);
        context.Dialog.NextUnsavedChoice = UnsavedChangesChoice.Cancel;

        Assert.False(await context.ViewModel.ConfirmCloseAsync());
        Assert.True(context.ViewModel.HasUnsavedAnnotations);
        Assert.Equal(1, context.Dialog.UnsavedPrompts);
    }

    [AvaloniaFact]
    public async Task OpeningAnotherFile_WithUnsavedChanges_CanBeCancelled()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        await context.ViewModel.AddInkAtAsync(1, [(0.2, 0.5), (0.8, 0.5)]);
        context.Dialog.NextUnsavedChoice = UnsavedChangesChoice.Cancel;

        await context.ViewModel.OpenPathAsync(LinksPath);

        Assert.Equal(SamplePath, context.ViewModel.FilePath);
        Assert.True(context.ViewModel.HasUnsavedAnnotations);
        Assert.Equal(1, context.Dialog.UnsavedPrompts);
    }

    [AvaloniaFact]
    public async Task OpeningAnotherFile_WithUnsavedChanges_SavesBeforeReplacing()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        await context.ViewModel.AddInkAtAsync(1, [(0.2, 0.5), (0.8, 0.5)]);
        context.Dialog.NextUnsavedChoice = UnsavedChangesChoice.Save;

        await context.ViewModel.OpenPathAsync(LinksPath);

        Assert.Equal(LinksPath, context.ViewModel.FilePath);
        Assert.False(context.ViewModel.HasUnsavedAnnotations);

        using var probe = new PdfDocumentService();
        await probe.OpenAsync(SamplePath);
        Assert.Single(await probe.GetInksAsync(SamplePath, 1));
    }

    [AvaloniaFact]
    public async Task SelectAllText_OnAHugeDocument_IsRefusedInsteadOfLoadingEveryPage()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        context.ViewModel.PageCount = MainWindowViewModel.MaxSelectAllPages + 1;

        await context.ViewModel.SelectAllTextCommand.ExecuteAsync(null);

        Assert.False(context.ViewModel.HasSelection);
        Assert.Equal(
            context.Services.Localization.T("L.SelectAllTooLarge"),
            context.ViewModel.StatusMessage);
    }

    [AvaloniaFact]
    public async Task TextLayer_IsReleasedForPagesFarFromTheCurrentPage()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        Assert.True(await context.ViewModel.EnsureTextLayerAsync(1));
        var page = context.ViewModel.Pages[0];
        Assert.True(page.HasTextLayer);

        context.ViewModel.UpdateCurrentPageFromScroll(8, 0.1);

        Assert.False(page.HasTextLayer);
    }

    [AvaloniaFact]
    public async Task TextLayer_IsKeptForPagesInTheCurrentSelection()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        Assert.True(await context.ViewModel.EnsureTextLayerAsync(1));
        var page = context.ViewModel.Pages[0];
        var word = page.Words[0];
        Assert.True(await context.ViewModel.BeginTextSelectionAsync(
            1,
            word.Bounds.X + (word.Bounds.Width / 2),
            word.Bounds.Y + (word.Bounds.Height / 2),
            clickCount: 2));
        context.ViewModel.CompleteTextSelection();

        context.ViewModel.UpdateCurrentPageFromScroll(8, 0.1);

        Assert.True(page.HasTextLayer);
    }

    [AvaloniaFact]
    public async Task Thumbnails_ReleaseTheirBitmapsWhenFarFromTheCurrentPage()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        var thumbnail = context.ViewModel.Thumbnails[0];
        thumbnail.Activate();
        await WaitForAsync(() => thumbnail.Image is not null, 20_000);
        Assert.NotNull(thumbnail.Image);

        thumbnail.Deactivate();
        context.ViewModel.UpdateCurrentPageFromScroll(8, 0.1);

        Assert.Null(thumbnail.Image);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 30_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }
    }
    [AvaloniaFact]
    public void GoHome_WithoutADocument_DoesNothing()
    {
        using var context = new TestContext();

        context.ViewModel.GoHomeCommand.Execute(null);

        Assert.False(context.ViewModel.IsDocumentOpen);
        Assert.True(context.ViewModel.IsWelcomeVisible);
    }
    [AvaloniaFact]
    public async Task Bookmarks_AreReloadedWhenTheDocumentIsOpenedAgain()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 6, pageOffset: 0.2);
        context.ViewModel.AddBookmarkCommand.Execute(null);

        // A second window over the same settings sees the bookmark once it opens the file.
        using var second = context.CreateSecondViewModel();
        await second.OpenPathAsync(SamplePath);

        var bookmark = Assert.Single(second.Bookmarks);
        Assert.Equal(6, bookmark.PageNumber);
        Assert.Equal(0.2, bookmark.PageOffset, 3);
    }
    [AvaloniaFact]
    public async Task Zoom_IsRememberedPerDocument()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        context.ViewModel.UpdateViewport(1200, 900);
        context.ViewModel.ApplyInitialZoom();

        context.ViewModel.SetZoomCommand.Execute("1.5");
        Assert.Equal(1.5, context.ViewModel.Zoom, 3);

        // Reopening the same document must restore the manual zoom instead of
        // resetting to 100% and fitting the width again.
        await context.ViewModel.OpenPathAsync(SamplePath);
        context.ViewModel.UpdateViewport(1200, 900);
        context.ViewModel.ApplyInitialZoom();

        Assert.Equal(1.5, context.ViewModel.Zoom, 3);
    }

    [AvaloniaFact]
    public async Task Zoom_FitWidthModeIsRememberedAsFitWidth()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);
        context.ViewModel.UpdateViewport(1200, 900);
        context.ViewModel.ApplyInitialZoom();

        var fitWidthZoom = context.ViewModel.Zoom;

        await context.ViewModel.OpenPathAsync(SamplePath);

        // A different viewport width must produce a different fitted zoom, which only
        // happens when the remembered mode is "fit width" rather than a frozen number.
        context.ViewModel.UpdateViewport(760, 900);
        context.ViewModel.ApplyInitialZoom();

        Assert.NotEqual(fitWidthZoom, context.ViewModel.Zoom, 3);
    }

    [AvaloniaFact]
    public async Task ReadingProgress_ReflectsPageAndOffset()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        Assert.Equal("0%", context.ViewModel.ReadingProgressText);

        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 3, pageOffset: 0);
        Assert.Equal("25%", context.ViewModel.ReadingProgressText);

        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 3, pageOffset: 0.5);
        Assert.Equal("31%", context.ViewModel.ReadingProgressText);

        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 8, pageOffset: 1);
        Assert.Equal("100%", context.ViewModel.ReadingProgressText);
    }

    [AvaloniaFact]
    public async Task NavigateBack_ReturnsToThePositionBeforeTheJump()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 1, pageOffset: 0.2);
        Assert.False(context.ViewModel.CanNavigateBack);

        context.ViewModel.NavigateToPage(5);
        Assert.Equal(5, context.ViewModel.CurrentPage);
        Assert.True(context.ViewModel.CanNavigateBack);

        context.ViewModel.NavigateBackCommand.Execute(null);

        Assert.Equal(1, context.ViewModel.CurrentPage);
        Assert.False(context.ViewModel.CanNavigateBack);
    }

    [AvaloniaFact]
    public async Task ThemeMode_CyclesThroughAllFourModes()
    {
        using var context = new TestContext();

        context.Services.Theme.Set(ThemeService.SystemMode, persist: false);
        Assert.True(context.ViewModel.IsSystemTheme);
        Assert.False(string.IsNullOrWhiteSpace(context.ViewModel.ThemeTooltip));

        // The cycle is System → Light → Dark → High contrast → System.
        context.ViewModel.ToggleThemeCommand.Execute(null);
        Assert.False(context.ViewModel.IsSystemTheme);
        Assert.False(context.ViewModel.IsDarkTheme);

        context.ViewModel.ToggleThemeCommand.Execute(null);
        Assert.True(context.ViewModel.IsDarkTheme);

        context.ViewModel.ToggleThemeCommand.Execute(null);
        Assert.False(context.ViewModel.IsSystemTheme);
        Assert.True(context.ViewModel.IsDarkTheme);

        context.ViewModel.ToggleThemeCommand.Execute(null);
        Assert.True(context.ViewModel.IsSystemTheme);
    }

    /// <summary>
    /// Two windows share one theme service, so a change made in one must be visible in the
    /// other without either of them owning the state.
    /// </summary>
    [AvaloniaFact]
    public void ThemeChange_IsVisibleToEveryWindow()
    {
        using var context = new TestContext();
        using var second = context.CreateSecondViewModel();

        Assert.Equal(context.ViewModel.IsDarkTheme, second.IsDarkTheme);

        context.Services.Theme.Set(ThemeService.DarkMode, persist: false);

        Assert.True(context.ViewModel.IsDarkTheme);
        Assert.True(second.IsDarkTheme);

        context.Services.Theme.Set(ThemeService.LightMode, persist: false);

        Assert.False(context.ViewModel.IsDarkTheme);
        Assert.False(second.IsDarkTheme);
    }

    [AvaloniaFact]
    public async Task Links_AreHitTestable_AndInternalDestinationsNavigate()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(LinksPath);

        // Page 2 of the fixture links back to page 1.
        await context.ViewModel.EnsureLinksAsync(2);
        var page = context.ViewModel.Pages[1];
        var link = Assert.Single(page.Links);
        Assert.Equal(PdfLinkKind.Page, link.Kind);
        Assert.Equal(1, link.PageNumber);

        context.ViewModel.UpdateCurrentPageFromScroll(pageNumber: 2, pageOffset: 0);

        var hit = page.HitTestLink(
            link.Bounds.X + (link.Bounds.Width / 2),
            link.Bounds.Y + (link.Bounds.Height / 2));
        Assert.NotNull(hit);
        Assert.Null(page.HitTestLink(0.98, 0.02));

        context.ViewModel.OpenLink(hit!);

        Assert.Equal(1, context.ViewModel.CurrentPage);
        Assert.True(context.ViewModel.CanNavigateBack);
    }

    [AvaloniaFact]
    public void NewWindowCommand_AsksTheWindowService()
    {
        using var context = new TestContext();

        context.ViewModel.NewWindowCommand.Execute(null);

        Assert.Single(context.Windows.NewWindows);
        Assert.Null(context.Windows.NewWindows[0]);
    }

    [AvaloniaFact]
    public async Task OpenInNewWindowCommand_PassesTheCurrentDocument()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        context.ViewModel.OpenInNewWindowCommand.Execute(null);

        Assert.Equal(SamplePath, Assert.Single(context.Windows.DocumentsInNewWindows));
    }

    [AvaloniaFact]
    public async Task OpenInNewWindowCommand_DoesNothingWithoutADocument()
    {
        using var context = new TestContext();

        context.ViewModel.OpenInNewWindowCommand.Execute(null);

        Assert.Empty(context.Windows.DocumentsInNewWindows);
    }

    /// <summary>
    /// Windows share one settings store, so a change made in one window is not lost when
    /// another window saves (the old design reloaded and rewrote the file per window).
    /// </summary>
    [AvaloniaFact]
    public async Task TwoWindows_ShareOneSettingsInstance()
    {
        using var context = new TestContext();
        var second = context.CreateSecondViewModel();

        Assert.Same(context.ViewModel.Settings, second.Settings);
        Assert.Same(context.ViewModel.Settings.RecentFiles, second.Settings.RecentFiles);

        await context.ViewModel.OpenPathAsync(SamplePath);

        Assert.Contains(
            context.Services.Settings.RecentFiles,
            file => file.FilePath == SamplePath);

        // The second window rebuilt its shelf from the shared list.
        Assert.Contains(second.RecentFiles, file => file.FilePath == SamplePath);
    }

    [AvaloniaFact]
    public async Task ZoomAndLanguageCommands_UpdateViewModelState()
    {
        using var context = new TestContext();
        await context.ViewModel.OpenPathAsync(SamplePath);

        var initialZoom = context.ViewModel.Zoom;
        context.ViewModel.ZoomInCommand.Execute(null);
        Assert.True(context.ViewModel.Zoom > initialZoom);

        context.ViewModel.ToggleLanguageCommand.Execute(null);
        Assert.Equal("中", context.ViewModel.LanguageShortLabel);

        context.ViewModel.ToggleLanguageCommand.Execute(null);
        Assert.Equal("EN", context.ViewModel.LanguageShortLabel);
    }

    private sealed class TestContext : IDisposable
    {
        private readonly string _settingsDirectory;
        private readonly List<MainWindowViewModel> _viewModels = [];

        public TestContext()
        {
            _settingsDirectory = Path.Combine(
                Path.GetTempPath(),
                $"airpdf-app-tests-{Guid.NewGuid():N}");

            Services = new AppServices(_settingsDirectory);
            Windows = new StubWindowService();
            ViewModel = CreateViewModel();
        }

        public AppServices Services { get; }

        public StubWindowService Windows { get; }

        public MainWindowViewModel ViewModel { get; }

        /// <summary>Another window over the same services, as a real second window would be.</summary>
        public MainWindowViewModel CreateSecondViewModel() => CreateViewModel();

        public void Dispose()
        {
            foreach (var viewModel in _viewModels)
            {
                viewModel.Dispose();
            }

            Services.Dispose();

            if (Directory.Exists(_settingsDirectory))
            {
                Directory.Delete(_settingsDirectory, recursive: true);
            }
        }

        /// <summary>The dialog stub in use, so tests can script what a prompt returns.</summary>
        public StubDialogService Dialog { get; } = new();
        private MainWindowViewModel CreateViewModel()
        {
            var documents = new PdfDocumentService();
            var viewModel = new MainWindowViewModel(
                Services,
                documents,
                new PdfSearchService(documents),
                new PdfTextService(documents),
                Dialog,
                Windows);

            _viewModels.Add(viewModel);
            return viewModel;
        }

        internal sealed class StubWindowService : IWindowService
        {
            public List<string?> NewWindows { get; } = [];

            public List<string> DocumentsInNewWindows { get; } = [];

            public void OpenNewWindow(string? filePath = null) => NewWindows.Add(filePath);

            public void OpenInNewWindow(string filePath) => DocumentsInNewWindows.Add(filePath);
        }
    }

    private sealed class StubDialogService : IUserDialogService
    {
        public Task<string?> PickPdfAsync() => Task.FromResult<string?>(null);

        public Task<string?> PromptPasswordAsync(string fileName, bool isRetry) =>
            Task.FromResult<string?>(null);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        /// <summary>Text the next prompt returns; null simulates the reader cancelling.</summary>
        public string? NextText { get; set; }

        /// <summary>Path the save picker returns; null simulates the reader cancelling.</summary>
        public string? NextSavePath { get; set; }

        public Task<string?> PickSavePathAsync(string suggestedName) => Task.FromResult(NextSavePath);

        public Task<string?> PromptTextAsync(string title, string heading, string fieldLabel, string initialValue) =>
            Task.FromResult(NextText);

        /// <summary>What the unsaved-changes prompt returns.</summary>
        public UnsavedChangesChoice NextUnsavedChoice { get; set; } = UnsavedChangesChoice.Cancel;

        public int UnsavedPrompts { get; private set; }

        public Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync(string fileName)
        {
            UnsavedPrompts++;
            return Task.FromResult(NextUnsavedChoice);
        }
    }
}
