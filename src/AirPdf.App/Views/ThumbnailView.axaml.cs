using Avalonia;
using Avalonia.Controls;
using AirPdf.App.ViewModels;

namespace AirPdf.App.Views;

public partial class ThumbnailView : UserControl
{
    private ThumbnailViewModel? _attachedThumbnail;

    public ThumbnailView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ReattachThumbnail();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachThumbnail();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachThumbnail();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Item containers are reused when the rail switches documents, so the previous
    /// thumbnail has to be deactivated before the next one is attached. Otherwise it stays
    /// active, keeps its bitmap, and can keep rendering in the background.
    /// </summary>
    private void ReattachThumbnail()
    {
        DetachThumbnail();
        AttachThumbnail();
    }

    private void DetachThumbnail()
    {
        if (_attachedThumbnail is { } thumbnail)
        {
            thumbnail.Deactivate();
            _attachedThumbnail = null;
        }
    }

    private void AttachThumbnail()
    {
        if (DataContext is ThumbnailViewModel thumbnail
            && !ReferenceEquals(thumbnail, _attachedThumbnail))
        {
            _attachedThumbnail = thumbnail;
            thumbnail.Activate();
        }
    }
}
