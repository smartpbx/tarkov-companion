using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;

namespace TarkovCompanion.Core.Domain.Keys;

public static class KeyIntelligenceBounds
{
    public const int MaximumRequirements = 64;
    public const int MaximumAssociations = 64;
    public const int MaximumReasons = 32;
    public const int MaximumExplanationLines = 32;
    public const int MaximumCorrectionsPerField = 64;
    public const int MaximumIdentifierLength = 256;
    public const int MaximumExplanationLength = 1_024;
}

/// <summary>The caller changes context, not the meaning of key intelligence.</summary>
public enum KeyIntelligenceEntryPoint
{
    ManualLookup = 1,
    Search,
    ContextScreenshot,
    StashScan,
    Planner,
}

public enum KeyRequirementTiming
{
    Current = 1,
    Future,
}

public enum KeyObtainability
{
    Unavailable = 1,
    Restricted,
    Available,
}

public enum KeyIntelligenceTier
{
    Review = 1,
    D,
    C,
    B,
    A,
    S,
}

public enum KeyIntelligenceReasonCategory
{
    CurrentFoundInRaidQuest = 1,
    CurrentQuest,
    FutureQuest,
    UsesAndCondition,
    Access,
    Obtainability,
    Economics,
    RouteUtility,
    EvidenceQuality,
    CuratedOverride,
}

/// <summary>A still-outstanding profile requirement. An absent requirement is never invented.</summary>
public sealed record KeyRequirementFact
{
    public KeyRequirementFact(
        string requirementId,
        KeyRequirementTiming timing,
        int remainingQuantity,
        bool requiresFoundInRaid,
        ResultStatus status,
        EvidenceProvenance provenance)
    {
        RequirementId = KeyIntelligenceGuard.Required(requirementId, nameof(requirementId));
        Timing = KeyIntelligenceGuard.Defined(timing, nameof(timing));
        RemainingQuantity = remainingQuantity >= 1
            ? remainingQuantity
            : throw new ArgumentOutOfRangeException(nameof(remainingQuantity));
        RequiresFoundInRaid = requiresFoundInRaid;
        Status = status ?? throw new ArgumentNullException(nameof(status));
        if (status.Completeness is ResultCompleteness.Unknown or ResultCompleteness.Unavailable)
        {
            throw new ArgumentException("A requirement fact with a remaining quantity cannot be unknown or unavailable.", nameof(status));
        }

        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
    }

    public string RequirementId { get; }
    public KeyRequirementTiming Timing { get; }
    public int RemainingQuantity { get; }
    public bool RequiresFoundInRaid { get; }
    public ResultStatus Status { get; }
    public EvidenceProvenance Provenance { get; }
}

/// <summary>Profile-scoped observed state. Corrections remain on each evidenced scalar.</summary>
public sealed record KeyInventoryFacts
{
    public KeyInventoryFacts(
        InventoryProfileScope profileScope,
        string itemId,
        EvidencedValue<int?> totalOwned,
        EvidencedValue<int?> foundInRaidOwned,
        EvidencedValue<int?> duplicateQuantity,
        EvidencedValue<int?> maximumUses,
        EvidencedValue<int?> remainingUses)
    {
        ProfileScope = profileScope ?? throw new ArgumentNullException(nameof(profileScope));
        ItemId = KeyIntelligenceGuard.Required(itemId, nameof(itemId));
        TotalOwned = KeyIntelligenceGuard.NonNegative(totalOwned, nameof(totalOwned));
        FoundInRaidOwned = KeyIntelligenceGuard.NonNegative(foundInRaidOwned, nameof(foundInRaidOwned));
        DuplicateQuantity = KeyIntelligenceGuard.NonNegative(duplicateQuantity, nameof(duplicateQuantity));
        MaximumUses = KeyIntelligenceGuard.NonNegative(maximumUses, nameof(maximumUses));
        RemainingUses = KeyIntelligenceGuard.NonNegative(remainingUses, nameof(remainingUses));

        if (TotalOwned.Value is { } total && FoundInRaidOwned.Value is { } fir && fir > total)
        {
            throw new ArgumentException("Found-in-raid quantity cannot exceed total owned quantity.", nameof(foundInRaidOwned));
        }

        if (TotalOwned.Value is { } owned && DuplicateQuantity.Value is { } duplicates && duplicates > Math.Max(0, owned - 1))
        {
            throw new ArgumentException("Duplicate quantity cannot exceed owned quantity after the first copy.", nameof(duplicateQuantity));
        }

        if (MaximumUses.Value is { } maximum && RemainingUses.Value is { } remaining && remaining > maximum)
        {
            throw new ArgumentException("Remaining uses cannot exceed maximum uses.", nameof(remainingUses));
        }
    }

    public InventoryProfileScope ProfileScope { get; }
    public string ItemId { get; }
    public EvidencedValue<int?> TotalOwned { get; }
    public EvidencedValue<int?> FoundInRaidOwned { get; }
    public EvidencedValue<int?> DuplicateQuantity { get; }
    public EvidencedValue<int?> MaximumUses { get; }
    public EvidencedValue<int?> RemainingUses { get; }
}

/// <summary>
/// One sourced access association. Map, lock and room remain distinct so a display name cannot be
/// parsed into a fabricated destination.
/// </summary>
public sealed record KeyAccessAssociation
{
    public KeyAccessAssociation(
        string associationId,
        EvidencedValue<string> mapId,
        EvidencedValue<string> lockId,
        EvidencedValue<string> roomId,
        EvidenceProvenance provenance)
    {
        AssociationId = KeyIntelligenceGuard.Required(associationId, nameof(associationId));
        MapId = KeyIntelligenceGuard.BoundedStrings(mapId, nameof(mapId));
        LockId = KeyIntelligenceGuard.BoundedStrings(lockId, nameof(lockId));
        RoomId = KeyIntelligenceGuard.BoundedStrings(roomId, nameof(roomId));
        if (MapId.Value is null && LockId.Value is null && RoomId.Value is null)
        {
            throw new ArgumentException("An access association must identify a map, lock, or room.");
        }

        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
    }

    public string AssociationId { get; }
    public EvidencedValue<string> MapId { get; }
    public EvidencedValue<string> LockId { get; }
    public EvidencedValue<string> RoomId { get; }
    public EvidenceProvenance Provenance { get; }
}

/// <summary>Source facts used by the key evaluator; absent claims stay absent.</summary>
public sealed record KeyUtilityFacts
{
    public KeyUtilityFacts(
        ResultStatus status,
        EvidenceProvenance provenance,
        IReadOnlyList<KeyAccessAssociation> associations,
        EvidencedValue<KeyObtainability?> traderObtainability,
        EvidencedValue<KeyObtainability?> fleaObtainability,
        EvidencedValue<long?> acquisitionCostRoubles,
        EvidencedValue<long?> expectedLootProxyRoubles,
        EvidencedValue<bool?> uniqueAccess,
        EvidencedValue<double?> routeUtility,
        EvidencedValue<double?> routeRisk)
    {
        Status = status ?? throw new ArgumentNullException(nameof(status));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        Associations = KeyIntelligenceGuard.CopyDistinct(
            associations,
            KeyIntelligenceBounds.MaximumAssociations,
            association => association.AssociationId,
            nameof(associations));
        TraderObtainability = KeyIntelligenceGuard.Defined(traderObtainability, nameof(traderObtainability));
        FleaObtainability = KeyIntelligenceGuard.Defined(fleaObtainability, nameof(fleaObtainability));
        AcquisitionCostRoubles = KeyIntelligenceGuard.NonNegative(acquisitionCostRoubles, nameof(acquisitionCostRoubles));
        ExpectedLootProxyRoubles = KeyIntelligenceGuard.NonNegative(expectedLootProxyRoubles, nameof(expectedLootProxyRoubles));
        UniqueAccess = uniqueAccess ?? throw new ArgumentNullException(nameof(uniqueAccess));
        RouteUtility = KeyIntelligenceGuard.UnitInterval(routeUtility, nameof(routeUtility));
        RouteRisk = KeyIntelligenceGuard.UnitInterval(routeRisk, nameof(routeRisk));
    }

    public ResultStatus Status { get; }
    public EvidenceProvenance Provenance { get; }
    public IReadOnlyList<KeyAccessAssociation> Associations { get; }
    public EvidencedValue<KeyObtainability?> TraderObtainability { get; }
    public EvidencedValue<KeyObtainability?> FleaObtainability { get; }
    public EvidencedValue<long?> AcquisitionCostRoubles { get; }
    public EvidencedValue<long?> ExpectedLootProxyRoubles { get; }
    public EvidencedValue<bool?> UniqueAccess { get; }
    public EvidencedValue<double?> RouteUtility { get; }
    public EvidencedValue<double?> RouteRisk { get; }
}

/// <summary>Reviewed score/advice substitution. Metadata is mandatory because it asserts facts.</summary>
public sealed record ReviewedKeyOverride
{
    public ReviewedKeyOverride(
        double? score,
        KeyIntelligenceTier? tier,
        string? advice,
        string? explanation,
        string gameVersion,
        string mapDataVersion,
        string reviewer,
        DateTimeOffset reviewedUtc,
        EvidenceProvenance provenance)
    {
        if (score is { } numericScore && (!double.IsFinite(numericScore) || numericScore is < 0 or > 100))
        {
            throw new ArgumentOutOfRangeException(nameof(score));
        }

        if (tier is { } suppliedTier)
        {
            Tier = KeyIntelligenceGuard.Defined(suppliedTier, nameof(tier));
        }

        if (score is null && tier is null && string.IsNullOrWhiteSpace(advice) && string.IsNullOrWhiteSpace(explanation))
        {
            throw new ArgumentException("A reviewed override must override at least one value.");
        }

        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        if (provenance.SourceClass != EvidenceSourceClass.CuratedData ||
            provenance.Confidence.Kind == EvidenceConfidenceKind.Unscored ||
            string.IsNullOrWhiteSpace(provenance.Reference))
        {
            throw new ArgumentException(
                "A reviewed override requires scored curated provenance with a source reference.",
                nameof(provenance));
        }

        Score = score;
        Advice = KeyIntelligenceGuard.Optional(advice, nameof(advice));
        Explanation = KeyIntelligenceGuard.Optional(explanation, nameof(explanation));
        GameVersion = KeyIntelligenceGuard.Required(gameVersion, nameof(gameVersion));
        MapDataVersion = KeyIntelligenceGuard.Required(mapDataVersion, nameof(mapDataVersion));
        Reviewer = KeyIntelligenceGuard.Required(reviewer, nameof(reviewer));
        ReviewedUtc = KeyIntelligenceGuard.Utc(reviewedUtc, nameof(reviewedUtc));
        if (ReviewedUtc > provenance.ObservedUtc)
        {
            throw new ArgumentException("Override provenance cannot predate its review.", nameof(provenance));
        }
    }

    public double? Score { get; }
    public KeyIntelligenceTier? Tier { get; }
    public string? Advice { get; }
    public string? Explanation { get; }
    public string GameVersion { get; }
    public string MapDataVersion { get; }
    public string Reviewer { get; }
    public DateTimeOffset ReviewedUtc { get; }
    public EvidenceProvenance Provenance { get; }
}

public sealed record KeyIntelligenceReason
{
    public KeyIntelligenceReason(
        KeyIntelligenceReasonCategory category,
        string code,
        string explanation,
        int priority,
        EvidenceProvenance provenance)
    {
        Category = KeyIntelligenceGuard.Defined(category, nameof(category));
        Code = KeyIntelligenceGuard.Required(code, nameof(code));
        Explanation = KeyIntelligenceGuard.Explanation(explanation, nameof(explanation));
        Priority = priority;
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
    }

    public KeyIntelligenceReasonCategory Category { get; }
    public string Code { get; }
    public string Explanation { get; }
    public int Priority { get; }
    public EvidenceProvenance Provenance { get; }
}

public sealed record KeyScoreComponentsV2
{
    public KeyScoreComponentsV2(
        double? quest,
        double? economy,
        double? uses,
        double? lockUtility,
        double? uniqueAccess,
        double? riskAdjustedLoot)
    {
        Quest = Component(quest, nameof(quest));
        Economy = Component(economy, nameof(economy));
        Uses = Component(uses, nameof(uses));
        LockUtility = Component(lockUtility, nameof(lockUtility));
        UniqueAccess = Component(uniqueAccess, nameof(uniqueAccess));
        RiskAdjustedLoot = Component(riskAdjustedLoot, nameof(riskAdjustedLoot));
    }

    public double? Quest { get; }
    public double? Economy { get; }
    public double? Uses { get; }
    public double? LockUtility { get; }
    public double? UniqueAccess { get; }
    public double? RiskAdjustedLoot { get; }

    public bool IsComplete =>
        Quest is not null && Economy is not null && Uses is not null && LockUtility is not null &&
        UniqueAccess is not null && RiskAdjustedLoot is not null;

    public double? WeightedTotal => !IsComplete
        ? null
        : (Quest!.Value * 0.30) +
          (Economy!.Value * 0.15) +
          (Uses!.Value * 0.10) +
          (LockUtility!.Value * 0.20) +
          (UniqueAccess!.Value * 0.15) +
          (RiskAdjustedLoot!.Value * 0.10);

    private static double? Component(double? value, string parameterName) =>
        value is null || (double.IsFinite(value.Value) && value.Value is >= 0 and <= 100)
            ? value
            : throw new ArgumentOutOfRangeException(parameterName);
}

/// <summary>The same typed response is used by lookup, capture, stash, and planning callers.</summary>
public sealed record ProfileAwareKeyIntelligenceResult
{
    public ProfileAwareKeyIntelligenceResult(
        string itemId,
        InventoryProfileScope profileScope,
        string rulesetVersion,
        ResultStatus status,
        KeyInventoryFacts inventory,
        ResultStatus requirementsStatus,
        EvidenceProvenance requirementsProvenance,
        IReadOnlyList<KeyRequirementFact> requirements,
        KeyUtilityFacts utility,
        KeyScoreComponentsV2 components,
        double? score,
        EvidenceProvenance? scoreProvenance,
        KeyIntelligenceTier tier,
        string advice,
        IReadOnlyList<KeyIntelligenceReason> reasons,
        IReadOnlyList<string> missingFacts,
        ReviewedKeyOverride? appliedOverride)
    {
        ItemId = KeyIntelligenceGuard.Required(itemId, nameof(itemId));
        ProfileScope = profileScope ?? throw new ArgumentNullException(nameof(profileScope));
        RulesetVersion = KeyIntelligenceGuard.Required(rulesetVersion, nameof(rulesetVersion));
        Status = status ?? throw new ArgumentNullException(nameof(status));
        Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        RequirementsStatus = requirementsStatus ?? throw new ArgumentNullException(nameof(requirementsStatus));
        RequirementsProvenance = requirementsProvenance ?? throw new ArgumentNullException(nameof(requirementsProvenance));
        Utility = utility ?? throw new ArgumentNullException(nameof(utility));
        if (!string.Equals(ItemId, inventory.ItemId, StringComparison.Ordinal) || ProfileScope != inventory.ProfileScope)
        {
            throw new ArgumentException("Returned inventory facts must match the key and profile scope.", nameof(inventory));
        }

        Requirements = KeyIntelligenceGuard.CopyDistinct(
            requirements,
            KeyIntelligenceBounds.MaximumRequirements,
            requirement => requirement.RequirementId,
            nameof(requirements));
        Components = components ?? throw new ArgumentNullException(nameof(components));
        if (score is { } numericScore && (!double.IsFinite(numericScore) || numericScore is < 0 or > 100))
        {
            throw new ArgumentOutOfRangeException(nameof(score));
        }

        Score = score;
        if ((score is null) != (scoreProvenance is null))
        {
            throw new ArgumentException("A key score and its provenance must be present or absent together.", nameof(scoreProvenance));
        }

        ScoreProvenance = scoreProvenance;
        Tier = KeyIntelligenceGuard.Defined(tier, nameof(tier));
        Advice = KeyIntelligenceGuard.Explanation(advice, nameof(advice));
        Reasons = KeyIntelligenceGuard.Copy(
            reasons,
            KeyIntelligenceBounds.MaximumReasons,
            nameof(reasons));
        if (Reasons.Count == 0)
        {
            throw new ArgumentException("Key intelligence must explain its answer.", nameof(reasons));
        }

        MissingFacts = KeyIntelligenceGuard.CopyStrings(
            missingFacts,
            KeyIntelligenceBounds.MaximumExplanationLines,
            nameof(missingFacts));
        AppliedOverride = appliedOverride;
    }

    public string ItemId { get; }
    public InventoryProfileScope ProfileScope { get; }
    public string RulesetVersion { get; }
    public ResultStatus Status { get; }
    public KeyInventoryFacts Inventory { get; }
    public ResultStatus RequirementsStatus { get; }
    public EvidenceProvenance RequirementsProvenance { get; }
    public IReadOnlyList<KeyRequirementFact> Requirements { get; }
    public KeyUtilityFacts Utility { get; }
    public KeyScoreComponentsV2 Components { get; }
    public double? Score { get; }
    public EvidenceProvenance? ScoreProvenance { get; }
    public KeyIntelligenceTier Tier { get; }
    public string Advice { get; }
    public IReadOnlyList<KeyIntelligenceReason> Reasons { get; }
    public IReadOnlyList<string> MissingFacts { get; }
    public ReviewedKeyOverride? AppliedOverride { get; }
}

internal static class KeyIntelligenceGuard
{
    public static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= KeyIntelligenceBounds.MaximumIdentifierLength
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName);
    }

    public static string Explanation(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= KeyIntelligenceBounds.MaximumExplanationLength
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName);
    }

    public static string? Optional(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? null : Explanation(value, parameterName);

    public static TEnum Defined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum =>
        Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(parameterName);

    public static DateTimeOffset Utc(DateTimeOffset value, string parameterName) =>
        value != default && value.Offset == TimeSpan.Zero
            ? value
            : throw new ArgumentException("A defined UTC instant is required.", parameterName);

    public static EvidencedValue<int?> NonNegative(EvidencedValue<int?> value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (NumericValues(value).Any(candidate => candidate < 0))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    public static EvidencedValue<long?> NonNegative(EvidencedValue<long?> value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (NumericValues(value).Any(candidate => candidate < 0))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    public static EvidencedValue<double?> UnitInterval(EvidencedValue<double?> value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (NumericValues(value).Any(candidate => !double.IsFinite(candidate) || candidate is < 0 or > 1))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    public static EvidencedValue<TEnum?> Defined<TEnum>(EvidencedValue<TEnum?> value, string parameterName)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (NullableValues(value).Any(candidate => candidate is { } present && !Enum.IsDefined(present)))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    public static EvidencedValue<string> BoundedStrings(EvidencedValue<string> value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        var strings = new[] { value.Value }
            .Concat(value.Candidates.Select(candidate => candidate.Value))
            .Concat(value.Corrections.SelectMany(correction =>
                new[] { correction.OriginalValue, correction.CorrectedValue }));
        if (strings.Any(candidate => candidate is { Length: > KeyIntelligenceBounds.MaximumIdentifierLength }))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    public static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> values, int maximum, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > maximum)
        {
            throw new ArgumentException($"The list cannot exceed {maximum} entries.", parameterName);
        }

        var copy = values.ToArray();
        if (copy.Any(value => value is null))
        {
            throw new ArgumentException("The list cannot contain null entries.", parameterName);
        }

        return Array.AsReadOnly(copy);
    }

    public static IReadOnlyList<T> CopyDistinct<T>(
        IReadOnlyList<T> values,
        int maximum,
        Func<T, string> key,
        string parameterName)
    {
        var copy = Copy(values, maximum, parameterName);
        if (copy.Select(key).Distinct(StringComparer.Ordinal).Count() != copy.Count)
        {
            throw new ArgumentException("Identifiers must be unique.", parameterName);
        }

        return copy;
    }

    public static IReadOnlyList<string> CopyStrings(
        IReadOnlyList<string> values,
        int maximum,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        return Copy(values.Select(value => Explanation(value, parameterName)).ToArray(), maximum, parameterName);
    }

    private static IEnumerable<int> NumericValues(EvidencedValue<int?> value) =>
        NullableValues(value).Where(candidate => candidate.HasValue).Select(candidate => candidate!.Value);

    private static IEnumerable<long> NumericValues(EvidencedValue<long?> value) =>
        NullableValues(value).Where(candidate => candidate.HasValue).Select(candidate => candidate!.Value);

    private static IEnumerable<double> NumericValues(EvidencedValue<double?> value) =>
        NullableValues(value).Where(candidate => candidate.HasValue).Select(candidate => candidate!.Value);

    private static IEnumerable<T?> NullableValues<T>(EvidencedValue<T?> value)
        where T : struct =>
        new[] { value.Value }
            .Concat(value.Candidates.Select(candidate => candidate.Value))
            .Concat(value.Corrections.SelectMany(correction =>
                new[] { correction.OriginalValue, correction.CorrectedValue }));
}
