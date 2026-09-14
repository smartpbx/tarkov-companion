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

/// <summary>
/// Maps the location token the game logs to the map id the companion uses.
/// </summary>
/// <remarks>
/// json.tarkov.dev publishes both halves of this pairing for every map, so it is synced fact
/// rather than a hand-maintained table. The previous hardcoded table had three wrong mappings
/// and was missing four maps outright.
/// </remarks>
public interface IMapAliasCatalog
{
    /// <summary>Location token to map id, compared without regard to case.</summary>
    Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken);

    void Invalidate();
}

/// <summary>Hand-authored seasonal event definitions.</summary>
/// <remarks>
/// json.tarkov.dev exposes no events endpoint, so these are configured locally rather than
/// fetched. An empty catalog is the normal state out of season and must read as such.
/// </remarks>
public interface IEventCatalog
{
    Task<IReadOnlyList<EventDefinition>> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Writes an event definition, so one can be made without leaving the application.
/// </summary>
/// <remarks>
/// <para>
/// The page told the player to write a JSON file into %LOCALAPPDATA% by hand and restart. That
/// is the whole of what is wrong with the Events page — the feature underneath it is one
/// Clayton wants, and it was nearly deleted on the strength of an onboarding nobody would ever
/// complete.
/// </para>
/// <para>
/// Separate from <see cref="IEventCatalog"/> on purpose. Reading definitions is something every
/// composition does and writing them is something only the page does, and a reader that can
/// also write is a reader every test has to be trusted not to.
/// </para>
/// </remarks>
public interface IEventAuthoring
{
    /// <summary>Writes one definition, replacing any file already holding that id.</summary>
    Task SaveAsync(EventDefinition definition, CancellationToken cancellationToken);

    /// <summary>Removes a definition, if the id names one.</summary>
    Task DeleteAsync(string eventId, CancellationToken cancellationToken);

    /// <summary>Where the files live, so the page can say it rather than hard-code it.</summary>
    string DefinitionsDirectory { get; }
}

/// <summary>
/// A projection built once from the item catalog that has to be dropped when it is refreshed.
/// </summary>
/// <remarks>
/// Exists so the startup coordinator can invalidate the recognition resolver alongside the
/// five catalogs it already invalidates after a sync. The resolver itself lives in
/// Infrastructure and Application cannot name it, and that is precisely how it came to be the
/// one cache the post-sync block missed: on a fresh install it was built from an empty item
/// table and kept, so every scan returned no_match until the application was restarted.
/// </remarks>
public interface IInvalidatableProjection
{
    void Invalidate();
}
