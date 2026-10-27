using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace AirPdf.App.ViewModels;

public sealed class RecentFileViewModel
{
    public RecentFileViewModel(string filePath, DateTimeOffset lastOpened, Func<string, Task> openAsync, Func<string, Task> removeAsync)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        LastOpened = lastOpened;
        Exists = File.Exists(filePath);
        OpenCommand = new AsyncRelayCommand(() => openAsync(FilePath));
        RemoveCommand = new AsyncRelayCommand(() => removeAsync(FilePath));
    }

    public string FilePath { get; }

    public string FileName { get; }

    public string Directory { get; }

    public DateTimeOffset LastOpened { get; }

    public bool Exists { get; }

    public string LastOpenedText => FormatRelative(LastOpened);

    public ICommand OpenCommand { get; }

    public ICommand RemoveCommand { get; }

    private static string FormatRelative(DateTimeOffset value)
    {
        var age = DateTimeOffset.UtcNow - value;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} min ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours} hr ago";
        }

        if (age < TimeSpan.FromDays(7))
        {
            return $"{(int)age.TotalDays} d ago";
        }

        return value.LocalDateTime.ToString("MMM d");
    }
}
