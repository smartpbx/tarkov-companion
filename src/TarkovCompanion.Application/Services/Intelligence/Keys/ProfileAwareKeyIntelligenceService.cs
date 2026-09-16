using System.Globalization;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Keys;

namespace TarkovCompanion.Application.Services.Intelligence.Keys;

public sealed record ProfileAwareKeyIntelligenceRequest
{
    public ProfileAwareKeyIntelligenceRequest(
        KeyIntelligenceEntryPoint entryPoint,
        string itemId,
        DateTimeOffset evaluatedUtc,
        KeyInventoryFacts inventory,
        ResultStatus requirementsStatus,
        EvidenceProvenance requirementsProvenance,
        IReadOnlyList<KeyRequirementFact> requirements,
        KeyUtilityFacts utility,
        ReviewedKeyOverride? reviewedOverride = null)
    {
        EntryPoint = Enum.IsDefined(entryPoint)
            ? entryPoint
            : throw new ArgumentOutOfRangeException(nameof(entryPoint));
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ItemId = itemId.Trim();
        if (ItemId.Length > KeyIntelligenceBounds.MaximumIdentifierLength)
        {
            throw new ArgumentOutOfRangeException(nameof(itemId));
        }

        EvaluatedUtc = evaluatedUtc != default && evaluatedUtc.Offset == TimeSpan.Zero
            ? evaluatedUtc
            : throw new ArgumentException("Evaluation time must be a defined UTC instant.", nameof(evaluatedUtc));
        Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        RequirementsStatus = requirementsStatus ?? throw new ArgumentNullException(nameof(requirementsStatus));
        RequirementsProvenance = requirementsProvenance ?? throw new ArgumentNullException(nameof(requirementsProvenance));
        Utility = utility ?? throw new ArgumentNullException(nameof(utility));
        if (!string.Equals(ItemId, inventory.ItemId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Inventory facts must describe the requested key.", nameof(inventory));
        }

        ArgumentNullException.ThrowIfNull(requirements);
        if (requirements.Count > KeyIntelligenceBounds.MaximumRequirements)
        {
            throw new ArgumentException(
                $"Key intelligence cannot accept more than {KeyIntelligenceBounds.MaximumRequirements} requirements.",
                nameof(requirements));
        }

        var copied = requirements
            .Select(requirement => requirement ?? throw new ArgumentException("Requirements cannot contain null.", nameof(requirements)))
            .OrderBy(requirement => requirement.RequirementId, StringComparer.Ordinal)
            .ToArray();
        if (copied.Select(requirement => requirement.RequirementId).Distinct(StringComparer.Ordinal).Count() != copied.Length)
        {
            throw new ArgumentException("Requirement identifiers must be unique.", nameof(requirements));
        }

        Requirements = Array.AsReadOnly(copied);
        if (reviewedOverride is { } reviewed && reviewed.ReviewedUtc > EvaluatedUtc)
        {
            throw new ArgumentException("A reviewed override cannot postdate this evaluation.", nameof(reviewedOverride));
        }

        ReviewedOverride = reviewedOverride;
    }

    public KeyIntelligenceEntryPoint EntryPoint { get; }
    public string ItemId { get; }
    public DateTimeOffset EvaluatedUtc { get; }
    public KeyInventoryFacts Inventory { get; }
    public ResultStatus RequirementsStatus { get; }
    public EvidenceProvenance RequirementsProvenance { get; }
    public IReadOnlyList<KeyRequirementFact> Requirements { get; }
    public KeyUtilityFacts Utility { get; }
    public ReviewedKeyOverride? ReviewedOverride { get; }
}

/// <summary>
/// Produces reviewable key advice from explicit facts. It deliberately accepts no display name,
/// so a lock, room, route, or expected-loot claim cannot be inferred from item text.
/// </summary>
public sealed class ProfileAwareKeyIntelligenceService
{
    public const string CurrentRulesetVersion = "key-intelligence-308.1";

    public ProfileAwareKeyIntelligenceResult Evaluate(
        ProfileAwareKeyIntelligenceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var reasons = new List<KeyIntelligenceReason>();
        var missing = new List<string>();
        var current = request.Requirements
            .Where(requirement => requirement.Timing == KeyRequirementTiming.Current)
            .ToArray();
        var future = request.Requirements
            .Where(requirement => requirement.Timing == KeyRequirementTiming.Future)
            .ToArray();
        var unresolvedRequirements = current.Concat(future)
            .Count(requirement => requirement.Status.Completeness != ResultCompleteness.Complete);
        if (unresolvedRequirements > 0)
        {
            missing.Add($"{unresolvedRequirements.ToString(CultureInfo.InvariantCulture)} requirement fact(s) are incomplete.");
            current = current
                .Where(requirement => requirement.Status.Completeness == ResultCompleteness.Complete)
                .ToArray();
            future = future
                .Where(requirement => requirement.Status.Completeness == ResultCompleteness.Complete)
                .ToArray();
        }

        foreach (var requirement in current)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = requirement.RequiresFoundInRaid
                ? KeyIntelligenceReasonCategory.CurrentFoundInRaidQuest
                : KeyIntelligenceReasonCategory.CurrentQuest;
            reasons.Add(new(
                category,
                requirement.RequiresFoundInRaid ? "key.need.current-fir" : "key.need.current",
                RequirementExplanation(requirement, "current"),
                requirement.RequiresFoundInRaid ? 1_000 : 950,
                requirement.Provenance));
        }

        foreach (var requirement in future)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reasons.Add(new(
                KeyIntelligenceReasonCategory.FutureQuest,
                requirement.RequiresFoundInRaid ? "key.need.future-fir" : "key.need.future",
                RequirementExplanation(requirement, "future"),
                900,
                requirement.Provenance));
        }

        var requirementsComplete = request.RequirementsStatus.Completeness == ResultCompleteness.Complete &&
                                   request.Requirements.All(requirement =>
                                       requirement.Status.Completeness == ResultCompleteness.Complete);
        var quest = requirementsComplete
            ? current.Length > 0 ? 100d : future.Length > 0 ? 65d : 0d
            : null;
        if (quest is null)
        {
            missing.Add("Profile requirement coverage is incomplete; current and future quest need cannot be ruled out.");
        }

        AddInventoryReasons(request.Inventory, reasons, missing);
        AddUtilityReasons(request.Utility, reasons, missing);

        var economy = Economy(Known(request.Utility.AcquisitionCostRoubles), Known(request.Utility.ExpectedLootProxyRoubles));
        var uses = Uses(Known(request.Inventory.MaximumUses), Known(request.Inventory.RemainingUses));
        var lockUtility = LockUtility(request.Utility);
        var uniqueAccess = Known(request.Utility.UniqueAccess) is { } unique ? unique ? 100d : 0d : null;
        var riskAdjustedLoot = RiskAdjustedLoot(request.Utility);
        var components = new KeyScoreComponentsV2(
            quest,
            economy,
            uses,
            lockUtility,
            uniqueAccess,
            riskAdjustedLoot);
        if (!components.IsComplete && economy is not null)
        {
            missing.Add("Economic evidence is present, but price alone cannot decide the key tier.");
        }

        var score = components.WeightedTotal;
        var scoreProvenance = score is null ? null : BuildScoreProvenance(request);
        if (score is not null && scoreProvenance is null)
        {
            score = null;
            missing.Add("The score lineage is unavailable or exceeds the bounded evidence contract.");
        }

        var tier = score is { } completeScore ? Tier(completeScore) : KeyIntelligenceTier.Review;
        if (current.Length > 0)
        {
            tier = Higher(tier, KeyIntelligenceTier.A);
        }
        else if (future.Length > 0)
        {
            tier = Higher(tier, KeyIntelligenceTier.B);
        }

        var advice = Advice(tier, current.Length, future.Length);
        if (request.ReviewedOverride is { } reviewed)
        {
            if (reviewed.Score is { } reviewedScore)
            {
                score = reviewedScore;
                scoreProvenance = reviewed.Provenance;
                if (reviewed.Tier is null)
                {
                    tier = Tier(reviewedScore);
                }
            }

            tier = reviewed.Tier ?? tier;
            advice = reviewed.Advice ?? advice;
            reasons.Add(new(
                KeyIntelligenceReasonCategory.CuratedOverride,
                "key.override.reviewed",
                reviewed.Explanation ??
                $"Reviewed override by {reviewed.Reviewer} for game {reviewed.GameVersion} and map data {reviewed.MapDataVersion}.",
                1_100,
                reviewed.Provenance));
        }

        if (score is null && request.ReviewedOverride?.Tier is null && current.Length == 0 && future.Length == 0)
        {
            tier = KeyIntelligenceTier.Review;
        }

        var freshness = Freshness(request);
        if (freshness == FreshnessState.Stale)
        {
            missing.Add("One or more decision facts are stale; Learn Mode must show their source time before reuse.");
        }

        if (missing.Count > 0)
        {
            reasons.Add(new(
                KeyIntelligenceReasonCategory.EvidenceQuality,
                "key.evidence.review-required",
                $"Review required: {missing.Count.ToString(CultureInfo.InvariantCulture)} decision fact(s) are missing or incomplete.",
                100,
                MissingEvidenceProvenance(request)));
        }

        var sortedReasons = reasons
            .OrderByDescending(reason => reason.Priority)
            .ThenBy(reason => reason.Code, StringComparer.Ordinal)
            .ToArray();
        var orderedReasons = sortedReasons.Length <= KeyIntelligenceBounds.MaximumReasons
            ? sortedReasons
            : sortedReasons
                .Take(KeyIntelligenceBounds.MaximumReasons - 1)
                .Append(new KeyIntelligenceReason(
                    KeyIntelligenceReasonCategory.EvidenceQuality,
                    "key.reasons.limit",
                    $"Additional reasons were retained by their source facts but omitted after the {KeyIntelligenceBounds.MaximumReasons.ToString(CultureInfo.InvariantCulture)}-reason display bound.",
                    int.MinValue,
                    MissingEvidenceProvenance(request)))
                .ToArray();
        var complete = score is not null && missing.Count == 0 &&
                       request.RequirementsStatus.Completeness == ResultCompleteness.Complete &&
                       request.Utility.Status.Completeness == ResultCompleteness.Complete;
        var completeness = complete
            ? ResultCompleteness.Complete
            : HasAnyDecisionFact(request)
                ? ResultCompleteness.Partial
                : ResultCompleteness.Unknown;
        var status = new ResultStatus(
            completeness,
            freshness,
            complete
                ? "key.intelligence.complete"
                : completeness == ResultCompleteness.Unknown
                    ? "key.intelligence.unknown"
                    : "key.intelligence.review-required",
            complete
                ? "All score inputs are explicit and bounded."
                : "Missing or incomplete facts remain visible in Learn Mode and keep stash planning conservative.");

        return new ProfileAwareKeyIntelligenceResult(
            request.ItemId,
            request.Inventory.ProfileScope,
            CurrentRulesetVersion,
            status,
            request.Inventory,
            request.RequirementsStatus,
            request.RequirementsProvenance,
            request.Requirements,
            request.Utility,
            components,
            score,
            scoreProvenance,
            tier,
            advice,
            orderedReasons,
            missing.Distinct(StringComparer.Ordinal).Take(KeyIntelligenceBounds.MaximumExplanationLines).ToArray(),
            request.ReviewedOverride);
    }

    private static void AddInventoryReasons(
        KeyInventoryFacts inventory,
        ICollection<KeyIntelligenceReason> reasons,
        ICollection<string> missing)
    {
        if (Known(inventory.DuplicateQuantity) is { } duplicates)
        {
            reasons.Add(new(
                KeyIntelligenceReasonCategory.UsesAndCondition,
                "key.inventory.duplicates",
                duplicates == 0
                    ? "No duplicate copy is present in the scoped inventory evidence."
                    : $"The scoped inventory contains {duplicates.ToString(CultureInfo.InvariantCulture)} duplicate copy/copies.",
                500,
                inventory.DuplicateQuantity.Provenance));
        }
        else
        {
            missing.Add("Duplicate quantity is unknown.");
        }

        if (Known(inventory.MaximumUses) is { } maximum && Known(inventory.RemainingUses) is { } remaining)
        {
            reasons.Add(new(
                KeyIntelligenceReasonCategory.UsesAndCondition,
                "key.uses.remaining",
                $"The observed copy has {remaining.ToString(CultureInfo.InvariantCulture)} of {maximum.ToString(CultureInfo.InvariantCulture)} uses remaining.",
                520,
                inventory.RemainingUses.Provenance));
        }
        else
        {
            missing.Add("Maximum or remaining uses are unknown; reusable and unread are not treated as the same state.");
        }

        if (Known(inventory.TotalOwned) is null)
        {
            missing.Add("Total owned quantity is unknown.");
        }

        if (Known(inventory.FoundInRaidOwned) is null)
        {
            missing.Add("Found-in-raid owned quantity is unknown.");
        }
    }

    private static void AddUtilityReasons(
        KeyUtilityFacts utility,
        ICollection<KeyIntelligenceReason> reasons,
        ICollection<string> missing)
    {
        if (utility.Status.Completeness == ResultCompleteness.Complete)
        {
            reasons.Add(new(
                KeyIntelligenceReasonCategory.Access,
                "key.access.associations",
                $"The reviewed data names {utility.Associations.Count.ToString(CultureInfo.InvariantCulture)} explicit map/lock/room association(s).",
                700,
                utility.Provenance));
        }
        else
        {
            missing.Add("Map, lock, and room association coverage is incomplete.");
        }

        AddObtainability("trader", utility.TraderObtainability, reasons, missing);
        AddObtainability("flea", utility.FleaObtainability, reasons, missing);

        if (Known(utility.AcquisitionCostRoubles) is { } cost && Known(utility.ExpectedLootProxyRoubles) is { } loot)
        {
            reasons.Add(new(
                KeyIntelligenceReasonCategory.Economics,
                "key.economics.proxy",
                $"Expected-loot proxy {loot.ToString(CultureInfo.InvariantCulture)} roubles; acquisition cost {cost.ToString(CultureInfo.InvariantCulture)} roubles.",
                400,
                utility.ExpectedLootProxyRoubles.Provenance));
        }
        else
        {
            missing.Add("Acquisition cost or expected-loot proxy is unknown; price alone cannot decide the tier.");
        }

        if (Known(utility.UniqueAccess) is { } unique)
        {
            reasons.Add(new(
                KeyIntelligenceReasonCategory.Access,
                "key.access.unique",
                unique ? "Reviewed data marks this access as unique." : "Reviewed data does not mark this access as unique.",
                650,
                utility.UniqueAccess.Provenance));
        }
        else
        {
            missing.Add("Unique-access status is unknown.");
        }

        if (Known(utility.RouteUtility) is { } routeUtility && Known(utility.RouteRisk) is { } routeRisk)
        {
            reasons.Add(new(
                KeyIntelligenceReasonCategory.RouteUtility,
                "key.route.reviewed-estimate",
                $"Route utility {routeUtility:P0}; sourced route-risk estimate {routeRisk:P0}. This is not live detection.",
                450,
                utility.RouteRisk.Provenance));
        }
        else
        {
            missing.Add("Route utility or sourced route-risk estimate is unknown; no live condition is inferred.");
        }
    }

    private static void AddObtainability(
        string channel,
        EvidencedValue<KeyObtainability?> fact,
        ICollection<KeyIntelligenceReason> reasons,
        ICollection<string> missing)
    {
        if (Known(fact) is { } obtainability)
        {
            reasons.Add(new(
                KeyIntelligenceReasonCategory.Obtainability,
                $"key.obtainability.{channel}",
                $"{char.ToUpperInvariant(channel[0])}{channel[1..]} obtainability is {obtainability.ToString().ToLowerInvariant()}.",
                600,
                fact.Provenance));
        }
        else
        {
            missing.Add($"{char.ToUpperInvariant(channel[0])}{channel[1..]} obtainability is unknown or unavailable.");
        }
    }

    private static string RequirementExplanation(KeyRequirementFact requirement, string timing)
    {
        var fir = requirement.RequiresFoundInRaid ? " found-in-raid" : string.Empty;
        return $"A {timing} profile requirement still needs {requirement.RemainingQuantity.ToString(CultureInfo.InvariantCulture)}{fir} copy/copies ({requirement.RequirementId}).";
    }

    private static double? Economy(long? cost, long? loot)
    {
        if (cost is null || loot is null)
        {
            return null;
        }

        if (loot <= 0)
        {
            return 0;
        }

        return cost <= 0 ? 100 : Math.Clamp((loot.Value / (double)cost.Value) / 3d * 100d, 0, 100);
    }

    private static double? Uses(int? maximum, int? remaining)
    {
        if (maximum is null || remaining is null)
        {
            return null;
        }

        return maximum <= 0 ? 0 : Math.Clamp(remaining.Value / (double)maximum.Value * 100d, 0, 100);
    }

    private static double? LockUtility(KeyUtilityFacts utility)
    {
        if (utility.Status.Completeness != ResultCompleteness.Complete || Known(utility.RouteUtility) is not { } routeUtility)
        {
            return null;
        }

        var breadth = Math.Min(100, utility.Associations.Count * 25d);
        return (breadth + (routeUtility * 100d)) / 2d;
    }

    private static double? RiskAdjustedLoot(KeyUtilityFacts utility)
    {
        if (Known(utility.AcquisitionCostRoubles) is not { } cost ||
            Known(utility.ExpectedLootProxyRoubles) is not { } loot ||
            Known(utility.RouteRisk) is not { } risk)
        {
            return null;
        }

        var adjusted = loot * (1d - risk);
        if (adjusted <= 0)
        {
            return 0;
        }

        return cost <= 0 ? 100 : Math.Clamp((adjusted / cost) / 2d * 100d, 0, 100);
    }

    private static KeyIntelligenceTier Tier(double score) => score switch
    {
        >= 80 => KeyIntelligenceTier.S,
        >= 65 => KeyIntelligenceTier.A,
        >= 50 => KeyIntelligenceTier.B,
        >= 35 => KeyIntelligenceTier.C,
        _ => KeyIntelligenceTier.D,
    };

    private static KeyIntelligenceTier Higher(KeyIntelligenceTier first, KeyIntelligenceTier second) =>
        (int)first >= (int)second ? first : second;

    private static string Advice(KeyIntelligenceTier tier, int currentNeeds, int futureNeeds) =>
        currentNeeds > 0
            ? "Keep enough copies for the current profile requirement; review found-in-raid eligibility and remaining uses."
            : futureNeeds > 0
                ? "Keep enough copies for the recorded future requirement, then review duplicates separately."
                : tier switch
                {
                    KeyIntelligenceTier.S or KeyIntelligenceTier.A => "High sourced utility; compare its explicit locks with the manual raid plan.",
                    KeyIntelligenceTier.B or KeyIntelligenceTier.C => "Situational utility; review the sourced access and route facts.",
                    KeyIntelligenceTier.D => "Low scored utility under the current sourced facts.",
                    _ => "Review the missing facts before keeping, selling, or planning around this key.",
                };

    private static EvidenceProvenance? BuildScoreProvenance(ProfileAwareKeyIntelligenceRequest request)
    {
        var inputs = new[]
        {
            request.RequirementsProvenance,
            request.Inventory.MaximumUses.Provenance,
            request.Inventory.RemainingUses.Provenance,
            request.Utility.Provenance,
            request.Utility.AcquisitionCostRoubles.Provenance,
            request.Utility.ExpectedLootProxyRoubles.Provenance,
            request.Utility.UniqueAccess.Provenance,
            request.Utility.RouteUtility.Provenance,
            request.Utility.RouteRisk.Provenance,
        };

        try
        {
            var hasModel = inputs.Any(ContainsModelledEstimate);
            if (!hasModel)
            {
                return new EvidenceProvenance(
                    EvidenceSourceClass.DerivedCalculation,
                    "tarkov-companion:key-score",
                    request.EvaluatedUtc,
                    EvidenceConfidence.Certain,
                    new ProducerIdentity("Tarkov Companion key intelligence", CurrentRulesetVersion),
                    generatedUtc: request.EvaluatedUtc,
                    reference: CurrentRulesetVersion,
                    inputs: inputs);
            }

            var scores = inputs
                .SelectMany(Flatten)
                .Select(provenance => provenance.Confidence.Score)
                .ToArray();
            if (scores.Any(score => score is null))
            {
                return null;
            }

            var minimumScore = scores.OfType<double>().Min();

            var modelVersion = inputs
                .SelectMany(Flatten)
                .Where(provenance => provenance.SourceClass == EvidenceSourceClass.ModelledEstimate)
                .Select(provenance => provenance.Producer.ModelVersion)
                .FirstOrDefault(version => !string.IsNullOrWhiteSpace(version));
            if (modelVersion is null)
            {
                return null;
            }

            var dataThrough = inputs.Max(input => input.EvidenceThroughUtc);
            return new EvidenceProvenance(
                EvidenceSourceClass.ModelledEstimate,
                "tarkov-companion:key-score",
                request.EvaluatedUtc,
                new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, minimumScore),
                new ProducerIdentity("Tarkov Companion key intelligence", CurrentRulesetVersion, modelVersion),
                dataThrough,
                request.EvaluatedUtc,
                new EvidenceCoverage(description: "Explicit bounded key-score inputs."),
                CurrentRulesetVersion,
                inputs);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool ContainsModelledEstimate(EvidenceProvenance provenance) =>
        provenance.SourceClass == EvidenceSourceClass.ModelledEstimate ||
        provenance.Inputs.Any(ContainsModelledEstimate);

    private static IEnumerable<EvidenceProvenance> Flatten(EvidenceProvenance provenance) =>
        new[] { provenance }.Concat(provenance.Inputs.SelectMany(Flatten));

    private static EvidenceProvenance MissingEvidenceProvenance(ProfileAwareKeyIntelligenceRequest request) =>
        new(
            EvidenceSourceClass.Unknown,
            "tarkov-companion:key-evidence-quality",
            request.EvaluatedUtc,
            EvidenceConfidence.Unscored,
            new ProducerIdentity("Tarkov Companion key intelligence", CurrentRulesetVersion),
            reference: CurrentRulesetVersion);

    private static T? Known<T>(EvidencedValue<T?> value)
        where T : struct =>
        value.Status.Completeness == ResultCompleteness.Complete ? value.Value : null;

    private static bool HasAnyDecisionFact(ProfileAwareKeyIntelligenceRequest request) =>
        request.Requirements.Any(requirement => requirement.Status.Completeness == ResultCompleteness.Complete) ||
        request.Utility.Status.Completeness == ResultCompleteness.Complete ||
        Known(request.Inventory.TotalOwned) is not null ||
        Known(request.Inventory.FoundInRaidOwned) is not null ||
        Known(request.Inventory.DuplicateQuantity) is not null ||
        Known(request.Inventory.MaximumUses) is not null ||
        Known(request.Inventory.RemainingUses) is not null ||
        Known(request.Utility.TraderObtainability) is not null ||
        Known(request.Utility.FleaObtainability) is not null ||
        Known(request.Utility.AcquisitionCostRoubles) is not null ||
        Known(request.Utility.ExpectedLootProxyRoubles) is not null ||
        Known(request.Utility.UniqueAccess) is not null ||
        Known(request.Utility.RouteUtility) is not null ||
        Known(request.Utility.RouteRisk) is not null;

    private static FreshnessState Freshness(ProfileAwareKeyIntelligenceRequest request)
    {
        var states = new[]
        {
            request.RequirementsStatus.Freshness,
            request.Utility.Status.Freshness,
            request.Inventory.TotalOwned.Status.Freshness,
            request.Inventory.FoundInRaidOwned.Status.Freshness,
            request.Inventory.DuplicateQuantity.Status.Freshness,
            request.Inventory.MaximumUses.Status.Freshness,
            request.Inventory.RemainingUses.Status.Freshness,
            request.Utility.TraderObtainability.Status.Freshness,
            request.Utility.FleaObtainability.Status.Freshness,
            request.Utility.AcquisitionCostRoubles.Status.Freshness,
            request.Utility.ExpectedLootProxyRoubles.Status.Freshness,
            request.Utility.UniqueAccess.Status.Freshness,
            request.Utility.RouteUtility.Status.Freshness,
            request.Utility.RouteRisk.Status.Freshness,
        };
        if (states.Contains(FreshnessState.Stale))
        {
            return FreshnessState.Stale;
        }

        return states.All(state => state == FreshnessState.Current)
            ? FreshnessState.Current
            : FreshnessState.Unknown;
    }
}
