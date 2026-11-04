using Avalonia.Media;

namespace AirPdf.App.ViewModels;

/// <summary>
/// A colour the reader can pick for markup. The brush is built once so the swatches and the
/// toolbar indicator do not allocate one per binding.
/// </summary>
public sealed record MarkupColor(string Name, byte Red, byte Green, byte Blue)
{
    public IBrush Brush { get; } = new SolidColorBrush(Color.FromRgb(Red, Green, Blue));
}