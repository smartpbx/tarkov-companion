using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Recommendations;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Recommendations;
using V2RecommendationAction = TarkovCompanion.Core.Abstractions.V2.RecommendationAction;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>
/// Gives Intel and Keep the same held-item answer from the #274 engine. The item-card question
/// deliberately leaves found-in-raid status unread: a catalog item is not a particular stash
/// copy, so claiming that status would turn a general card into invented evidence.
/// </summary>
public sealed class ItemRecommendationAdvisor(
    IItemRepository items,
    IProfileRuntimeContextService profileContext,
    LootScanRecommendationSource facts,
    TimeProvider? timeProvider = null) : IItemRecommendationAdvisor
{
    private static readonly ProducerIdentity Producer = new(
        "Tarkov Companion item card",
        "item-card-recommendation-1");

    private readonly IItemRepository _items = items ?? throw new ArgumentNullException(nameof(items));
    private readonly IProfileRuntimeContextService _profileContext = profileContext ?? throw new ArgumentNullException(nameof(profileContext));
    private readonly LootScanRecommendationSource _facts = facts ?? throw new ArgumentNullException(nameof(facts));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyDictionary<string, V2ItemRecommendation>> GetAsync(
        IReadOnlyCollection<string> itemIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count == 0)
        {
            return new Dictionary<string, V2ItemRecommendation>(StringComparer.Ordinal);
        }

        var runtime = _profileContext.Current.IsInitialized
            ? _profileContext.Current
            : await _profileContext.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (runtime.ActiveProfile is not { } profile)
        {
            return new Dictionary<string, V2ItemRecommendation>(StringComparer.Ordinal);
        }

        var evaluatedUtc = _timeProvider.GetUtcNow();
        var scope = new InventoryProfileScope(
            profile.Context.Identity.ProfileId,
            profile.Context.Identity.Generation,
            profile.Context.Mode.ToString());
        var (rates, needs) = await _facts.ReadSharedFactsAsync(cancellationToken).ConfigureAwait(false);
        var engine = new ExplainableRecommendationEngine();
        var results = new Dictionary<string, V2ItemRecommendation>(StringComparer.Ordinal);
        foreach (var itemId in itemIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = await _items.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                continue;
            }

            var provenance = new EvidenceProvenance(
                EvidenceSourceClass.PublicStructuredData,
                $"json.tarkov.dev/items/{item.Id}",
                Earlier(item.Provenance.SourceUpdatedUtc, evaluatedUtc),
                EvidenceConfidence.Unscored,
                Producer);
            var read = await _facts.ReadItemFactsAsync(
                    new(item.Id, item.Dimensions.Width, item.Dimensions.Height, 1, provenance),
                    profile,
                    needs,
                    rates,
                    evaluatedUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            if (read is null)
            {
                continue;
            }

            var recommendation = engine.Evaluate(
                new ExplainableRecommendationRequest(
                    $"item-card-{item.Id}",
                    item.Id,
                    RecommendationUseCase.ItemCard,
                    evaluatedUtc,
                    scope,
                    profile.Context.DataSnapshot.SnapshotId,
                    Unread<bool?>("candidate.fir", "item-card.copy-unspecified", provenance),
                    read.Profile,
                    read.Economics,
                    read.Scarcity),
                cancellationToken);
            if (recommendation.Decision.Value is not { } decision)
            {
                continue;
            }

            results[item.Id] = new(
                decision.Action,
                Verdict(decision.Action),
                decision.Reasons.FirstOrDefault()?.Explanation ?? decision.Summary,
                recommendation.RulesetVersion);
        }

        return results;
    }

    private static DateTimeOffset Earlier(DateTimeOffset? observedUtc, DateTimeOffset evaluatedUtc) =>
        observedUtc is { } observed && observed < evaluatedUtc ? observed : evaluatedUtc;

    private static EvidencedValue<T> Unread<T>(string fieldId, string code, EvidenceProvenance provenance) =>
        new(fieldId, default, new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current, code), provenance);

    private static string Verdict(V2RecommendationAction action) => action switch
    {
        V2RecommendationAction.Keep => "Keep",
        V2RecommendationAction.SellOnFlea or V2RecommendationAction.SellToTrader => "Sell",
        V2RecommendationAction.UseSoon => "Use soon",
        V2RecommendationAction.AvoidConsume => "Don't use",
        V2RecommendationAction.Take => "Take",
        V2RecommendationAction.Leave => "Leave",
        _ => "Review",
    };
}
