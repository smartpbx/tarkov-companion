using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace TarkovCompanion.Core.Domain.Profiles;

/// <summary>Mode is explicit because a missing import must not become a PvP profile.</summary>
public enum ProfileGameMode { Unknown, Pvp, Pve, Seasonal }

public sealed record ProfileIdentity
{
    public ProfileIdentity(Guid profileId, string generation)
    {
        if (profileId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(profileId), "A profile id is required.");
        ProfileId = profileId;
        Generation = ProfileText.Required(generation, nameof(generation), 128);
    }

    public Guid ProfileId { get; }
    public string Generation { get; }
}

public sealed record WipeSeason
{
    public WipeSeason(string value) => Value = ProfileText.Required(value, nameof(value), 128);
    public string Value { get; }
}

public sealed record ProfileLocale
{
    private static readonly Regex LanguagePattern = new("^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant);
    private static readonly Regex RegionPattern = new("^[A-Za-z0-9-]{2,16}$", RegexOptions.CultureInvariant);

    public ProfileLocale(string language, string region, string timeZone)
    {
        Language = ProfileText.Required(language, nameof(language), 35);
        Region = ProfileText.Required(region, nameof(region), 16).ToUpperInvariant();
        TimeZone = ProfileText.Required(timeZone, nameof(timeZone), 128);
        if (!LanguagePattern.IsMatch(Language)) throw new ArgumentException("Language must be a BCP-47-like tag.", nameof(language));
        if (!RegionPattern.IsMatch(Region)) throw new ArgumentException("Region contains unsupported characters.", nameof(region));
    }

    public string Language { get; }
    public string Region { get; }
    public string TimeZone { get; }
}

public sealed record DataSnapshotContext
{
    public DataSnapshotContext(string snapshotId, DateTimeOffset publishedUtc)
    {
        SnapshotId = ProfileText.Required(snapshotId, nameof(snapshotId), 256);
        PublishedUtc = publishedUtc.ToUniversalTime();
    }

    public string SnapshotId { get; }
    public DateTimeOffset PublishedUtc { get; }
}

public sealed record ProfileContext
{
    public ProfileContext(ProfileIdentity identity, ProfileGameMode mode, WipeSeason wipeSeason, ProfileLocale locale, DataSnapshotContext dataSnapshot)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Mode = mode;
        WipeSeason = wipeSeason ?? throw new ArgumentNullException(nameof(wipeSeason));
        Locale = locale ?? throw new ArgumentNullException(nameof(locale));
        DataSnapshot = dataSnapshot ?? throw new ArgumentNullException(nameof(dataSnapshot));
    }

    public ProfileIdentity Identity { get; }
    public ProfileGameMode Mode { get; }
    public WipeSeason WipeSeason { get; }
    public ProfileLocale Locale { get; }
    public DataSnapshotContext DataSnapshot { get; }
}

public sealed record ProfilePin
{
    public ProfilePin(string targetKind, string targetId, int sortOrder, string? note)
    {
        TargetKind = ProfileText.Required(targetKind, nameof(targetKind), 64);
        TargetId = ProfileText.Required(targetId, nameof(targetId), 256);
        if (sortOrder < 0) throw new ArgumentOutOfRangeException(nameof(sortOrder));
        SortOrder = sortOrder;
        Note = ProfileText.Optional(note, nameof(note), 1024);
    }

    public string TargetKind { get; }
    public string TargetId { get; }
    public int SortOrder { get; }
    public string? Note { get; }
}

/// <summary>
/// State is owned by one identity/generation context and copied at every boundary. That copy is
/// what prevents a rapid switch from leaking a wishlist or a manually recorded event result.
/// </summary>
public sealed record ProfileProgress
{
    public ProfileProgress(
        int level, IReadOnlyDictionary<string, int>? traderLevels = null, IReadOnlyCollection<string>? completedTaskIds = null,
        IReadOnlyDictionary<string, int>? objectiveProgress = null, IReadOnlyDictionary<string, int>? hideoutStationLevels = null,
        IReadOnlyCollection<string>? wishlistItemIds = null, IReadOnlyDictionary<string, int>? ownedItemCounts = null,
        IReadOnlyDictionary<string, string>? eventItemStates = null, IReadOnlyDictionary<string, string>? itemOverrides = null,
        IReadOnlyList<ProfilePin>? pins = null)
    {
        if (level is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(level));
        Level = level;
        TraderLevels = CopyCounts(traderLevels, nameof(traderLevels), 4);
        CompletedTaskIds = CopySet(completedTaskIds, nameof(completedTaskIds));
        ObjectiveProgress = CopyCounts(objectiveProgress, nameof(objectiveProgress), int.MaxValue);
        HideoutStationLevels = CopyCounts(hideoutStationLevels, nameof(hideoutStationLevels), int.MaxValue);
        WishlistItemIds = CopySet(wishlistItemIds, nameof(wishlistItemIds));
        OwnedItemCounts = CopyCounts(ownedItemCounts, nameof(ownedItemCounts), int.MaxValue);
        EventItemStates = CopyText(eventItemStates, nameof(eventItemStates));
        ItemOverrides = CopyText(itemOverrides, nameof(itemOverrides));
        Pins = CopyPins(pins);
    }

    public int Level { get; }
    public IReadOnlyDictionary<string, int> TraderLevels { get; }
    public IReadOnlyCollection<string> CompletedTaskIds { get; }
    public IReadOnlyDictionary<string, int> ObjectiveProgress { get; }
    public IReadOnlyDictionary<string, int> HideoutStationLevels { get; }
    public IReadOnlyCollection<string> WishlistItemIds { get; }
    public IReadOnlyDictionary<string, int> OwnedItemCounts { get; }
    public IReadOnlyDictionary<string, string> EventItemStates { get; }
    public IReadOnlyDictionary<string, string> ItemOverrides { get; }
    public IReadOnlyList<ProfilePin> Pins { get; }

    private static IReadOnlyDictionary<string, int> CopyCounts(IReadOnlyDictionary<string, int>? values, string parameterName, int maximum)
    {
        var result = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, value) in values ?? EmptyDictionary<int>.Value)
        {
            if (ProfileText.Required(key, parameterName, 256) != key || value < 0 || value > maximum) throw new ArgumentOutOfRangeException(parameterName);
            result.Add(key, value);
        }

        return new ReadOnlyDictionary<string, int>(result);
    }

    private static IReadOnlyCollection<string> CopySet(IReadOnlyCollection<string>? values, string parameterName) =>
        (values ?? EmptySet.Value).Select(value => ProfileText.Required(value, parameterName, 256)).ToFrozenSet(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> CopyText(IReadOnlyDictionary<string, string>? values, string parameterName)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values ?? EmptyDictionary<string>.Value)
            result.Add(ProfileText.Required(key, parameterName, 256), ProfileText.Required(value, parameterName, 1024));
        return new ReadOnlyDictionary<string, string>(result);
    }

    private static IReadOnlyList<ProfilePin> CopyPins(IReadOnlyList<ProfilePin>? pins)
    {
        var result = (pins ?? []).Select(pin => pin ?? throw new ArgumentException("Pins cannot contain null.", nameof(pins)))
            .OrderBy(pin => pin.SortOrder).ThenBy(pin => pin.TargetKind, StringComparer.Ordinal).ThenBy(pin => pin.TargetId, StringComparer.Ordinal).ToArray();
        if (result.Select(pin => $"{pin.TargetKind}\u001f{pin.TargetId}").Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new ArgumentException("Pins must have distinct targets.", nameof(pins));
        return Array.AsReadOnly(result);
    }

    private static class EmptyDictionary<T> { public static readonly IReadOnlyDictionary<string, T> Value = new ReadOnlyDictionary<string, T>(new Dictionary<string, T>()); }
    private static class EmptySet { public static readonly IReadOnlyCollection<string> Value = Array.Empty<string>(); }
}

public enum ProfileLifecycle { Active, Archived }

public sealed record ProfileRecord
{
    public ProfileRecord(ProfileContext context, string name, ProfileProgress progress, ProfileLifecycle lifecycle, DateTimeOffset updatedUtc)
    {
        if (!Enum.IsDefined(lifecycle)) throw new ArgumentOutOfRangeException(nameof(lifecycle));
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Name = ProfileText.Required(name, nameof(name), 256);
        Progress = progress ?? throw new ArgumentNullException(nameof(progress));
        Lifecycle = lifecycle;
        UpdatedUtc = updatedUtc.ToUniversalTime();
    }

    public ProfileContext Context { get; }
    public string Name { get; }
    public ProfileProgress Progress { get; }
    public ProfileLifecycle Lifecycle { get; }
    public DateTimeOffset UpdatedUtc { get; }
}

public sealed record ProfileWorkspaceSnapshot
{
    public ProfileWorkspaceSnapshot(long revision, Guid? activeProfileId, IReadOnlyList<ProfileRecord>? profiles)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        var copied = (profiles ?? []).Select(profile => profile ?? throw new ArgumentException("Profiles cannot contain null.", nameof(profiles)))
            .OrderBy(profile => profile.Context.Identity.ProfileId).ToArray();
        if (copied.Select(profile => profile.Context.Identity.ProfileId).Distinct().Count() != copied.Length)
            throw new ArgumentException("Profiles must have distinct stable ids; a different generation is a visible conflict, not a second profile.", nameof(profiles));
        if (activeProfileId is { } active && !copied.Any(profile => profile.Context.Identity.ProfileId == active && profile.Lifecycle == ProfileLifecycle.Active))
            throw new ArgumentException("The active profile must exist and be active.", nameof(activeProfileId));
        Revision = revision;
        ActiveProfileId = activeProfileId;
        Profiles = Array.AsReadOnly(copied);
    }

    public long Revision { get; }
    public Guid? ActiveProfileId { get; }
    public IReadOnlyList<ProfileRecord> Profiles { get; }
    public ProfileRecord ActiveProfile => ActiveProfileId is { } id ? Profiles.Single(profile => profile.Context.Identity.ProfileId == id) : throw new InvalidOperationException("No active profile is selected.");
}

internal static class ProfileText
{
    public static string Required(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException($"{parameterName} is required, bounded, and cannot contain control characters.", parameterName);
        return value;
    }

    public static string? Optional(string? value, string parameterName, int maximumLength) => value is null ? null : Required(value, parameterName, maximumLength);
}
