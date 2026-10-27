namespace AirPdf.App.Services;

public interface IUserDialogService
{
    Task<string?> PickPdfAsync();

    Task<string?> PromptPasswordAsync(string fileName, bool isRetry);

    Task ShowErrorAsync(string title, string message);
}
