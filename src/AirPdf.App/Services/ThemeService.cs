using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using Avalonia.Styling;
using AirPdf.Core.Services;

namespace AirPdf.App.Services;

/// <summary>
/// The application theme, shared by every window. Avalonia's requested theme variant and
/// the merged resource dictionaries are process-wide, so each window observing its own
/// copy would fight over them; one instance owns the state instead and raises
/// <see cref="Changed"/> so all windows stay in step.
/// </summary>
public sealed class ThemeService : IDisposable
{
    public const string SystemMode = "System";
    public const string LightMode = "Light";
    public const string DarkMode = "Dark";
    public const string HighContrastMode = "HighContrast";

    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private IResourceProvider? _highContrastPalette;

    public ThemeService(SettingsService settingsService, AppSettings settings)
    {
        _settingsService = settingsService;
        _settings = settings;

        Mode = Normalize(settings.Theme);
        _settings.Theme = Mode;
        IsDark = ResolveIsDark(Mode);
        Apply();

        if (Application.Current?.PlatformSettings is { } platformSettings)
        {
            platformSettings.ColorValuesChanged += OnPlatformColorValuesChanged;
        }
    }

    /// <summary>One of <c>System</c>, <c>Light</c>, <c>Dark</c>, <c>HighContrast</c>.</summary>
    public string Mode { get; private set; }

    /// <summary>Resolved appearance; <c>System</c> follows the operating system.</summary>
    public bool IsDark { get; private set; }

    public bool IsSystem => string.Equals(Mode, SystemMode, StringComparison.Ordinal);

    public event EventHandler? Changed;

    public static string ModeLabelKey(string mode) => mode switch
    {
        DarkMode => "L.ThemeDark",
        LightMode => "L.ThemeLight",
        HighContrastMode => "L.ThemeHighContrast",
        _ => "L.ThemeSystem"
    };

    /// <summary>Cycles System → Light → Dark → High contrast → System.</summary>
    public void Cycle() => Set(
        Mode switch
        {
            SystemMode => LightMode,
            LightMode => DarkMode,
            DarkMode => HighContrastMode,
            _ => SystemMode
        },
        persist: true);

    public void Set(string mode, bool persist)
    {
        Mode = Normalize(mode);
        _settings.Theme = Mode;
        IsDark = ResolveIsDark(Mode);

        // IsDark may be unchanged (e.g. System-light -> Light), so apply explicitly.
        Apply();

        if (persist)
        {
            _settingsService.Save(_settings);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (Application.Current?.PlatformSettings is { } platformSettings)
        {
            platformSettings.ColorValuesChanged -= OnPlatformColorValuesChanged;
        }
    }

    private static string Normalize(string? mode) => mode switch
    {
        LightMode => LightMode,
        DarkMode => DarkMode,
        HighContrastMode => HighContrastMode,
        _ => SystemMode
    };

    private static bool ResolveIsDark(string mode) => mode switch
    {
        DarkMode or HighContrastMode => true,
        LightMode => false,
        _ => SystemIsDark()
    };

    private static bool SystemIsDark()
    {
        try
        {
            return Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant
                == PlatformThemeVariant.Dark;
        }
        catch
        {
            // Headless or unusual platform hosts may not expose theme values.
            return false;
        }
    }

    private void Apply()
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        application.RequestedThemeVariant = Mode switch
        {
            LightMode => ThemeVariant.Light,
            DarkMode or HighContrastMode => ThemeVariant.Dark,
            // Default lets Avalonia follow the operating system.
            _ => ThemeVariant.Default
        };

        ApplyHighContrastPalette(application, string.Equals(Mode, HighContrastMode, StringComparison.Ordinal));
    }

    /// <summary>
    /// Avalonia 11 has no high-contrast theme variant, so the high-contrast palette is a
    /// flat dictionary merged on top of <c>Styles/Colors.axaml</c>. Later merged
    /// dictionaries win, so every key it defines overrides the light/dark palette.
    /// </summary>
    private void ApplyHighContrastPalette(Application application, bool enabled)
    {
        if (!enabled)
        {
            if (_highContrastPalette is not null)
            {
                application.Resources.MergedDictionaries.Remove(_highContrastPalette);
                _highContrastPalette = null;
            }

            return;
        }

        if (_highContrastPalette is not null)
        {
            return;
        }

        try
        {
            var include = new ResourceInclude(new Uri("avares://AirPdf/"))
            {
                Source = new Uri("avares://AirPdf/Styles/HighContrast.axaml")
            };

            if (include.Loaded is { } palette)
            {
                application.Resources.MergedDictionaries.Add(palette);
                _highContrastPalette = palette;
            }
        }
        catch
        {
            // A missing palette must not stop the app from starting; the window keeps the
            // light/dark palette.
        }
    }

    private void OnPlatformColorValuesChanged(object? sender, PlatformColorValues e)
    {
        if (!IsSystem)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!IsSystem)
            {
                return;
            }

            IsDark = e.ThemeVariant == PlatformThemeVariant.Dark;
            Apply();
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }
}
