using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AirPdf.App.Views;

namespace AirPdf.App.Services;

public sealed class DialogService : IUserDialogService
{
    private readonly Func<Window?> _ownerProvider;
    private readonly LocalizationService _localization;

    public DialogService(Func<Window?> ownerProvider, LocalizationService localization)
    {
        _ownerProvider = ownerProvider;
        _localization = localization;
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
            Title = _localization.T("L.OpenDialogTitle"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(_localization.T("L.PdfDocuments"))
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

        var dialog = new PasswordDialog(fileName, isRetry, _localization);
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
