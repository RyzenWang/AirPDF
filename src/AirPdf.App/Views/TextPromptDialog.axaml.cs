using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AirPdf.App.Services;

namespace AirPdf.App.Views;

/// <summary>
/// A one-field dialog for naming things, currently bookmark labels. Returns the trimmed text,
/// or null when the reader cancels or leaves it empty.
/// </summary>
public partial class TextPromptDialog : Window
{
    public TextPromptDialog()
    {
        InitializeComponent();
    }

    public TextPromptDialog(
        string title,
        string heading,
        string fieldLabel,
        string initialValue,
        LocalizationService localization)
        : this()
    {
        Title = title;
        HeadingText.Text = heading;
        FieldLabel.Text = fieldLabel;
        ValueBox.Text = initialValue;

        Opened += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) => Submit();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnValueKeyDown(object? sender, KeyEventArgs e)
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
        var value = (ValueBox.Text ?? string.Empty).Trim();
        Close(value.Length == 0 ? null : value);
    }
}
