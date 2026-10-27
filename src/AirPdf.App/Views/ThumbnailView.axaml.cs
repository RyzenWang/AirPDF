using Avalonia;
using Avalonia.Controls;
using AirPdf.App.ViewModels;

namespace AirPdf.App.Views;

public partial class ThumbnailView : UserControl
{
    public ThumbnailView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachThumbnail();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachThumbnail();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is ThumbnailViewModel thumbnail)
        {
            thumbnail.Deactivate();
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void AttachThumbnail()
    {
        if (DataContext is ThumbnailViewModel thumbnail)
        {
            thumbnail.Activate();
        }
    }
}
