using System.Runtime.CompilerServices;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Platform.Windows.Watching;

/// <summary>
/// Reports screenshots the game has written, by looking at the folder rather than by
/// subscribing to it.
/// </summary>
/// <remarks>
/// This started out on <see cref="FileSystemWatcher"/> and its notifications never arrived on
/// a real installation, exactly as they never arrived for the game's logs. Polling costs one
/// directory listing a second and cannot miss a file, so the same approach is used here.
///
/// Screenshots already on disk when watching starts are not replayed, with one exception: a
/// file written in the couple of minutes before startup is still worth reporting, because the
/// alternative is losing the shot the player took while the companion was restarting. Anything
/// older is left alone, so that yesterday's screenshot cannot announce a raid that is over.
/// </remarks>
public sealed class WindowsScreenshotWatcher(bool developerMode = false, TimeSpan? pollInterval = null)
    : IScreenshotWatcher
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>How far back a file already on disk at startup is still considered new.</summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(2);

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg",
    };

    private readonly TimeSpan _pollInterval = pollInterval ?? DefaultPollInterval;

    public async IAsyncEnumerable<string> WatchAsync(
        string screenshotRoot,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screenshotRoot);
        if (!Directory.Exists(screenshotRoot))
        {
            throw new DirectoryNotFoundException($"EFT screenshot directory does not exist: {screenshotRoot}");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cutoff = DateTime.UtcNow - StartupGrace;
        var first = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var candidate in Snapshot(screenshotRoot))
            {
                if (!seen.Add(candidate.Path))
                {
                    continue;
                }

                if (first && candidate.WrittenUtc < cutoff)
                {
                    continue;
                }

                yield return candidate.Path;
            }

            first = false;
            if (!await WaitAsync(cancellationToken).ConfigureAwait(false))
            {
                yield break;
            }
        }
    }

    private async Task<bool> WaitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Lists the screenshots currently in the folder, oldest first.
    /// </summary>
    /// <remarks>
    /// Oldest first so that when several arrive between two polls the newest is reported last
    /// and therefore wins. A folder that has just been deleted or is briefly unreadable yields
    /// nothing rather than ending the watch; the game may still recreate it.
    /// </remarks>
    private IEnumerable<(string Path, DateTime WrittenUtc)> Snapshot(string screenshotRoot)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(screenshotRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return files
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .Where(path => developerMode || !path.Contains("EftSimulator", StringComparison.OrdinalIgnoreCase))
            .Select(path => (Path: path, WrittenUtc: WrittenUtc(path)))
            .OrderBy(entry => entry.WrittenUtc)
            .ToArray();
    }

    private static DateTime WrittenUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DateTime.UtcNow;
        }
    }
}
