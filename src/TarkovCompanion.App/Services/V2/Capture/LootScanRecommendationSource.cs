using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>
/// Turns each item a scan named into the inputs the recommendation engine asks for, using only
/// what the companion actually holds.
/// </summary>
/// <remarks>
/// <para>
/// The handoff used to pass the decision service an empty list, so every named item came back
/// "no recommendation was produced" and the workspace had never once been shown a real value.
/// </para>
/// <para>
/// What the catalog settles is supplied as settled: what a trader pays, the 24-hour flea average,
/// the squares the item was seen to occupy. What nothing in the companion knows is supplied as
/// unknown, never as a convenient default, and the engine declines accordingly:
/// the flea fee (json.tarkov.dev publishes neither the fee nor its rates, so a flea price is
/// gross and the net is absent), whether a quest or the hideout still needs the item (the needs
/// aggregation is not connected here yet), and how scarce it is. A scan made today is therefore
/// valued but not decided, and the workspace says so.
/// </para>
/// </remarks>
public sealed class LootScanRecommendationSource(IItemRepository items)
{
    private static readonly ProducerIdentity Producer = new("Tarkov Companion loot scan", "loot-scan-recommendation-source-1");

    private readonly IItemRepository _items = items ?? throw new ArgumentNullException(nameof(items));

    public async Task<IReadOnlyList<LootScanCandidateRecommendation>> BuildAsync(
        GridReconstructionResult visibleLoot,
        CaptureSessionId sessionId,
        string artifactId,
        int decodeRevision,
        string contentSha256,
        InventoryProfileScope scope,
        string dataSnapshotId,
        DateTimeOffset evaluatedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(visibleLoot);
        var candidates = new List<LootScanCandidateRecommendation>();
        foreach (var cell in visibleLoot.Recognition?.Cells ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cell.Item.Value is not { } item ||
                cell.Item.Status.Completeness != ResultCompleteness.Complete ||
                item.CanonicalId.Value is not { } itemId ||
                item.WidthCells.Value is not { } width ||
                item.HeightCells.Value is not { } height ||
                item.Quantity.Value is not { } quantity)
            {
                continue;
            }

            var definition = await _items.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
            var price = await _items.GetPriceAsync(itemId, cancellationToken).ConfigureAwait(false);
            if (definition is null || price is null)
            {
                continue;
            }

            // A price synced after this evaluation began cannot be evidence for it.
            var observedUtc = price.Provenance.ObservedUtc <= evaluatedUtc ? price.Provenance.ObservedUtc : evaluatedUtc;
            var catalog = new EvidenceProvenance(
                EvidenceSourceClass.PublicStructuredData,
                $"json.tarkov.dev/items/{itemId}",
                observedUtc,
                EvidenceConfidence.Certain,
                Producer);
            var unknown = new EvidenceProvenance(
                EvidenceSourceClass.PublicStructuredData,
                $"json.tarkov.dev/items/{itemId}",
                observedUtc,
                EvidenceConfidence.Unscored,
                Producer);

            var fleaGross = definition.FleaEligible ? price.Average24HourRoubles ?? price.FleaPriceRoubles : null;
            var trader = price.BestTrader?.ValueRoubles;
            var economics = new RecommendationEconomics(
                fleaGross is { } gross
                    ? Known<long?>("economics.flea-gross", checked(gross * quantity), catalog)
                    : Absent<long?>("economics.flea-gross", "flea-gross.not-sold", catalog),
                Unread<long?>("economics.flea-fee", "flea-fee.not-synced", unknown),
                fleaGross is null
                    ? Absent<long?>("economics.flea-net", "flea-net.not-sold", catalog)
                    : Unread<long?>("economics.flea-net", "flea-net.fee-not-synced", unknown),
                trader is { } traderValue
                    ? Known<long?>("economics.trader", checked(traderValue * quantity), catalog)
                    : Absent<long?>("economics.trader", "trader.not-bought", catalog),
                Known<int?>("economics.squares", checked(width * height), item.WidthCells.Provenance),
                item.Condition.Value == ItemConditionReading.NotApplicable
                    ? Known<double?>("economics.condition", 1, catalog)
                    : Unread<double?>("economics.condition", "condition.unread", unknown));

            var profile = new RecommendationProfileFacts(
                new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current, "profile.needs-not-evaluated"),
                unknown,
                Known<RecommendationExplicitActionState?>("profile.explicit-action", RecommendationExplicitActionState.None, catalog),
                Known<bool?>("profile.protected", false, catalog),
                Known<bool?>("profile.pinned", false, catalog),
                Unread<bool?>("profile.wishlist", "wishlist.not-consulted", unknown),
                new RecommendationEventStateFacts(null, Known<EventItemState?>("profile.event-state", EventItemState.Unknown, catalog)),
                []);

            candidates.Add(new(
                new LootScanEvidenceBinding(sessionId, artifactId, decodeRevision, contentSha256, cell.Anchor, itemId),
                $"loot-scan-{cell.Anchor.Row}-{cell.Anchor.Column}",
                scope,
                dataSnapshotId,
                profile,
                economics,
                new RecommendationScarcityFacts(Unread<RecommendationObtainabilityBand?>(
                    "scarcity.obtainability",
                    "scarcity.not-evaluated",
                    unknown))));
        }

        return candidates;
    }

    private static EvidencedValue<T> Known<T>(string fieldId, T value, EvidenceProvenance provenance) =>
        new(fieldId, value, new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, fieldId + ".known"), provenance);

    /// <summary>The source says there is no such value: a channel that does not buy this item.</summary>
    private static EvidencedValue<T> Absent<T>(string fieldId, string code, EvidenceProvenance provenance) =>
        new(fieldId, default, new ResultStatus(ResultCompleteness.Unavailable, FreshnessState.Current, code), provenance);

    /// <summary>Nothing the companion holds answers this, which is not the same as "no".</summary>
    private static EvidencedValue<T> Unread<T>(string fieldId, string code, EvidenceProvenance provenance) =>
        new(fieldId, default, new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current, code), provenance);
}
