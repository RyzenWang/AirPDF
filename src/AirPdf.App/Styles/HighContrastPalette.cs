using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AirPdf.App.Styles;

/// <summary>
/// The high-contrast palette as a compiled resource dictionary.
/// </summary>
/// <remarks>
/// Constructing it runs the generated populate method, so nothing is parsed or resolved by
/// URI at runtime. Loading the same file through <c>ResourceInclude(Uri)</c> is annotated as
/// requiring unreferenced code: it locates the XAML through the resource loader, which a
/// trimmed or NativeAOT build can no longer guarantee. <see cref="Services.ThemeService"/>
/// merges an instance of this class instead.
/// </remarks>
public partial class HighContrastPalette : ResourceDictionary
{
    public HighContrastPalette() => AvaloniaXamlLoader.Load(this);
}
