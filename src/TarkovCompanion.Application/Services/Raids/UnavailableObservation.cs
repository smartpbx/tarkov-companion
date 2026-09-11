using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// Stands in for the Windows observation surface on other platforms.
/// </summary>
/// <remarks>
/// Registering these keeps the composition identical everywhere, so the observation service
/// has one shape rather than a pile of optional dependencies, and Linux test runs exercise
/// the same graph the packaged application uses.
/// </remarks>
public sealed class UnavailableEftPathLocator : IEftPathLocator
{
    public Task<EftPaths> FindAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new EftPaths(null, null, null, Confidence.Unknown));
}

public sealed class UnavailableEftLogWatcher : IEftLogWatcher
{
    public async IAsyncEnumerable<RaidEvidence> WatchAsync(
        string logRoot,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logRoot);
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }
}

public sealed class UnavailableScreenshotWatcher : IScreenshotWatcher
{
    public async IAsyncEnumerable<string> WatchAsync(
        string screenshotRoot,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screenshotRoot);
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }
}
