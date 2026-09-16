using System.Collections.ObjectModel;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Core.Domain.Stash;

namespace TarkovCompanion.Application.Services.StashScan;

/// <summary>
/// Pixel-free input from one reviewed capture. The content identity is process-local dedupe state
/// and is deliberately omitted from every output and persistence contract.
/// </summary>
public sealed record StashScanCaptureFrame
{
    public StashScanCaptureFrame(
        CaptureSessionId sessionId,
        string artifactId,
        int captureOrdinal,
        CaptureCorrelationId correlationId,
        CaptureContextMetadata context,
        string contentSha256,
        string containerPath,
        DateTimeOffset capturedUtc,
        int decodeRevision,
        EvidenceProvenance provenance,
        GridReconstructionResult reconstruction,
        EvidencedValue<int?> totalContainerCells,
        bool confirmsContainerStart = false,
        EvidencedValue<GridCellAddress?>? originHint = null,
        IReadOnlyDictionary<GridCellAddress, EvidencedValue<long?>>? itemNetValues = null,
        IReadOnlyList<string>? closedContainerPaths = null)
    {
        SessionId = sessionId.Value != Guid.Empty
            ? sessionId
            : throw new ArgumentException("A capture session is required.", nameof(sessionId));
        ArtifactId = Required(artifactId, nameof(artifactId), 128);
        CaptureOrdinal = captureOrdinal >= 0
            ? captureOrdinal
            : throw new ArgumentOutOfRangeException(nameof(captureOrdinal));
        CorrelationId = correlationId.IsDefined
            ? correlationId
            : throw new ArgumentException("A capture correlation id is required.", nameof(correlationId));
        Context = context ?? throw new ArgumentNullException(nameof(context));
        ContentSha256 = Sha256(contentSha256, nameof(contentSha256));
        ContainerPath = ContainerPaths.Validate(containerPath, nameof(containerPath));
        CapturedUtc = capturedUtc.Offset == TimeSpan.Zero && capturedUtc != default
            ? capturedUtc
            : throw new ArgumentException("Capture time must be a defined UTC instant.", nameof(capturedUtc));
        DecodeRevision = decodeRevision >= 0
            ? decodeRevision
            : throw new ArgumentOutOfRangeException(nameof(decodeRevision));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        if (provenance.SourceClass is not (
                EvidenceSourceClass.GameWrittenScreenshot or
                EvidenceSourceClass.ExternalVisiblePixels) ||
            provenance.ObservedUtc < CapturedUtc)
        {
            throw new ArgumentException(
                "Stash captures must retain visible-pixel provenance observed no earlier than capture.",
                nameof(provenance));
        }

        Reconstruction = reconstruction ?? throw new ArgumentNullException(nameof(reconstruction));
        if (reconstruction.Surface is not (InventoryGridSurface.Stash or InventoryGridSurface.Container))
        {
            throw new ArgumentException("A stash frame must describe a stash or container grid.", nameof(reconstruction));
        }

        TotalContainerCells = totalContainerCells ?? throw new ArgumentNullException(nameof(totalContainerCells));
        ValidateCellCount(totalContainerCells, nameof(totalContainerCells));
        ConfirmsContainerStart = confirmsContainerStart;
        OriginHint = originHint;
        if (originHint?.Value is { } hinted && confirmsContainerStart && hinted != new GridCellAddress(0, 0))
        {
            throw new ArgumentException("A confirmed container start must be at cell zero.", nameof(originHint));
        }

        var recognizedAnchors = reconstruction.Recognition?.Cells
            .Select(cell => cell.Anchor)
            .ToHashSet() ?? [];
        if (reconstruction.Recognition?.Cells.Any(cell =>
                cell.NestedContainerPath is { } nested &&
                !string.Equals(ContainerPaths.Parent(nested), ContainerPath, StringComparison.Ordinal)) == true)
        {
            throw new ArgumentException(
                "An opened nested-container path must be a direct child of this frame's container.",
                nameof(reconstruction));
        }

        var values = itemNetValues ?? new Dictionary<GridCellAddress, EvidencedValue<long?>>();
        if (values.Count > GridGeometry.MaxCells || values.Keys.Any(anchor => !recognizedAnchors.Contains(anchor)))
        {
            throw new ArgumentException(
                "Item values must bind only to recognized anchors in this frame.",
                nameof(itemNetValues));
        }

        var valueCopy = new Dictionary<GridCellAddress, EvidencedValue<long?>>(values.Count);
        foreach (var (anchor, value) in values)
        {
            ArgumentNullException.ThrowIfNull(value, nameof(itemNetValues));
            if (EvidenceValues(value).Any(amount => amount < 0))
            {
                throw new ArgumentOutOfRangeException(nameof(itemNetValues), "Item values cannot be negative.");
            }

            valueCopy.Add(anchor, value);
        }

        ItemNetValues = new ReadOnlyDictionary<GridCellAddress, EvidencedValue<long?>>(valueCopy);

        var closed = (closedContainerPaths ?? [])
            .Select(path => ContainerPaths.Validate(path, nameof(closedContainerPaths)))
            .ToArray();
        if (closed.Length > GridGeometry.MaxCells ||
            closed.Distinct(StringComparer.Ordinal).Count() != closed.Length ||
            closed.Any(path => !string.Equals(ContainerPaths.Parent(path), ContainerPath, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Closed-container paths must be unique direct children of the captured container.",
                nameof(closedContainerPaths));
        }

        ClosedContainerPaths = Array.AsReadOnly(closed);
    }

    public CaptureSessionId SessionId { get; }
    public string ArtifactId { get; }
    public int CaptureOrdinal { get; }
    public CaptureCorrelationId CorrelationId { get; }
    public CaptureContextMetadata Context { get; }
    public string ContentSha256 { get; }
    public string ContainerPath { get; }
    public DateTimeOffset CapturedUtc { get; }
    public int DecodeRevision { get; }
    public EvidenceProvenance Provenance { get; }
    public GridReconstructionResult Reconstruction { get; }
    public EvidencedValue<int?> TotalContainerCells { get; }
    public bool ConfirmsContainerStart { get; }
    public EvidencedValue<GridCellAddress?>? OriginHint { get; }
    public IReadOnlyDictionary<GridCellAddress, EvidencedValue<long?>> ItemNetValues { get; }
    public IReadOnlyList<string> ClosedContainerPaths { get; }

    private static void ValidateCellCount(EvidencedValue<int?> value, string parameterName)
    {
        if (EvidenceValues(value).Any(count => count is < 1 or > GridGeometry.MaxCells))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Container cells must fit the bounded grid space.");
        }
    }

    private static IEnumerable<T> EvidenceValues<T>(EvidencedValue<T?> value)
        where T : struct
    {
        if (value.Value is { } current)
        {
            yield return current;
        }

        foreach (var candidate in value.Candidates)
        {
            if (candidate.Value is { } possible)
            {
                yield return possible;
            }
        }

        foreach (var correction in value.Corrections)
        {
            if (correction.OriginalValue is { } original)
            {
                yield return original;
            }

            if (correction.CorrectedValue is { } corrected)
            {
                yield return corrected;
            }
        }
    }

    private static string Required(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName);
    }

    private static string Sha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Length == 64 && value.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw new ArgumentException("Content identity must be canonical lowercase SHA-256.", parameterName);
    }
}

public sealed record StashScanAssemblyRequest
{
    public StashScanAssemblyRequest(
        string resultId,
        string snapshotId,
        CaptureSessionId sessionId,
        InventoryProfileScope profileScope,
        string dataSnapshotId,
        DateTimeOffset assembledUtc,
        IReadOnlyList<StashScanCaptureFrame> captures)
    {
        ResultId = Required(resultId, nameof(resultId));
        SnapshotId = Required(snapshotId, nameof(snapshotId));
        SessionId = sessionId.Value != Guid.Empty
            ? sessionId
            : throw new ArgumentException("A capture session is required.", nameof(sessionId));
        ProfileScope = profileScope ?? throw new ArgumentNullException(nameof(profileScope));
        DataSnapshotId = Required(dataSnapshotId, nameof(dataSnapshotId));
        AssembledUtc = assembledUtc.Offset == TimeSpan.Zero && assembledUtc != default
            ? assembledUtc
            : throw new ArgumentException("Assembly time must be a defined UTC instant.", nameof(assembledUtc));
        ArgumentNullException.ThrowIfNull(captures);
        if (captures.Count is < 1 or > StashScanBounds.MaximumCaptures)
        {
            throw new ArgumentException(
                $"A stash session must carry 1-{StashScanBounds.MaximumCaptures} captures.",
                nameof(captures));
        }

        var copy = captures
            .Select(capture => capture ?? throw new ArgumentException("Captures cannot contain null.", nameof(captures)))
            .ToArray();
        if (copy.Any(capture => capture.SessionId != sessionId))
        {
            throw new ArgumentException("Every capture must belong to the requested session.", nameof(captures));
        }

        if (copy.Skip(1).Any(capture => !Equals(capture.Context, copy[0].Context)))
        {
            throw new ArgumentException("Every capture in a stash session must retain the same frozen context.", nameof(captures));
        }

        if (copy[0].Context.ProfileContext is { } profileContext &&
            (profileContext.Identity.ProfileId != profileScope.ProfileId ||
             !string.Equals(profileContext.Identity.Generation, profileScope.Generation, StringComparison.Ordinal) ||
             !string.Equals(profileContext.Mode.ToString(), profileScope.GameMode, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(profileContext.DataSnapshot.SnapshotId, DataSnapshotId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The frozen capture profile context must match the requested stash scope and data snapshot.",
                nameof(captures));
        }

        var recognizedNodes = copy.Sum(capture => (long)(capture.Reconstruction.Recognition?.Cells.Count ?? 0)) +
                              copy.Select(capture => capture.ContainerPath).Distinct(StringComparer.Ordinal).LongCount();
        if (recognizedNodes > StashScanBounds.MaximumPlanItems)
        {
            throw new ArgumentException(
                $"A durable stash session cannot exceed {StashScanBounds.MaximumPlanItems} recognized item nodes.",
                nameof(captures));
        }

        if (copy.Select(capture => capture.CaptureOrdinal).Distinct().Count() != copy.Length)
        {
            throw new ArgumentException("Capture ordinals must be unique.", nameof(captures));
        }

        if (copy.Any(capture =>
                capture.CapturedUtc > assembledUtc || capture.Provenance.EvidenceThroughUtc > assembledUtc))
        {
            throw new ArgumentException("Assembly cannot predate a capture or its visible evidence.", nameof(assembledUtc));
        }

        Captures = Array.AsReadOnly(copy);
    }

    public string ResultId { get; }
    public string SnapshotId { get; }
    public CaptureSessionId SessionId { get; }
    public InventoryProfileScope ProfileScope { get; }
    public string DataSnapshotId { get; }
    public DateTimeOffset AssembledUtc { get; }
    public IReadOnlyList<StashScanCaptureFrame> Captures { get; }

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= 256
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}

public sealed record StashScanAssemblyResult(
    InventoryProfileScope ProfileScope,
    string DataSnapshotId,
    RecognitionResultEnvelope<StashRecognition> Recognition,
    StashScanSessionReport Report)
{
    public InventoryProfileScope ProfileScope { get; } =
        ProfileScope ?? throw new ArgumentNullException(nameof(ProfileScope));

    public string DataSnapshotId { get; } = string.IsNullOrWhiteSpace(DataSnapshotId)
        ? throw new ArgumentException("A data snapshot id is required.", nameof(DataSnapshotId))
        : DataSnapshotId.Trim();

    public RecognitionResultEnvelope<StashRecognition> Recognition { get; } =
        Recognition ?? throw new ArgumentNullException(nameof(Recognition));

    public StashScanSessionReport Report { get; } =
        Report ?? throw new ArgumentNullException(nameof(Report));
}

/// <summary>
/// Deterministically stitches reviewed, pixel-free frames. It never captures pixels or performs an
/// inventory action; ambiguous placement stays unresolved and is returned as retry guidance.
/// </summary>
public sealed class StashScanAssembler
{
    private const int MinimumOverlapAnchors = 2;
    private const int MaximumAlignmentCandidateVisits = 100_000;
    private const int MaximumCoverageCellVisits = StashScanBounds.MaximumCaptures * GridGeometry.MaxCells;

    public StashScanAssemblyResult Assemble(
        StashScanAssemblyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var budget = new AssemblyBudget(cancellationToken);
        var issues = new List<StashScanIssue>();
        var duplicates = new List<string>();
        AddInputOrderIssues(request.Captures, issues);

        var seenContent = new HashSet<string>(StringComparer.Ordinal);
        var seenArtifacts = new HashSet<string>(StringComparer.Ordinal);
        var accepted = new List<StashScanCaptureFrame>(request.Captures.Count);
        foreach (var frame in request.Captures)
        {
            budget.Visit();
            if (seenContent.Contains(frame.ContentSha256) || seenArtifacts.Contains(frame.ArtifactId))
            {
                duplicates.Add(frame.ArtifactId);
                AddIssue(issues, new StashScanIssue(
                    StashScanIssueKind.DuplicateCapture,
                    "stash.capture.duplicate",
                    StashScanRetryAction.None,
                    frame.ContainerPath,
                    frame.ArtifactId,
                    frame.CaptureOrdinal));
                continue;
            }

            seenContent.Add(frame.ContentSha256);
            seenArtifacts.Add(frame.ArtifactId);
            accepted.Add(frame);
        }

        if (accepted.Count == 0)
        {
            throw new ArgumentException("A stash session cannot consist only of duplicate captures.", nameof(request));
        }

        accepted.Sort((left, right) => left.CaptureOrdinal.CompareTo(right.CaptureOrdinal));
        var regions = PlaceRegions(request, accepted, issues, budget);
        var validContainers = RetainContractValidContainerTrees(regions, issues);
        regions = regions
            .Where(region => validContainers.Contains(region.Frame.ContainerPath))
            .ToList();

        var coverage = BuildContainerCoverage(request, regions, validContainers, issues, budget);
        var overallCoverage = CombineCoverage(coverage);
        var unresolved = CountUnresolved(regions, issues);
        var totalKnownValue = SumKnownValues(request, regions, issues, budget);
        var blockingIssue = issues.Any(issue => issue.Kind != StashScanIssueKind.DuplicateCapture);
        var complete = !blockingIssue &&
                       overallCoverage.Fraction == 1 &&
                       unresolved == 0 &&
                       totalKnownValue.Status.Completeness == ResultCompleteness.Complete;
        var status = new ResultStatus(
            complete ? ResultCompleteness.Complete : ResultCompleteness.Partial,
            FreshnessState.Current,
            complete ? "stash.scan.complete" : "stash.scan.review-required",
            complete ? null : "Review the targeted capture guidance before using exact stash totals.");
        var provenance = AggregateProvenance(request, accepted, overallCoverage);
        var stash = new StashRecognition(
            request.SnapshotId,
            regions
                .OrderBy(region => region.Frame.CaptureOrdinal)
                .Select(region => new StashCaptureRegion(
                    $"region-{region.Frame.CaptureOrdinal:D4}",
                    region.Frame.ArtifactId,
                    region.Frame.CaptureOrdinal,
                    region.Frame.ContainerPath,
                    region.Origin,
                    region.Frame.Reconstruction.Recognition!))
                .ToArray(),
            coverage,
            totalKnownValue,
            new EvidencedValue<int?>(
                "stash.unresolved-cells",
                unresolved,
                new ResultStatus(
                    unresolved is null ? ResultCompleteness.Unknown : ResultCompleteness.Complete,
                    FreshnessState.Current,
                    unresolved is null ? "stash.unresolved-count-overflow" : "stash.unresolved-count"),
                provenance));

        var finalFrame = accepted[^1];
        var detectedContext = new EvidencedValue<RecognizedContext?>(
            "recognition.detected-context",
            RecognizedContext.Stash,
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current),
            provenance);
        var header = new RecognitionResultHeader(
            request.ResultId,
            V2ContractVersion.Current,
            request.SessionId,
            finalFrame.ArtifactId,
            finalFrame.CapturedUtc,
            ScanIntent.Stash,
            detectedContext);
        var envelope = new RecognitionResultEnvelope<StashRecognition>(
            header,
            new EvidencedValue<StashRecognition>(
                "recognition.stash",
                stash,
                status,
                provenance));
        var report = new StashScanSessionReport(
            request.SessionId,
            request.SnapshotId,
            status,
            overallCoverage,
            accepted.Select(frame => frame.ArtifactId).ToArray(),
            duplicates.Distinct(StringComparer.Ordinal).ToArray(),
            issues);
        return new StashScanAssemblyResult(
            request.ProfileScope,
            request.DataSnapshotId,
            envelope,
            report);
    }

    private static void AddInputOrderIssues(
        IReadOnlyList<StashScanCaptureFrame> frames,
        ICollection<StashScanIssue> issues)
    {
        var last = -1;
        foreach (var frame in frames)
        {
            if (frame.CaptureOrdinal < last)
            {
                AddIssue(issues, new StashScanIssue(
                    StashScanIssueKind.OutOfOrderCapture,
                    "stash.capture.out-of-order",
                    StashScanRetryAction.ReviewConflictingCells,
                    frame.ContainerPath,
                    frame.ArtifactId,
                    frame.CaptureOrdinal));
            }

            last = frame.CaptureOrdinal;
        }
    }

    private static List<PlacedRegion> PlaceRegions(
        StashScanAssemblyRequest request,
        IReadOnlyList<StashScanCaptureFrame> frames,
        ICollection<StashScanIssue> issues,
        AssemblyBudget budget)
    {
        var regions = new List<PlacedRegion>(frames.Count);
        var placedCells = new Dictionary<AbsoluteCell, PlacedCell>();
        var conflictingCells = new HashSet<AbsoluteCell>();
        foreach (var frame in frames)
        {
            budget.Visit();
            if (frame.Reconstruction.Recognition is null)
            {
                throw new ArgumentException("A reviewed stash frame must carry reconstructable grid state.", nameof(frames));
            }

            var origin = ResolveOrigin(request, frame, placedCells, issues, budget);
            var region = new PlacedRegion(frame, origin.Evidence);
            regions.Add(region);

            var unresolvedInFrame = frame.Reconstruction.UnresolvedCells.Count;
            if (frame.Reconstruction.Outcome != GridReconstructionOutcome.Complete)
            {
                AddIssue(issues, new StashScanIssue(
                    StashScanIssueKind.PartialRecognition,
                    "stash.capture.partial-recognition",
                    StashScanRetryAction.RetryOccludedRegion,
                    frame.ContainerPath,
                    frame.ArtifactId,
                    frame.CaptureOrdinal,
                    unresolvedInFrame));
            }

            if (unresolvedInFrame > 0)
            {
                AddIssue(issues, new StashScanIssue(
                    StashScanIssueKind.OccludedCells,
                    "stash.capture.occluded-cells",
                    StashScanRetryAction.RetryOccludedRegion,
                    frame.ContainerPath,
                    frame.ArtifactId,
                    frame.CaptureOrdinal,
                    unresolvedInFrame));
            }

            foreach (var closedPath in frame.ClosedContainerPaths)
            {
                AddIssue(issues, new StashScanIssue(
                    StashScanIssueKind.ClosedContainer,
                    "stash.container.closed",
                    StashScanRetryAction.ReopenAndCaptureContainer,
                    closedPath,
                    frame.ArtifactId,
                    frame.CaptureOrdinal));
            }

            if (origin.Value is not { } placedOrigin)
            {
                continue;
            }

            var conflicts = 0;
            foreach (var cell in frame.Reconstruction.Recognition.Cells)
            {
                budget.Visit();
                if (!TrySignature(cell, out var signature))
                {
                    continue;
                }

                var absolute = new AbsoluteCell(
                    frame.ContainerPath,
                    placedOrigin.Row + cell.Anchor.Row,
                    placedOrigin.Column + cell.Anchor.Column);
                if (conflictingCells.Contains(absolute))
                {
                    continue;
                }

                if (placedCells.TryGetValue(absolute, out var existing) && existing.Signature != signature)
                {
                    placedCells.Remove(absolute);
                    conflictingCells.Add(absolute);
                    conflicts++;
                    continue;
                }

                placedCells.TryAdd(absolute, new PlacedCell(signature, frame.ArtifactId, frame.Provenance));
            }

            if (conflicts > 0)
            {
                AddIssue(issues, new StashScanIssue(
                    StashScanIssueKind.MovementConflict,
                    "stash.capture.movement-conflict",
                    StashScanRetryAction.ReviewConflictingCells,
                    frame.ContainerPath,
                    frame.ArtifactId,
                    frame.CaptureOrdinal,
                    conflicts));
            }
        }

        return regions;
    }

    private static OriginResolution ResolveOrigin(
        StashScanAssemblyRequest request,
        StashScanCaptureFrame frame,
        IReadOnlyDictionary<AbsoluteCell, PlacedCell> placedCells,
        ICollection<StashScanIssue> issues,
        AssemblyBudget budget)
    {
        if (frame.OriginHint?.Value is { } hint &&
            frame.OriginHint.Status.Completeness is ResultCompleteness.Partial or ResultCompleteness.Complete)
        {
            return new OriginResolution(hint, frame.OriginHint);
        }

        if (frame.ConfirmsContainerStart)
        {
            var value = new GridCellAddress(0, 0);
            return new OriginResolution(value, OriginEvidence(
                frame,
                value,
                request.AssembledUtc,
                "stash.origin.confirmed-start",
                [frame.Provenance]));
        }

        var alignment = FindAlignment(frame, placedCells, budget);
        if (alignment.Origin is { } aligned)
        {
            var inputs = alignment.Inputs.Count == 0 ? [frame.Provenance] : alignment.Inputs;
            return new OriginResolution(aligned, OriginEvidence(
                frame,
                aligned,
                request.AssembledUtc,
                "stash.origin.overlap",
                inputs));
        }

        var firstForContainer = !placedCells.Keys.Any(cell =>
            string.Equals(cell.ContainerPath, frame.ContainerPath, StringComparison.Ordinal));
        AddIssue(issues, new StashScanIssue(
            alignment.WasAmbiguous ? StashScanIssueKind.AmbiguousOverlap : StashScanIssueKind.OriginUnresolved,
            alignment.WasAmbiguous ? "stash.origin.ambiguous-overlap" : "stash.origin.unresolved",
            firstForContainer
                ? StashScanRetryAction.ReturnToContainerStart
                : StashScanRetryAction.CaptureWithMoreOverlap,
            frame.ContainerPath,
            frame.ArtifactId,
            frame.CaptureOrdinal));
        return new OriginResolution(null, new EvidencedValue<GridCellAddress?>(
            $"stash.origin.{frame.CaptureOrdinal}",
            null,
            new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current, "stash.origin.unresolved"),
            frame.Provenance));
    }

    private static AlignmentResult FindAlignment(
        StashScanCaptureFrame frame,
        IReadOnlyDictionary<AbsoluteCell, PlacedCell> placedCells,
        AssemblyBudget budget)
    {
        var grid = frame.Reconstruction.Recognition!;
        var existingBySignature = placedCells
            .Where(pair => string.Equals(pair.Key.ContainerPath, frame.ContainerPath, StringComparison.Ordinal))
            .GroupBy(pair => pair.Value.Signature)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var current = grid.Cells
            .Select(cell => TrySignature(cell, out var signature)
                ? new LocalCell(cell.Anchor, signature)
                : (LocalCell?)null)
            .Where(cell => cell is not null)
            .Select(cell => cell!.Value)
            .ToArray();
        if (existingBySignature.Count == 0 || current.Length < MinimumOverlapAnchors)
        {
            return AlignmentResult.None;
        }

        var candidates = new HashSet<GridCellAddress>();
        var visits = 0;
        foreach (var cell in current)
        {
            if (!existingBySignature.TryGetValue(cell.Signature, out var matches))
            {
                continue;
            }

            foreach (var match in matches)
            {
                budget.Visit();
                if (++visits > MaximumAlignmentCandidateVisits)
                {
                    return AlignmentResult.Ambiguous;
                }

                var row = match.Key.Row - cell.Anchor.Row;
                var column = match.Key.Column - cell.Anchor.Column;
                if (row >= 0 && column >= 0 &&
                    FitsBoundedSpace(grid, row, column))
                {
                    candidates.Add(new GridCellAddress(row, column));
                }
            }
        }

        var scored = new List<(GridCellAddress Origin, int Matches, IReadOnlyList<EvidenceProvenance> Inputs)>();
        foreach (var candidate in candidates)
        {
            var matches = 0;
            var conflicts = 0;
            var inputs = new List<EvidenceProvenance> { frame.Provenance };
            foreach (var cell in current)
            {
                budget.Visit();
                var absolute = new AbsoluteCell(
                    frame.ContainerPath,
                    candidate.Row + cell.Anchor.Row,
                    candidate.Column + cell.Anchor.Column);
                if (!placedCells.TryGetValue(absolute, out var existing))
                {
                    continue;
                }

                if (existing.Signature == cell.Signature)
                {
                    matches++;
                    if (!inputs.Contains(existing.Provenance) && inputs.Count < EvidenceProvenance.MaxInputCount)
                    {
                        inputs.Add(existing.Provenance);
                    }
                }
                else
                {
                    conflicts++;
                }
            }

            if (matches >= MinimumOverlapAnchors && conflicts == 0)
            {
                scored.Add((candidate, matches, inputs));
            }
        }

        var ordered = scored
            .OrderByDescending(item => item.Matches)
            .ThenBy(item => item.Origin.Row)
            .ThenBy(item => item.Origin.Column)
            .ToArray();
        if (ordered.Length == 0)
        {
            return candidates.Count == 0 ? AlignmentResult.None : AlignmentResult.Ambiguous;
        }

        if (ordered.Length > 1 && ordered[0].Matches == ordered[1].Matches)
        {
            return AlignmentResult.Ambiguous;
        }

        return new AlignmentResult(ordered[0].Origin, false, ordered[0].Inputs);
    }

    private static bool FitsBoundedSpace(GridRecognition grid, int originRow, int originColumn)
    {
        var rows = grid.Geometry.Rows.Value ??
            (grid.Cells.Count == 0 ? 1 : grid.Cells.Max(cell => cell.Anchor.Row) + 1);
        var columns = grid.Geometry.Columns.Value ??
            (grid.Cells.Count == 0 ? 1 : grid.Cells.Max(cell => cell.Anchor.Column) + 1);
        return rows <= GridGeometry.MaxRows - originRow &&
               columns <= GridGeometry.MaxColumns - originColumn;
    }

    private static EvidencedValue<GridCellAddress?> OriginEvidence(
        StashScanCaptureFrame frame,
        GridCellAddress value,
        DateTimeOffset generatedUtc,
        string sourceIdentifier,
        IReadOnlyList<EvidenceProvenance> inputs)
    {
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.DerivedCalculation,
            sourceIdentifier,
            generatedUtc,
            EvidenceConfidence.Certain,
            new ProducerIdentity("Tarkov Companion stash stitcher", "stash-stitch-1"),
            generatedUtc: generatedUtc,
            coverage: new EvidenceCoverage(sampleSize: inputs.Count, description: "Capture evidence used to place this region."),
            inputs: inputs);
        return new EvidencedValue<GridCellAddress?>(
            $"stash.origin.{frame.CaptureOrdinal}",
            value,
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current),
            provenance);
    }

    private static HashSet<string> RetainContractValidContainerTrees(
        IReadOnlyList<PlacedRegion> regions,
        ICollection<StashScanIssue> issues)
    {
        var allPaths = regions
            .Select(region => region.Frame.ContainerPath)
            .ToHashSet(StringComparer.Ordinal);
        var valid = allPaths
            .Where(path => ContainerPaths.Parent(path) is null)
            .ToHashSet(StringComparer.Ordinal);
        var opened = regions
            .SelectMany(region => region.Frame.Reconstruction.Recognition!.Cells
                .Where(cell => cell.NestedContainerPath is not null)
                .Select(cell => (Parent: region.Frame.ContainerPath, Child: cell.NestedContainerPath!)))
            .ToHashSet();

        for (var depth = 1; depth < ContainerPaths.MaxDepth; depth++)
        {
            var changed = false;
            foreach (var path in allPaths)
            {
                if (valid.Contains(path) || ContainerPaths.Parent(path) is not { } parent ||
                    !valid.Contains(parent) || !opened.Contains((parent, path)))
                {
                    continue;
                }

                valid.Add(path);
                changed = true;
            }

            if (!changed)
            {
                break;
            }
        }

        foreach (var region in regions.Where(region => !valid.Contains(region.Frame.ContainerPath)))
        {
            AddIssue(issues, new StashScanIssue(
                StashScanIssueKind.ContainerGeometryConflict,
                "stash.container.parent-not-observed-open",
                StashScanRetryAction.ReopenAndCaptureContainer,
                region.Frame.ContainerPath,
                region.Frame.ArtifactId,
                region.Frame.CaptureOrdinal));
        }

        return valid;
    }

    private static IReadOnlyList<StashContainerCoverage> BuildContainerCoverage(
        StashScanAssemblyRequest request,
        IReadOnlyList<PlacedRegion> regions,
        IReadOnlySet<string> validContainers,
        ICollection<StashScanIssue> issues,
        AssemblyBudget budget)
    {
        var result = new List<StashContainerCoverage>(validContainers.Count);
        foreach (var containerPath in validContainers
                     .OrderBy(path => path.Count(character => character == '/'))
                     .ThenBy(path => path, StringComparer.Ordinal))
        {
            var containerRegions = regions
                .Where(region => string.Equals(region.Frame.ContainerPath, containerPath, StringComparison.Ordinal))
                .ToArray();
            var inputs = containerRegions
                .Select(region => region.Frame.Provenance)
                .Distinct()
                .ToArray();
            var observed = new HashSet<GridCellAddress>();
            var placementIncomplete = false;
            foreach (var region in containerRegions)
            {
                budget.Visit();
                if (region.Origin.Value is not { } origin)
                {
                    placementIncomplete = true;
                    continue;
                }

                var grid = region.Frame.Reconstruction.Recognition!;
                if (grid.Geometry.Rows.Value is { } rows && grid.Geometry.Columns.Value is { } columns)
                {
                    for (var row = 0; row < rows; row++)
                    {
                        for (var column = 0; column < columns; column++)
                        {
                            budget.CoverageVisit();
                            observed.Add(new GridCellAddress(origin.Row + row, origin.Column + column));
                        }
                    }
                }
                else
                {
                    placementIncomplete = true;
                    foreach (var cell in grid.Cells)
                    {
                        budget.CoverageVisit();
                        observed.Add(new GridCellAddress(
                            origin.Row + cell.Anchor.Row,
                            origin.Column + cell.Anchor.Column));
                    }
                }
            }

            var totalClaims = containerRegions
                .Select(region => region.Frame.TotalContainerCells)
                .Where(value => value.Value is not null)
                .ToArray();
            var totalValues = totalClaims
                .Select(value => value.Value!.Value)
                .Distinct()
                .ToArray();
            var totalValue = totalValues.Length == 1 ? totalValues[0] : (int?)null;
            if (totalValues.Length > 1)
            {
                AddIssue(issues, new StashScanIssue(
                    StashScanIssueKind.ContainerGeometryConflict,
                    "stash.container.total-cells-conflict",
                    StashScanRetryAction.ReviewConflictingCells,
                    containerPath));
            }

            if (totalValue is { } declaredTotal && observed.Count > declaredTotal)
            {
                AddIssue(issues, new StashScanIssue(
                    StashScanIssueKind.ContainerGeometryConflict,
                    "stash.container.coverage-exceeds-total",
                    StashScanRetryAction.ReviewConflictingCells,
                    containerPath,
                    affectedCells: observed.Count - declaredTotal));
                totalValue = null;
            }

            var provenance = AggregateProvenance(
                request,
                inputs,
                new EvidenceCoverage(
                    observed.Count,
                    totalValue is { } total && total > 0 ? (double)observed.Count / total : null,
                    "Distinct container cells covered by placed capture regions."),
                "stash.container.coverage");
            var observedStatus = new ResultStatus(
                placementIncomplete ? ResultCompleteness.Partial : ResultCompleteness.Complete,
                FreshnessState.Current,
                placementIncomplete ? "stash.coverage.lower-bound" : "stash.coverage.observed");
            var totalStatus = new ResultStatus(
                totalValue is null
                    ? totalValues.Length > 1 ? ResultCompleteness.Partial : ResultCompleteness.Unknown
                    : totalClaims.All(claim => claim.Status.Completeness == ResultCompleteness.Complete)
                        ? ResultCompleteness.Complete
                        : ResultCompleteness.Partial,
                FreshnessState.Current,
                totalValue is null ? "stash.coverage.total-unresolved" : "stash.coverage.total-observed");
            result.Add(new StashContainerCoverage(
                containerPath,
                new EvidencedValue<int?>(
                    $"stash.coverage.{containerPath}.observed",
                    observed.Count,
                    observedStatus,
                    provenance),
                new EvidencedValue<int?>(
                    $"stash.coverage.{containerPath}.total",
                    totalValue,
                    totalStatus,
                    provenance)));

            if (totalValue is null || observed.Count < totalValue)
            {
                AddIssue(issues, new StashScanIssue(
                    StashScanIssueKind.MissingCoverage,
                    totalValue is null ? "stash.coverage.total-unresolved" : "stash.coverage.missing-cells",
                    StashScanRetryAction.CaptureMissingRange,
                    containerPath,
                    affectedCells: totalValue is null ? null : totalValue - observed.Count));
            }
        }

        return result;
    }

    private static EvidenceCoverage CombineCoverage(IReadOnlyList<StashContainerCoverage> coverage)
    {
        long observed = 0;
        long total = 0;
        var exact = coverage.Count > 0;
        foreach (var container in coverage)
        {
            if (container.ObservedCells.Value is { } observedCells)
            {
                observed += observedCells;
            }
            else
            {
                exact = false;
            }

            if (container.TotalCells.Value is { } totalCells)
            {
                total += totalCells;
            }
            else
            {
                exact = false;
            }
        }

        return new EvidenceCoverage(
            observed,
            exact && total > 0 ? Math.Clamp((double)observed / total, 0, 1) : null,
            exact
                ? "Fraction of declared stash and opened-container cells captured."
                : "Coverage is a lower bound because at least one container total or placement is unresolved.");
    }

    private static int? CountUnresolved(
        IReadOnlyList<PlacedRegion> regions,
        IReadOnlyCollection<StashScanIssue> issues)
    {
        long unresolved = 0;
        foreach (var region in regions)
        {
            unresolved += region.Frame.Reconstruction.UnresolvedCells.Count;
            unresolved += region.Frame.Reconstruction.Recognition!.Cells.Count(cell =>
                region.Origin.Value is null || !HasCompleteItemEvidence(cell));
        }

        unresolved += issues
            .Where(issue => issue.Kind == StashScanIssueKind.MovementConflict &&
                            issue.Code == "stash.capture.movement-conflict")
            .Sum(issue => (long)(issue.AffectedCells ?? 0));
        return unresolved <= GridGeometry.MaxCells ? (int)unresolved : null;
    }

    private static bool HasCompleteItemEvidence(GridCellRecognition cell)
    {
        if (cell.Item.Status.Completeness != ResultCompleteness.Complete ||
            cell.Item.Value is not { } item)
        {
            return false;
        }

        return item.CanonicalId.Status.Completeness == ResultCompleteness.Complete &&
               !string.IsNullOrWhiteSpace(item.CanonicalId.Value) &&
               item.Quantity.Status.Completeness == ResultCompleteness.Complete &&
               item.Quantity.Value is not null &&
               item.WidthCells.Status.Completeness == ResultCompleteness.Complete &&
               item.WidthCells.Value is not null &&
               item.HeightCells.Status.Completeness == ResultCompleteness.Complete &&
               item.HeightCells.Value is not null &&
               item.Rotated.Status.Completeness == ResultCompleteness.Complete &&
               item.Rotated.Value is not null &&
               item.FoundInRaid.Status.Completeness == ResultCompleteness.Complete &&
               item.FoundInRaid.Value is not null &&
               item.Condition.Status.Completeness == ResultCompleteness.Complete &&
               item.Condition.Value is not null;
    }

    private static EvidencedValue<long?> SumKnownValues(
        StashScanAssemblyRequest request,
        IReadOnlyList<PlacedRegion> regions,
        ICollection<StashScanIssue> issues,
        AssemblyBudget budget)
    {
        var values = new Dictionary<AbsoluteCell, ValueObservation>();
        var conflicts = new HashSet<AbsoluteCell>();
        var incomplete = false;
        foreach (var region in regions)
        {
            if (region.Origin.Value is not { } origin)
            {
                incomplete = true;
                continue;
            }

            foreach (var cell in region.Frame.Reconstruction.Recognition!.Cells)
            {
                budget.Visit();
                if (!TrySignature(cell, out var signature))
                {
                    incomplete = true;
                    continue;
                }

                var absolute = new AbsoluteCell(
                    region.Frame.ContainerPath,
                    origin.Row + cell.Anchor.Row,
                    origin.Column + cell.Anchor.Column);
                if (conflicts.Contains(absolute))
                {
                    continue;
                }

                region.Frame.ItemNetValues.TryGetValue(cell.Anchor, out var valueEvidence);
                var current = valueEvidence?.Value;
                var completeValue = valueEvidence?.Status.Completeness == ResultCompleteness.Complete && current is not null;
                if (!values.TryGetValue(absolute, out var existing))
                {
                    values.Add(absolute, new ValueObservation(
                        signature,
                        current,
                        valueEvidence?.Provenance));
                    incomplete |= !completeValue;
                    continue;
                }

                if (existing.Signature != signature ||
                    existing.Value is { } oldValue && current is { } newValue && oldValue != newValue)
                {
                    values.Remove(absolute);
                    conflicts.Add(absolute);
                    incomplete = true;
                    continue;
                }

                if (existing.Value is null && current is not null)
                {
                    values[absolute] = new ValueObservation(
                        signature,
                        current,
                        valueEvidence?.Provenance);
                }
            }
        }

        foreach (var conflict in conflicts)
        {
            AddIssue(issues, new StashScanIssue(
                StashScanIssueKind.MovementConflict,
                "stash.value.conflicting-overlap",
                StashScanRetryAction.ReviewConflictingCells,
                conflict.ContainerPath,
                affectedCells: 1));
        }

        long? total = 0;
        try
        {
            total = values.Values
                .Where(value => value.Value is not null)
                .Aggregate(0L, (sum, value) => checked(sum + value.Value!.Value));
        }
        catch (OverflowException)
        {
            total = null;
            incomplete = true;
        }

        var inputs = regions.Select(region => region.Frame.Provenance)
            .Concat(values.Values
                .Where(value => value.Value is not null && value.Provenance is not null)
                .Select(value => value.Provenance!))
            .Distinct()
            .ToArray();
        var lineageFits = FitsDerivedProvenance(inputs) &&
                          !inputs.Any(ContainsModelledEstimate) &&
                          inputs.All(input => input.EvidenceThroughUtc <= request.AssembledUtc);
        if (!lineageFits)
        {
            total = null;
            incomplete = true;
            inputs = regions.Select(region => region.Frame.Provenance).Distinct().ToArray();
        }

        var provenance = AggregateProvenance(
            request,
            inputs,
            new EvidenceCoverage(values.Count, description: lineageFits
                ? "Distinct placed item anchors included in the known-value total."
                : "The known-value total was withheld because its evidence lineage exceeded the bounded calculation contract."),
            "stash.total-known-value");
        var completeness = total is null
            ? ResultCompleteness.Unknown
            : incomplete ? ResultCompleteness.Partial : ResultCompleteness.Complete;
        return new EvidencedValue<long?>(
            "stash.total-known-value",
            total,
            new ResultStatus(
                completeness,
                FreshnessState.Current,
                lineageFits
                    ? $"stash.value.{completeness.ToString().ToLowerInvariant()}"
                    : "stash.value.lineage-limit"),
            provenance);
    }

    private static EvidenceProvenance AggregateProvenance(
        StashScanAssemblyRequest request,
        IReadOnlyList<StashScanCaptureFrame> frames,
        EvidenceCoverage coverage) =>
        AggregateProvenance(
            request,
            frames.Select(frame => frame.Provenance).Distinct().ToArray(),
            coverage,
            "stash.scan.assembled");

    private static EvidenceProvenance AggregateProvenance(
        StashScanAssemblyRequest request,
        IReadOnlyList<EvidenceProvenance> inputs,
        EvidenceCoverage coverage,
        string sourceIdentifier)
    {
        if (inputs.Count == 0)
        {
            throw new ArgumentException("A stash calculation must retain at least one capture input.", nameof(inputs));
        }

        var scores = inputs.Select(input => input.Confidence.Score).ToArray();
        var confidence = scores.Any(score => score is null)
            ? EvidenceConfidence.Unscored
            : scores.All(score => score == 1)
                ? EvidenceConfidence.Certain
                : new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, scores.Min()!.Value);
        return new EvidenceProvenance(
            EvidenceSourceClass.DerivedCalculation,
            sourceIdentifier,
            request.AssembledUtc,
            confidence,
            new ProducerIdentity("Tarkov Companion stash assembler", "stash-assembly-1"),
            generatedUtc: request.AssembledUtc,
            coverage: coverage,
            inputs: inputs);
    }

    private static bool FitsDerivedProvenance(IReadOnlyList<EvidenceProvenance> inputs) =>
        inputs.Count > 0 &&
        inputs.Sum(input => 1 + DescendantInputCount(input)) <= EvidenceProvenance.MaxInputCount &&
        inputs.All(input => ProvenanceDepth(input) < EvidenceProvenance.MaxInputDepth);

    private static int DescendantInputCount(EvidenceProvenance provenance) =>
        provenance.Inputs.Count + provenance.Inputs.Sum(DescendantInputCount);

    private static int ProvenanceDepth(EvidenceProvenance provenance) =>
        1 + (provenance.Inputs.Count == 0 ? 0 : provenance.Inputs.Max(ProvenanceDepth));

    private static bool ContainsModelledEstimate(EvidenceProvenance provenance) =>
        provenance.SourceClass == EvidenceSourceClass.ModelledEstimate ||
        provenance.Inputs.Any(ContainsModelledEstimate);

    private static bool TrySignature(GridCellRecognition cell, out CellSignature signature)
    {
        if (cell.Item.Value is not { } item || string.IsNullOrWhiteSpace(item.CanonicalId.Value))
        {
            signature = default;
            return false;
        }

        signature = new CellSignature(
            item.CanonicalId.Value.Trim(),
            item.WidthCells.Value,
            item.HeightCells.Value,
            item.Rotated.Value,
            item.Quantity.Value,
            item.FoundInRaid.Value,
            item.Condition.Value,
            cell.NestedContainerPath);
        return true;
    }

    private static void AddIssue(ICollection<StashScanIssue> issues, StashScanIssue issue)
    {
        if (issues.Contains(issue))
        {
            return;
        }

        if (issues.Count < StashScanBounds.MaximumIssues - 1)
        {
            issues.Add(issue);
            return;
        }

        if (issues.Any(existing => existing.Kind == StashScanIssueKind.IssueLimitReached))
        {
            return;
        }

        issues.Add(new StashScanIssue(
            StashScanIssueKind.IssueLimitReached,
            "stash.review.issue-limit-reached",
            StashScanRetryAction.ReviewConflictingCells,
            issue.ContainerPath));
    }

    private readonly record struct CellSignature(
        string ItemId,
        int? Width,
        int? Height,
        bool? Rotated,
        int? Quantity,
        bool? FoundInRaid,
        ItemConditionReading? Condition,
        string? NestedContainerPath);

    private readonly record struct AbsoluteCell(string ContainerPath, int Row, int Column);

    private readonly record struct LocalCell(GridCellAddress Anchor, CellSignature Signature);

    private sealed record PlacedCell(
        CellSignature Signature,
        string ArtifactId,
        EvidenceProvenance Provenance);

    private sealed record PlacedRegion(
        StashScanCaptureFrame Frame,
        EvidencedValue<GridCellAddress?> Origin);

    private readonly record struct OriginResolution(
        GridCellAddress? Value,
        EvidencedValue<GridCellAddress?> Evidence);

    private sealed record AlignmentResult(
        GridCellAddress? Origin,
        bool WasAmbiguous,
        IReadOnlyList<EvidenceProvenance> Inputs)
    {
        public static AlignmentResult None { get; } = new(null, false, []);
        public static AlignmentResult Ambiguous { get; } = new(null, true, []);
    }

    private readonly record struct ValueObservation(
        CellSignature Signature,
        long? Value,
        EvidenceProvenance? Provenance);

    private sealed class AssemblyBudget(CancellationToken cancellationToken)
    {
        private long _visits;
        private int _coverageVisits;

        public void Visit()
        {
            if ((++_visits & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        public void CoverageVisit()
        {
            Visit();
            if (++_coverageVisits > MaximumCoverageCellVisits)
            {
                throw new InvalidOperationException("Stash coverage exceeded the bounded capture cell budget.");
            }
        }
    }
}
