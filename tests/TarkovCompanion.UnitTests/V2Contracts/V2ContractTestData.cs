using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.UnitTests.V2Contracts;

internal static class V2ContractTestData
{
    public static readonly DateTimeOffset ObservedUtc =
        new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The screenshot predates its acquisition, as a late import does.</summary>
    public static readonly DateTimeOffset CapturedUtc = ObservedUtc.AddMinutes(-3);

    public static readonly WorkspaceOrigin Origin = new(
        new WorkspaceId(Guid.Parse("10000000-0000-0000-0000-000000000001")),
        new CompanionDeviceId(Guid.Parse("10000000-0000-0000-0000-000000000002")),
        WorkspaceOriginKind.DesktopApplication,
        "desktop-primary");

    public static readonly CaptureSessionId SessionId =
        new(Guid.Parse("20000000-0000-0000-0000-000000000001"));

    public static EvidenceProvenance ScreenshotProvenance() => new(
        EvidenceSourceClass.GameWrittenScreenshot,
        "fixture://extract-screen",
        ObservedUtc,
        new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.96),
        new ProducerIdentity("fixture-ocr", "2.0"));

    public static EvidenceProvenance PublicDataProvenance(DateTimeOffset? observedUtc = null) => new(
        EvidenceSourceClass.PublicStructuredData,
        "fixture://catalog",
        observedUtc ?? ObservedUtc.AddDays(-2),
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

    public static ResultStatus CompleteStatus { get; } = new(ResultCompleteness.Complete, FreshnessState.Current);

    public static EvidencedValue<T> Complete<T>(
        string fieldId,
        T value,
        EvidenceProvenance? provenance = null,
        EvidenceRegion? bounds = null,
        IReadOnlyList<EvidenceCandidate<T>>? candidates = null,
        IReadOnlyList<EvidenceCorrection<T>>? corrections = null) => new(
        fieldId,
        value,
        CompleteStatus,
        provenance ?? ScreenshotProvenance(),
        bounds,
        candidates,
        corrections);

    public static EvidencedValue<T> Unknown<T>(string fieldId, EvidenceProvenance? provenance = null) => new(
        fieldId,
        default,
        new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current),
        provenance ?? ScreenshotProvenance());

    public static RecognizedItem Item(string id = "item-a", int width = 1, int height = 1, ItemConditionReading? condition = null) => new(
        Complete("item.id", id),
        Complete("item.name", "Item A"),
        Complete<int?>("item.quantity", 1),
        Complete<int?>("item.width", width),
        Complete<int?>("item.height", height),
        Complete<bool?>("item.rotated", false),
        Complete<bool?>("item.foundInRaid", true),
        Complete("item.condition", condition ?? ItemConditionReading.NotApplicable));

    public static GridRecognition Grid(params GridCellRecognition[] cells) => new(
        new GridGeometry(
            Complete<int?>("grid.rows", 4),
            Complete<int?>("grid.columns", 10),
            Complete<int?>("grid.cellWidth", 63),
            Complete<int?>("grid.cellHeight", 63)),
        cells);

    public static GridCellRecognition Cell(int row, int column, RecognizedItem? item = null, string? nested = null) => new(
        new GridCellAddress(row, column),
        Complete($"grid.{row}.{column}", item ?? Item()),
        nested);

    public static RecognitionResultHeader Header(RecognizedContext? context) => new(
        "result-1",
        V2ContractVersion.Current,
        SessionId,
        "artifact-1",
        CapturedUtc,
        ScanIntent.Auto,
        context is { } detected
            ? Complete<RecognizedContext?>("context", detected)
            : Unknown<RecognizedContext?>("context"));
}
