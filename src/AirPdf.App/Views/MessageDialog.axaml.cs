using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AirPdf.App.Views;

public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    public MessageDialog(string title, string message)
        : this()
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
