using TarkovCompanion.Core.Common;
using Velopack;

namespace TarkovCompanion.App.Services.Updates;

/// <summary>Where an older build can be installed from.</summary>
public enum RollbackSource
{
    /// <summary>The channel's feed still lists it.</summary>
    Feed,

    /// <summary>The package this machine ran before the last update, kept in the data folder.</summary>
    KeptCopy,
}

/// <summary>An older full build that "Go back" would install.</summary>
/// <param name="Version">The build's version.</param>
/// <param name="FileName">The package's file name, beside the feed or in the kept folder.</param>
/// <param name="Sha256">The SHA256 its bytes must have, upper-case hex.</param>
/// <param name="Size">The package's length in bytes.</param>
/// <param name="Source">Whether it comes from the feed or from the kept copy.</param>
public sealed record RollbackCandidate(string Version, string FileName, string Sha256, long Size, RollbackSource Source);

/// <summary>What Setup asks before it offers to go back.</summary>
/// <param name="Installed">The running build's version.</param>
/// <param name="Previous">The build it would go back to, or null when there is none.</param>
/// <param name="LatestInFeed">The newest build the feed lists, when it could be read.</param>
/// <param name="Reason">Why there is nothing to go back to, when there is not.</param>
public sealed record RollbackOffer(string Installed, RollbackCandidate? Previous, string? LatestInFeed, string? Reason);

/// <summary>
/// "Stay on this version until the next build": set by going back, cleared by a build newer
/// than anything the feed had when it was set.
/// </summary>
/// <remarks>
/// Without it the automatic check, two minutes after the older build reopened, found the build
/// the player had just left, offered it, and the badge asked them to take it again.
/// </remarks>
/// <param name="Version">The build that was gone back to.</param>
/// <param name="HoldThrough">The newest build to keep quiet about. Anything newer is offered, and ends the pin.</param>
/// <param name="SetUtc">When it was set.</param>
public sealed record UpdatePin(string Version, string HoldThrough, DateTimeOffset SetUtc);

/// <summary>Chooses the build to go back to, and decides when a pin stops holding.</summary>
public static class UpdateRollbackRules
{
    /// <summary>
    /// The newest full build older than <paramref name="installed"/>, from the feed or the kept copy.
    /// </summary>
    /// <remarks>
    /// Newest older, not oldest: going back is for "the last build worked and this one does
    /// not", and anything further back throws away fixes the player did not complain about.
    /// A delta package is never a candidate, because the updater only downgrades to a whole
    /// build. On a tie the feed wins, because its copy is the one the feed's hash describes.
    /// </remarks>
    public static RollbackCandidate? Choose(
        string installed,
        IEnumerable<UpdateFeedPackage> feed,
        string? packageId,
        RollbackCandidate? kept)
    {
        ArgumentNullException.ThrowIfNull(feed);
        if (!SemanticVersion.TryParse(installed, out var current))
        {
            return null;
        }

        var candidates = feed
            .Where(package => package.IsFull
                && (packageId is null || package.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)))
            .Select(package => new RollbackCandidate(package.Version, package.FileName, package.Sha256, package.Size, RollbackSource.Feed))
            .Append(kept);
        RollbackCandidate? best = null;
        SemanticVersion? bestVersion = null;
        foreach (var candidate in candidates)
        {
            if (candidate is null
                || !SemanticVersion.TryParse(candidate.Version, out var version)
                || version >= current)
            {
                continue;
            }

            if (bestVersion is null || version > bestVersion)
            {
                best = candidate;
                bestVersion = version;
            }
        }

        return best;
    }

    /// <summary>The newest full build of <paramref name="packageId"/> the feed lists, or null.</summary>
    public static string? Latest(IEnumerable<UpdateFeedPackage> feed, string? packageId)
    {
        ArgumentNullException.ThrowIfNull(feed);
        return Newest(feed
            .Where(package => package.IsFull
                && (packageId is null || package.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)))
            .Select(package => package.Version));
    }

    /// <summary>The pin going back from <paramref name="installed"/> to <paramref name="target"/> sets.</summary>
    /// <remarks>
    /// Held through whichever is newer of the build being left and the newest in the feed: the
    /// build being left may be newer than the feed (a feed that was rolled back itself), and
    /// the feed may already hold a newer build the player has not taken.
    /// </remarks>
    public static UpdatePin PinFor(string target, string installed, string? latestInFeed, DateTimeOffset nowUtc) =>
        new(target, Newest([installed, latestInFeed]) ?? installed, nowUtc);

    /// <summary>
    /// Whether <paramref name="pin"/> still keeps <paramref name="offered"/> from being offered.
    /// </summary>
    /// <remarks>
    /// A pin holds only while the build it was set for is the one running. If going back did
    /// not apply, or somebody ran an installer since, the pin describes a machine that no
    /// longer exists and must not keep quiet about anything.
    /// </remarks>
    public static bool Holds(UpdatePin pin, string installed, string offered)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (!SemanticVersion.TryParse(pin.Version, out var pinned)
            || !SemanticVersion.TryParse(installed, out var running)
            || pinned != running
            || !SemanticVersion.TryParse(pin.HoldThrough, out var holdThrough)
            || !SemanticVersion.TryParse(offered, out var candidate))
        {
            return false;
        }

        return candidate <= holdThrough;
    }

    private static string? Newest(IEnumerable<string?> versions)
    {
        string? best = null;
        SemanticVersion? bestVersion = null;
        foreach (var text in versions)
        {
            if (text is not null
                && SemanticVersion.TryParse(text, out var version)
                && (bestVersion is null || version > bestVersion))
            {
                best = text;
                bestVersion = version;
            }
        }

        return best;
    }
}

/// <summary>What was applied, as recorded at the moment it was handed to the updater.</summary>
/// <param name="Version">The build handed over.</param>
/// <param name="Channel">The channel's name.</param>
/// <param name="FeedHost">The feed's host name, or "local folder".</param>
/// <param name="Sha256">The SHA256 the feed listed for the package, which the download matched.</param>
/// <param name="AppliedUtc">When it was handed to the updater.</param>
/// <param name="WentBack">Whether it was a step back rather than forward.</param>
public sealed record UpdateProvenance(
    string Version,
    string Channel,
    string FeedHost,
    string Sha256,
    DateTimeOffset AppliedUtc,
    bool WentBack);

/// <summary>One line of Setup › Updates' provenance block.</summary>
public sealed record UpdateProvenanceRow(string Label, string Value);

/// <summary>The running build's provenance, as Setup shows it.</summary>
public static class UpdateProvenanceText
{
    public const string NotRecorded = "Not recorded · installed by Setup or an older build";

    /// <summary>The feed's host, which is all of the address a player needs to recognise.</summary>
    public static string HostOf(Uri feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        return feed.IsFile ? "local folder" : feed.Host;
    }

    /// <summary>
    /// The rows: version, channel, feed host, package SHA256 and when it was applied.
    /// </summary>
    /// <remarks>
    /// The record is only believed when it names the running build. One that names another
    /// build is an apply that did not happen (or an installer run since), and showing its hash
    /// beside this version would be a statement about bytes that are not the ones running.
    /// </remarks>
    /// <param name="feedSha256">The SHA256 the feed lists now for the running build, when it lists it.</param>
    public static IReadOnlyList<UpdateProvenanceRow> Rows(
        string installed,
        string channel,
        Uri feed,
        UpdateProvenance? recorded,
        string? feedSha256)
    {
        var record = recorded is not null && SameVersion(recorded.Version, installed) ? recorded : null;
        var sha = record?.Sha256 ?? (feedSha256 is null ? "Not recorded" : $"{feedSha256} · as the feed lists it");
        var applied = record is null
            ? NotRecorded
            : record.WentBack
                ? $"{LocalTime.Moment(record.AppliedUtc)} · went back"
                : LocalTime.Moment(record.AppliedUtc);
        return
        [
            new("Version", installed),
            new("Channel", record?.Channel ?? channel),
            new("Feed", record?.FeedHost ?? HostOf(feed)),
            new("SHA-256", sha),
            new("Applied", applied),
        ];
    }

    private static bool SameVersion(string left, string right) =>
        SemanticVersion.TryParse(left, out var a) && SemanticVersion.TryParse(right, out var b)
            ? a == b
            : left.Equals(right, StringComparison.Ordinal);
}
