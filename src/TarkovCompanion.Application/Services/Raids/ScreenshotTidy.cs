namespace TarkovCompanion.Application.Services.Raids;

/// <summary>Why a screenshot the game wrote is not going to the recycle bin.</summary>
public enum TidySkipReason
{
    /// <summary>The newest one: the map may still be showing a position read from it.</summary>
    NewestKept,

    /// <summary>Not yet older than the retention window.</summary>
    TooRecent,

    /// <summary>A OneDrive-style placeholder, which the shell would download to delete.</summary>
    CloudPlaceholder,

    /// <summary>A symbolic link or other reparse point: what it points at is not this folder's file to remove.</summary>
    LinkOrReparsePoint,

    /// <summary>Replaced or rewritten since it was previewed, so what was approved is not what is there.</summary>
    ChangedSincePreview,

    /// <summary>Gone by the time it was reached.</summary>
    Vanished,
}

/// <summary>One screenshot that would be, or was, moved to the recycle bin.</summary>
public sealed record TidyCandidate(string Name, long Bytes, DateTimeOffset LastWriteUtc);

/// <summary>One screenshot the game wrote that is being left alone, and why.</summary>
public sealed record TidyExclusion(string Name, TidySkipReason Reason);

/// <summary>One file that should have moved and did not.</summary>
public sealed record TidyFailure(string Name, string Reason);

/// <summary>
/// Exactly what a tidy would do to a folder, before it does it (#309).
/// </summary>
/// <param name="Root">The folder that was read.</param>
/// <param name="RetentionHours">The age a screenshot must pass, after the clamp.</param>
/// <param name="Refusal">Set when the folder is not one to tidy at all; nothing else is then filled in.</param>
/// <param name="Eligible">The files that would move, oldest first.</param>
/// <param name="Excluded">Game-named files that stay, each with its reason.</param>
/// <param name="NotGameFiles">Files in the folder the game did not name. They are never touched and never listed.</param>
public sealed record ScreenshotTidyPlan(
    string Root,
    int RetentionHours,
    string? Refusal,
    IReadOnlyList<TidyCandidate> Eligible,
    IReadOnlyList<TidyExclusion> Excluded,
    int NotGameFiles)
{
    public bool IsRefused => Refusal is not null;

    public int Count => Eligible.Count;

    public long TotalBytes => Eligible.Sum(file => file.Bytes);

    public int ExcludedCount(TidySkipReason reason) => Excluded.Count(item => item.Reason == reason);

    public static ScreenshotTidyPlan Refused(string root, int hours, string reason) =>
        new(root, hours, reason, [], [], 0);
}

/// <summary>What one tidy did, or with a dry run what it would have done.</summary>
public sealed record ScreenshotTidyResult(
    bool DryRun,
    ScreenshotTidyPlan Plan,
    int Moved,
    long MovedBytes,
    IReadOnlyList<TidyFailure> Failures,
    DateTimeOffset AtUtc,
    bool LedgerFailed = false);

/// <summary>
/// One line of the last-run ledger. It holds counts and reasons and no file names: a screenshot's name
/// carries where the player was standing, and the ledger outlives the run it describes.
/// </summary>
public sealed record TidyLedgerEntry(
    DateTimeOffset AtUtc,
    int RetentionHours,
    int Moved,
    long MovedBytes,
    int Failed,
    IReadOnlyDictionary<string, int> FailureReasons);

/// <summary>Where tidies are recorded, newest last, so "what did it do, and did it fail" has an answer.</summary>
public interface IScreenshotTidyLedger
{
    IReadOnlyList<TidyLedgerEntry> Read();

    void Append(TidyLedgerEntry entry);
}
