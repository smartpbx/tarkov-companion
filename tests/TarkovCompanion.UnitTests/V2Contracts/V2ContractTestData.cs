using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.UnitTests.V2Contracts;

internal static class V2ContractTestData
{
    public static readonly DateTimeOffset ObservedUtc =
        new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    public static EvidenceProvenance ScreenshotProvenance() => new(
        EvidenceSourceClass.GameWrittenScreenshot,
        "fixture://extract-screen",
        ObservedUtc,
        new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.96),
        new ProducerIdentity("fixture-ocr", "2.0"));

    public static EvidenceProvenance PublicDataProvenance() => new(
        EvidenceSourceClass.PublicStructuredData,
        "fixture://catalog",
        ObservedUtc,
        EvidenceConfidence.Certain,
        new ProducerIdentity("fixture-catalog", "2.0"));

    public static EvidenceProvenance ModelProvenance(EvidenceSourceClass sourceClass) => new(
        sourceClass,
        "fixture://traffic-model",
        ObservedUtc,
        new EvidenceConfidence(EvidenceConfidenceKind.CalibratedEstimate, 0.72, "calibration-2026-09"),
        new ProducerIdentity("fixture-model", "2.0", "traffic-model-4"),
        ObservedUtc.AddDays(-1),
        ObservedUtc.AddHours(-1),
        new EvidenceCoverage(240, 0.80, "Two hundred forty historical route samples"));

    public static EvidencedValue<T> Complete<T>(
        string fieldId,
        T value,
        EvidenceProvenance? provenance = null,
        EvidenceRegion? bounds = null,
        IReadOnlyList<EvidenceCandidate<T>>? candidates = null,
        IReadOnlyList<EvidenceCorrection<T>>? corrections = null) => new(
        fieldId,
        value,
        new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current),
        provenance ?? ScreenshotProvenance(),
        bounds,
        candidates,
        corrections);

    public static RecognitionResultHeader Header(RecognizedContext context) => new(
        "result-1",
        V2ContractVersion.Current,
        new CaptureSessionId(Guid.Parse("20000000-0000-0000-0000-000000000001")),
        "artifact-1",
        ScanIntent.Auto,
        context);
}
