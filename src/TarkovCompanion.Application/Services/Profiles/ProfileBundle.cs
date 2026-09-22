using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Profiles;

/// <summary>
/// One profile's progress in a single file (#269): what Setup › Game &amp; Profile exports and
/// imports. It carries what the player built up (level, traders, hideout, quests, wishlist, keep
/// and sell marks, event marks, quest pins, raid history) and nothing that signs anybody in.
/// </summary>
/// <remarks>
/// The profile's own id and generation are left out on purpose. An import lands in a profile the
/// player chooses, and carrying the source's identity would let a file claim to be a profile it
/// is not; everything scoped to the profile is re-keyed to the target on the way in.
/// </remarks>
public sealed record ProfileBundle(
    string Format,
    int Version,
    DateTimeOffset ExportedUtc,
    ProfileBundleIdentity Profile,
    ProfileBundleProgress Progress,
    ProfileBundleQuests Quests,
    IReadOnlyList<ProfileBundleRaid> Raids);

/// <summary>What the profile was called and which game it tracked.</summary>
public sealed record ProfileBundleIdentity(string Name, ProfileGameMode Mode, string Wipe);

public sealed record ProfileBundleProgress(
    int Level,
    Faction Faction,
    string? Edition,
    IReadOnlyDictionary<string, int> TraderLevels,
    IReadOnlyList<string> CompletedTaskIds,
    IReadOnlyDictionary<string, int> ObjectiveProgress,
    IReadOnlyDictionary<string, int> HideoutStationLevels,
    IReadOnlyList<string> WishlistItemIds,
    IReadOnlyDictionary<string, int> OwnedItemCounts,
    IReadOnlyDictionary<string, EventItemState> EventItemStates,
    IReadOnlyDictionary<string, string> ItemOverrides);

public sealed record ProfileBundleTask(string TaskId, RecordedTaskState State);

public sealed record ProfileBundleObjective(string ObjectiveId, RecordedObjectiveState State, decimal? Count);

public sealed record ProfileBundleHolding(string ItemId, bool FoundInRaid, int Count);

public sealed record ProfileBundlePin(QuestPinTargetKind TargetKind, string TargetId, int SortOrder, string? Note);

public sealed record ProfileBundleQuests(
    IReadOnlyList<ProfileBundleTask> Tasks,
    IReadOnlyList<ProfileBundleObjective> Objectives,
    IReadOnlyList<ProfileBundleHolding> Holdings,
    IReadOnlyList<ProfileBundlePin> Pins)
{
    public static readonly ProfileBundleQuests Empty = new([], [], [], []);
}

public sealed record ProfileBundleRaid(
    Guid Id,
    string? MapId,
    string Mode,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? EndedUtc,
    string? Outcome,
    string? Notes,
    RaidManualMetadata? Manual);

/// <summary>A file this version cannot read because a newer version of the app wrote it.</summary>
public sealed class ProfileBundleVersionException(int version)
    : FormatException($"This file is from a newer version of Tarkov Companion (format {version}). Update the app to import it.")
{
    public int FileVersion { get; } = version;
}

/// <summary>
/// Reads and writes <see cref="ProfileBundle"/> as JSON. The file is untrusted input: unknown
/// fields are ignored, a missing required field or a foreign format is refused with a sentence a
/// player can act on, and a newer version is refused rather than half-read.
/// </summary>
public static class ProfileBundleCodec
{
    public const string FormatId = "tarkov-companion/profile";

    public const int CurrentVersion = 1;

    /// <summary>Far above any real profile (a whole wipe's raids is a few hundred kilobytes).</summary>
    public const int MaximumLength = 32 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Write(ProfileBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return JsonSerializer.Serialize(bundle with { Format = FormatId, Version = CurrentVersion }, Options);
    }

    /// <exception cref="ProfileBundleVersionException">A newer app wrote it.</exception>
    /// <exception cref="InvalidDataException">It is not a profile file, or a required part is missing.</exception>
    public static ProfileBundle Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("The file is empty.");
        }

        if (json.Length > MaximumLength)
        {
            throw new InvalidDataException("The file is too large to be a profile.");
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The file is not valid JSON.", exception);
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String ||
            format.GetString() != FormatId)
        {
            throw new InvalidDataException("This is not a Tarkov Companion profile file.");
        }

        if (!root.TryGetProperty("version", out var versionElement) || !versionElement.TryGetInt32(out var version) || version < 1)
        {
            throw new InvalidDataException("The profile file has no valid version.");
        }

        if (version > CurrentVersion)
        {
            throw new ProfileBundleVersionException(version);
        }

        ProfileBundle? bundle;
        try
        {
            bundle = root.Deserialize<ProfileBundle>(Options);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The profile file is damaged: {exception.Message}", exception);
        }

        if (bundle?.Profile is not { } profile || string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new InvalidDataException("The profile file does not name its profile.");
        }

        if (bundle.Progress is not { } progress)
        {
            throw new InvalidDataException("The profile file has no progress in it.");
        }

        return bundle with
        {
            Progress = Normalize(progress),
            Quests = Normalize(bundle.Quests),
            Raids = [.. (bundle.Raids ?? []).Where(raid => raid is not null && !string.IsNullOrWhiteSpace(raid.Mode))],
        };
    }

    /// <summary>A list or map the file left out is empty, never null, so nothing downstream has to ask.</summary>
    private static ProfileBundleProgress Normalize(ProfileBundleProgress progress) => progress with
    {
        TraderLevels = progress.TraderLevels ?? new Dictionary<string, int>(),
        CompletedTaskIds = progress.CompletedTaskIds ?? [],
        ObjectiveProgress = progress.ObjectiveProgress ?? new Dictionary<string, int>(),
        HideoutStationLevels = progress.HideoutStationLevels ?? new Dictionary<string, int>(),
        WishlistItemIds = progress.WishlistItemIds ?? [],
        OwnedItemCounts = progress.OwnedItemCounts ?? new Dictionary<string, int>(),
        EventItemStates = progress.EventItemStates ?? new Dictionary<string, EventItemState>(),
        ItemOverrides = progress.ItemOverrides ?? new Dictionary<string, string>(),
    };

    private static ProfileBundleQuests Normalize(ProfileBundleQuests? quests) => quests is null
        ? ProfileBundleQuests.Empty
        : new(
            [.. (quests.Tasks ?? []).Where(task => task is not null && !string.IsNullOrWhiteSpace(task.TaskId))],
            [.. (quests.Objectives ?? []).Where(objective => objective is not null && !string.IsNullOrWhiteSpace(objective.ObjectiveId))],
            [.. (quests.Holdings ?? []).Where(holding => holding is not null && !string.IsNullOrWhiteSpace(holding.ItemId))],
            [.. (quests.Pins ?? []).Where(pin => pin is not null && !string.IsNullOrWhiteSpace(pin.TargetId))]);
}

/// <summary>One line of an import preview: what it is now, and what it will be.</summary>
public sealed record ProfileBundleChange(string Area, string Now, string After);

/// <summary>What importing a file would change in a profile, worked out before anything is written.</summary>
public static class ProfileBundleChanges
{
    /// <summary>
    /// The differences between the profile as it is and the file. Progress fields are replaced by
    /// the file's; quest records the file has are set; raids are added unless already there.
    /// </summary>
    public static IReadOnlyList<ProfileBundleChange> Compare(ProfileBundle current, ProfileBundle incoming)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        var changes = new List<ProfileBundleChange>();
        var now = current.Progress;
        var after = incoming.Progress;

        if (now.Level != after.Level)
        {
            changes.Add(new("Level", now.Level.ToString(CultureInfo.InvariantCulture), after.Level.ToString(CultureInfo.InvariantCulture)));
        }

        if (now.Faction != after.Faction)
        {
            changes.Add(new("Faction", now.Faction.ToString(), after.Faction.ToString()));
        }

        Levels(changes, "Trader levels", now.TraderLevels, after.TraderLevels);
        Levels(changes, "Hideout stations", now.HideoutStationLevels, after.HideoutStationLevels);
        Set(changes, "Quests done", now.CompletedTaskIds, after.CompletedTaskIds);
        Levels(changes, "Objective counts", now.ObjectiveProgress, after.ObjectiveProgress);
        Set(changes, "Wishlist", now.WishlistItemIds, after.WishlistItemIds);
        Map(changes, "Keep and sell marks", now.ItemOverrides, after.ItemOverrides);
        Map(changes, "Event marks", now.EventItemStates, after.EventItemStates);
        Levels(changes, "Owned counts", now.OwnedItemCounts, after.OwnedItemCounts);

        var taskStates = current.Quests.Tasks.ToDictionary(task => task.TaskId, task => task.State, StringComparer.Ordinal);
        var tasksChanging = incoming.Quests.Tasks.Count(task => !taskStates.TryGetValue(task.TaskId, out var state) || state != task.State);
        if (tasksChanging > 0)
        {
            changes.Add(new("Quest states", $"{current.Quests.Tasks.Count}", $"{tasksChanging} set from the file"));
        }

        var pins = current.Quests.Pins.Select(pin => (pin.TargetKind, pin.TargetId)).ToHashSet();
        var pinsAdded = incoming.Quests.Pins.Count(pin => !pins.Contains((pin.TargetKind, pin.TargetId)));
        if (pinsAdded > 0)
        {
            changes.Add(new("Quest pins", $"{current.Quests.Pins.Count}", $"{current.Quests.Pins.Count + pinsAdded}"));
        }

        var raidsAdded = NewRaids(current.Raids, incoming.Raids).Count;
        if (raidsAdded > 0)
        {
            changes.Add(new("Raids", $"{current.Raids.Count}", $"{current.Raids.Count + raidsAdded}"));
        }

        return changes;
    }

    /// <summary>
    /// The file's raids that the profile does not already have. A raid is "already here" by id, or
    /// by map and start time, because importing into a new profile gives each raid a new id and a
    /// second import of the same file must not double the history.
    /// </summary>
    public static IReadOnlyList<ProfileBundleRaid> NewRaids(IReadOnlyList<ProfileBundleRaid> current, IReadOnlyList<ProfileBundleRaid> incoming)
    {
        var ids = current.Select(raid => raid.Id).ToHashSet();
        var keys = current.Where(raid => raid.StartedUtc is not null).Select(Key).ToHashSet();
        return [.. incoming.Where(raid => !ids.Contains(raid.Id) && (raid.StartedUtc is null || !keys.Contains(Key(raid))))];
    }

    private static (string, DateTimeOffset?) Key(ProfileBundleRaid raid) => (raid.MapId ?? string.Empty, raid.StartedUtc?.ToUniversalTime());

    private static void Levels(List<ProfileBundleChange> changes, string area, IReadOnlyDictionary<string, int> now, IReadOnlyDictionary<string, int> after)
    {
        var differing = now.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Count(key => now.GetValueOrDefault(key) != after.GetValueOrDefault(key));
        if (differing > 0)
        {
            // Equal counts with different values ("3 → 3") would read as no change, so say how many differ.
            changes.Add(new(area, $"{now.Count}", now.Count == after.Count ? $"{after.Count} ({differing} differ)" : $"{after.Count}"));
        }
    }

    private static void Set(List<ProfileBundleChange> changes, string area, IReadOnlyList<string> now, IReadOnlyList<string> after)
    {
        if (!now.ToHashSet(StringComparer.Ordinal).SetEquals(after))
        {
            changes.Add(new(area, $"{now.Count}", $"{after.Count}"));
        }
    }

    private static void Map<T>(List<ProfileBundleChange> changes, string area, IReadOnlyDictionary<string, T> now, IReadOnlyDictionary<string, T> after)
    {
        var same = now.Count == after.Count && now.All(pair =>
            after.TryGetValue(pair.Key, out var value) && EqualityComparer<T>.Default.Equals(value, pair.Value));
        if (!same)
        {
            changes.Add(new(area, $"{now.Count}", $"{after.Count}"));
        }
    }
}
