using System.Text.RegularExpressions;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// How long the game's screenshots are kept before the companion tidies them away.
/// </summary>
/// <param name="IsEnabled">Whether the companion touches them at all.</param>
/// <param name="RetentionHours">How old a screenshot must be before it is tidied.</param>
public sealed record ScreenshotRetentionSettings(bool IsEnabled, int RetentionHours)
{
    /// <summary>
    /// A day, which is long enough to go back for one worth keeping.
    /// </summary>
    /// <remarks>
    /// The point of a delay rather than deleting on read is that a player sometimes wants a
    /// screenshot afterwards, and a day covers an evening's play plus the next morning.
    /// </remarks>
    public static ScreenshotRetentionSettings Default { get; } = new(true, 24);

    /// <summary>Bounds that keep a hand-edited file from doing something surprising.</summary>
    public int SafeRetentionHours => Math.Clamp(RetentionHours, 1, 24 * 30);
}

public interface IScreenshotRetentionStore
{
    Task<ScreenshotRetentionSettings> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(ScreenshotRetentionSettings settings, CancellationToken cancellationToken);
}

/// <summary>
/// Sends a file to the recycle bin rather than destroying it.
/// </summary>
/// <remarks>
/// These are the player's own pictures and the companion did not create them, so a mistake
/// here has to be recoverable. Windows already has a place for things somebody might want
/// back, and the whole point of the feature is that the folder stops growing, which a recycle
/// bin the operating system manages achieves just as well as deletion.
/// </remarks>
public interface IRecycleBin
{
    bool IsAvailable { get; }

    bool Recycle(string path);
}

/// <summary>
/// Tidies away screenshots the player is unlikely to want, and nothing else.
/// </summary>
/// <remarks>
/// Three rules, each of which exists because deleting somebody's files is not a thing to be
/// approximate about.
///
/// Only files whose names match the game's own screenshot convention are ever touched. The
/// folder belongs to the player and may hold anything; a stray document in it is not the
/// companion's business.
///
/// The newest screenshot is always kept, whatever its age. It is the one the map may still be
/// showing a position from, and a folder that empties completely the moment somebody stops
/// playing looks like a bug.
///
/// And everything goes to the recycle bin, so the answer to "it deleted one I wanted" is to
/// open the bin rather than to apologise.
/// </remarks>
public sealed partial class ScreenshotRetentionService(IRecycleBin recycleBin, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>What the game names its screenshots, loosely enough to match every variant.</summary>
    /// <remarks>
    /// Deliberately looser than the parser that reads coordinates out of these names, because
    /// this only needs to answer "did the game write this", and a screenshot taken outside a
    /// raid carries no coordinates at all yet is still the game's to tidy.
    /// </remarks>
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}\[\d{2}-\d{2}\].*\.(png|jpg|jpeg)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScreenshotName();

    /// <summary>Tidies the folder and reports how many went, without ever throwing.</summary>
    public int Tidy(string screenshotRoot, ScreenshotRetentionSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screenshotRoot);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsEnabled || !recycleBin.IsAvailable)
        {
            return 0;
        }

        FileInfo[] candidates;
        try
        {
            candidates = new DirectoryInfo(screenshotRoot)
                .EnumerateFiles()
                .Where(file => ScreenshotName().IsMatch(file.Name))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        if (candidates.Length <= 1)
        {
            return 0;
        }

        // Kept whatever its age: the map may still be showing a position from it.
        var newest = candidates.MaxBy(file => file.LastWriteTimeUtc);
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - TimeSpan.FromHours(settings.SafeRetentionHours);
        var tidied = 0;
        foreach (var file in candidates)
        {
            if (ReferenceEquals(file, newest) || file.LastWriteTimeUtc > cutoff)
            {
                continue;
            }

            if (recycleBin.Recycle(file.FullName))
            {
                tidied++;
            }
        }

        return tidied;
    }
}
