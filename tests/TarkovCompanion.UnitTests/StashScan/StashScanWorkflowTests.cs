using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Core.Domain.Stash;
using TarkovCompanion.UnitTests.V2Contracts;

namespace TarkovCompanion.UnitTests.StashScan;

public sealed class StashScanWorkflowTests
{
    private static readonly CaptureSessionId SessionId = new(
        Guid.Parse("83000000-0000-0000-0000-000000000001"));

    private static readonly InventoryProfileScope Scope = new(
        Guid.Parse("83000000-0000-0000-0000-000000000002"),
        "wipe-2026-09",
        "Pvp");

    [Fact]
    public void CaptureBoundLeavesRoomForTwoMaximumAssemblyRootsInAComparison()
    {
        Assert.Equal(
            EvidenceProvenance.MaxInputCount,
            2 + (2 * StashScanBounds.MaximumCaptures));
        Assert.True(EvidenceProvenance.MaxInputDepth >= 3);
    }

    [Fact]
    public void OrderedOverlappingFramesStitchWithoutDoubleCounting()
    {
        var first = Frame(
            0,
            'a',
            Grid(
                Cell(0, "item-a"),
                Cell(1, "item-b"),
                Cell(2, "item-d")),
            confirmsStart: true,
            values: new Dictionary<GridCellAddress, EvidencedValue<long?>>
            {
                [new(0, 0)] = Complete<long?>(10),
                [new(1, 0)] = Complete<long?>(20),
                [new(2, 0)] = Complete<long?>(30),
            });
        var second = Frame(
            1,
            'b',
            Grid(
                Cell(0, "item-b"),
                Cell(1, "item-d"),
                Cell(2, "item-c")),
            values: new Dictionary<GridCellAddress, EvidencedValue<long?>>
            {
                [new(0, 0)] = Complete<long?>(20),
                [new(1, 0)] = Complete<long?>(30),
                [new(2, 0)] = Complete<long?>(40),
            });

        var result = new StashScanAssembler().Assemble(Request(first, second));

        Assert.Equal(ResultCompleteness.Complete, result.Report.Status.Completeness);
        Assert.Equal(1, result.Recognition.Result.Value!.CapturedRegions[1].OriginInContainer.Value!.Value.Row);
        Assert.Equal(4, Assert.Single(result.Recognition.Result.Value.Coverage).ObservedCells.Value);
        Assert.Equal(100L, result.Recognition.Result.Value.TotalKnownValueRoubles.Value);
        Assert.Equal(3, result.Recognition.Result.Value.TotalKnownValueRoubles.Provenance.Inputs.Count);
        Assert.Empty(result.Report.Issues);
        Assert.Equal(EvidenceSourceClass.DerivedCalculation, result.Recognition.Result.Provenance.SourceClass);
        Assert.Equal(2, result.Recognition.Result.Provenance.Inputs.Count);
    }

    [Fact]
    public void MissingThenCompleteOverlappingValueUsesTheFinalReconciledObservation()
    {
        var missing = Frame(
            0,
            'a',
            Grid(Cell(0, "item-a")),
            confirmsStart: true,
            totalCells: 3);
        var complete = Frame(
            1,
            'b',
            Grid(Cell(0, "item-a")),
            origin: new GridCellAddress(0, 0),
            values: Values((0, 25)),
            totalCells: 3);

        var result = new StashScanAssembler().Assemble(Request(missing, complete));

        Assert.Equal(25L, result.Recognition.Result.Value!.TotalKnownValueRoubles.Value);
        Assert.Equal(
            ResultCompleteness.Complete,
            result.Recognition.Result.Value.TotalKnownValueRoubles.Status.Completeness);
        Assert.Equal(ResultCompleteness.Complete, result.Report.Status.Completeness);
    }

    [Fact]
    public void AllMissingOverlappingValuesKeepTheKnownTotalPartial()
    {
        var first = Frame(
            0,
            'a',
            Grid(Cell(0, "item-a")),
            confirmsStart: true,
            totalCells: 3);
        var second = Frame(
            1,
            'b',
            Grid(Cell(0, "item-a")),
            origin: new GridCellAddress(0, 0),
            totalCells: 3);

        var result = new StashScanAssembler().Assemble(Request(first, second));

        Assert.Equal(0L, result.Recognition.Result.Value!.TotalKnownValueRoubles.Value);
        Assert.Equal(
            ResultCompleteness.Partial,
            result.Recognition.Result.Value.TotalKnownValueRoubles.Status.Completeness);
        Assert.Equal(ResultCompleteness.Partial, result.Report.Status.Completeness);
    }

    [Fact]
    public void DuplicateContentIsAcceptedOnceAndItsIdentityDoesNotEscapeTheReport()
    {
        var first = Frame(0, 'a', Grid(Cell(0, "item-a")), confirmsStart: true);
        var duplicate = Frame(1, 'a', Grid(Cell(0, "item-a")), origin: new GridCellAddress(0, 0));

        var result = new StashScanAssembler().Assemble(Request(first, duplicate));

        Assert.Equal([first.ArtifactId], result.Report.AcceptedArtifactIds);
        Assert.Equal([duplicate.ArtifactId], result.Report.DuplicateArtifactIds);
        Assert.Contains(result.Report.Issues, issue => issue.Kind == StashScanIssueKind.DuplicateCapture);
        Assert.DoesNotContain(first.ContentSha256, result.Report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectedArtifactCollisionDoesNotPoisonANewContentIdentity()
    {
        var first = Frame(0, 'a', Grid(Cell(0, "item-a")), confirmsStart: true);
        var artifactCollision = Frame(
            1,
            'b',
            Grid(Cell(0, "item-b")),
            origin: new GridCellAddress(1, 0),
            artifactId: first.ArtifactId);
        var laterValid = Frame(2, 'b', Grid(Cell(0, "item-b")), origin: new GridCellAddress(1, 0));

        var result = new StashScanAssembler().Assemble(Request(first, artifactCollision, laterValid));

        Assert.Equal([first.ArtifactId, laterValid.ArtifactId], result.Report.AcceptedArtifactIds);
        Assert.Equal([artifactCollision.ArtifactId], result.Report.DuplicateArtifactIds);
    }

    [Fact]
    public void SessionRejectsCaptureWhoseFrozenContextChangedMidSequence()
    {
        var first = Frame(0, 'a', Grid(Cell(0, "item-a")), confirmsStart: true);
        var changedContext = Frame(
            1,
            'b',
            Grid(Cell(0, "item-b")),
            origin: new GridCellAddress(1, 0),
            context: new CaptureContextMetadata(null, null, null, null, null, "other-scan", null));

        Assert.Throws<ArgumentException>(() => Request(first, changedContext));
    }

    [Fact]
    public void ClosedNestedContainerProducesGuidanceWithoutInventingCoverage()
    {
        var frame = Frame(
            0,
            'a',
            Grid(Cell(0, "bag-item")),
            confirmsStart: true,
            closedContainers: ["stash/bag-a"]);

        var result = new StashScanAssembler().Assemble(Request(frame));

        var stash = result.Recognition.Result.Value!;
        Assert.Single(stash.Coverage);
        Assert.Equal("stash", stash.Coverage[0].ContainerPath);
        var issue = Assert.Single(result.Report.Issues.Where(item => item.Kind == StashScanIssueKind.ClosedContainer));
        Assert.Equal(StashScanRetryAction.ReopenAndCaptureContainer, issue.RetryAction);
        Assert.Equal("stash/bag-a", issue.ContainerPath);
    }

    [Fact]
    public void OpenedNestedContainerIsRetainedOnlyThroughItsObservedParentCell()
    {
        var root = Frame(
            0,
            'a',
            Grid(Cell(0, "bag-item", "stash/bag-a")),
            confirmsStart: true);
        var bag = Frame(
            1,
            'b',
            Grid(Cell(0, "inside-item")),
            confirmsStart: true,
            containerPath: "stash/bag-a");

        var result = new StashScanAssembler().Assemble(Request(root, bag));

        Assert.Equal(
            ["stash", "stash/bag-a"],
            result.Recognition.Result.Value!.Coverage.Select(item => item.ContainerPath));
        Assert.Contains(result.Recognition.Result.Value.CapturedRegions, region =>
            region.ContainerPath == "stash/bag-a");
    }

    [Fact]
    public void OutOfOrderInputKeepsOrdinalsAndTargetsReview()
    {
        var later = Frame(1, 'b', Grid(Cell(0, "item-b")), origin: new GridCellAddress(1, 0));
        var first = Frame(0, 'a', Grid(Cell(0, "item-a")), confirmsStart: true);

        var result = new StashScanAssembler().Assemble(Request(later, first));

        Assert.Equal([0, 1], result.Recognition.Result.Value!.CapturedRegions.Select(region => region.CaptureOrdinal));
        Assert.Contains(result.Report.Issues, issue =>
            issue.Kind == StashScanIssueKind.OutOfOrderCapture &&
            issue.RetryAction == StashScanRetryAction.ReviewConflictingCells);
    }

    [Fact]
    public void RepeatedAnchorsWithTiedOffsetsStayUnplaced()
    {
        var first = Frame(
            0,
            'a',
            Grid(Cell(0, "item-a"), Cell(1, "item-a"), Cell(2, "item-a")),
            confirmsStart: true);
        var ambiguous = Frame(
            1,
            'b',
            Grid(Cell(0, "item-a"), Cell(1, "item-a")));

        var result = new StashScanAssembler().Assemble(Request(first, ambiguous));

        Assert.Null(result.Recognition.Result.Value!.CapturedRegions[1].OriginInContainer.Value);
        Assert.Contains(result.Report.Issues, issue =>
            issue.Kind == StashScanIssueKind.AmbiguousOverlap &&
            issue.RetryAction == StashScanRetryAction.CaptureWithMoreOverlap);
    }

    [Fact]
    public void MovementConflictRemainsExcludedWhenAThirdFrameMatchesTheOriginal()
    {
        var original = Frame(
            0,
            'a',
            Grid(Cell(0, "item-a"), Cell(1, "item-b"), Cell(2, "item-c")),
            confirmsStart: true,
            values: Values((0, 10), (1, 20), (2, 30)));
        var changed = Frame(
            1,
            'b',
            Grid(Cell(0, "item-a"), Cell(1, "item-x"), Cell(2, "item-c")),
            origin: new GridCellAddress(0, 0),
            values: Values((0, 10), (1, 99), (2, 30)));
        var repeatedOriginal = Frame(
            2,
            'c',
            Grid(Cell(0, "item-a"), Cell(1, "item-b"), Cell(2, "item-c")),
            origin: new GridCellAddress(0, 0),
            values: Values((0, 10), (1, 20), (2, 30)));

        var result = new StashScanAssembler().Assemble(Request(original, changed, repeatedOriginal));

        Assert.Equal(40L, result.Recognition.Result.Value!.TotalKnownValueRoubles.Value);
        Assert.Equal(ResultCompleteness.Partial, result.Recognition.Result.Value.TotalKnownValueRoubles.Status.Completeness);
        Assert.Equal(1, result.Recognition.Result.Value.UnresolvedCells.Value);
        Assert.Contains(result.Report.Issues, issue => issue.Kind == StashScanIssueKind.MovementConflict);
    }

    [Fact]
    public void UncertainStackCountSurvivesAndKeepsTheSnapshotUnderReview()
    {
        var frame = Frame(0, 'a', Grid(CellWithUnknownQuantity(0, "ammo-a")), confirmsStart: true);

        var result = new StashScanAssembler().Assemble(Request(frame));
        var stash = result.Recognition.Result.Value!;

        Assert.Null(stash.CapturedRegions[0].Grid.Cells[0].Item.Value!.Quantity.Value);
        Assert.Equal(1, stash.UnresolvedCells.Value);
        Assert.Equal(ResultCompleteness.Partial, result.Report.Status.Completeness);
    }

    [Fact]
    public void ComparisonSeparatesObservedFromInferredMovement()
    {
        var previous = Envelope(
            "previous",
            Region("previous-region", "previous-artifact", 0, DirectOrigin(0), Grid(Cell(0, "item-a"))));
        var current = Envelope(
            "current",
            Region("current-region", "current-artifact", 0, DerivedOrigin(1), Grid(Cell(0, "item-a"))));

        var result = new StashSnapshotComparer().Compare(
            previous,
            current,
            V2ContractTestData.ObservedUtc.AddMinutes(2));

        Assert.Contains(result.Changes, change =>
            change.Kind == StashSnapshotChangeKind.Moved &&
            change.PreviousState == StashObservationState.Observed &&
            change.CurrentState == StashObservationState.Inferred);
        Assert.Contains(result.Changes, change => change.Kind == StashSnapshotChangeKind.EvidenceChanged);
    }

    [Fact]
    public void KeyWithoutProfileAwareSpecialistResultStaysInReview()
    {
        var provenance = V2ContractTestData.ScreenshotProvenance();
        var unknownMoney = Unknown<long?>("money", provenance);
        var unknownAge = Unknown<TimeSpan?>("age", provenance);
        var unknownText = Unknown<string>("text", provenance);
        var input = new StashPlanningItemInput(
            "item-key",
            "canonical-key",
            "stash",
            new GridCellAddress(0, 0),
            null,
            StashSpecialistIntelligenceKind.Key,
            new ResultStatus(ResultCompleteness.Unavailable, FreshnessState.Unknown, "key-intelligence.unavailable"),
            unknownMoney,
            unknownMoney,
            unknownMoney,
            unknownAge,
            unknownText,
            unknownText);
        var request = new StashOrganizationPlanRequest(
            "plan-1",
            "snapshot-1",
            1,
            V2ContractTestData.ObservedUtc,
            [input]);

        var item = Assert.Single(new StashOrganizationPlanner().Build(request).Items);

        Assert.Equal(StashPlanGroup.Review, item.Group);
        Assert.Contains("stash.specialist.key-unresolved", item.ReasonCodes);
        Assert.Contains(StashManualOperation.ReviewEvidence, item.Operations);
        Assert.Equal(ResultCompleteness.Unknown, item.RecommendationStatus.Completeness);
    }

    [Fact]
    public async Task OneWorkflowActionPersistsBatchAndExportsOnlyPixelFreeContract()
    {
        var store = new MemorySnapshotStore();
        var workflow = new StashScanWorkflow(
            new StashScanAssembler(),
            store,
            new StashSnapshotComparer());
        var frame = Frame(0, 'f', Grid(Cell(0, "item-a")), confirmsStart: true);
        var durableId = Guid.Parse("83000000-0000-0000-0000-000000000099");

        await workflow.CompleteAsync(
            Request(frame),
            durableId,
            true,
            TestContext.Current.CancellationToken);
        var export = await workflow.ExportAsync(Scope, durableId, TestContext.Current.CancellationToken);

        Assert.NotNull(store.Snapshot);
        Assert.Equal("catalog-1", store.Snapshot.DataSnapshotId);
        var json = Assert.IsType<string>(export);
        Assert.Contains("tarkov-companion.stash-snapshot.v2", json, StringComparison.Ordinal);
        Assert.Contains("catalog-1", json, StringComparison.Ordinal);
        Assert.DoesNotContain(frame.ContentSha256, json, StringComparison.Ordinal);
        Assert.DoesNotContain("contentSha256", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManualCorrectionFlowsToTheAppendOnlyReviewSink()
    {
        var sink = new MemoryReviewSink();
        var workflow = new StashScanWorkflow(
            new StashScanAssembler(),
            new MemorySnapshotStore(),
            new StashSnapshotComparer(),
            sink);
        var command = new StashReviewCommand(
            Guid.Parse("83000000-0000-0000-0000-000000000088"),
            "snapshot-1",
            StashReviewActionKind.CorrectQuantity,
            ["item-key"],
            V2ContractTestData.ObservedUtc,
            "stash-review",
            correctedQuantity: 7,
            reason: "Reviewed visible stack count.");

        await workflow.ReviewAsync(command, TestContext.Current.CancellationToken);

        Assert.Same(command, Assert.Single(sink.Commands));
    }

    private static StashScanAssemblyRequest Request(params StashScanCaptureFrame[] frames) => new(
        "result-1",
        "snapshot-1",
        SessionId,
        Scope,
        "catalog-1",
        V2ContractTestData.ObservedUtc.AddMinutes(1),
        frames);

    private static StashScanCaptureFrame Frame(
        int ordinal,
        char hashCharacter,
        GridRecognition grid,
        bool confirmsStart = false,
        GridCellAddress? origin = null,
        IReadOnlyDictionary<GridCellAddress, EvidencedValue<long?>>? values = null,
        IReadOnlyList<string>? closedContainers = null,
        string? artifactId = null,
        CaptureContextMetadata? context = null,
        string containerPath = "stash",
        int totalCells = 4)
    {
        var provenance = ScreenshotProvenance(ordinal);
        return new StashScanCaptureFrame(
            SessionId,
            artifactId ?? $"artifact-{ordinal}",
            ordinal,
            new CaptureCorrelationId(Guid.Parse($"83000000-0000-0000-0000-{ordinal + 10:D12}")),
            context ?? CaptureContextMetadata.Empty,
            new string(hashCharacter, 64),
            containerPath,
            V2ContractTestData.CapturedUtc.AddSeconds(ordinal),
            0,
            provenance,
            new GridReconstructionResult(
                GridReconstructionOutcome.Complete,
                containerPath == "stash" ? InventoryGridSurface.Stash : InventoryGridSurface.Container,
                grid,
                [],
                []),
            Complete<int?>(totalCells, provenance, "container.total"),
            confirmsStart,
            origin is null ? null : DirectOrigin(origin.Value.Row),
            values,
            closedContainers);
    }

    private static GridRecognition Grid(params GridCellRecognition[] cells) => new(
        new GridGeometry(
            Complete<int?>(3, fieldId: "grid.rows"),
            Complete<int?>(1, fieldId: "grid.columns"),
            Complete<int?>(63, fieldId: "grid.cell-width"),
            Complete<int?>(63, fieldId: "grid.cell-height")),
        cells);

    private static GridCellRecognition Cell(
        int row,
        string itemId,
        string? nestedContainerPath = null) => new(
        new GridCellAddress(row, 0),
        Complete(
            new RecognizedItem(
                Complete(itemId, fieldId: "item.id"),
                Complete(itemId, fieldId: "item.name"),
                Complete<int?>(1, fieldId: "item.quantity"),
                Complete<int?>(1, fieldId: "item.width"),
                Complete<int?>(1, fieldId: "item.height"),
                Complete<bool?>(false, fieldId: "item.rotated"),
                Complete<bool?>(true, fieldId: "item.fir"),
                Complete(ItemConditionReading.NotApplicable, fieldId: "item.condition")),
            fieldId: $"cell.{row}"),
        nestedContainerPath);

    private static GridCellRecognition CellWithUnknownQuantity(int row, string itemId)
    {
        var provenance = V2ContractTestData.ScreenshotProvenance();
        return new GridCellRecognition(
            new GridCellAddress(row, 0),
            Complete(
                new RecognizedItem(
                    Complete(itemId, provenance, "item.id"),
                    Complete(itemId, provenance, "item.name"),
                    Unknown<int?>("item.quantity", provenance),
                    Complete<int?>(1, provenance, "item.width"),
                    Complete<int?>(1, provenance, "item.height"),
                    Complete<bool?>(false, provenance, "item.rotated"),
                    Complete<bool?>(true, provenance, "item.fir"),
                    Complete(ItemConditionReading.NotApplicable, provenance, "item.condition")),
                provenance,
                $"cell.{row}"));
    }

    private static IReadOnlyDictionary<GridCellAddress, EvidencedValue<long?>> Values(
        params (int Row, long Value)[] values) =>
        values.ToDictionary(
            value => new GridCellAddress(value.Row, 0),
            value => Complete<long?>(value.Value));

    private static StashCaptureRegion Region(
        string regionId,
        string artifactId,
        int ordinal,
        EvidencedValue<GridCellAddress?> origin,
        GridRecognition grid) => new(regionId, artifactId, ordinal, "stash", origin, grid);

    private static RecognitionResultEnvelope<StashRecognition> Envelope(
        string snapshotId,
        StashCaptureRegion region)
    {
        var provenance = V2ContractTestData.ScreenshotProvenance();
        var stash = new StashRecognition(
            snapshotId,
            [region],
            [new StashContainerCoverage(
                "stash",
                Complete<int?>(3, provenance, "coverage.observed"),
                Complete<int?>(4, provenance, "coverage.total"))],
            Unknown<long?>("stash.value", provenance),
            Complete<int?>(0, provenance, "stash.unresolved"));
        return new RecognitionResultEnvelope<StashRecognition>(
            new RecognitionResultHeader(
                $"result-{snapshotId}",
                V2ContractVersion.Current,
                SessionId,
                region.ArtifactId,
                V2ContractTestData.CapturedUtc,
                ScanIntent.Stash,
                Complete<RecognizedContext?>(RecognizedContext.Stash, provenance, "context")),
            Complete(stash, provenance, "stash"));
    }

    private static EvidencedValue<GridCellAddress?> DirectOrigin(int row) =>
        Complete<GridCellAddress?>(new GridCellAddress(row, 0), fieldId: "origin");

    private static EvidencedValue<GridCellAddress?> DerivedOrigin(int row)
    {
        var input = V2ContractTestData.ScreenshotProvenance();
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.DerivedCalculation,
            "fixture://origin",
            V2ContractTestData.ObservedUtc,
            input.Confidence,
            new ProducerIdentity("fixture-stitcher", "1"),
            generatedUtc: V2ContractTestData.ObservedUtc,
            inputs: [input]);
        return Complete<GridCellAddress?>(new GridCellAddress(row, 0), provenance, "origin");
    }

    private static EvidencedValue<T> Complete<T>(
        T value,
        EvidenceProvenance? provenance = null,
        string fieldId = "field") =>
        V2ContractTestData.Complete(fieldId, value, provenance);

    private static EvidencedValue<T> Unknown<T>(string fieldId, EvidenceProvenance provenance) =>
        V2ContractTestData.Unknown<T>(fieldId, provenance);

    private static EvidenceProvenance ScreenshotProvenance(int ordinal) => new(
        EvidenceSourceClass.GameWrittenScreenshot,
        $"fixture://stash-{ordinal}",
        V2ContractTestData.ObservedUtc,
        new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.96),
        new ProducerIdentity("fixture-ocr", "2.0"));

    private sealed class MemorySnapshotStore : IStashSnapshotStore
    {
        public StashSnapshotRecord? Snapshot { get; private set; }

        public Task SaveAsync(StashSnapshotRecord snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshot = snapshot;
            return Task.CompletedTask;
        }

        public Task<StashSnapshotRecord?> ReadCurrentAsync(
            InventoryProfileScope scope,
            CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot is { IsCurrent: true } snapshot && snapshot.ProfileScope == scope
                ? snapshot
                : null);

        public Task<StashSnapshotRecord?> ReadAsync(
            InventoryProfileScope scope,
            Guid snapshotId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot is { } snapshot && snapshot.SnapshotId == snapshotId && snapshot.ProfileScope == scope
                ? snapshot
                : null);

        public Task<IReadOnlyList<StashSnapshotSummary>> ListAsync(
            InventoryProfileScope scope,
            int maximumCount,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StashSnapshotSummary>>([]);

        public Task<StashSnapshotDeleteResult> DeleteAsync(
            InventoryProfileScope scope,
            Guid snapshotId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new StashSnapshotDeleteResult(false, null));

        public Task<StashSnapshotRetentionResult> ApplyRetentionAsync(
            InventoryProfileScope scope,
            DateTimeOffset retainFromUtc,
            bool dryRun,
            CancellationToken cancellationToken) =>
            Task.FromResult(new StashSnapshotRetentionResult(0, 0, dryRun));
    }

    private sealed class MemoryReviewSink : IStashReviewCommandSink
    {
        private readonly List<StashReviewCommand> _commands = [];

        public IReadOnlyList<StashReviewCommand> Commands => _commands.AsReadOnly();

        public Task AppendAsync(StashReviewCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _commands.Add(command);
            return Task.CompletedTask;
        }
    }
}
