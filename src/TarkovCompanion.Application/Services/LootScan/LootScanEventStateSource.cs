using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.Application.Services.LootScan;

/// <summary>
/// What the Events page holds about an item, in the shape the recommendation engine asks for.
/// </summary>
/// <remarks>
/// <para>
/// The Loot Scan told the engine every item's event state was "Unknown", known and certain, so
/// food the player had marked Allergic on the Events page was weighed on price like anything
/// else. The page writes its results into the active profile, keyed by event and item; this
/// reads them back for the events that are running at the moment of the scan.
/// </para>
/// <para>
/// An item in no running event is outside every event, which is a settled "Unknown" and needs
/// no scope. One in a running event with nothing recorded is Untested. Where two running events
/// list the same item, an Allergic result in either wins, because that is the answer that keeps
/// the player from eating it.
/// </para>
/// </remarks>
public sealed class LootScanEventStateSource(IPlayerProfileService profiles, IEventCatalog events)
{
    private readonly IPlayerProfileService _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    private readonly IEventCatalog _events = events ?? throw new ArgumentNullException(nameof(events));

    /// <summary>Reads the profile's recorded results and the running events once for a whole scan.</summary>
    public async Task<LootScanEventStateSnapshot> ReadAsync(DateTimeOffset evaluatedUtc, CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var definitions = await _events.GetAsync(cancellationToken).ConfigureAwait(false);
        return new(
            profile,
            [.. definitions.Where(definition => IsRunning(definition, evaluatedUtc))]);
    }

    internal static bool IsRunning(EventDefinition definition, DateTimeOffset at) =>
        definition.Active &&
        (definition.StartUtc is not { } start || start <= at) &&
        (definition.EndUtc is not { } end || end >= at);
}

/// <summary>One scan's view of the running events: read once, asked once per item.</summary>
public sealed class LootScanEventStateSnapshot
{
    private static readonly ProducerIdentity Producer = new("Tarkov Companion loot scan", "loot-scan-event-state-source-1");

    private readonly PlayerProfile _profile;
    private readonly IReadOnlyList<EventDefinition> _running;

    internal LootScanEventStateSnapshot(PlayerProfile profile, IReadOnlyList<EventDefinition> running)
    {
        _profile = profile;
        _running = running;
    }

    /// <summary>
    /// The item's state in a running event with the scope that state belongs to, or a settled
    /// "outside every event" with no scope.
    /// </summary>
    public RecommendationEventStateFacts StateFor(string itemId, InventoryProfileScope profileScope, DateTimeOffset evaluatedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentNullException.ThrowIfNull(profileScope);
        var recorded = _running
            .Where(definition => definition.ApplicableItemIds.Contains(itemId))
            .Select(definition => (
                Definition: definition,
                State: _profile.EventItemStates.GetValueOrDefault($"{definition.Id}:{itemId}", EventItemState.Untested)))
            .OrderByDescending(entry => entry.State == EventItemState.Allergic)
            .ThenBy(entry => entry.Definition.Id, StringComparer.Ordinal)
            .ToArray();

        // The player's own record, dated when the profile last changed and never after the scan.
        var observedUtc = _profile.UpdatedUtc <= evaluatedUtc ? _profile.UpdatedUtc : evaluatedUtc;
        if (recorded.Length == 0)
        {
            return new(null, Known(EventItemState.Unknown, "events/none", observedUtc));
        }

        var (definition, state) = recorded[0];
        return new(
            new RecommendationEventScope(profileScope, definition.Id, RulesetVersion(definition), itemId),
            Known(state, $"events/{definition.Id}/{itemId}", observedUtc));
    }

    /// <summary>
    /// A definition edited on the Events page is a different ruleset, so a result recorded under
    /// the old rules is not carried silently into the new ones by the engine's scope check.
    /// </summary>
    private static string RulesetVersion(EventDefinition definition) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(definition.RulesJson ?? string.Empty)))[..16];

    private static EvidencedValue<EventItemState?> Known(EventItemState state, string identifier, DateTimeOffset observedUtc) =>
        new(
            "profile.event-state",
            state,
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "profile.event-state.known"),
            // The engine trusts Safe and Allergic only as the player's own entry.
            new EvidenceProvenance(
                EvidenceSourceClass.UserEntered,
                identifier,
                observedUtc,
                EvidenceConfidence.Certain,
                Producer));
}
