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

public sealed record ConsentEvidence(
    string ConsentHash,
    IReadOnlyList<string> AllowedUses,
    DateTimeOffset RetentionExpiresUtc,
    string RevocationState);

public sealed record PrivacyReviewEvidence(string State, string RedactionState, DateTimeOffset ReviewedUtc);

/// <summary>
/// Context is deliberately bounded and opaque. It helps slice a benchmark without becoming
/// implicit truth; user names, paths, filenames, pixels, OCR strings, and consent records do
/// not belong in this interchange.
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

public sealed record TruthClaim(string TruthId, TruthState State, string Kind, string? Value);

public sealed record CorpusSample(
    string SampleId,
    CorpusEvidenceClass EvidenceClass,
    string? DecodedPixelSha256,
    IReadOnlyList<string> NearDuplicateHashes,
    string DeclaredSplitUnitId,
    CaptureContext Context,
    SequenceLineage Lineage,
    IReadOnlyList<TruthClaim> Truth,
    ConsentEvidence? Consent,
    PrivacyReviewEvidence? PrivacyReview);

public sealed record CorpusManifest(
    string CorpusId,
    string NearDuplicateGraphVersion,
    IReadOnlyList<CorpusSample> Samples);

public sealed record RunPlanSample(
    string SampleId,
    CorpusSplit Split,
    BenchmarkIntent Intent,
    CaptureContext Context,
    SequenceLineage Lineage);

public sealed record RunPlan(string RunId, string ProducerId, string ProducerVersion, string PolicyVersion, IReadOnlyList<RunPlanSample> Samples);

public sealed record PredictionClaim(string ClaimId, string Kind, string? Value);

public sealed record ProducerPrediction(
    string SampleId,
    BenchmarkIntent Intent,
    CorpusEvidenceClass EvidenceClass,
    string Type,
    PredictionStatus Status,
    decimal Confidence,
    IReadOnlyList<PredictionClaim> Claims,
    decimal? ElapsedMilliseconds = null);

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
    int IndependentSplitUnits,
    int TruePositives,
    int FalsePositives,
    int FalseNegatives,
    int Abstentions,
    int ConfidentWrong,
    int MissingFrames,
    int ReorderedFrames,
    int OverlapDeduplicationErrors,
    decimal Coverage,
    decimal AbstentionRate,
    decimal ConfidentWrongRate,
    decimal ConfidenceIntervalLower,
    decimal ConfidenceIntervalUpper,
    decimal? MeanElapsedMilliseconds);
