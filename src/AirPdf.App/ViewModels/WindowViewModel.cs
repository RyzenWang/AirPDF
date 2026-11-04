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
    private readonly Func<WindowViewModel, bool, MainWindowViewModel> _createTab;
    private readonly List<MainWindowViewModel> _owned = [];

    /// <summary>
    /// Creates a tab. The host is passed in because the home tab is built during construction,
    /// before the window field holding the host exists, and every tab needs a way back.
    /// </summary>
    public WindowViewModel(Func<WindowViewModel, bool, MainWindowViewModel> createTab)
    {
        _createTab = createTab;

        HomeTab = createTab(this, true);
        _owned.Add(HomeTab);
        Tabs.Add(HomeTab);
        SelectedTab = HomeTab;
    }

    /// <summary>The welcome screen as a place, so returning to it does not close documents.</summary>
    public MainWindowViewModel HomeTab { get; }

    public ObservableCollection<MainWindowViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private MainWindowViewModel? _selectedTab;

    /// <summary>The strip is visible from the first tab, home included.</summary>
    public bool HasTabs => Tabs.Count > 0;

    public MainWindowViewModel AddTab(string? filePath = null)
    {
        var tab = _createTab(this, false);
        _owned.Add(tab);
        Tabs.Add(tab);
        SelectedTab = tab;
        OnPropertyChanged(nameof(HasTabs));

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

        if (ReferenceEquals(tab, HomeTab))
        {
            // Home is a place, not a document: there is nothing to close.
            SelectedTab = HomeTab;
            return;
        }

        if (Tabs.Count <= 1)
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        // Leave the tab before the strip removes it. Removing the selected item makes the
        // ListBox write null back through the two-way SelectedTab binding, and a selection
        // checked after the removal is already gone: the window then kept the closed tab as
        // its DataContext. The welcome screen stayed on screen, but opening a recent file
        // from it ran on the disposed view model, so the document opened with no renderer
        // behind it - blank pages at the right reading position - and the next page click
        // crashed on the disposed service.
        if (ReferenceEquals(SelectedTab, tab))
        {
            SelectedTab = HomeTab;
        }

        Tabs.Remove(tab);

        // Also covers a selection the list cleared for some other reason, or a selected tab
        // that is no longer in the collection.
        if (SelectedTab is null || !Tabs.Contains(SelectedTab))
        {
            SelectedTab = HomeTab;
        }

        OnPropertyChanged(nameof(HasTabs));
        tab.Dispose();
    }

    /// <summary>Opens a document in a new tab of this window.</summary>
    public void OpenInNewTab(string filePath) => AddTab(filePath);

    public void ShowHome() => SelectedTab = HomeTab;

    public MainWindowViewModel? SelectTab(string filePath)
    {
        var tab = Tabs.FirstOrDefault(
            candidate => !candidate.IsHomeTab
                && string.Equals(candidate.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        if (tab is not null)
        {
            SelectedTab = tab;
        }

        return tab;
    }

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
