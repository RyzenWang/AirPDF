using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AirPdf.App.Views;

namespace AirPdf.App.Services;

public sealed class DialogService : IUserDialogService
{
    private readonly Func<Window?> _ownerProvider;

    public DialogService(Func<Window?> ownerProvider)
    {
        _ownerProvider = ownerProvider;
    }

    public async Task<string?> PickPdfAsync()
    {
        var owner = _ownerProvider();
        if (owner is null)
        {
            return null;
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a PDF",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("PDF documents")
                {
                    Patterns = ["*.pdf", "*.PDF"],
                    MimeTypes = ["application/pdf"],
                    AppleUniformTypeIdentifiers = ["com.adobe.pdf"]
                }
            ]
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PromptPasswordAsync(string fileName, bool isRetry)
    {
        var owner = _ownerProvider();
        if (owner is null)
        {
            return null;
        }

        var dialog = new PasswordDialog(fileName, isRetry);
        return await dialog.ShowDialog<string?>(owner);
    }

    public async Task ShowErrorAsync(string title, string message)
    {
        var owner = _ownerProvider();
        var dialog = new MessageDialog(title, message);
        if (owner is not null && owner.IsVisible)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }
    }
}
