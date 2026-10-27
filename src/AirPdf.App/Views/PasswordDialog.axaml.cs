using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AirPdf.App.Views;

public partial class PasswordDialog : Window
{
    public PasswordDialog()
    {
        InitializeComponent();
    }

    public PasswordDialog(string fileName, bool isRetry)
        : this()
    {
        FileNameText.Text = fileName;
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
            ErrorText.Text = "Enter a password to continue.";
            ErrorText.IsVisible = true;
            PasswordBox.Focus();
            return;
        }

        Close(password);
    }
}
