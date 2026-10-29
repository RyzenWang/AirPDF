using Velopack;
using Velopack.Sources;

namespace AirPdf.App.Services;

/// <summary>
/// Thin wrapper around Velopack. Update checks are only meaningful in an
/// installed build; running from <c>bin</c> is treated as not installed.
/// The manager is created lazily so constructing the view model in tests or
/// tools does not require a Velopack installation.
/// </summary>
public sealed class UpdateService
{
    private const string RepositoryUrl = "https://github.com/RyzenWang/AirPDF";

    /// <summary>
    /// Startup checks are a process-level concern: with several windows open, only the
    /// first one should hit the update feed and download.
    /// </summary>
    private static int _startupCheckStarted;

    private readonly Lazy<UpdateManager> _manager = new(
        () => new UpdateManager(new GithubSource(RepositoryUrl, null, false)));

    /// <summary>True for the first caller in this process, false afterwards.</summary>
    public static bool TryBeginStartupCheck() =>
        Interlocked.Exchange(ref _startupCheckStarted, 1) == 0;

    private UpdateManager Manager => _manager.Value;

    public bool IsInstalled
    {
        get
        {
            try
            {
                return Manager.IsInstalled;
            }
            catch
            {
                return false;
            }
        }
    }

    public Task<UpdateInfo?> CheckForUpdatesAsync() =>
        IsInstalled ? Manager.CheckForUpdatesAsync() : Task.FromResult<UpdateInfo?>(null);

    public Task DownloadAsync(UpdateInfo update, Action<int> progress, CancellationToken cancellationToken) =>
        Manager.DownloadUpdatesAsync(update, progress, cancellationToken);

    public void ApplyAndRestart(UpdateInfo update) =>
        Manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
}
