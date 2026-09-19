using TarkovCompanion.Application.Services.Recommendations;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Core.Domain.Stash;
using RecommendationReason = TarkovCompanion.Core.Abstractions.V2.RecommendationReason;
using RecommendationResult = TarkovCompanion.Core.Abstractions.V2.RecommendationResult;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>One sorted stash: the plan, and for each item the engine's reasons in words.</summary>
public sealed record StashSortPlan(
    StashOrganizationPlan Plan,
    IReadOnlyDictionary<string, IReadOnlyList<RecommendationReason>> ReasonsByItemKey);

/// <summary>
/// Sorts a reconstructed stash into Keep, Sell, Use soon and Review.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StashOrganizationPlanner"/> was complete and tested and nothing called it, so
/// every item in every stash scan sat under Review and three of the workspace's four plan tiles
/// said "not wired". What it lacked was a caller that could hand it a recommendation per item.
/// This is that caller: each named tile is put to the same engine the Loot Scan uses, as a
/// stash question rather than a loot one, from the same facts
/// (<see cref="LootScanRecommendationSource.ReadItemFactsAsync"/>): the profile's pins and item
/// rules, outstanding quest and hideout needs, the flea net after its fee, what a trader pays.
/// </para>
/// <para>
/// No holdings are handed to the engine. The stash being sorted <em>is</em> the holdings, and
/// subtracting it from a need would count the item against itself: three Salewas held against
/// a quest that wants three would all read "need met, sell". So an item something still needs
/// is Keep for every copy, and the reason says how many are wanted. Telling the spare copies
/// apart is not done yet.
/// </para>
/// <para>
/// Ammo and keys stay under Review here. The planner itself requires their answer to come from
/// the profile-aware ammo and key services, and those have no stash handoff yet.
/// </para>
/// </remarks>
public sealed class StashPlanSource(
    LootScanRecommendationSource facts,
    ExplainableRecommendationEngine? engine = null,
    StashOrganizationPlanner? planner = null)
{
    private static readonly ProducerIdentity Producer = new("Tarkov Companion stash plan", "stash-plan-source-1");

    private readonly LootScanRecommendationSource _facts = facts ?? throw new ArgumentNullException(nameof(facts));
    private readonly ExplainableRecommendationEngine _engine = engine ?? new ExplainableRecommendationEngine();
    private readonly StashOrganizationPlanner _planner = planner ?? new StashOrganizationPlanner();

    /// <param name="specialistKind">Which items are ammo or keys, which the caller already knows from the catalog.</param>
    public async Task<StashSortPlan> BuildAsync(
        StashReconstruction reconstruction,
        string snapshotId,
        ProfileRecord profile,
        Func<string, StashSpecialistIntelligenceKind> specialistKind,
        DateTimeOffset evaluatedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reconstruction);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(specialistKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        var scope = new InventoryProfileScope(
            profile.Context.Identity.ProfileId,
            profile.Context.Identity.Generation,
            profile.Context.Mode.ToString());
        var (rates, needs) = await _facts.ReadSharedFactsAsync(cancellationToken).ConfigureAwait(false);
        var inputs = new List<StashPlanningItemInput>();
        var reasons = new Dictionary<string, IReadOnlyList<RecommendationReason>>(StringComparer.Ordinal);
        foreach (var tile in reconstruction.Containers
                     .SelectMany(container => container.Tiles)
                     .Where(tile => tile.ItemId is not null)
                     .Take(StashScanBounds.MaximumPlanItems))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemId = tile.ItemId!;
            var kind = specialistKind(itemId);
            var unread = new EvidenceProvenance(
                EvidenceSourceClass.PublicStructuredData,
                $"json.tarkov.dev/items/{itemId}",
                evaluatedUtc,
                EvidenceConfidence.Unscored,
                Producer);
            var read = kind == StashSpecialistIntelligenceKind.None
                ? await _facts.ReadItemFactsAsync(
                        new(itemId, tile.Width, tile.Height, tile.Quantity ?? 1, Footprint(tile, evaluatedUtc)),
                        profile,
                        needs,
                        rates,
                        evaluatedUtc,
                        cancellationToken)
                    .ConfigureAwait(false)
                : null;
            RecommendationResult? recommendation = null;
            if (read is not null)
            {
                recommendation = _engine.Evaluate(
                    new ExplainableRecommendationRequest(
                        $"stash-plan-{tile.ItemKey}",
                        itemId,
                        RecommendationUseCase.Stash,
                        evaluatedUtc,
                        scope,
                        profile.Context.DataSnapshot.SnapshotId,
                        Unread<bool?>("candidate.fir", "fir.unread", unread),
                        read.Profile,
                        read.Economics,
                        read.Scarcity),
                    cancellationToken);
                if (recommendation.Decision.Value is { } decision)
                {
                    reasons[tile.ItemKey] = decision.Reasons;
                }
            }

            var economics = read?.Economics;
            var net = Best(economics?.FleaNetRoubles, economics?.TraderRoubles)
                      ?? Unread<long?>("stash.plan.net-value", "net-value.unread", unread);
            inputs.Add(new(
                tile.ItemKey,
                itemId,
                tile.ContainerPath,
                new GridCellAddress(tile.Row, tile.Column),
                recommendation,
                kind,
                kind == StashSpecialistIntelligenceKind.None
                    ? new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "stash.specialist.not-applicable")
                    : new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current, "stash.specialist.no-stash-handoff"),
                economics?.FleaFeeRoubles ?? Unread<long?>("stash.plan.flea-fee", "flea-fee.unread", unread),
                net,
                net.Value is { } value && tile.Width * tile.Height > 0
                    ? new EvidencedValue<long?>("stash.plan.value-per-square", value / (tile.Width * tile.Height), net.Status, net.Provenance)
                    : Unread<long?>("stash.plan.value-per-square", "value-per-square.unread", unread),
                Unread<TimeSpan?>("stash.plan.age", "age.not-tracked", unread),
                Unread<string>("stash.plan.scarcity", "scarcity.never-claimed", unread),
                read?.Scarcity.Obtainability.Value is { } band
                    ? new EvidencedValue<string>(
                        "stash.plan.obtainability",
                        band.ToString(),
                        read.Scarcity.Obtainability.Status,
                        read.Scarcity.Obtainability.Provenance)
                    : Unread<string>("stash.plan.obtainability", "obtainability.unread", unread)));
        }

        var plan = _planner.Build(new(
            $"stash-plan-{snapshotId}",
            snapshotId,
            revision: 1,
            evaluatedUtc,
            inputs));
        return new(plan, reasons);
    }

    /// <summary>The better of the two ways to sell, where either is settled.</summary>
    private static EvidencedValue<long?>? Best(EvidencedValue<long?>? fleaNet, EvidencedValue<long?>? trader) =>
        (fleaNet?.Value, trader?.Value) switch
        {
            (null, null) => null,
            ({ } flea, { } paid) => flea >= paid ? fleaNet : trader,
            ({ }, null) => fleaNet,
            _ => trader,
        };

    /// <summary>
    /// The tile's footprint, as sure as the reading that found it and dated to the plan.
    /// </summary>
    /// <remarks>
    /// The engine ages a footprint like a price, which suits a loot screen read a moment ago. A
    /// stash snapshot is opened again the next day, and the squares an item covers have not
    /// changed overnight, so the footprint is restated when the plan is made and keeps the
    /// original reading, with its own date and confidence, as what it was derived from.
    /// </remarks>
    private static EvidenceProvenance Footprint(StashReconstructedTile tile, DateTimeOffset evaluatedUtc) =>
        tile.Provenance.EvidenceThroughUtc > evaluatedUtc
            ? tile.Provenance
            : new(
                EvidenceSourceClass.DerivedCalculation,
                $"stash-plan/footprint/{tile.ItemKey}",
                evaluatedUtc,
                tile.Provenance.Confidence,
                Producer,
                generatedUtc: evaluatedUtc,
                inputs: [tile.Provenance]);

    private static EvidencedValue<T> Unread<T>(string fieldId, string code, EvidenceProvenance provenance) =>
        new(fieldId, default!, new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current, code), provenance);
}
