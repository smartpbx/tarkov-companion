using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Stash;

namespace TarkovCompanion.Application.Services.StashScan;

public enum StashSpecialistIntelligenceKind
{
    None = 1,
    Ammo,
    Key,

    /// <summary>
    /// Something the player uses rather than trades: a weapon, armour, a rig, a case, meds.
    /// </summary>
    /// <remarks>
    /// Whether a rifle is surplus depends on the loadouts the player means to run, which the
    /// loadout planner knows and a price does not. The first real render of a sorted stash put
    /// an ammo case full of ammo, an M4A1 and the player's armour under "Sell", because nothing
    /// needed them for a quest. Until the loadout planner answers, gear waits under Review with
    /// what it would fetch, the way ammo and keys wait for their own services.
    /// </remarks>
    Gear,
}

/// <summary>
/// One occurrence plus already-computed recommendation/economic facts. Ammo and key intelligence
/// is supplied by its owning service; this planner has no scoring fallback and therefore cannot
/// silently re-derive #308 when that dependency is unresolved.
/// </summary>
public sealed record StashPlanningItemInput
{
    public StashPlanningItemInput(
        string itemKey,
        string canonicalItemId,
        string containerPath,
        GridCellAddress anchor,
        RecommendationResult? recommendation,
        StashSpecialistIntelligenceKind specialistKind,
        ResultStatus specialistStatus,
        EvidencedValue<long?> fleaFeeRoubles,
        EvidencedValue<long?> netValueRoubles,
        EvidencedValue<long?> valuePerSquareRoubles,
        EvidencedValue<TimeSpan?> age,
        EvidencedValue<string> scarcity,
        EvidencedValue<string> obtainability)
    {
        ItemKey = Required(itemKey, nameof(itemKey));
        CanonicalItemId = Required(canonicalItemId, nameof(canonicalItemId));
        ContainerPath = ContainerPaths.Validate(containerPath, nameof(containerPath));
        Anchor = anchor;
        Recommendation = recommendation;
        SpecialistKind = Enum.IsDefined(specialistKind)
            ? specialistKind
            : throw new ArgumentOutOfRangeException(nameof(specialistKind));
        SpecialistStatus = specialistStatus ?? throw new ArgumentNullException(nameof(specialistStatus));
        FleaFeeRoubles = fleaFeeRoubles ?? throw new ArgumentNullException(nameof(fleaFeeRoubles));
        NetValueRoubles = netValueRoubles ?? throw new ArgumentNullException(nameof(netValueRoubles));
        ValuePerSquareRoubles = valuePerSquareRoubles ?? throw new ArgumentNullException(nameof(valuePerSquareRoubles));
        Age = age ?? throw new ArgumentNullException(nameof(age));
        Scarcity = scarcity ?? throw new ArgumentNullException(nameof(scarcity));
        Obtainability = obtainability ?? throw new ArgumentNullException(nameof(obtainability));
    }

    public string ItemKey { get; }
    public string CanonicalItemId { get; }
    public string ContainerPath { get; }
    public GridCellAddress Anchor { get; }
    public RecommendationResult? Recommendation { get; }
    public StashSpecialistIntelligenceKind SpecialistKind { get; }
    public ResultStatus SpecialistStatus { get; }
    public EvidencedValue<long?> FleaFeeRoubles { get; }
    public EvidencedValue<long?> NetValueRoubles { get; }
    public EvidencedValue<long?> ValuePerSquareRoubles { get; }
    public EvidencedValue<TimeSpan?> Age { get; }
    public EvidencedValue<string> Scarcity { get; }
    public EvidencedValue<string> Obtainability { get; }

    /// <summary>A snapshot-scoped review pin; unlike a profile pin it addresses this occurrence.</summary>
    public bool IsPinned { get; init; }

    /// <summary>How many matching occurrences can be moved together, including this one.</summary>
    public int MoveTogetherCount { get; init; } = 1;

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= 256 ? trimmed : throw new ArgumentOutOfRangeException(parameterName);
    }
}

public sealed record StashOrganizationPlanRequest
{
    public StashOrganizationPlanRequest(
        string planId,
        string snapshotId,
        long revision,
        DateTimeOffset generatedUtc,
        IReadOnlyList<StashPlanningItemInput> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        PlanId = planId.Trim();
        SnapshotId = snapshotId.Trim();
        Revision = revision >= 1 ? revision : throw new ArgumentOutOfRangeException(nameof(revision));
        GeneratedUtc = generatedUtc.Offset == TimeSpan.Zero && generatedUtc != default
            ? generatedUtc
            : throw new ArgumentException("Generation time must be a defined UTC instant.", nameof(generatedUtc));
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > StashScanBounds.MaximumPlanItems)
        {
            throw new ArgumentException("Planning items exceed their bound.", nameof(items));
        }

        var copy = items
            .Select(item => item ?? throw new ArgumentException("Planning items cannot contain null.", nameof(items)))
            .ToArray();
        if (copy.Select(item => item.ItemKey).Distinct(StringComparer.Ordinal).Count() != copy.Length)
        {
            throw new ArgumentException("Planning item keys must be unique.", nameof(items));
        }

        Items = Array.AsReadOnly(copy);
    }

    public string PlanId { get; }
    public string SnapshotId { get; }
    public long Revision { get; }
    public DateTimeOffset GeneratedUtc { get; }
    public IReadOnlyList<StashPlanningItemInput> Items { get; }
}

/// <summary>Produces a checklist only; it has no operation that can control or mutate EFT.</summary>
public sealed class StashOrganizationPlanner
{
    public StashOrganizationPlan Build(StashOrganizationPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var moveTogetherCounts = request.Items
            .Where(CanMoveTogether)
            .GroupBy(item => item.CanonicalItemId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var items = request.Items
            .Select(item => Project(
                item with { MoveTogetherCount = moveTogetherCounts.GetValueOrDefault(item.CanonicalItemId, 1) },
                request.GeneratedUtc))
            .ToArray();
        return new StashOrganizationPlan(
            request.PlanId,
            request.SnapshotId,
            request.Revision,
            request.GeneratedUtc,
            items);
    }

    private static bool CanMoveTogether(StashPlanningItemInput input)
    {
        var decision = input.Recommendation?.Decision.Value;
        var specialistUnresolved = input.SpecialistKind != StashSpecialistIntelligenceKind.None &&
                                   input.SpecialistStatus.Completeness != ResultCompleteness.Complete;
        return !input.IsPinned &&
               (specialistUnresolved || decision is null || decision.Action == RecommendationAction.Review);
    }

    private static StashOrganizationItem Project(
        StashPlanningItemInput input,
        DateTimeOffset generatedUtc)
    {
        var decision = input.Recommendation?.Decision.Value;
        var specialistUnresolved = input.SpecialistKind != StashSpecialistIntelligenceKind.None &&
                                   input.SpecialistStatus.Completeness != ResultCompleteness.Complete;
        var organizeTogether = input.MoveTogetherCount > 1 &&
                               (specialistUnresolved || decision is null || decision.Action == RecommendationAction.Review);
        var group = input.IsPinned
            ? StashPlanGroup.Keep
            : organizeTogether
                ? StashPlanGroup.Organize
                : specialistUnresolved || decision is null
                    ? StashPlanGroup.Review
                    : decision.Action switch
                    {
                        RecommendationAction.Keep => StashPlanGroup.Keep,
                        RecommendationAction.SellOnFlea or RecommendationAction.SellToTrader => StashPlanGroup.Sell,
                        RecommendationAction.UseSoon => StashPlanGroup.UseSoon,
                        RecommendationAction.Organize => StashPlanGroup.Organize,
                        RecommendationAction.Review => StashPlanGroup.Review,
                        _ => StashPlanGroup.Review,
                    };
        var allReasonCodes = decision?.Reasons.Select(reason => BoundedCode(reason.Code)).ToArray() ?? [];
        var reasonCodes = allReasonCodes
            .Take(StashScanBounds.MaximumReasonsPerItem - 1)
            .ToList();
        if (allReasonCodes.Length >= StashScanBounds.MaximumReasonsPerItem)
        {
            reasonCodes.Add("stash.plan.reason-limit");
        }

        if (specialistUnresolved)
        {
            reasonCodes.Insert(0, input.SpecialistKind switch
            {
                StashSpecialistIntelligenceKind.Ammo => "stash.specialist.ammo-unresolved",
                StashSpecialistIntelligenceKind.Gear => "stash.specialist.gear-unresolved",
                _ => "stash.specialist.key-unresolved",
            });
            if (reasonCodes.Count > StashScanBounds.MaximumReasonsPerItem)
            {
                reasonCodes.RemoveAt(reasonCodes.Count - 1);
            }
        }
        else if (decision is null)
        {
            reasonCodes.Add("stash.recommendation.unresolved");
        }

        if (organizeTogether)
        {
            reasonCodes.Insert(0, $"stash.organize.move-together.{input.MoveTogetherCount}");
        }

        if (input.IsPinned)
        {
            reasonCodes.Insert(0, "stash.review-command.pinned");
        }

        if (reasonCodes.Count > StashScanBounds.MaximumReasonsPerItem)
        {
            reasonCodes.RemoveRange(StashScanBounds.MaximumReasonsPerItem, reasonCodes.Count - StashScanBounds.MaximumReasonsPerItem);
        }

        var recommendationStatus = input.IsPinned
            ? new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "stash.plan.review-pin")
            : organizeTogether
                ? new ResultStatus(
                    ResultCompleteness.Partial,
                    CombineFreshness(input.Recommendation?.Decision.Status.Freshness, input.SpecialistStatus.Freshness),
                    "stash.plan.move-together",
                    "Matching occurrences can be grouped even while their keep or sell decision remains unresolved.")
                : specialistUnresolved
            ? new ResultStatus(
                input.Recommendation is null ? ResultCompleteness.Unknown : ResultCompleteness.Partial,
                CombineFreshness(input.Recommendation?.Decision.Status.Freshness, input.SpecialistStatus.Freshness),
                "stash.plan.specialist-unresolved",
                "Ammo, key or gear intelligence must come from the profile-aware specialist service before this item leaves Review.")
            : input.Recommendation?.Decision.Status ?? new ResultStatus(
                ResultCompleteness.Unknown,
                FreshnessState.Unknown,
                "stash.plan.recommendation-unresolved");
        var version = input.Recommendation?.RulesetVersion ?? "stash-plan-1:unresolved";
        return new StashOrganizationItem(
            input.ItemKey,
            input.CanonicalItemId,
            input.ContainerPath,
            input.Anchor,
            group,
            Operations(group),
            reasonCodes,
            input.FleaFeeRoubles,
            input.NetValueRoubles,
            input.ValuePerSquareRoubles,
            input.Age,
            input.Scarcity,
            input.Obtainability,
            recommendationStatus,
            version,
            generatedUtc);
    }

    private static IReadOnlyList<StashManualOperation> Operations(StashPlanGroup group) => group switch
    {
        StashPlanGroup.Keep => [StashManualOperation.Group, StashManualOperation.Stage],
        StashPlanGroup.Sell => [StashManualOperation.Group, StashManualOperation.AddToSellQueue],
        StashPlanGroup.UseSoon => [StashManualOperation.Group, StashManualOperation.Stage],
        StashPlanGroup.Organize => [StashManualOperation.Group, StashManualOperation.Consolidate],
        _ => [StashManualOperation.ReviewEvidence, StashManualOperation.Rescan],
    };

    private static FreshnessState CombineFreshness(FreshnessState? recommendation, FreshnessState specialist)
    {
        if (recommendation == FreshnessState.Stale || specialist == FreshnessState.Stale)
        {
            return FreshnessState.Stale;
        }

        return recommendation == FreshnessState.Current && specialist == FreshnessState.Current
            ? FreshnessState.Current
            : FreshnessState.Unknown;
    }

    private static string BoundedCode(string code)
    {
        if (code.Length <= 128)
        {
            return code;
        }

        return $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant()}";
    }
}
