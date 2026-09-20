using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Profile;

namespace TarkovCompanion.Application.Services.Planning;

/// <summary>
/// Which planned items the player has recorded an allergy to (#285). The record is made on the
/// Events page, per event; it matters for as long as that event is running, and only for things
/// that get eaten, drunk or injected.
/// </summary>
public static class AllergyWarningPlanner
{
    /// <summary>Item id to the running event the allergy was recorded in.</summary>
    public static IReadOnlyDictionary<string, string> AllergicItems(
        PlayerProfile profile,
        IEnumerable<EventDefinition> definitions,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(definitions);
        var allergic = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in definitions
                     .Where(definition => IsRunning(definition, at))
                     .OrderBy(definition => definition.Id, StringComparer.Ordinal))
        {
            foreach (var itemId in definition.ApplicableItemIds)
            {
                if (profile.EventItemStates.GetValueOrDefault($"{definition.Id}:{itemId}", EventItemState.Untested) ==
                    EventItemState.Allergic)
                {
                    allergic.TryAdd(itemId, definition.Name);
                }
            }
        }

        return allergic;
    }

    /// <summary>Whether an allergy to this kind of item is something a plan should mention.</summary>
    public static bool IsConsumed(ItemCategory category) => category is ItemCategory.Provision or ItemCategory.Medicine;

    /// <summary>The line a planned item carries, or null where there is nothing to warn about.</summary>
    public static string? Warning(string itemId, ItemCategory category, IReadOnlyDictionary<string, string> allergic)
    {
        ArgumentNullException.ThrowIfNull(allergic);
        return IsConsumed(category) && allergic.TryGetValue(itemId, out var eventName)
            ? $"Allergic · {eventName}"
            : null;
    }

    private static bool IsRunning(EventDefinition definition, DateTimeOffset at) =>
        definition.Active &&
        (definition.StartUtc is not { } start || start <= at) &&
        (definition.EndUtc is not { } end || end >= at);
}

/// <summary>
/// Reads the active profile and the event definitions and answers, for the planning pages, which
/// item ids carry an allergy warning and what it says. An empty answer is the normal one.
/// </summary>
public sealed class AllergyWarningService(
    IPlayerProfileService profiles,
    IEventCatalog events,
    IItemRepository items,
    TimeProvider clock)
{
    public async Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken)
    {
        var profile = await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var definitions = await events.GetAsync(cancellationToken).ConfigureAwait(false);
        var allergic = AllergyWarningPlanner.AllergicItems(profile, definitions, clock.GetUtcNow());
        var warnings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var itemId in allergic.Keys)
        {
            var item = await items.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
            if (item is not null && AllergyWarningPlanner.Warning(itemId, item.Category, allergic) is { } warning)
            {
                warnings[itemId] = warning;
            }
        }

        return warnings;
    }
}
