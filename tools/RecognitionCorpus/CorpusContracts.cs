namespace TarkovCompanion.RecognitionCorpus;

public enum CorpusEvidenceClass
{
    RealRaster,
    SyntheticRaster,
    PostOcrEvidence,
}

public enum TruthState
{
    Known,
    Unknown,
}

public enum CorpusSplit
{
    Train,
    Tune,
    Test,
}

public enum BenchmarkIntent
{
    LootDecision,
    FullStash,
    Ammo,
    Keys,
    QuestItems,
    MapExtractsTimers,
    HealthCharacter,
    AutoDetect,
}

public enum PredictionStatus
{
    Detected,
    Abstained,
    Unavailable,
}

public enum PredictionType
{
    Context,
    Region,
    GridCell,
    Item,
    Attribute,
    Extract,
    Timer,
    Health,
    Recommendation,
}

public sealed record PixelRegion(int X, int Y, int Width, int Height);

public sealed record ConsentEvidence(
    string ConsentId,
    string ConsentHash,
    IReadOnlyList<string> AllowedUses,
    DateTimeOffset ConsentedUtc,
    DateTimeOffset RetentionExpiresUtc,
    string RevocationState);

public sealed record PrivacyReviewEvidence(
    string ReviewId,
    string ReviewHash,
    string State,
    string RedactionState,
    DateTimeOffset ReviewedUtc);

/// <summary>
/// Context is deliberately bounded and opaque. It helps slice a benchmark without becoming
/// implicit truth; user names, paths, filenames, pixels, OCR strings, and consent records do
/// not belong in the producer interchange.
/// </summary>
public sealed record CaptureContext(
    string CaptureIntentId,
    string SessionId,
    string CorrelationId,
    int CaptureOrdinal,
    string? WorkspaceId,
    string? ProfileId,
    string? MapId,
    string? FloorId,
    string? PlanId,
    IReadOnlyList<string> ObjectiveIds,
    string? SelectedReference,
    string? PriorScanReference,
    string DeviceClass,
    string Surface,
    int Width,
    int Height,
    decimal UiScale,
    string Locale,
    string GameVersion,
    string CompanionUiVersion);

public sealed record SequenceLineage(
    string SequenceId,
    int FrameOrdinal,
    string ViewportId,
    string ContainerIdentity,
    decimal OverlapWithPrevious,
    string? ParentContainerIdentity);

public sealed record TruthClaim(
    string TruthId,
    TruthState State,
    string Kind,
    string? Value,
    PixelRegion? Region = null);

public sealed record CorpusSample(
    string SampleId,
    CorpusEvidenceClass EvidenceClass,
    string? DecodedPixelSha256,
    IReadOnlyList<string> NearDuplicateHashes,
    string DeclaredSplitUnitId,
    CaptureContext Context,
    SequenceLineage Lineage,
    IReadOnlyList<TruthClaim> Truth,
    string? ConsentHash,
    string? PrivacyReviewHash);

/// <summary>
/// These records are private importer authority. A sample's hash-shaped summaries never become
/// eligibility evidence unless the corresponding full records and observed decoded-pixel hash
/// are present here and agree at validation time.
/// </summary>
public sealed record PrivateSampleEvidence(
    string SampleId,
    string? ObservedDecodedPixelSha256,
    ConsentEvidence? Consent,
    PrivacyReviewEvidence? PrivacyReview);

public sealed record CorpusManifest(
    string CorpusId,
    string NearDuplicateGraphVersion,
    IReadOnlyList<CorpusSample> Samples,
    IReadOnlyList<PrivateSampleEvidence> PrivateEvidence);

public sealed record RunPlanSample(
    string SampleId,
    CorpusSplit Split,
    BenchmarkIntent Intent,
    CorpusEvidenceClass EvidenceClass,
    CaptureContext Context,
    SequenceLineage Lineage);

public sealed record RunPlan(
    string RunId,
    string ProducerId,
    string ProducerVersion,
    string PolicyVersion,
    string CorpusId,
    string NearDuplicateGraphVersion,
    string PlanLock,
    IReadOnlyList<RunPlanSample> Samples);

public sealed record PredictionClaim(
    string ClaimId,
    string Kind,
    string? Value,
    PixelRegion? Region = null);

public sealed record ProducerPrediction(
    string SampleId,
    BenchmarkIntent Intent,
    CorpusEvidenceClass EvidenceClass,
    PredictionType Type,
    PredictionStatus Status,
    decimal Confidence,
    IReadOnlyList<PredictionClaim> Claims,
    decimal ElapsedMilliseconds);

/// <summary>
/// Producer output is bound to the lock of the exact plan it answered, not only to a reusable
/// run id, so predictions for a superseded plan cannot be scored against its replacement.
/// </summary>
public sealed record PredictionDocument(
    string RunId,
    string ProducerId,
    string ProducerVersion,
    string PlanLock,
    IReadOnlyList<ProducerPrediction> Predictions);

public sealed record FrozenThresholds(
    string PolicyVersion,
    int MinimumIndependentSplitUnits,
    int MinimumKnownClaims,
    decimal MinimumCoverage,
    decimal MaximumAbstentionRate,
    decimal MaximumConfidentWrongRate,
    decimal MinimumF1);

public sealed record SliceMetrics(
    BenchmarkIntent Intent,
    CorpusEvidenceClass EvidenceClass,
    string Status,
    int Numerator,
    int Denominator,
    int ExcludedUnknowns,
    int ExcludedPredictionClaims,
    int IndependentSplitUnits,
    int AttemptedKnownClaims,
    int TruePositives,
    int FalsePositives,
    int FalseNegatives,
    int Abstentions,
    int ConfidentWrong,
    int MissingFrames,
    int ReorderedFrames,
    int OverlapDeduplicationErrors,
    int AccuracyNumerator,
    int AccuracyDenominator,
    int RecallNumerator,
    int RecallDenominator,
    int FalsePositiveNumerator,
    int FalsePositiveDenominator,
    decimal Coverage,
    decimal Accuracy,
    decimal Recall,
    decimal FalsePositiveRate,
    decimal F1,
    decimal AbstentionRate,
    decimal ConfidentWrongRate,
    decimal ConfidenceIntervalLower,
    decimal ConfidenceIntervalUpper,
    int PerformanceSampleCount,
    decimal? MeanElapsedMilliseconds,
    decimal? MaximumElapsedMilliseconds);

public sealed record AggregatePrivacy(
    bool SafeToPublish,
    int MinimumIndependentSplitUnits,
    bool ContainsPerSampleResults);

/// <summary>
/// This is the only scorer output allowed to cross the private corpus boundary. It retains
/// run/plan provenance and aggregate arithmetic, but never sample ids, labels, or private
/// consent evidence. Publication still requires semantic validation; SafeToPublish is an
/// asserted field to verify, not authority by itself.
/// </summary>
public sealed record AggregateResults(
    string RunId,
    string ProducerId,
    string ProducerVersion,
    string CorpusId,
    string PlanLock,
    string PolicyVersion,
    CorpusEvidenceClass EvidenceClass,
    DateTimeOffset ScoredUtc,
    AggregatePrivacy Privacy,
    IReadOnlyList<SliceMetrics> Slices);
