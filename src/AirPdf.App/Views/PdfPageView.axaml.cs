using Avalonia;
using Avalonia.Controls;
using AirPdf.App.ViewModels;

namespace AirPdf.App.Views;

public partial class PdfPageView : UserControl
{
    public PdfPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachPage();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachPage();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is PageViewModel page)
        {
            page.Deactivate();
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void AttachPage()
    {
        if (DataContext is PageViewModel page && TopLevel.GetTopLevel(this) is { } topLevel)
        {
            page.Activate(topLevel.RenderScaling);

            // Link annotations are needed for hit-testing, so fetch them with the render.
            _ = page.EnsureLinksAsync();
        }
    }
}
