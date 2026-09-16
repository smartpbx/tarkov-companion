using TarkovCompanion.Application.Services.Intelligence.Keys;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Keys;

namespace TarkovCompanion.UnitTests.Keys;

public sealed class ProfileAwareKeyIntelligenceServiceTests
{
    private static readonly DateTimeOffset ObservedUtc =
        new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FullFactsProduceVersionedProfileAwareAnswerWithOrderedReasons()
    {
        var result = new ProfileAwareKeyIntelligenceService().Evaluate(
            CompleteRequest(KeyIntelligenceEntryPoint.StashScan),
            CancellationToken.None);

        Assert.Equal(ResultCompleteness.Complete, result.Status.Completeness);
        Assert.Equal(FreshnessState.Current, result.Status.Freshness);
        Assert.Equal(ProfileAwareKeyIntelligenceService.CurrentRulesetVersion, result.RulesetVersion);
        Assert.NotNull(result.Score);
        Assert.NotNull(result.ScoreProvenance);
        Assert.Equal(EvidenceSourceClass.DerivedCalculation, result.ScoreProvenance.SourceClass);
        Assert.True(result.Tier is KeyIntelligenceTier.A or KeyIntelligenceTier.S);
        Assert.Contains(result.Reasons, reason => reason.Code == "key.need.current-fir");
        Assert.Contains(result.Reasons, reason => reason.Code == "key.inventory.duplicates");
        Assert.Contains(result.Reasons, reason => reason.Code == "key.access.associations");
        var association = Assert.Single(result.Utility.Associations);
        Assert.Equal("customs", association.MapId.Value);
        Assert.Equal("dorms-206-lock", association.LockId.Value);
        Assert.Equal("dorms-206", association.RoomId.Value);
        Assert.Equal(2, Assert.Single(result.Requirements).RemainingQuantity);
        Assert.True(Assert.Single(result.Requirements).RequiresFoundInRaid);
        Assert.Equal(2, result.Inventory.DuplicateQuantity.Value);
        Assert.Equal(
            result.Reasons.OrderByDescending(reason => reason.Priority).ThenBy(reason => reason.Code, StringComparer.Ordinal),
            result.Reasons);
        Assert.Empty(result.MissingFacts);
    }

    [Fact]
    public void EveryEntryPointUsesTheSameTypedDecisionContract()
    {
        var service = new ProfileAwareKeyIntelligenceService();
        var results = Enum.GetValues<KeyIntelligenceEntryPoint>()
            .Select(entryPoint => service.Evaluate(CompleteRequest(entryPoint), CancellationToken.None))
            .ToArray();

        var baseline = results[0];
        Assert.All(results, result =>
        {
            Assert.IsType<ProfileAwareKeyIntelligenceResult>(result);
            Assert.Equal(baseline.Score, result.Score);
            Assert.Equal(baseline.Tier, result.Tier);
            Assert.Equal(baseline.Status, result.Status);
            Assert.Equal(baseline.Reasons.Select(reason => reason.Code), result.Reasons.Select(reason => reason.Code));
        });
    }

    [Fact]
    public void FutureNeedRemainsDistinctFromCurrentNeedAndCarriesRemainingFirQuantity()
    {
        var result = new ProfileAwareKeyIntelligenceService().Evaluate(
            CompleteRequest(
                KeyIntelligenceEntryPoint.ManualLookup,
                requirementTiming: KeyRequirementTiming.Future),
            CancellationToken.None);

        Assert.DoesNotContain(result.Reasons, reason => reason.Code.StartsWith("key.need.current", StringComparison.Ordinal));
        var future = Assert.Single(result.Reasons, reason => reason.Code == "key.need.future-fir");
        Assert.Contains("needs 2 found-in-raid", future.Explanation, StringComparison.Ordinal);
        Assert.True(result.Tier is KeyIntelligenceTier.B or KeyIntelligenceTier.A or KeyIntelligenceTier.S);
    }

    [Fact]
    public void PriceOnlyEvidenceCannotInventAccessOrProduceStrongTier()
    {
        var provenance = PublicProvenance();
        var inventory = new KeyInventoryFacts(
            Scope(),
            "Dorm room 206 key",
            Known("total", 1, provenance),
            Known("fir", 0, provenance),
            Known("duplicates", 0, provenance),
            Unknown<int>("maximum-uses"),
            Unknown<int>("remaining-uses"));
        var utility = new KeyUtilityFacts(
            PartialStatus(),
            provenance,
            [],
            Unknown<KeyObtainability>("trader"),
            Known("flea", KeyObtainability.Available, provenance),
            Known("cost", 800_000L, provenance),
            Known("loot", 2_000_000L, provenance),
            Unknown<bool>("unique"),
            Unknown<double>("route-utility"),
            Unknown<double>("route-risk"));
        var request = new ProfileAwareKeyIntelligenceRequest(
            KeyIntelligenceEntryPoint.ManualLookup,
            inventory.ItemId,
            ObservedUtc.AddMinutes(1),
            inventory,
            CompleteStatus(),
            provenance,
            [],
            utility);

        var result = new ProfileAwareKeyIntelligenceService().Evaluate(request, CancellationToken.None);

        Assert.Equal(KeyIntelligenceTier.Review, result.Tier);
        Assert.Null(result.Score);
        Assert.Equal(ResultCompleteness.Partial, result.Status.Completeness);
        Assert.DoesNotContain(result.Reasons, reason => reason.Code == "key.access.associations");
        Assert.Contains(result.Reasons, reason => reason.Code == "key.economics.proxy");
        Assert.Contains(result.MissingFacts, fact => fact.Contains("price alone", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Reasons, reason =>
            reason.Explanation.Contains("206", StringComparison.Ordinal));
    }

    [Fact]
    public void EntirelyUnresolvedInputsReturnUnknownReviewInsteadOfEmptyFacts()
    {
        var unknownStatus = new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Unknown);
        var inventory = new KeyInventoryFacts(
            Scope(),
            "key-unknown",
            Unknown<int>("total"),
            Unknown<int>("fir"),
            Unknown<int>("duplicates"),
            Unknown<int>("maximum-uses"),
            Unknown<int>("remaining-uses"));
        var utility = new KeyUtilityFacts(
            unknownStatus,
            UnknownProvenance(),
            [],
            Unknown<KeyObtainability>("trader"),
            Unknown<KeyObtainability>("flea"),
            Unknown<long>("cost"),
            Unknown<long>("loot"),
            Unknown<bool>("unique"),
            Unknown<double>("route-utility"),
            Unknown<double>("route-risk"));
        var request = new ProfileAwareKeyIntelligenceRequest(
            KeyIntelligenceEntryPoint.ContextScreenshot,
            inventory.ItemId,
            ObservedUtc.AddMinutes(1),
            inventory,
            unknownStatus,
            UnknownProvenance(),
            [],
            utility);

        var result = new ProfileAwareKeyIntelligenceService().Evaluate(request, CancellationToken.None);

        Assert.Equal(ResultCompleteness.Unknown, result.Status.Completeness);
        Assert.Equal(KeyIntelligenceTier.Review, result.Tier);
        Assert.Null(result.Score);
        Assert.NotEmpty(result.MissingFacts);
        Assert.Contains(result.Reasons, reason => reason.Code == "key.evidence.review-required");
    }

    [Fact]
    public void SourcedModelInputKeepsTheCombinedScoreModelledAndNeverLive()
    {
        var publicInput = PublicProvenance();
        var modelledRisk = new EvidenceProvenance(
            EvidenceSourceClass.ModelledEstimate,
            "reviewed-route-risk",
            ObservedUtc,
            new EvidenceConfidence(EvidenceConfidenceKind.CalibratedEstimate, 0.7, "fixture-calibration"),
            new ProducerIdentity("route fixture", "1", "route-model-1"),
            dataThroughUtc: ObservedUtc,
            generatedUtc: ObservedUtc,
            coverage: new EvidenceCoverage(sampleSize: 50),
            reference: "fixture://route-risk",
            inputs: [publicInput]);
        var request = CompleteRequest(KeyIntelligenceEntryPoint.Planner, routeRiskProvenance: modelledRisk);

        var result = new ProfileAwareKeyIntelligenceService().Evaluate(request, CancellationToken.None);

        Assert.Equal(ResultCompleteness.Complete, result.Status.Completeness);
        Assert.NotNull(result.ScoreProvenance);
        Assert.Equal(EvidenceSourceClass.ModelledEstimate, result.ScoreProvenance.SourceClass);
        Assert.Equal("route-model-1", result.ScoreProvenance.Producer.ModelVersion);
        Assert.Contains(result.Reasons, reason =>
            reason.Code == "key.route.reviewed-estimate" &&
            reason.Explanation.Contains("not live detection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OversizedScoreLineageDegradesToReviewInsteadOfDroppingEvidence()
    {
        var leaves = Enumerable.Range(0, EvidenceProvenance.MaxInputCount)
            .Select(index => PublicProvenance($"fixture-{index}"))
            .ToArray();
        var maximumLineage = new EvidenceProvenance(
            EvidenceSourceClass.DerivedCalculation,
            "maximum-lineage",
            ObservedUtc,
            EvidenceConfidence.Certain,
            new ProducerIdentity("fixture", "1"),
            generatedUtc: ObservedUtc,
            inputs: leaves);
        var request = CompleteRequest(
            KeyIntelligenceEntryPoint.Search,
            acquisitionCostProvenance: maximumLineage,
            requirementTiming: null);

        var result = new ProfileAwareKeyIntelligenceService().Evaluate(request, CancellationToken.None);

        Assert.Null(result.Score);
        Assert.Null(result.ScoreProvenance);
        Assert.Equal(KeyIntelligenceTier.Review, result.Tier);
        Assert.Equal(ResultCompleteness.Partial, result.Status.Completeness);
        Assert.Contains(result.MissingFacts, fact => fact.Contains("lineage", StringComparison.Ordinal));
    }

    [Fact]
    public void ReviewedOverrideRequiresReviewMetadataAndSourcedCuratedProvenance()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ReviewedKeyOverride(
            90,
            KeyIntelligenceTier.S,
            "Reviewed.",
            null,
            "0.16",
            "maps-1",
            "reviewer",
            ObservedUtc,
            PublicProvenance()));

        Assert.Contains("curated provenance", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewedOverrideCarriesReviewerVersionsAndWinsGeneratedScore()
    {
        var curated = new EvidenceProvenance(
            EvidenceSourceClass.CuratedData,
            "reviewed key manifest",
            ObservedUtc,
            new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.85),
            new ProducerIdentity("key reviewers", "1"),
            reference: "manifest://keys/review-1");
        var reviewed = new ReviewedKeyOverride(
            88,
            KeyIntelligenceTier.S,
            "Reviewed route priority.",
            "Reviewed against the named game and map versions.",
            "0.16.9",
            "maps-2026-09-16",
            "reviewer-1",
            ObservedUtc,
            curated);
        var baseline = CompleteRequest(KeyIntelligenceEntryPoint.Search);
        var request = new ProfileAwareKeyIntelligenceRequest(
            baseline.EntryPoint,
            baseline.ItemId,
            baseline.EvaluatedUtc,
            baseline.Inventory,
            baseline.RequirementsStatus,
            baseline.RequirementsProvenance,
            baseline.Requirements,
            baseline.Utility,
            reviewed);

        var result = new ProfileAwareKeyIntelligenceService().Evaluate(request, CancellationToken.None);

        Assert.Equal(88d, result.Score);
        Assert.Equal(KeyIntelligenceTier.S, result.Tier);
        Assert.Same(reviewed, result.AppliedOverride);
        Assert.Equal(curated, result.ScoreProvenance);
        Assert.Equal("reviewer-1", result.AppliedOverride!.Reviewer);
        Assert.Contains(result.Reasons, reason => reason.Code == "key.override.reviewed");
    }

    [Fact]
    public void StaleFactsRemainExplicitAndRequireReview()
    {
        var request = CompleteRequest(KeyIntelligenceEntryPoint.ContextScreenshot, stale: true);

        var result = new ProfileAwareKeyIntelligenceService().Evaluate(request, CancellationToken.None);

        Assert.Equal(ResultCompleteness.Partial, result.Status.Completeness);
        Assert.Equal(FreshnessState.Stale, result.Status.Freshness);
        Assert.Contains(result.MissingFacts, fact => fact.Contains("stale", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ContractsRejectDuplicateAssociationsAndOutOfRangeCandidateValues()
    {
        var provenance = PublicProvenance();
        var association = Association(provenance);
        Assert.Throws<ArgumentException>(() => FullUtility(
            provenance,
            associations: [association, association]));

        var badCandidate = new EvidenceCandidate<double?>(
            "bad",
            "bad",
            1.1,
            provenance);
        var routeRisk = new EvidencedValue<double?>(
            "route-risk",
            0.2,
            CompleteStatus(),
            provenance,
            candidates: [badCandidate]);
        Assert.Throws<ArgumentOutOfRangeException>(() => FullUtility(provenance, routeRisk: routeRisk));
    }

    [Fact]
    public void MaximumRequirementSetProducesBoundedReasonsWithAnExplicitLimitMarker()
    {
        var baseline = CompleteRequest(KeyIntelligenceEntryPoint.Planner, requirementTiming: null);
        var requirements = Enumerable.Range(0, KeyIntelligenceBounds.MaximumRequirements)
            .Select(index => new KeyRequirementFact(
                $"quest-{index}",
                KeyRequirementTiming.Future,
                1,
                false,
                CompleteStatus(),
                PublicProvenance($"quest-source-{index}")))
            .ToArray();
        var request = new ProfileAwareKeyIntelligenceRequest(
            baseline.EntryPoint,
            baseline.ItemId,
            baseline.EvaluatedUtc,
            baseline.Inventory,
            baseline.RequirementsStatus,
            baseline.RequirementsProvenance,
            requirements,
            baseline.Utility);

        var result = new ProfileAwareKeyIntelligenceService().Evaluate(request, CancellationToken.None);

        Assert.Equal(KeyIntelligenceBounds.MaximumReasons, result.Reasons.Count);
        Assert.Equal("key.reasons.limit", result.Reasons[^1].Code);
        Assert.Equal(KeyIntelligenceBounds.MaximumRequirements, result.Requirements.Count);
    }

    [Fact]
    public void CancellationIsObservedBeforeEvaluation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new ProfileAwareKeyIntelligenceService().Evaluate(
                CompleteRequest(KeyIntelligenceEntryPoint.Search),
                cancellation.Token));
    }

    private static ProfileAwareKeyIntelligenceRequest CompleteRequest(
        KeyIntelligenceEntryPoint entryPoint,
        EvidenceProvenance? routeRiskProvenance = null,
        EvidenceProvenance? acquisitionCostProvenance = null,
        KeyRequirementTiming? requirementTiming = KeyRequirementTiming.Current,
        bool stale = false)
    {
        var provenance = PublicProvenance();
        var status = stale ? new ResultStatus(ResultCompleteness.Complete, FreshnessState.Stale) : CompleteStatus();
        var inventory = new KeyInventoryFacts(
            Scope(),
            "key-1",
            Known("total", 3, provenance, status),
            Known("fir", 2, provenance, status),
            Known("duplicates", 2, provenance, status),
            Known("maximum-uses", 40, provenance, status),
            Known("remaining-uses", 32, provenance, status));
        var utility = FullUtility(
            provenance,
            status,
            routeRisk: Known("route-risk", 0.25, routeRiskProvenance ?? provenance, status),
            acquisitionCostProvenance: acquisitionCostProvenance);
        KeyRequirementFact[] requirements = requirementTiming is { } timing
            ? [new("quest-1", timing, 2, true, status, provenance)]
            : [];
        return new ProfileAwareKeyIntelligenceRequest(
            entryPoint,
            inventory.ItemId,
            ObservedUtc.AddMinutes(1),
            inventory,
            status,
            provenance,
            requirements,
            utility);
    }

    private static KeyUtilityFacts FullUtility(
        EvidenceProvenance provenance,
        ResultStatus? status = null,
        IReadOnlyList<KeyAccessAssociation>? associations = null,
        EvidencedValue<double?>? routeRisk = null,
        EvidenceProvenance? acquisitionCostProvenance = null)
    {
        var complete = status ?? CompleteStatus();
        return new KeyUtilityFacts(
            complete,
            provenance,
            associations ?? [Association(provenance, complete)],
            Known("trader", KeyObtainability.Restricted, provenance, complete),
            Known("flea", KeyObtainability.Available, provenance, complete),
            Known("cost", 100_000L, acquisitionCostProvenance ?? provenance, complete),
            Known("loot", 300_000L, provenance, complete),
            Known("unique", true, provenance, complete),
            Known("route-utility", 0.8, provenance, complete),
            routeRisk ?? Known("route-risk", 0.25, provenance, complete));
    }

    private static KeyAccessAssociation Association(
        EvidenceProvenance provenance,
        ResultStatus? status = null) =>
        new(
            "association-1",
            KnownString("map", "customs", provenance, status),
            KnownString("lock", "dorms-206-lock", provenance, status),
            KnownString("room", "dorms-206", provenance, status),
            provenance);

    private static InventoryProfileScope Scope() =>
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "wipe-1", "regular");

    private static ResultStatus CompleteStatus() =>
        new(ResultCompleteness.Complete, FreshnessState.Current);

    private static ResultStatus PartialStatus() =>
        new(ResultCompleteness.Partial, FreshnessState.Current);

    private static EvidencedValue<T?> Known<T>(
        string fieldId,
        T value,
        EvidenceProvenance provenance,
        ResultStatus? status = null)
        where T : struct =>
        new(fieldId, value, status ?? CompleteStatus(), provenance);

    private static EvidencedValue<T?> Unknown<T>(string fieldId)
        where T : struct =>
        new(
            fieldId,
            null,
            new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Unknown),
            UnknownProvenance());

    private static EvidencedValue<string> KnownString(
        string fieldId,
        string value,
        EvidenceProvenance provenance,
        ResultStatus? status = null) =>
        new(fieldId, value, status ?? CompleteStatus(), provenance);

    private static EvidenceProvenance PublicProvenance(string sourceIdentifier = "json.tarkov.dev fixture") =>
        new(
            EvidenceSourceClass.PublicStructuredData,
            sourceIdentifier,
            ObservedUtc,
            new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.9),
            new ProducerIdentity("fixture", "1"),
            reference: "fixture://keys");

    private static EvidenceProvenance UnknownProvenance() =>
        new(
            EvidenceSourceClass.Unknown,
            "missing fixture fact",
            ObservedUtc,
            EvidenceConfidence.Unscored,
            new ProducerIdentity("fixture", "1"));
}
