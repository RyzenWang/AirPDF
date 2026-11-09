using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AirPdf.App.ViewModels;

namespace AirPdf.App.Views;

public partial class PdfPageView : UserControl
{
    private PageViewModel? _attachedPage;

    public PdfPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ReattachPage();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachPage();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachPage();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// The item container is reused when the page list switches documents, so the previous
    /// page has to be released explicitly: nothing detaches it from the visual tree, and an
    /// active page keeps its bitmap and keeps rendering in the background.
    /// </summary>
    private void ReattachPage()
    {
        DetachPage();
        AttachPage();
    }

    private void DetachPage()
    {
        if (_attachedPage is { } page)
        {
            page.PropertyChanged -= OnPagePropertyChanged;
            page.Deactivate();
            _attachedPage = null;
        }
    }

    private void AttachPage()
    {
        if (DataContext is PageViewModel page
            && !ReferenceEquals(page, _attachedPage)
            && TopLevel.GetTopLevel(this) is { } topLevel)
        {
            _attachedPage = page;
            page.PropertyChanged += OnPagePropertyChanged;
            RenderOptions.SetBitmapInterpolationMode(PageImage, page.BitmapInterpolationMode);
            page.Activate(topLevel.RenderScaling);

            // Link annotations are needed for hit-testing, so fetch them with the render.
            _ = page.EnsureLinksAsync();
        }
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageViewModel.BitmapInterpolationMode) &&
            sender is PageViewModel page)
        {
            RenderOptions.SetBitmapInterpolationMode(PageImage, page.BitmapInterpolationMode);
        }
    }
}
