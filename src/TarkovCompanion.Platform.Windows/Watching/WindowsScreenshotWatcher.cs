using System.Runtime.CompilerServices;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Application.Services.Raids;

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

        // Not a HashSet of paths any more: a file is held back until its length has stopped
        // changing between polls, because the listing shows a screenshot the moment the game
        // creates it and the picture arrives afterwards.
        var gate = new SettledFileGate();
        var cutoff = DateTime.UtcNow - StartupGrace;
        var first = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var candidate in Snapshot(screenshotRoot))
            {
                if (gate.WasReleased(candidate.Path))
                {
                    continue;
                }

                if (first && candidate.WrittenUtc < cutoff)
                {
                    // From an earlier session and finished by definition, so it is retired
                    // rather than watched for a change that will never come.
                    gate.Release(candidate.Path);
                    continue;
                }

                if (!gate.IsSettled(candidate.Path, LengthOrUnknown(candidate.Path)))
                {
                    continue;
                }

                // A length that has settled is not proof the writer has let go, and a file
                // still held exclusively cannot be decoded. One open attempt says so.
                if (!CanRead(candidate.Path))
                {
                    gate.Retry(candidate.Path);
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

    /// <summary>The file's length, or -1 where it cannot be measured.</summary>
    private static long LengthOrUnknown(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>Whether the file can be opened for reading right now.</summary>
    /// <remarks>
    /// Opened and closed rather than handed on, because the reader downstream opens it again
    /// by path. The point is only to find out whether the game has let go.
    /// </remarks>
    private static bool CanRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
