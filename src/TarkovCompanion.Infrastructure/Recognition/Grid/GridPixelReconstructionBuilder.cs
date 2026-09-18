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
        int maximumQuantityOcrCalls = 64,
        IconCandidateSeparationPolicy? shortlistPolicy = null,
        double minimumPixelCorrelation = DefaultMinimumPixelCorrelation,
        double minimumPixelCorrelationMargin = DefaultMinimumPixelCorrelationMargin)
    {
        SeparationPolicy = separationPolicy ?? throw new ArgumentNullException(nameof(separationPolicy));
        MaximumQuantityOcrCalls = maximumQuantityOcrCalls >= 0
            ? maximumQuantityOcrCalls
            : throw new ArgumentOutOfRangeException(nameof(maximumQuantityOcrCalls));
        ShortlistPolicy = shortlistPolicy ?? DefaultShortlistPolicy;
        MinimumPixelCorrelation = minimumPixelCorrelation is > 0 and <= 1
            ? minimumPixelCorrelation
            : throw new ArgumentOutOfRangeException(nameof(minimumPixelCorrelation));
        MinimumPixelCorrelationMargin = minimumPixelCorrelationMargin is > 0 and <= 1
            ? minimumPixelCorrelationMargin
            : throw new ArgumentOutOfRangeException(nameof(minimumPixelCorrelationMargin));
    }

    /// <summary>
    /// The lowest pixel correlation that may name an item. Measured on composed frames: a true
    /// item never scored under 0.945, even resampled from 1080p to 1440p, and the one wrong
    /// answer the pixel check ever gave scored under 0.90.
    /// </summary>
    public const double DefaultMinimumPixelCorrelation = 0.90;

    /// <summary>
    /// How far the best item has to stand clear of the next. Art twins that differ only by a
    /// printed label (keys, ammunition boxes) sit within a few hundredths of each other; 0.04
    /// refuses them, which costs about one item in ten and has not yet named a wrong one.
    /// </summary>
    public const double DefaultMinimumPixelCorrelationMargin = 0.04;

    /// <summary>The exact-hash policy, used only when a reference's pixels cannot be read.</summary>
    public IconCandidateSeparationPolicy SeparationPolicy { get; }

    /// <summary>How the difference hash picks the few references worth comparing pixel by pixel.</summary>
    public IconCandidateSeparationPolicy ShortlistPolicy { get; }

    public double MinimumPixelCorrelation { get; }

    public double MinimumPixelCorrelationMargin { get; }

    public int MaximumQuantityOcrCalls { get; }

    public static IconCandidateSeparationPolicy DefaultShortlistPolicy { get; } = new(
        "grid-recognition-icon-shortlist-1",
        maximumCandidateDistanceBits: 24,
        minimumRunnerUpGapBits: 1,
        maximumReturnedCandidates: IconCandidateSeparationPolicy.MaximumReturnedCandidatesLimit);

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
    ContainerGridSegmenter? segmenter = null,
    IconReferenceIndex? referenceIndex = null)
{
    private static readonly ProducerIdentity Producer = new("Tarkov Companion grid recognizer", "grid-recognition-1");

    private readonly IconReferenceIndex _references = referenceIndex ?? new(
        iconCache ?? throw new ArgumentNullException(nameof(iconCache)),
        items ?? throw new ArgumentNullException(nameof(items)));
    private readonly IOcrEngine _ocrEngine = ocrEngine ?? throw new ArgumentNullException(nameof(ocrEngine));
    private readonly IconCandidateSeparator _separator = separator ?? new();
    private readonly ContainerGridDetector _gridDetector = gridDetector ?? new();
    private readonly ContainerGridSegmenter _segmenter = segmenter ?? new();

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

        var spec = _gridDetector.Detect(image, cancellationToken) ?? DetectInCenteredSafeArea(image, cancellationToken);
        if (spec is null || BuildLattice(spec, observedUtc) is not { } lattice)
        {
            return new(surface, null, []);
        }

        var segments = _segmenter.Segment(image, spec, cancellationToken);
        var borders = GridBorderProbe.Measure(image, lattice, cancellationToken);
        var footprints = MergeFootprints(segments, borders, spec.Rows, spec.Columns, cancellationToken);
        if (footprints.Count == 0)
        {
            return new(surface, lattice, []);
        }

        var references = await _references.GetAsync(cancellationToken).ConfigureAwait(false);

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
                    references,
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
    /// Groups cells that have no border line between them into rectangular footprints.
    /// </summary>
    /// <remarks>
    /// Every Tarkov item occupies a solid rectangle of cells with a border round it and none
    /// inside it (see <see cref="GridBorderProbe"/>). A group of several cells is an item whatever
    /// its corners look like; a single cell is an item only when the segmenter saw something in
    /// it. A group that is not a rectangle means a border went unread, and falls back to one 1x1
    /// footprint per occupied cell instead of guessing a shape nothing measured.
    /// </remarks>
    private static List<Footprint> MergeFootprints(
        IReadOnlyList<ContainerSegment> segments,
        GridBorders borders,
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
                if (visited[row, column])
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
                    TryEnqueue(r - 1, c, r > 0 && !borders.Below[r - 1, c]);
                    TryEnqueue(r + 1, c, !borders.Below[r, c]);
                    TryEnqueue(r, c - 1, c > 0 && !borders.Right[r, c - 1]);
                    TryEnqueue(r, c + 1, !borders.Right[r, c]);

                    void TryEnqueue(int nr, int nc, bool open)
                    {
                        if (open && nr >= 0 && nr < rows && nc >= 0 && nc < columns && !visited[nr, nc])
                        {
                            visited[nr, nc] = true;
                            queue.Enqueue((nr, nc));
                        }
                    }
                }

                var boundingWidth = maxColumn - minColumn + 1;
                var boundingHeight = maxRow - minRow + 1;
                if (component.Count == 1)
                {
                    if (occupied[row, column])
                    {
                        footprints.Add(new(row, column, 1, 1));
                    }
                }
                else if (component.Count == boundingWidth * boundingHeight)
                {
                    footprints.Add(new(minRow, minColumn, boundingWidth, boundingHeight));
                }
                else
                {
                    foreach (var (r, c) in component.Where(cell => occupied[cell.Row, cell.Column]))
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

    /// <summary>The pixels to fingerprint: the footprint plus its closing border.</summary>
    /// <remarks>
    /// Neighbouring cells share their border line, so an item runs from its own left border to
    /// its right one inclusive, and the reference art is drawn that way: a 1x1 grid image is 64
    /// pixels for a 63-pixel pitch. Fingerprinting the pitch alone left the last border off and
    /// moved every block boundary of the hash, so no crop ever matched its own reference.
    /// </remarks>
    private static EvidenceRegion FingerprintBounds(EvidenceRegion footprint) =>
        new(footprint.X, footprint.Y, footprint.Width + 1, footprint.Height + 1, footprint.CoordinateSpace);

    private async Task<GridCellObservation> BuildObservationAsync(
        CapturedImage image,
        Footprint footprint,
        EvidenceRegion bounds,
        IconReferenceIndex.Snapshot references,
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

        var cropped = Crop(image, FingerprintBounds(bounds));
        var match = await MatchAsync(cropped, footprint, references, options, cancellationToken).ConfigureAwait(false);

        int? quantity = attemptQuantityOcr
            ? await ReadQuantityAsync(image, bounds, cancellationToken).ConfigureAwait(false)
            : null;

        EvidencedValue<RecognizedItem> itemField;
        if (match.Identified is { } identified)
        {
            // A named item carries the score that named it. The decision service will not act on
            // unscored evidence, and until this was scored every cell the recognizer got right
            // was still turned away as "evidence incomplete".
            var scored = new EvidenceProvenance(
                EvidenceSourceClass.GameWrittenScreenshot,
                "recognition.grid.cell",
                observedUtc,
                new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, Math.Clamp(match.Score, 0, 1)),
                Producer);
            itemField = new EvidencedValue<RecognizedItem>(
                "grid.cell.item",
                BuildRecognizedItem(identified, footprint, quantity, scored),
                new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "grid.cell.item.separated"),
                scored,
                bounds);
        }
        else
        {
            var candidates = match.Candidates
                .Select(candidate =>
                {
                    var candidateItem = BuildRecognizedItem(candidate, footprint, quantity, provenance);
                    return new EvidenceCandidate<RecognizedItem>(
                        candidate.Definition.Id,
                        candidate.Definition.Name,
                        candidateItem,
                        provenance);
                })
                .ToArray();
            var status = new ResultStatus(
                candidates.Length == 0 ? ResultCompleteness.Unknown : ResultCompleteness.Partial,
                FreshnessState.Current,
                candidates.Length == 0 ? "grid.cell.item.no-candidate" : "grid.cell.item.ambiguous");
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

    /// <summary>
    /// Shortlists references by difference hash, then lets the pixels decide.
    /// </summary>
    /// <remarks>
    /// Only references drawn at the footprint's own orientation are compared. A rotated item is
    /// drawn rotated, and a reference picture turned on its side has not been tried against
    /// anything real yet, so a rotated item is refused rather than matched by its hash alone.
    /// </remarks>
    private async Task<IconMatch> MatchAsync(
        CapturedImage cropped,
        Footprint footprint,
        IconReferenceIndex.Snapshot references,
        GridPixelReconstructionOptions options,
        CancellationToken cancellationToken)
    {
        var shaped = references.OfShape(footprint.Width, footprint.Height);
        if (shaped.Count == 0)
        {
            return IconMatch.None;
        }

        var byKey = shaped.ToDictionary(reference => reference.Evidence.Key);
        var query = new IconFingerprintEvidence(
            IconFingerprintAlgorithms.DifferenceHashLuminance9X8,
            IconFingerprintAlgorithms.DifferenceHashLuminance9X8Version,
            IconFingerprintAlgorithms.DifferenceHashLuminance9X8Bits,
            SkiaPerceptualIconMatcher.ComputeDifferenceHash(cropped));
        var evidence = shaped.Select(reference => reference.Evidence).ToArray();
        var shortlist = _separator.Separate(query, evidence, options.ShortlistPolicy, cancellationToken).Candidates;
        var described = IconPixelDescriptor.Create(cropped, footprint.Width, footprint.Height);

        var scores = new Dictionary<string, (IconReference Reference, double Score)>(StringComparer.Ordinal);
        if (described is not null)
        {
            foreach (var candidate in shortlist)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reference = byKey[candidate.Evidence.Key];
                if (await references.DescribeAsync(reference, cancellationToken).ConfigureAwait(false) is not { } descriptor)
                {
                    continue;
                }

                var score = described.Correlate(descriptor);
                if (!scores.TryGetValue(reference.Definition.Id, out var best) || score > best.Score)
                {
                    scores[reference.Definition.Id] = (reference, score);
                }
            }
        }

        if (scores.Count == 0)
        {
            // No reference pixels to compare against: fall back to what the hash alone can
            // honestly claim, which is an exact fingerprint standing clear of every other.
            var exact = _separator.Separate(query, evidence, options.SeparationPolicy, cancellationToken);
            return exact.Outcome == IconCandidateSeparationOutcome.Separated
                ? new(byKey[exact.Candidates[0].Evidence.Key], 1, [])
                : new(null, 0, exact.Candidates.Select(candidate => byKey[candidate.Evidence.Key]).ToArray());
        }

        var ranked = scores.Values
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Reference.Definition.Id, StringComparer.Ordinal)
            .ToArray();
        var distinctItems = shaped.Select(reference => reference.Definition.Id).Distinct(StringComparer.Ordinal).Take(2).Count();
        var clear = ranked.Length == 1 || ranked[0].Score - ranked[1].Score >= options.MinimumPixelCorrelationMargin;
        if (distinctItems >= 2 && clear && ranked[0].Score >= options.MinimumPixelCorrelation)
        {
            return new(ranked[0].Reference, ranked[0].Score, []);
        }

        return new(
            null,
            0,
            ranked
                .Where(entry => entry.Score >= MinimumCandidateCorrelation)
                .Take(MaximumShownCandidates)
                .Select(entry => entry.Reference)
                .ToArray());
    }

    /// <summary>Below this a lookalike is not worth showing a person as a possibility.</summary>
    private const double MinimumCandidateCorrelation = 0.6;

    private const int MaximumShownCandidates = 5;

    private sealed record IconMatch(IconReference? Identified, double Score, IReadOnlyList<IconReference> Candidates)
    {
        public static IconMatch None { get; } = new(null, 0, []);
    }

    private static RecognizedItem BuildRecognizedItem(
        IconReference reference,
        Footprint footprint,
        int? quantity,
        EvidenceProvenance provenance)
    {
        var definition = reference.Definition;
        // Two things the catalog settles without a pixel being read. An item that does not
        // stack is one item, and an item with no durability or uses has no condition to report.
        // Anything that could be otherwise stays unknown: a stack's count is only what the badge
        // says, and a medkit's remaining uses are not in the picture this reads.
        var quantityField = quantity is { } read
            ? Complete("grid.cell.item.quantity", (int?)read, provenance)
            : !IsStackable(definition)
                ? Complete("grid.cell.item.quantity", (int?)1, provenance)
                : Unknown<int?>("grid.cell.item.quantity", provenance);
        var conditionField = HasNoCondition(definition)
            ? Complete("grid.cell.item.condition", ItemConditionReading.NotApplicable, provenance)
            : Unknown<ItemConditionReading>("grid.cell.item.condition", provenance);
        var rotatedField = definition.Dimensions.Width == footprint.Width && definition.Dimensions.Height == footprint.Height
            ? Complete("grid.cell.item.rotated", (bool?)false, provenance)
            : definition.Dimensions.Width == footprint.Height && definition.Dimensions.Height == footprint.Width
                ? Complete("grid.cell.item.rotated", (bool?)true, provenance)
                : Unknown<bool?>("grid.cell.item.rotated", provenance);
        return new RecognizedItem(
            Complete("grid.cell.item.canonical-id", definition.Id, provenance),
            Complete("grid.cell.item.display-name", definition.Name, provenance),
            quantityField,
            Complete("grid.cell.item.width-cells", (int?)footprint.Width, provenance),
            Complete("grid.cell.item.height-cells", (int?)footprint.Height, provenance),
            rotatedField,
            Unknown<bool?>("grid.cell.item.found-in-raid", provenance),
            conditionField);
    }

    private static readonly HashSet<string> CurrencyItemIds = new(StringComparer.Ordinal)
    {
        "5449016a4bdc2d6f028b456f",
        "5696686a4bdc2da3298b456a",
        "569668774bdc2da2298b4568",
    };

    private static bool IsStackable(ItemDefinition definition) =>
        definition.Category == ItemCategory.Ammunition || CurrencyItemIds.Contains(definition.Id);

    private static bool HasNoCondition(ItemDefinition definition) => definition.Category is
        ItemCategory.Barter or
        ItemCategory.Ammunition or
        ItemCategory.AmmunitionPack or
        ItemCategory.Attachment or
        ItemCategory.Container or
        ItemCategory.Backpack or
        ItemCategory.Headset;

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
