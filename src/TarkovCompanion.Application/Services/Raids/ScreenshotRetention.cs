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
    /// What a player who has never touched this setting gets: nothing tidied, at a day once they
    /// turn it on.
    /// </summary>
    /// <remarks>
    /// #309: tidying is off until the player chooses it, on a fresh install and after a migration
    /// with nothing to import alike. A day, once enabled, is long enough to go back for a
    /// screenshot worth keeping: the point of a delay rather than deleting on read is that a
    /// player sometimes wants one afterwards, and a day covers an evening's play plus the
    /// following morning.
    /// </remarks>
    public static ScreenshotRetentionSettings Default { get; } = new(false, 24);

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
public sealed partial class ScreenshotRetentionService(
    IRecycleBin recycleBin,
    TimeProvider? timeProvider = null,
    // #309: where a run that moved something, or failed to, is written down. Optional so a composition
    // without one (and every test that builds this by hand) still tidies, it just leaves no ledger.
    IScreenshotTidyLedger? ledger = null)
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

        return Run(screenshotRoot, settings, dryRun: false).Moved;
    }

    /// <summary>
    /// Reads the folder and says exactly which files a tidy would move, which it would leave and why.
    /// Works whether or not tidying is turned on: it is what the player is shown before they turn it on.
    /// </summary>
    public ScreenshotTidyPlan Plan(string screenshotRoot, ScreenshotRetentionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var hours = settings.SafeRetentionHours;
        if (string.IsNullOrWhiteSpace(screenshotRoot))
        {
            return ScreenshotTidyPlan.Refused(screenshotRoot ?? string.Empty, hours, "There is no screenshot folder yet.");
        }

        string root;
        try
        {
            root = Path.GetFullPath(screenshotRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ScreenshotTidyPlan.Refused(screenshotRoot, hours, "That is not a usable folder path.");
        }

        // A drive root is never a screenshot folder. Only files named like the game's are ever touched, but
        // a wrong root that happens to hold a few of them is exactly the mistake worth refusing outright.
        if (string.Equals(Path.GetPathRoot(root), root, StringComparison.OrdinalIgnoreCase))
        {
            return ScreenshotTidyPlan.Refused(root, hours, "That is a drive root, not a screenshot folder.");
        }

        FileInfo[] files;
        try
        {
            if (!Directory.Exists(root))
            {
                return ScreenshotTidyPlan.Refused(root, hours, "That folder does not exist.");
            }

            files = new DirectoryInfo(root).EnumerateFiles().ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ScreenshotTidyPlan.Refused(root, hours, "That folder could not be read.");
        }

        var named = files.Where(file => ScreenshotName().IsMatch(file.Name)).ToArray();
        var excluded = new List<TidyExclusion>();
        var eligible = new List<TidyCandidate>();
        // Kept whatever its age: the map may still be showing a position from it.
        var newest = named.Length > 1 ? named.MaxBy(file => file.LastWriteTimeUtc) : null;
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - TimeSpan.FromHours(hours);
        foreach (var file in named.OrderBy(file => file.LastWriteTimeUtc))
        {
            if (named.Length <= 1 || ReferenceEquals(file, newest))
            {
                excluded.Add(new(file.Name, TidySkipReason.NewestKept));
            }
            else if (file.LastWriteTimeUtc > cutoff)
            {
                excluded.Add(new(file.Name, TidySkipReason.TooRecent));
            }
            else if (SkipReason(file.Attributes, file.LinkTarget) is { } reason)
            {
                excluded.Add(new(file.Name, reason));
            }
            else
            {
                eligible.Add(new(file.Name, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)));
            }
        }

        return new(root, hours, null, eligible, excluded, files.Length - named.Length);
    }

    /// <summary>
    /// Plans, then (unless <paramref name="dryRun"/>) moves the eligible files to the recycle bin, one at a
    /// time, checking each is still the file that was planned. Never throws.
    /// </summary>
    /// <remarks>
    /// A dry run goes through the same planning as a real run and stops before the first move, so what the
    /// preview shows is what the run does rather than a second opinion about it.
    /// </remarks>
    public ScreenshotTidyResult Run(string screenshotRoot, ScreenshotRetentionSettings settings, bool dryRun)
    {
        var plan = Plan(screenshotRoot, settings);
        var now = _timeProvider.GetUtcNow();
        if (dryRun || plan.IsRefused || !recycleBin.IsAvailable)
        {
            return new(dryRun, plan, 0, 0, [], now);
        }

        var moved = 0;
        long movedBytes = 0;
        var failures = new List<TidyFailure>();
        foreach (var candidate in plan.Eligible)
        {
            var path = Path.Combine(plan.Root, candidate.Name);
            try
            {
                var current = new FileInfo(path);
                if (!current.Exists)
                {
                    failures.Add(new(candidate.Name, "It was gone before it could be moved."));
                }
                else if (current.Length != candidate.Bytes
                    || new DateTimeOffset(current.LastWriteTimeUtc, TimeSpan.Zero) != candidate.LastWriteUtc
                    || SkipReason(current.Attributes, current.LinkTarget) is not null)
                {
                    // Replaced, rewritten or turned into a link since it was planned. What was approved is
                    // not what is there now, so it stays.
                    failures.Add(new(candidate.Name, "It changed after it was checked, so it was left alone."));
                }
                else if (recycleBin.Recycle(path))
                {
                    moved++;
                    movedBytes += candidate.Bytes;
                }
                else
                {
                    failures.Add(new(candidate.Name, "The recycle bin would not take it."));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // One file that cannot be moved is not a reason to leave the rest, or to stop the sweep.
                failures.Add(new(candidate.Name, exception is UnauthorizedAccessException
                    ? "It could not be moved: permission was denied."
                    : "It could not be moved: another program has it, or the disk refused."));
            }
        }

        var ledgerFailed = false;
        if (ledger is not null && (moved > 0 || failures.Count > 0))
        {
            try
            {
                ledger.Append(new(
                    now,
                    plan.RetentionHours,
                    moved,
                    movedBytes,
                    failures.Count,
                    failures.GroupBy(failure => failure.Reason).ToDictionary(group => group.Key, group => group.Count())));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A full disk must not turn a tidy that worked into one that reads as failed.
                ledgerFailed = true;
            }
        }

        return new(false, plan, moved, movedBytes, failures, now, ledgerFailed);
    }

    private static TidySkipReason? SkipReason(FileAttributes attributes, string? linkTarget)
    {
        if (linkTarget is not null || ((attributes & FileAttributes.ReparsePoint) != 0 && !IsCloudOnlyByAttribute(attributes)))
        {
            return TidySkipReason.LinkOrReparsePoint;
        }

        return IsCloudOnly(attributes) ? TidySkipReason.CloudPlaceholder : null;
    }

    /// <summary>The cloud marks other than the generic reparse bit, which a symbolic link also carries.</summary>
    private static bool IsCloudOnlyByAttribute(FileAttributes attributes) =>
        (attributes & FileAttributes.Offline) != 0 || ((int)attributes & RecallOnDataAccess) != 0;

    /// <summary>Whether the file is a cloud placeholder rather than bytes on this disk.</summary>
    /// <remarks>
    /// The game's screenshot folder is inside OneDrive on a default Windows install, and
    /// OneDrive eventually replaces older files with reparse points that hold nothing. Handing
    /// one to the shell forces a download of the very file being thrown away, over a
    /// connection nobody asked it to use, in the middle of a raid.
    ///
    /// Skipping them is also the kinder answer: the picture is still in the player's cloud
    /// storage and is already not taking up space on the disk, which is the whole complaint
    /// this feature exists to answer.
    ///
    /// <c>RECALL_ON_DATA_ACCESS</c> is the attribute OneDrive actually sets and .NET has no
    /// name for it, so it is tested by value.
    /// </remarks>
    public static bool IsCloudOnly(FileAttributes attributes) =>
        (attributes & FileAttributes.Offline) != 0 ||
        (attributes & FileAttributes.ReparsePoint) != 0 ||
        ((int)attributes & RecallOnDataAccess) != 0;

    private const int RecallOnDataAccess = 0x0040_0000;
}
