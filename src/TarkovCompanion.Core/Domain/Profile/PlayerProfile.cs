using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;

namespace TarkovCompanion.Core.Domain.Profile;

public enum Faction
{
    Unknown,
    Usec,
    Bear,
}

public sealed record PlayerProfile(
    Guid Id,
    string Name,
    GameMode GameMode,
    int Level,
    Faction Faction,
    string? Edition,
    IReadOnlyDictionary<string, int> TraderLevels,
    IReadOnlySet<string> CompletedTaskIds,
    IReadOnlyDictionary<string, int> ObjectiveProgress,
    IReadOnlyDictionary<string, int> HideoutStationLevels,
    IReadOnlySet<string> WishlistItemIds,
    IReadOnlyDictionary<string, int> OwnedItemCounts,
    IReadOnlyDictionary<string, EventItemState> EventItemStates,
    IReadOnlyDictionary<string, string> ItemOverrides,
    DateTimeOffset UpdatedUtc);

public sealed record ProfileExport(int SchemaVersion, PlayerProfile Profile, DateTimeOffset ExportedUtc);
