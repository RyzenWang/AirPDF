using System.Windows.Input;
using AirPdf.App.Services;
using CommunityToolkit.Mvvm.Input;

namespace AirPdf.App.ViewModels;

public sealed class RecentFileViewModel
{
    private readonly LocalizationService _localization;

    public RecentFileViewModel(
        string filePath,
        DateTimeOffset lastOpened,
        LocalizationService localization,
        Func<string, Task> openAsync,
        Func<string, Task> removeAsync)
    {
        _localization = localization;
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

    private string FormatRelative(DateTimeOffset value)
    {
        var age = DateTimeOffset.UtcNow - value;
        if (age < TimeSpan.FromMinutes(1))
        {
            return _localization.T("L.RecentJustNow");
        }

        if (age < TimeSpan.FromHours(1))
        {
            return _localization.Format("L.RecentMinutes", (int)age.TotalMinutes);
        }

        if (age < TimeSpan.FromDays(1))
        {
            return _localization.Format("L.RecentHours", (int)age.TotalHours);
        }

        if (age < TimeSpan.FromDays(7))
        {
            return _localization.Format("L.RecentDays", (int)age.TotalDays);
        }

        return value.LocalDateTime.ToString(
            _localization.IsChinese ? "M月d日" : "MMM d",
            _localization.Culture);
    }
}
