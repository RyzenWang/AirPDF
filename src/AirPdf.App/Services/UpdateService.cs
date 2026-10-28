using Velopack;
using Velopack.Sources;

namespace AirPdf.App.Services;

/// <summary>
/// Thin wrapper around Velopack. Update checks are only meaningful in an
/// installed build; running from <c>bin</c> is treated as not installed.
/// </summary>
public sealed class UpdateService
{
    private const string RepositoryUrl = "https://github.com/RyzenWang/AirPDF";

    private readonly UpdateManager _manager = new(
        new GithubSource(RepositoryUrl, null, false));

    public bool IsInstalled => _manager.IsInstalled;

    public Task<UpdateInfo?> CheckForUpdatesAsync() =>
        _manager.IsInstalled ? _manager.CheckForUpdatesAsync() : Task.FromResult<UpdateInfo?>(null);

    public Task DownloadAsync(UpdateInfo update, Action<int> progress, CancellationToken cancellationToken) =>
        _manager.DownloadUpdatesAsync(update, progress, cancellationToken);

    public void ApplyAndRestart(UpdateInfo update) =>
        _manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
}
