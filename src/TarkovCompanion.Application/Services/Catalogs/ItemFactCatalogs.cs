using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Events;

namespace TarkovCompanion.Application.Services.Catalogs;

/// <summary>
/// Loads the fact tables the intelligence services are built from.
/// </summary>
/// <remarks>
/// Those services take their facts as constructor collections, and nothing ever registered
/// one, so every page they back returned nothing. Loading through a catalog instead of a
/// container registration matters for a second reason: on a clean install the database is
/// still empty when the container is built, and the first sync lands seconds later. A catalog
/// is read when a page asks, so the data appears as soon as it exists rather than at the next
/// restart.
/// </remarks>
public interface IItemFactCatalog
{
    Task<IReadOnlyList<AmmoStats>> GetAmmoAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<AmmoPackContents>> GetAmmoPacksAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<LoadoutItemFacts>> GetLoadoutFactsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<KeyFacts>> GetKeyFactsAsync(CancellationToken cancellationToken);

    /// <summary>Drops any cached projection so the next read reflects a completed sync.</summary>
    void Invalidate();
}

/// <summary>
/// Loads what the player's quests and hideout still require, by item.
/// </summary>
/// <remarks>
/// Every sync writes hideout stations, levels and requirements into SQLite and, until now,
/// nothing read them back. The same gap left the scanner unable to say "this is a quest item"
/// or "your hideout needs this", which are three of the five reasons it ranks loot by.
/// </remarks>
public interface IRequirementCatalog
{
    Task<IReadOnlyList<HideoutItemRequirement>> GetHideoutRequirementsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<QuestItemRequirement>> GetQuestRequirementsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<HideoutStationSummary>> GetStationsAsync(CancellationToken cancellationToken);

    void Invalidate();
}

/// <summary>A hideout station and the levels it can be built to.</summary>
public sealed record HideoutStationSummary(
    string StationId,
    string Name,
    IReadOnlyList<int> Levels);

/// <summary>Hand-authored seasonal event definitions.</summary>
/// <remarks>
/// json.tarkov.dev exposes no events endpoint, so these are configured locally rather than
/// fetched. An empty catalog is the normal state out of season and must read as such.
/// </remarks>
public interface IEventCatalog
{
    Task<IReadOnlyList<EventDefinition>> GetAsync(CancellationToken cancellationToken);
}
