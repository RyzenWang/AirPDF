namespace AirPdf.App.Services;

public interface IUserDialogService
{
    Task<string?> PickPdfAsync();

    Task<string?> PromptPasswordAsync(string fileName, bool isRetry);

    Task ShowErrorAsync(string title, string message);

    /// <summary>Asks where to write a new PDF; null means the reader cancelled.</summary>
    Task<string?> PickSavePathAsync(string suggestedName);

    /// <summary>Asks for a single line of text; null means the reader cancelled.</summary>
    Task<string?> PromptTextAsync(string title, string heading, string fieldLabel, string initialValue);
}
