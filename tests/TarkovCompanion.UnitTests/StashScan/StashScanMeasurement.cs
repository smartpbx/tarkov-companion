using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Core.Domain.Stash;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.StashScanFixtures;

namespace TarkovCompanion.UnitTests.StashScan;

/// <summary>What one run of the guided stash path measured, against a stash whose truth is known.</summary>
internal sealed record StashScanMeasurementResult(
    string Scenario,
    int Frames,
    int LatticesFound,
    int LatticesExact,
    int TruthFootprintsVisible,
    int FootprintsFound,
    int SpuriousFootprints,
    int Identified,
    int IdentifiedCorrectly,
    int RegionsPlaced,
    int RegionsPlacedCorrectly,
    int TruthOccurrences,
    int ReconstructedOccurrences,
    int ReconstructedCorrect,
    int ReconstructedWrong,
    int DoubleCounted,
    int UnknownTiles,
    IReadOnlyList<string> IssueCodes,
    StashScanAssemblyResult? Assembly)
{
    public IReadOnlyList<string> Lattices { get; init; } = [];

    public double Precision => ReconstructedOccurrences == 0 ? 0 : (double)ReconstructedCorrect / ReconstructedOccurrences;

    public double Recall => TruthOccurrences == 0 ? 0 : (double)ReconstructedCorrect / TruthOccurrences;

    public string Describe()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"[stash-e2e] {Scenario}");
        text.AppendLine(CultureInfo.InvariantCulture, $"[stash-e2e]   frames {Frames} · lattice found {LatticesFound}/{Frames} · lattice exact {LatticesExact}/{Frames}");
        text.AppendLine(CultureInfo.InvariantCulture, $"[stash-e2e]   per frame: footprints found {FootprintsFound}/{TruthFootprintsVisible} · spurious {SpuriousFootprints} · identified {Identified} · correct {IdentifiedCorrectly}");
        text.AppendLine(CultureInfo.InvariantCulture, $"[stash-e2e]   stitched: regions placed {RegionsPlaced}/{Frames} · placed at the true row {RegionsPlacedCorrectly}/{Frames}");
        text.AppendLine(CultureInfo.InvariantCulture, $"[stash-e2e]   reconstruction: {ReconstructedCorrect} correct of {TruthOccurrences} true items · {ReconstructedWrong} wrong · {DoubleCounted} double-counted · {UnknownTiles} left unknown");
        text.AppendLine(CultureInfo.InvariantCulture, $"[stash-e2e]   precision {Precision:P1} · recall {Recall:P1}");
        text.AppendLine(CultureInfo.InvariantCulture, $"[stash-e2e]   guidance: {(IssueCodes.Count == 0 ? "none" : string.Join(", ", IssueCodes))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"[stash-e2e]   lattices: {string.Join(" | ", Lattices)}");
        return text.ToString();
    }
}

/// <summary>
/// Runs painted stash frames through the real pixel reader, reconstructor and assembler and scores
/// the result against the layout that was painted.
/// </summary>
/// <remarks>
/// The box has no real stash screenshots (the corpus directory and Clayton's drop folder are both
/// empty as of 2026-09-18), so this is what "measure the guided path" can honestly mean today: the
/// geometry is the measured one, the truth is exact, and the item art is invented. It finds
/// plumbing faults - a packed grid merged into one footprint, an overlap that cannot stitch, a
/// double count - and it cannot say how the recognizer fares on the game's own pixels.
/// </remarks>
internal enum StashIconReferences
{
    /// <summary>Nothing in the icon cache: the application as it ships today.</summary>
    None,

    /// <summary>The published icon of each item, as a catalogue icon index would supply.</summary>
    CatalogueIcons,

    /// <summary>Each item's fingerprint as the game itself draws the tile.</summary>
    InGameTiles,
}

internal static class StashScanMeasurement
{
    public static readonly DateTimeOffset ObservedUtc = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly InventoryProfileScope Scope = new(
        Guid.Parse("55555555-5555-5555-5555-555555555555"),
        "generation-a",
        "Pvp");

    public static async Task<StashScanMeasurementResult> RunAsync(
        string scenario,
        SyntheticStashLayout layout,
        IReadOnlyList<int> firstRows,
        SyntheticStashFrameOptions options,
        StashIconReferences references,
        bool layoutStitch = true)
    {
        var catalog = SyntheticStashLayout.Catalog;
        var evidence = references switch
        {
            StashIconReferences.CatalogueIcons => catalog
                .Select(item => Evidence(item.ItemId, SkiaPerceptualIconMatcher.ComputeDifferenceHash(SyntheticStashPainter.RenderReferenceIcon(item))))
                .ToArray(),
            StashIconReferences.InGameTiles => catalog
                .Select(item => Evidence(item.ItemId, InGameFingerprint(item, options)))
                .ToArray(),
            _ => [],
        };
        var builder = new GridPixelReconstructionBuilder(
            new FixedIconEvidenceCache(evidence),
            new FixedItemRepository(catalog.ToDictionary(item => item.ItemId, Definition, StringComparer.Ordinal)),
            new UnavailableOcrEngine());
        var reconstructor = new InventoryGridReconstructor();
        var sessionId = new CaptureSessionId(Guid.NewGuid());

        var latticesFound = 0;
        var latticesExact = 0;
        var truthVisible = 0;
        var footprintsFound = 0;
        var spurious = 0;
        var identified = 0;
        var identifiedCorrectly = 0;
        var frames = new List<StashScanCaptureFrame>();
        var frameRowOffsets = new List<int?>();
        var lattices = new List<string>();
        for (var ordinal = 0; ordinal < firstRows.Count; ordinal++)
        {
            var firstRow = firstRows[ordinal];
            var image = SyntheticStashPainter.RenderFrame(layout, firstRow, options);
            var request = await builder.BuildAsync(image, InventoryGridSurface.Stash, ObservedUtc, cancellationToken: CancellationToken.None);
            int? rowOffset = null;
            lattices.Add(request.Lattice is { } found
                ? $"{found.Columns}x{found.Rows} @{found.CellWidthPixels}px from ({found.Bounds.X},{found.Bounds.Y})"
                : "none");
            if (request.Lattice is { } lattice)
            {
                latticesFound++;
                var exact = lattice.Columns == SyntheticStashLayout.Columns &&
                            lattice.CellWidthPixels == options.Pitch &&
                            lattice.CellHeightPixels == options.Pitch &&
                            Math.Abs(lattice.Bounds.X - options.PanelX) <= 1 &&
                            Math.Abs(lattice.Bounds.Y - options.PanelY) <= 1;
                if (exact)
                {
                    latticesExact++;
                }

                // Score against the truth by where the lattice actually sits, so a lattice that
                // starts a row low is not also charged with every footprint being "wrong".
                rowOffset = (int)Math.Round((lattice.Bounds.Y - options.PanelY) / (double)lattice.CellHeightPixels);
                var columnOffset = (int)Math.Round((lattice.Bounds.X - options.PanelX) / (double)lattice.CellWidthPixels);
                var visible = layout.Placements
                    .Where(placement => placement.Row >= firstRow && placement.Row + placement.Item.Height <= firstRow + options.VisibleRows)
                    .ToDictionary(placement => (placement.Row - firstRow, placement.Column));
                truthVisible += visible.Count;
                foreach (var observation in request.OccupiedCells)
                {
                    var key = (observation.Anchor.Row + rowOffset.Value, observation.Anchor.Column + columnOffset);
                    var width = FootprintWidth(observation);
                    var height = FootprintHeight(observation);
                    if (visible.TryGetValue(key, out var truth) && truth.Item.Width == width && truth.Item.Height == height)
                    {
                        footprintsFound++;
                        if (observation.Item.Value is { } item)
                        {
                            identified++;
                            if (string.Equals(item.CanonicalId.Value, truth.Item.ItemId, StringComparison.Ordinal))
                            {
                                identifiedCorrectly++;
                            }
                        }
                    }
                    else if (key.Item1 < options.VisibleRows)
                    {
                        spurious++;
                        if (observation.Item.Value is not null)
                        {
                            identified++;
                        }
                    }
                }
            }
            else
            {
                truthVisible += layout.Placements.Count(placement =>
                    placement.Row >= firstRow && placement.Row + placement.Item.Height <= firstRow + options.VisibleRows);
            }

            frameRowOffsets.Add(rowOffset);
            var reconstruction = reconstructor.Reconstruct(request, CancellationToken.None);
            if (reconstruction.Recognition is null)
            {
                continue;
            }

            frames.Add(Frame(sessionId, ordinal, image, reconstruction, confirmsStart: ordinal == 0));
        }

        if (frames.Count == 0)
        {
            return new(scenario, firstRows.Count, latticesFound, latticesExact, truthVisible, 0, 0, 0, 0, 0, 0,
                layout.Placements.Count, 0, 0, 0, 0, 0, ["no frame carried a grid"], null);
        }

        var assembler = new StashScanAssembler();
        StashScanAssemblyRequest Request(IReadOnlyList<StashScanCaptureFrame> captures) => new(
            "measure-result",
            "measure-snapshot",
            sessionId,
            Scope,
            "measure-data",
            ObservedUtc,
            captures);
        var assembly = assembler.Assemble(Request(frames));
        if (layoutStitch)
        {
            assembly = assembler.Assemble(Request(new StashLayoutAligner().AddLayoutOrigins(frames, assembly, ObservedUtc)));
        }

        return Score(scenario, layout, firstRows, frameRowOffsets, assembly, latticesFound, latticesExact, truthVisible, footprintsFound, spurious, identified, identifiedCorrectly) with
        {
            Lattices = lattices,
        };
    }

    private static StashScanMeasurementResult Score(
        string scenario,
        SyntheticStashLayout layout,
        IReadOnlyList<int> firstRows,
        IReadOnlyList<int?> frameRowOffsets,
        StashScanAssemblyResult assembly,
        int latticesFound,
        int latticesExact,
        int truthVisible,
        int footprintsFound,
        int spurious,
        int identified,
        int identifiedCorrectly)
    {
        var stash = assembly.Recognition.Result.Value!;
        var truth = layout.Placements.ToDictionary(placement => (placement.Row, placement.Column));
        var placed = 0;
        var placedCorrectly = 0;
        var seen = new Dictionary<(int Row, int Column), string?>();
        var doubleCounted = 0;
        var occurrences = 0;
        foreach (var region in stash.CapturedRegions)
        {
            if (region.OriginInContainer.Value is not { } origin)
            {
                continue;
            }

            placed++;
            var offset = frameRowOffsets[region.CaptureOrdinal] ?? 0;
            if (origin.Row + offset == firstRows[region.CaptureOrdinal])
            {
                placedCorrectly++;
            }

            foreach (var cell in region.Grid.Cells)
            {
                var key = (origin.Row + cell.Anchor.Row, origin.Column + cell.Anchor.Column);
                var id = cell.Item.Value?.CanonicalId.Value;
                if (id is not null)
                {
                    occurrences++;
                }

                if (!seen.TryAdd(key, id) && seen[key] is null)
                {
                    seen[key] = id;
                }
            }
        }

        // What a consumer summing every region's cells reports, less what is really there once
        // the overlap is folded: the double count a per-region list shows the player.
        doubleCounted = occurrences - seen.Values.Count(id => id is not null);
        var correct = 0;
        var wrong = 0;
        var unknown = 0;
        foreach (var (key, id) in seen)
        {
            if (id is null)
            {
                unknown++;
            }
            else if (truth.TryGetValue(key, out var placement) && string.Equals(placement.Item.ItemId, id, StringComparison.Ordinal))
            {
                correct++;
            }
            else
            {
                wrong++;
            }
        }

        return new(
            scenario,
            firstRows.Count,
            latticesFound,
            latticesExact,
            truthVisible,
            footprintsFound,
            spurious,
            identified,
            identifiedCorrectly,
            placed,
            placedCorrectly,
            layout.Placements.Count,
            correct + wrong,
            correct,
            wrong,
            doubleCounted,
            unknown,
            assembly.Report.Issues.Select(issue => issue.Code).Distinct(StringComparer.Ordinal).ToArray(),
            assembly);
    }

    /// <summary>
    /// The fingerprint of the item as the game draws it: one tile painted alone and cropped to
    /// exactly the rectangle the pixel reader crops.
    /// </summary>
    public static ulong InGameFingerprint(SyntheticStashItem item, SyntheticStashFrameOptions options)
    {
        var image = SyntheticStashPainter.RenderFrame(SyntheticStashLayout.Of(8, new SyntheticStashPlacement(item, 1, 1)), 0, options);
        return Fingerprint(image, options.PanelX + options.Pitch, options.PanelY + options.Pitch, item.Width * options.Pitch, item.Height * options.Pitch);
    }

    public static ulong Fingerprint(CapturedImage image, int x, int y, int width, int height)
    {
        var buffer = new byte[width * 4 * height];
        for (var row = 0; row < height; row++)
        {
            image.Pixels.Span.Slice(((y + row) * image.Stride) + (x * 4), width * 4).CopyTo(buffer.AsSpan(row * width * 4));
        }

        return SkiaPerceptualIconMatcher.ComputeDifferenceHash(
            new CapturedImage(buffer, width, height, width * 4, image.Format, image.CapturedUtc, "measure-crop"));
    }

    /// <summary>
    /// What a near-match rule would do with catalogue icons, without changing the shipped rule:
    /// every whole tile in the first frame is hashed and compared with every catalogue icon of its
    /// own footprint, under "within <paramref name="maximumDistance"/> bits and
    /// <paramref name="minimumGap"/> clear of the runner-up".
    /// </summary>
    public static string DescribeNearMatch(SyntheticStashLayout layout, SyntheticStashFrameOptions options, int maximumDistance, int minimumGap)
    {
        var image = SyntheticStashPainter.RenderFrame(layout, 0, options);
        var catalogue = SyntheticStashLayout.Catalog.ToDictionary(
            item => item.ItemId,
            item => SkiaPerceptualIconMatcher.ComputeDifferenceHash(SyntheticStashPainter.RenderReferenceIcon(item)),
            StringComparer.Ordinal);
        var tiles = 0;
        var exact = 0;
        var separated = 0;
        var wrong = 0;
        var distances = new List<int>();
        foreach (var placement in layout.Placements.Where(placement => placement.Row + placement.Item.Height <= options.VisibleRows))
        {
            tiles++;
            var hash = Fingerprint(
                image,
                options.PanelX + (placement.Column * options.Pitch),
                options.PanelY + (placement.Row * options.Pitch),
                placement.Item.Width * options.Pitch,
                placement.Item.Height * options.Pitch);
            var ranked = SyntheticStashLayout.Catalog
                .Where(item => item.Width == placement.Item.Width && item.Height == placement.Item.Height)
                .Select(item => (item.ItemId, Distance: System.Numerics.BitOperations.PopCount(hash ^ catalogue[item.ItemId])))
                .OrderBy(entry => entry.Distance)
                .ToArray();
            distances.Add(ranked.First(entry => entry.ItemId == placement.Item.ItemId).Distance);
            if (ranked[0].Distance == 0)
            {
                exact++;
            }

            if (ranked.Length >= 2 && ranked[0].Distance <= maximumDistance && ranked[1].Distance - ranked[0].Distance >= minimumGap)
            {
                separated++;
                if (ranked[0].ItemId != placement.Item.ItemId)
                {
                    wrong++;
                }
            }
        }

        distances.Sort();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[stash-e2e] catalogue icons against in-game tiles: {tiles} tiles · bit-exact {exact} · distance to the true icon median {distances[distances.Count / 2]} max {distances[^1]} bits · a rule of <= {maximumDistance} bits and >= {minimumGap} clear would name {separated}, {wrong} of them wrongly");
    }

    public static StashScanCaptureFrame Frame(
        CaptureSessionId sessionId,
        int ordinal,
        CapturedImage image,
        GridReconstructionResult reconstruction,
        bool confirmsStart)
    {
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.GameWrittenScreenshot,
            "recognition.stash.capture",
            ObservedUtc,
            EvidenceConfidence.Unscored,
            new ProducerIdentity("stash measurement", "stash-measurement-1"));
        return new StashScanCaptureFrame(
            sessionId,
            $"measure-artifact-{ordinal:D2}",
            captureOrdinal: ordinal,
            CaptureCorrelationId.New(),
            new CaptureContextMetadata(null, null, null, null, null, null, "desktop"),
            Convert.ToHexStringLower(SHA256.HashData(image.Pixels.Span)),
            containerPath: "stash",
            ObservedUtc,
            0,
            provenance,
            reconstruction,
            totalContainerCells: new EvidencedValue<int?>(
                "stash.capture.total-cells",
                null,
                new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current, "stash.capture.total-cells.unresolved"),
                provenance),
            confirmsContainerStart: confirmsStart);
    }

    private static int FootprintWidth(GridCellObservation observation) =>
        observation.Item.Value?.WidthCells.Value ??
        observation.Item.Candidates.FirstOrDefault()?.Value.WidthCells.Value ??
        WidthFromBounds(observation);

    private static int FootprintHeight(GridCellObservation observation) =>
        observation.Item.Value?.HeightCells.Value ??
        observation.Item.Candidates.FirstOrDefault()?.Value.HeightCells.Value ??
        HeightFromBounds(observation);

    private static int WidthFromBounds(GridCellObservation observation) =>
        observation.Item.Bounds is { } bounds ? Math.Max(1, (int)Math.Round(bounds.Width / 63.0)) : 1;

    private static int HeightFromBounds(GridCellObservation observation) =>
        observation.Item.Bounds is { } bounds ? Math.Max(1, (int)Math.Round(bounds.Height / 63.0)) : 1;

    public static IconContentEvidence Evidence(string canonicalId, ulong fingerprint) => new(
        new IconEvidenceKey(canonicalId, new Uri($"https://example.invalid/icons/{canonicalId}.png")),
        ObservedUtc,
        new string('a', 64),
        new EvidenceProvenance(
            EvidenceSourceClass.PublicStructuredData,
            "fixture-icon-source",
            ObservedUtc,
            EvidenceConfidence.Certain,
            new ProducerIdentity("fixture", "1")),
        new IconPixelDimensions(64, 64),
        new IconFingerprintEvidence(
            IconFingerprintAlgorithms.DifferenceHashLuminance9X8,
            IconFingerprintAlgorithms.DifferenceHashLuminance9X8Version,
            IconFingerprintAlgorithms.DifferenceHashLuminance9X8Bits,
            fingerprint));

    public static ItemDefinition Definition(SyntheticStashItem item) => new(
        item.ItemId,
        item.Name,
        item.Name,
        string.Empty,
        ItemCategory.Barter,
        new ItemDimensions(item.Width, item.Height),
        true,
        null,
        null,
        null,
        null,
        null,
        new HashSet<string>(),
        new DataProvenance("fixture", ObservedUtc));

    internal sealed class FixedIconEvidenceCache(IReadOnlyList<IconContentEvidence> evidence) : IIconEvidenceCache
    {
        public Task<IconContentEvidenceAsset> StoreAsync(IconContentWriteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Fixture cache is read-only.");

        public Task<IconContentEvidenceAsset?> GetAsync(IconEvidenceKey key, CancellationToken cancellationToken) =>
            Task.FromResult<IconContentEvidenceAsset?>(null);

        public Task<IReadOnlyList<IconContentEvidence>> ListEvidenceAsync(CancellationToken cancellationToken) =>
            Task.FromResult(evidence);
    }

    internal sealed class FixedItemRepository(IReadOnlyDictionary<string, ItemDefinition> byId) : IItemRepository
    {
        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(byId.TryGetValue(itemId, out var item) ? item : null);

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(null);
    }

    internal sealed class UnavailableOcrEngine : IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(CapturedImage image, OcrRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrResult([], TimeSpan.Zero, "fixture-ocr", IsAvailable: false, DiagnosticCode: "fixture_ocr_unavailable"));
    }
}
