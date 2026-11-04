using AirPdf.App.Services;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AirPdf.App.Views;

/// <summary>
/// Three-way close prompt. Closing with the window X or Escape returns Cancel, which is the
/// safe default: the document stays open with its annotations intact.
/// </summary>
public partial class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialog()
    {
        InitializeComponent();
    }

    public UnsavedChangesDialog(string fileName, LocalizationService localization)
        : this()
    {
        Title = localization.T("L.UnsavedChangesTitle");
        TitleText.Text = Title;
        MessageText.Text = localization.Format("L.UnsavedChangesMessage", fileName);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) =>
        Close(UnsavedChangesChoice.Save);

    private void OnDiscardClick(object? sender, RoutedEventArgs e) =>
        Close(UnsavedChangesChoice.Discard);

    private void OnCancelClick(object? sender, RoutedEventArgs e) =>
        Close(UnsavedChangesChoice.Cancel);
}
