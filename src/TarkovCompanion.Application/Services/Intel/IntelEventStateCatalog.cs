using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Events;

namespace TarkovCompanion.Application.Services.Intel;

/// <summary>
/// Every item's event state, for the running events, in one lookup rather than one query a row.
/// </summary>
/// <remarks>
/// <para>
/// The Events page already keeps this fact — <see cref="IEventTrackerService"/> reads and writes
/// it — but only one item at a time, by event id. Intel shows the chip on every row of a search,
/// a landing section and the Crafts &amp; barters tab at once, and a per-row round trip through
/// the profile and the event catalog for forty-odd rows on every one-second refresh tick is the
/// shape of query <c>OffInterfaceThread</c>'s own remarks describe as a frozen dispatcher turn.
/// So this reads the profile and the running event definitions once and hands back a map instead.
/// </para>
/// <para>
/// Mirrors <c>LootScanEventStateSource</c>'s reasoning (an item outside every running event is a
/// settled "outside", not an unknown; an Allergic result in any running event wins), without that
/// class's evidence/scope bookkeeping, which the recommendation engine needs and a chip does not.
/// </para>
/// </remarks>
public interface IIntelEventStateCatalog
{
    /// <summary>
    /// Every item that is part of a currently-running event, mapped to its worst recorded state
    /// (Allergic beats Safe beats Untested). An item missing from the map is outside every
    /// running event — Intel draws no chip for it.
    /// </summary>
    Task<IReadOnlyDictionary<string, EventItemState>> GetActiveAsync(CancellationToken cancellationToken);
}

public sealed class IntelEventStateCatalog(
    IPlayerProfileService profileService,
    IEventCatalog events,
    TimeProvider? timeProvider = null) : IIntelEventStateCatalog
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyDictionary<string, EventItemState>> GetActiveAsync(CancellationToken cancellationToken)
    {
        var profile = await profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var definitions = await events.GetAsync(cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();

        var map = new Dictionary<string, EventItemState>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (!IsRunning(definition, now))
            {
                continue;
            }

            foreach (var itemId in definition.ApplicableItemIds)
            {
                var state = profile.EventItemStates.GetValueOrDefault($"{definition.Id}:{itemId}", EventItemState.Untested);
                if (!map.TryGetValue(itemId, out var existing) || Rank(state) > Rank(existing))
                {
                    map[itemId] = state;
                }
            }
        }

        return map;
    }

    private static int Rank(EventItemState state) => state switch
    {
        EventItemState.Allergic => 2,
        EventItemState.Safe => 1,
        _ => 0,
    };

    private static bool IsRunning(EventDefinition definition, DateTimeOffset at) =>
        definition.Active &&
        (definition.StartUtc is not { } start || start <= at) &&
        (definition.EndUtc is not { } end || end >= at);
}
