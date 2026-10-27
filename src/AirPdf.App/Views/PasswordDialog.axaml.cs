using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AirPdf.App.Services;

namespace AirPdf.App.Views;

public partial class PasswordDialog : Window
{
    private LocalizationService _localization;

    public PasswordDialog()
    {
        _localization = new LocalizationService();
        InitializeComponent();
    }

    public PasswordDialog(string fileName, bool isRetry, LocalizationService localization)
        : this()
    {
        _localization = localization;
        FileNameText.Text = fileName;
        ErrorText.Text = localization.T("L.PasswordRetry");
        ErrorText.IsVisible = isRetry;
        Opened += (_, _) => PasswordBox.Focus();
    }

    private void OnUnlockClick(object? sender, RoutedEventArgs e) => Submit();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Submit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
        }
    }

    private void Submit()
    {
        var password = PasswordBox.Text ?? string.Empty;
        if (password.Length == 0)
        {
            ErrorText.Text = _localization.T("L.PasswordEmpty");
            ErrorText.IsVisible = true;
            PasswordBox.Focus();
            return;
        }

        Close(password);
    }
}
