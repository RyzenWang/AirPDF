using System.Collections.ObjectModel;
using AirPdf.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AirPdf.App.ViewModels;

/// <summary>
/// Owns the tabs of one window. Each tab is a complete <see cref="MainWindowViewModel"/> —
/// its own document, page view models, renderer, and reading position — and the window
/// swaps which one it is showing. Keeping the document state in a view model per tab is
/// what lets several documents stay open without reloading when you switch.
/// </summary>
public sealed partial class WindowViewModel : ObservableObject, ITabService, IDisposable
{
    private readonly Func<MainWindowViewModel> _createTab;
    private readonly List<MainWindowViewModel> _owned = [];

    public WindowViewModel(Func<MainWindowViewModel> createTab) => _createTab = createTab;

    public ObservableCollection<MainWindowViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private MainWindowViewModel? _selectedTab;

    public bool HasMultipleTabs => Tabs.Count > 1;

    public MainWindowViewModel AddTab(string? filePath = null)
    {
        var tab = _createTab();
        _owned.Add(tab);
        Tabs.Add(tab);
        SelectedTab = tab;
        OnPropertyChanged(nameof(HasMultipleTabs));

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            _ = tab.OpenPathAsync(filePath);
        }

        return tab;
    }

    /// <summary>
    /// Closes a tab. The last tab is not removed: closing it closes its document, so the
    /// window stays open on the welcome screen exactly as it did before tabs existed.
    /// </summary>
    public void CloseTab(MainWindowViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        if (Tabs.Count <= 1)
        {
            tab.CloseDocumentCommand.Execute(null);
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        Tabs.Remove(tab);

        if (ReferenceEquals(SelectedTab, tab))
        {
            SelectedTab = Tabs[Math.Clamp(index - 1, 0, Tabs.Count - 1)];
        }

        OnPropertyChanged(nameof(HasMultipleTabs));
        tab.Dispose();
    }

    /// <summary>Opens a document in a new tab of this window.</summary>
    public void OpenInNewTab(string filePath) => AddTab(filePath);

    /// <summary>Cycles the selection, wrapping at both ends.</summary>
    public void SelectNext(int delta)
    {
        if (Tabs.Count < 2 || SelectedTab is null)
        {
            return;
        }

        var index = Tabs.IndexOf(SelectedTab);
        if (index < 0)
        {
            return;
        }

        SelectedTab = Tabs[((index + delta) % Tabs.Count + Tabs.Count) % Tabs.Count];
    }

    public void Dispose()
    {
        foreach (var tab in _owned)
        {
            tab.Dispose();
        }

        _owned.Clear();
        Tabs.Clear();
    }
}
