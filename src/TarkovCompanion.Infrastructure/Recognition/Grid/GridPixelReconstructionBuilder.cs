using System.Text.RegularExpressions;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;

namespace TarkovCompanion.Infrastructure.Recognition.Grid;

/// <summary>Bounds for the icon-separation policy and the OCR-quantity safety valve.</summary>
/// <remarks>
/// The separation policy's numbers are a reasonable starting point, not a calibrated release
/// policy: #272 owns tuning candidate-distance and runner-up-gap thresholds against measured
/// data. The OCR call cap is an operation-count safety valve, not a confidence threshold - it
/// bounds how many Tesseract calls one capture can trigger, the same way
/// <c>ContainerRecognitionService.MaximumCellFallbacks</c> already does for V1 container scans.
/// </remarks>
public sealed record GridPixelReconstructionOptions
{
    public GridPixelReconstructionOptions(
        IconCandidateSeparationPolicy separationPolicy,
        int maximumQuantityOcrCalls = 64)
    {
        SeparationPolicy = separationPolicy ?? throw new ArgumentNullException(nameof(separationPolicy));
        MaximumQuantityOcrCalls = maximumQuantityOcrCalls >= 0
            ? maximumQuantityOcrCalls
            : throw new ArgumentOutOfRangeException(nameof(maximumQuantityOcrCalls));
    }

    public IconCandidateSeparationPolicy SeparationPolicy { get; }

    public int MaximumQuantityOcrCalls { get; }

    public static IconCandidateSeparationPolicy DefaultSeparationPolicy { get; } = new(
        "grid-recognition-icon-separation-1",
        maximumCandidateDistanceBits: 12,
        minimumRunnerUpGapBits: 4,
        maximumReturnedCandidates: 5);

    public static GridPixelReconstructionOptions Default { get; } = new(DefaultSeparationPolicy);
}

/// <summary>
/// Turns a captured screenshot into a <see cref="GridReconstructionRequest"/>: the piece #273 was
/// missing between a raw frame and <see cref="InventoryGridReconstructor"/>.
/// </summary>
/// <remarks>
/// Geometry reuses #273's own V1 grid-line detector (<see cref="ContainerGridDetector"/>) and
/// occupancy segmenter (<see cref="ContainerGridSegmenter"/>) rather than duplicating them: both
/// already measure pitch and phase from the frame itself, so nothing here is a fixed resolution
/// constant. Item identity reuses the local icon evidence cache (#355) and its Hamming-distance
/// separator rather than the V1 OCR-name resolver, because matching against a rendered icon does
/// not need the item's printed name to be legible. A weak or tied icon match stays an absent
/// value with candidates attached; this type never invents a canonical id or a footprint.
/// </remarks>
public sealed class GridPixelReconstructionBuilder(
    IIconEvidenceCache iconCache,
    IItemRepository items,
    IOcrEngine ocrEngine,
    IconCandidateSeparator? separator = null,
    ContainerGridDetector? gridDetector = null,
    ContainerGridSegmenter? segmenter = null)
{
    private static readonly ProducerIdentity Producer = new("Tarkov Companion grid recognizer", "grid-recognition-1");

    private readonly IIconEvidenceCache _iconCache = iconCache ?? throw new ArgumentNullException(nameof(iconCache));
    private readonly IItemRepository _items = items ?? throw new ArgumentNullException(nameof(items));
    private readonly IOcrEngine _ocrEngine = ocrEngine ?? throw new ArgumentNullException(nameof(ocrEngine));
    private readonly IconCandidateSeparator _separator = separator ?? new();
    private readonly ContainerGridDetector _gridDetector = gridDetector ?? new();
    private readonly ContainerGridSegmenter _segmenter = segmenter ?? new();

    // [V2 rough package 40] The stash is the one surface whose lattice shape is known in advance.
    private readonly StashPanelLatticeDetector _stashLattice = new();
    private readonly StashFootprintReader _stashFootprints = new();

    public async Task<GridReconstructionRequest> BuildAsync(
        CapturedImage image,
        InventoryGridSurface surface,
        DateTimeOffset observedUtc,
        GridPixelReconstructionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= GridPixelReconstructionOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();

        if (CapturedImagePixels.ExceedsPixelCeiling(image))
        {
            return new(surface, null, []);
        }

        var spec = (surface == InventoryGridSurface.Stash ? _stashLattice.Detect(image, cancellationToken) : null)
            ?? _gridDetector.Detect(image, cancellationToken)
            ?? DetectInCenteredSafeArea(image, cancellationToken);
        if (spec is null || BuildLattice(spec, observedUtc) is not { } lattice)
        {
            return new(surface, null, []);
        }

        // [V2 rough package 40] A packed stash has no gaps for the general merge to split on, so
        // the stash reads its footprints from the lines between cells instead.
        var footprints = surface == InventoryGridSurface.Stash
            ? _stashFootprints.Read(image, spec, cancellationToken)
                .Select(footprint => new Footprint(footprint.Row, footprint.Column, footprint.Width, footprint.Height))
                .ToList()
            : MergeFootprints(_segmenter.Segment(image, spec, cancellationToken), spec.Rows, spec.Columns, cancellationToken);
        if (footprints.Count == 0)
        {
            return new(surface, lattice, []);
        }

        var evidence = await _iconCache.ListEvidenceAsync(cancellationToken).ConfigureAwait(false);
        var definitions = await LoadDefinitionsAsync(evidence, cancellationToken).ConfigureAwait(false);

        var observations = new List<GridCellObservation>(footprints.Count);
        var quantityOcrCalls = 0;
        foreach (var footprint in footprints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bounds = FootprintBounds(lattice, footprint);
            var attemptQuantityOcr = quantityOcrCalls < options.MaximumQuantityOcrCalls;
            if (attemptQuantityOcr)
            {
                quantityOcrCalls++;
            }

            observations.Add(await BuildObservationAsync(
                    image,
                    footprint,
                    bounds,
                    evidence,
                    definitions,
                    options,
                    observedUtc,
                    attemptQuantityOcr,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        return new(surface, lattice, observations);
    }

    /// <summary>
    /// Retries detection inside a centered, roughly 16:9 crop of an ultrawide frame.
    /// </summary>
    /// <remarks>
    /// A fixed-width game panel that does not stretch to fill an ultrawide frame - Clayton's own
    /// setup is 3840x1080 - can cover too little of the whole frame's width for the line
    /// detector's coverage threshold, even though the same panel is easily found on a narrower
    /// monitor. This is a heuristic pending real ultrawide corpus evidence (the benchmark harness
    /// reports whether it helps); the whole-frame attempt still runs first and wins whenever a
    /// panel spans enough of the frame to clear that threshold on its own.
    /// </remarks>
    private ContainerGridSpec? DetectInCenteredSafeArea(CapturedImage image, CancellationToken cancellationToken)
    {
        if (image.Height <= 0 || image.Width <= (image.Height * 16) / 9)
        {
            return null;
        }

        var safeWidth = Math.Min(image.Width, (image.Height * 16) / 9);
        var offsetX = (image.Width - safeWidth) / 2;
        var cropped = Crop(image, new EvidenceRegion(offsetX, 0, safeWidth, image.Height, EvidenceCoordinateSpace.SourcePixels));
        var spec = _gridDetector.Detect(cropped, cancellationToken);
        return spec is null ? null : spec with { Bounds = spec.Bounds with { X = spec.Bounds.X + offsetX } };
    }

    private static DetectedGridLattice? BuildLattice(ContainerGridSpec spec, DateTimeOffset observedUtc)
    {
        if (spec.Columns < 1 || spec.Rows < 1 ||
            spec.Columns > GridGeometry.MaxColumns || spec.Rows > GridGeometry.MaxRows ||
            spec.Bounds.X < 0 || spec.Bounds.Y < 0 ||
            spec.Bounds.Width < spec.Columns || spec.Bounds.Height < spec.Rows)
        {
            return null;
        }

        var cellWidth = Math.Max(1, (int)Math.Round((double)spec.Bounds.Width / spec.Columns, MidpointRounding.AwayFromZero));
        var cellHeight = Math.Max(1, (int)Math.Round((double)spec.Bounds.Height / spec.Rows, MidpointRounding.AwayFromZero));
        if (cellWidth > GridGeometry.MaxCellPixels || cellHeight > GridGeometry.MaxCellPixels)
        {
            return null;
        }

        var width = checked(cellWidth * spec.Columns);
        var height = checked(cellHeight * spec.Rows);
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.GameWrittenScreenshot,
            "recognition.grid.lattice",
            observedUtc,
            EvidenceConfidence.Unscored,
            Producer);
        var status = new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "grid.lattice.detected");
        var bounds = new EvidenceRegion(spec.Bounds.X, spec.Bounds.Y, width, height, EvidenceCoordinateSpace.SourcePixels);
        try
        {
            return new DetectedGridLattice(spec.Rows, spec.Columns, cellWidth, cellHeight, status, provenance, bounds);
        }
        catch (ArgumentException)
        {
            // A measured lattice that still fails the contract's own bounds (e.g. an
            // implausible rows*columns) is safer reported as undetected than forced through.
            return null;
        }
    }

    /// <summary>
    /// Groups adjacent occupied cells into rectangular footprints via 4-connected flood fill.
    /// </summary>
    /// <remarks>
    /// Every Tarkov item occupies a solid rectangle of cells, so a component is only trusted as
    /// one item when its cell count exactly fills its own bounding box. An irregular blob - noise,
    /// or two items whose icons touch - falls back to one 1x1 footprint per occupied cell instead
    /// of guessing a shape nothing measured.
    /// </remarks>
    private static List<Footprint> MergeFootprints(
        IReadOnlyList<ContainerSegment> segments,
        int rows,
        int columns,
        CancellationToken cancellationToken)
    {
        var occupied = new bool[rows, columns];
        foreach (var segment in segments)
        {
            if (segment.IsOccupied && segment.Row >= 0 && segment.Row < rows && segment.Column >= 0 && segment.Column < columns)
            {
                occupied[segment.Row, segment.Column] = true;
            }
        }

        var visited = new bool[rows, columns];
        var footprints = new List<Footprint>();
        var visits = 0;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                if (!occupied[row, column] || visited[row, column])
                {
                    continue;
                }

                if ((++visits & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var component = new List<(int Row, int Column)>();
                var queue = new Queue<(int Row, int Column)>();
                queue.Enqueue((row, column));
                visited[row, column] = true;
                var minRow = row;
                var maxRow = row;
                var minColumn = column;
                var maxColumn = column;
                while (queue.Count > 0)
                {
                    var (r, c) = queue.Dequeue();
                    component.Add((r, c));
                    minRow = Math.Min(minRow, r);
                    maxRow = Math.Max(maxRow, r);
                    minColumn = Math.Min(minColumn, c);
                    maxColumn = Math.Max(maxColumn, c);
                    TryEnqueue(r - 1, c);
                    TryEnqueue(r + 1, c);
                    TryEnqueue(r, c - 1);
                    TryEnqueue(r, c + 1);

                    void TryEnqueue(int nr, int nc)
                    {
                        if (nr >= 0 && nr < rows && nc >= 0 && nc < columns && occupied[nr, nc] && !visited[nr, nc])
                        {
                            visited[nr, nc] = true;
                            queue.Enqueue((nr, nc));
                        }
                    }
                }

                var boundingWidth = maxColumn - minColumn + 1;
                var boundingHeight = maxRow - minRow + 1;
                if (component.Count == boundingWidth * boundingHeight)
                {
                    footprints.Add(new(minRow, minColumn, boundingWidth, boundingHeight));
                }
                else
                {
                    foreach (var (r, c) in component)
                    {
                        footprints.Add(new(r, c, 1, 1));
                    }
                }
            }
        }

        return footprints;
    }

    private static EvidenceRegion FootprintBounds(DetectedGridLattice lattice, Footprint footprint) => new(
        lattice.Bounds.X + (footprint.Column * lattice.CellWidthPixels),
        lattice.Bounds.Y + (footprint.Row * lattice.CellHeightPixels),
        footprint.Width * lattice.CellWidthPixels,
        footprint.Height * lattice.CellHeightPixels,
        lattice.Bounds.CoordinateSpace);

    private async Task<Dictionary<string, ItemDefinition?>> LoadDefinitionsAsync(
        IReadOnlyList<IconContentEvidence> evidence,
        CancellationToken cancellationToken)
    {
        var definitions = new Dictionary<string, ItemDefinition?>(StringComparer.Ordinal);
        foreach (var canonicalId in evidence.Select(entry => entry.CanonicalItemId).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            definitions[canonicalId] = await _items.GetAsync(canonicalId, cancellationToken).ConfigureAwait(false);
        }

        return definitions;
    }

    private async Task<GridCellObservation> BuildObservationAsync(
        CapturedImage image,
        Footprint footprint,
        EvidenceRegion bounds,
        IReadOnlyList<IconContentEvidence> evidence,
        IReadOnlyDictionary<string, ItemDefinition?> definitions,
        GridPixelReconstructionOptions options,
        DateTimeOffset observedUtc,
        bool attemptQuantityOcr,
        CancellationToken cancellationToken)
    {
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.GameWrittenScreenshot,
            "recognition.grid.cell",
            observedUtc,
            EvidenceConfidence.Unscored,
            Producer);

        var cropped = Crop(image, bounds);
        var query = new IconFingerprintEvidence(
            IconFingerprintAlgorithms.DifferenceHashLuminance9X8,
            IconFingerprintAlgorithms.DifferenceHashLuminance9X8Version,
            IconFingerprintAlgorithms.DifferenceHashLuminance9X8Bits,
            SkiaPerceptualIconMatcher.ComputeDifferenceHash(cropped));

        var references = evidence
            .Where(entry => definitions.TryGetValue(entry.CanonicalItemId, out var definition) &&
                            definition is not null &&
                            MatchesFootprint(definition.Dimensions, footprint.Width, footprint.Height))
            .ToArray();
        var separation = _separator.Separate(query, references, options.SeparationPolicy, cancellationToken);

        int? quantity = attemptQuantityOcr
            ? await ReadQuantityAsync(image, bounds, cancellationToken).ConfigureAwait(false)
            : null;
        var widthField = Complete("grid.cell.item.width-cells", (int?)footprint.Width, provenance);
        var heightField = Complete("grid.cell.item.height-cells", (int?)footprint.Height, provenance);
        var quantityField = quantity is { } read
            ? Complete("grid.cell.item.quantity", (int?)read, provenance)
            : Unknown<int?>("grid.cell.item.quantity", provenance);
        var foundInRaidField = Unknown<bool?>("grid.cell.item.found-in-raid", provenance);
        var conditionField = Unknown<ItemConditionReading>("grid.cell.item.condition", provenance);

        EvidencedValue<RecognizedItem> itemField;
        if (separation.Outcome == IconCandidateSeparationOutcome.Separated)
        {
            var item = await BuildRecognizedItemAsync(
                    separation.Candidates[0].Evidence.CanonicalItemId,
                    footprint,
                    definitions,
                    widthField,
                    heightField,
                    quantityField,
                    foundInRaidField,
                    conditionField,
                    provenance,
                    cancellationToken)
                .ConfigureAwait(false);
            itemField = new EvidencedValue<RecognizedItem>(
                "grid.cell.item",
                item,
                new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "grid.cell.item.separated"),
                provenance,
                bounds);
        }
        else
        {
            var candidates = new List<EvidenceCandidate<RecognizedItem>>();
            foreach (var candidate in separation.Candidates)
            {
                var candidateItem = await BuildRecognizedItemAsync(
                        candidate.Evidence.CanonicalItemId,
                        footprint,
                        definitions,
                        widthField,
                        heightField,
                        quantityField,
                        foundInRaidField,
                        conditionField,
                        provenance,
                        cancellationToken)
                    .ConfigureAwait(false);
                candidates.Add(new EvidenceCandidate<RecognizedItem>(
                    candidate.Evidence.CanonicalItemId,
                    candidateItem.DisplayName.Value ?? candidate.Evidence.CanonicalItemId,
                    candidateItem,
                    provenance));
            }

            var status = new ResultStatus(
                candidates.Count == 0 ? ResultCompleteness.Unknown : ResultCompleteness.Partial,
                FreshnessState.Current,
                candidates.Count == 0 ? "grid.cell.item.no-candidate" : "grid.cell.item.ambiguous");
            itemField = new EvidencedValue<RecognizedItem>(
                "grid.cell.item",
                null,
                status,
                provenance,
                bounds,
                candidates);
        }

        return new GridCellObservation(
            $"cell-{footprint.Row:D3}-{footprint.Column:D3}",
            new GridCellAddress(footprint.Row, footprint.Column),
            itemField);
    }

    private async Task<RecognizedItem> BuildRecognizedItemAsync(
        string canonicalId,
        Footprint footprint,
        IReadOnlyDictionary<string, ItemDefinition?> definitions,
        EvidencedValue<int?> widthField,
        EvidencedValue<int?> heightField,
        EvidencedValue<int?> quantityField,
        EvidencedValue<bool?> foundInRaidField,
        EvidencedValue<ItemConditionReading> conditionField,
        EvidenceProvenance provenance,
        CancellationToken cancellationToken)
    {
        if (!definitions.TryGetValue(canonicalId, out var definition))
        {
            cancellationToken.ThrowIfCancellationRequested();
            definition = await _items.GetAsync(canonicalId, cancellationToken).ConfigureAwait(false);
        }

        var displayName = definition?.Name ?? canonicalId;
        var rotatedField = definition is null
            ? Unknown<bool?>("grid.cell.item.rotated", provenance)
            : definition.Dimensions.Width == footprint.Width && definition.Dimensions.Height == footprint.Height
                ? Complete("grid.cell.item.rotated", (bool?)false, provenance)
                : definition.Dimensions.Width == footprint.Height && definition.Dimensions.Height == footprint.Width
                    ? Complete("grid.cell.item.rotated", (bool?)true, provenance)
                    : Unknown<bool?>("grid.cell.item.rotated", provenance);

        return new RecognizedItem(
            Complete("grid.cell.item.canonical-id", canonicalId, provenance),
            Complete("grid.cell.item.display-name", displayName, provenance),
            quantityField,
            widthField,
            heightField,
            rotatedField,
            foundInRaidField,
            conditionField);
    }

    private async Task<int?> ReadQuantityAsync(CapturedImage image, EvidenceRegion bounds, CancellationToken cancellationToken)
    {
        // The stack-count badge sits bottom-right of a cell's footprint; the caption (item name)
        // that StashGrid crops separately sits top-left, so the two never overlap.
        var badgeWidth = Math.Max(1, (int)Math.Round(bounds.Width * 0.5));
        var badgeHeight = Math.Max(1, (int)Math.Round(bounds.Height * 0.32));
        var badgeX = Math.Clamp(bounds.X + bounds.Width - badgeWidth, 0, Math.Max(0, image.Width - 1));
        var badgeY = Math.Clamp(bounds.Y + bounds.Height - badgeHeight, 0, Math.Max(0, image.Height - 1));
        var clampedWidth = Math.Min(badgeWidth, image.Width - badgeX);
        var clampedHeight = Math.Min(badgeHeight, image.Height - badgeY);
        if (clampedWidth <= 0 || clampedHeight <= 0)
        {
            return null;
        }

        var badge = new PixelRect(badgeX, badgeY, clampedWidth, clampedHeight);
        var result = await _ocrEngine
            .RecognizeAsync(image, new OcrRequest(ScanContext.Container, badge), cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsAvailable || result.Lines.Count == 0)
        {
            return null;
        }

        var text = string.Concat(result.Lines.Select(line => line.Text));
        var match = Regex.Match(text, @"\d{1,4}");
        return match.Success && int.TryParse(match.Value, out var quantity) && quantity > 0 ? quantity : null;
    }

    private static bool MatchesFootprint(ItemDimensions dimensions, int width, int height) =>
        (dimensions.Width == width && dimensions.Height == height) ||
        (dimensions.Width == height && dimensions.Height == width);

    private static CapturedImage Crop(CapturedImage image, EvidenceRegion region)
    {
        var bytesPerPixel = CapturedImagePixels.BytesPerPixel(image.Format);
        var x = Math.Clamp(region.X, 0, Math.Max(0, image.Width - 1));
        var y = Math.Clamp(region.Y, 0, Math.Max(0, image.Height - 1));
        var width = Math.Clamp(region.Width, 1, image.Width - x);
        var height = Math.Clamp(region.Height, 1, image.Height - y);
        var stride = width * bytesPerPixel;
        var buffer = new byte[stride * height];
        var source = image.Pixels.Span;
        for (var row = 0; row < height; row++)
        {
            var sourceOffset = ((y + row) * image.Stride) + (x * bytesPerPixel);
            source.Slice(sourceOffset, stride).CopyTo(buffer.AsSpan(row * stride, stride));
        }

        return new CapturedImage(buffer, width, height, stride, image.Format, image.CapturedUtc, "grid-cell-crop");
    }

    private static EvidencedValue<T> Complete<T>(string fieldId, T value, EvidenceProvenance provenance) =>
        new(fieldId, value, new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, fieldId + ".read"), provenance);

    private static EvidencedValue<T> Unknown<T>(string fieldId, EvidenceProvenance provenance) =>
        new(fieldId, default, new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current, fieldId + ".unresolved"), provenance);

    private readonly record struct Footprint(int Row, int Column, int Width, int Height);
}
