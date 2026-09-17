using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;

namespace TarkovCompanion.RecognitionTests.Grid;

/// <summary>
/// Synthetic-fixture coverage for #273's pixel-to-grid geometry: the lattice and footprint math
/// must be resolution/UI-scale independent, and item identity must stay honest (unresolved rather
/// than guessed) whenever the local icon evidence cache has nothing, or nothing distinct, to
/// compare against.
/// </summary>
public sealed class GridPixelReconstructionBuilderTests
{
    private static readonly DateTimeOffset ObservedUtc = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1920, 1080, 120, 90, 63)]
    [InlineData(2560, 1440, 200, 140, 84)]
    [InlineData(3840, 2160, 260, 220, 126)]
    // A 6-column, 63px-pitch panel covers under 10% of a 3840-wide frame - below the reused
    // whole-frame line detector's coverage threshold - so this case only passes through the
    // centered-safe-area fallback, exercising it end to end on Clayton's own 32:9 setup.
    [InlineData(3840, 1080, 1100, 90, 63)]
    public async Task DetectsLatticeGeometryFromPixelsAcrossResolutionsAndAspectRatios(
        int frameWidth,
        int frameHeight,
        int originX,
        int originY,
        int pitch)
    {
        const int columns = 6;
        const int rows = 5;
        var occupied = new HashSet<(int Row, int Column)> { (1, 1) };
        var image = SyntheticGrid.Build(frameWidth, frameHeight, originX, originY, columns, rows, pitch, occupied);
        var builder = new GridPixelReconstructionBuilder(new EmptyIconEvidenceCache(), new StubItemRepository(), new UnavailableOcrEngine());

        var request = await builder.BuildAsync(image, InventoryGridSurface.Stash, ObservedUtc, cancellationToken: CancellationToken.None);

        Assert.NotNull(request.Lattice);
        Assert.Equal(rows, request.Lattice!.Rows);
        Assert.Equal(columns, request.Lattice.Columns);
        Assert.Equal(pitch, request.Lattice.CellWidthPixels);
        Assert.Equal(pitch, request.Lattice.CellHeightPixels);
        // The reused line detector groups a contiguous contrast run to its midpoint, which can
        // land a pixel off a hand-drawn fixture's exact origin; cell size and count are exact.
        Assert.InRange(request.Lattice.Bounds.X, originX - 1, originX + 1);
        Assert.InRange(request.Lattice.Bounds.Y, originY - 1, originY + 1);
        Assert.True(request.Lattice.Bounds.X + request.Lattice.Bounds.Width <= frameWidth);
        Assert.True(request.Lattice.Bounds.Y + request.Lattice.Bounds.Height <= frameHeight);
        var observation = Assert.Single(request.OccupiedCells);
        Assert.Equal(new GridCellAddress(1, 1), observation.Anchor);
    }

    [Fact]
    public async Task MergesAFilledRectangleOfOccupiedCellsIntoOneMultiCellFootprint()
    {
        const int pitch = 70;
        const int originX = 100;
        const int originY = 80;
        var occupied = new HashSet<(int Row, int Column)> { (1, 1), (1, 2), (2, 1), (2, 2) };
        var image = SyntheticGrid.Build(1920, 1080, originX, originY, columns: 6, rows: 5, pitch, occupied);

        var trueQuery = ComputeMeasuredFingerprint(image, row: 1, column: 1, widthCells: 2, heightCells: 2);
        var decoyFingerprint = trueQuery ^ ulong.MaxValue;
        var cache = new EmptyIconEvidenceCache(
        [
            FakeEvidence("fixture-item-true", trueQuery),
            FakeEvidence("fixture-item-decoy", decoyFingerprint),
        ]);
        var items = new StubItemRepository(new Dictionary<string, ItemDefinition>(StringComparer.Ordinal)
        {
            ["fixture-item-true"] = Definition("fixture-item-true", "Fixture True", 2, 2),
            ["fixture-item-decoy"] = Definition("fixture-item-decoy", "Fixture Decoy", 2, 2),
        });
        var builder = new GridPixelReconstructionBuilder(cache, items, new UnavailableOcrEngine());

        var request = await builder.BuildAsync(image, InventoryGridSurface.Stash, ObservedUtc, cancellationToken: CancellationToken.None);

        var observation = Assert.Single(request.OccupiedCells);
        Assert.Equal(new GridCellAddress(1, 1), observation.Anchor);
        var item = Assert.IsType<RecognizedItem>(observation.Item.Value, exactMatch: false);
        Assert.Equal("fixture-item-true", item.CanonicalId.Value);
        Assert.Equal(2, item.WidthCells.Value);
        Assert.Equal(2, item.HeightCells.Value);
        Assert.Equal(ResultCompleteness.Complete, observation.Item.Status.Completeness);
    }

    [Fact]
    public async Task ReportsOccupiedCellsAsUnresolvedRatherThanGuessingWhenTheIconCacheIsEmpty()
    {
        var occupied = new HashSet<(int Row, int Column)> { (1, 1) };
        var image = SyntheticGrid.Build(1920, 1080, 120, 90, columns: 6, rows: 5, pitch: 63, occupied);
        var builder = new GridPixelReconstructionBuilder(new EmptyIconEvidenceCache(), new StubItemRepository(), new UnavailableOcrEngine());

        var request = await builder.BuildAsync(image, InventoryGridSurface.Stash, ObservedUtc, cancellationToken: CancellationToken.None);

        var observation = Assert.Single(request.OccupiedCells);
        Assert.Null(observation.Item.Value);
        Assert.Empty(observation.Item.Candidates);
        Assert.Equal(ResultCompleteness.Unknown, observation.Item.Status.Completeness);
    }

    [Fact]
    public async Task KeepsWeaklySeparatedCandidatesAmbiguousInsteadOfPickingOne()
    {
        const int pitch = 70;
        const int originX = 100;
        const int originY = 80;
        var occupied = new HashSet<(int Row, int Column)> { (1, 1) };
        var image = SyntheticGrid.Build(1920, 1080, originX, originY, columns: 6, rows: 5, pitch, occupied);

        var query = ComputeMeasuredFingerprint(image, row: 1, column: 1, widthCells: 1, heightCells: 1);
        // A single compatible reference is an insufficient reference set even at zero distance:
        // separation cannot rule out every other item this cache never learned about.
        var cache = new EmptyIconEvidenceCache([FakeEvidence("fixture-item-solo", query)]);
        var items = new StubItemRepository(new Dictionary<string, ItemDefinition>(StringComparer.Ordinal)
        {
            ["fixture-item-solo"] = Definition("fixture-item-solo", "Fixture Solo", 1, 1),
        });
        var builder = new GridPixelReconstructionBuilder(cache, items, new UnavailableOcrEngine());

        var request = await builder.BuildAsync(image, InventoryGridSurface.Stash, ObservedUtc, cancellationToken: CancellationToken.None);

        var observation = Assert.Single(request.OccupiedCells);
        Assert.Null(observation.Item.Value);
        var candidate = Assert.Single(observation.Item.Candidates);
        Assert.Equal("fixture-item-solo", candidate.Value.CanonicalId.Value);
        Assert.Equal(ResultCompleteness.Partial, observation.Item.Status.Completeness);
    }

    [Fact]
    public async Task ReturnsAnEmptyRequestWhenNoRegularGridIsVisible()
    {
        var image = SyntheticGrid.BuildBlank(1920, 1080);
        var builder = new GridPixelReconstructionBuilder(new EmptyIconEvidenceCache(), new StubItemRepository(), new UnavailableOcrEngine());

        var request = await builder.BuildAsync(image, InventoryGridSurface.Stash, ObservedUtc, cancellationToken: CancellationToken.None);

        Assert.Null(request.Lattice);
        Assert.Empty(request.OccupiedCells);
    }

    [Fact]
    public async Task ReturnsAnEmptyRequestForAFrameOverThePixelCeiling()
    {
        var oversized = new CapturedImage(new byte[16], 20_000, 20_000, 80_000, PixelFormat.Bgra8888, ObservedUtc, "oversized");
        var builder = new GridPixelReconstructionBuilder(new EmptyIconEvidenceCache(), new StubItemRepository(), new UnavailableOcrEngine());

        var request = await builder.BuildAsync(oversized, InventoryGridSurface.Stash, ObservedUtc, cancellationToken: CancellationToken.None);

        Assert.Null(request.Lattice);
        Assert.Empty(request.OccupiedCells);
    }

    /// <summary>
    /// Computes an icon fingerprint over exactly the pixels <see cref="GridPixelReconstructionBuilder"/>
    /// itself would crop for a footprint, by running the same detector it reuses. The line
    /// detector's grouped-midpoint origin can land a pixel off a fixture's hand-drawn origin, so
    /// hashing the fixture's own drawn coordinates instead would compare against a differently
    /// cropped region than production ever does.
    /// </summary>
    private static ulong ComputeMeasuredFingerprint(CapturedImage image, int row, int column, int widthCells, int heightCells)
    {
        var spec = new ContainerGridDetector().Detect(image, CancellationToken.None) ??
            throw new InvalidOperationException("Fixture grid was not detected.");
        var pitch = (int)Math.Round((double)spec.Bounds.Width / spec.Columns, MidpointRounding.AwayFromZero);
        var x = spec.Bounds.X + (column * pitch);
        var y = spec.Bounds.Y + (row * pitch);
        return SyntheticGrid.ComputeFingerprint(image, x, y, pitch * widthCells, pitch * heightCells);
    }

    private static IconContentEvidence FakeEvidence(string canonicalId, ulong fingerprint) => new(
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

    private static ItemDefinition Definition(string id, string name, int width, int height) => new(
        id,
        name,
        name,
        string.Empty,
        ItemCategory.Backpack,
        new ItemDimensions(width, height),
        true,
        null,
        null,
        null,
        null,
        null,
        new HashSet<string>(),
        new DataProvenance("fixture", ObservedUtc));

    private sealed class EmptyIconEvidenceCache(IReadOnlyList<IconContentEvidence>? evidence = null) : IIconEvidenceCache
    {
        private readonly IReadOnlyList<IconContentEvidence> _evidence = evidence ?? [];

        public Task<IconContentEvidenceAsset> StoreAsync(IconContentWriteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Fixture cache is read-only.");

        public Task<IconContentEvidenceAsset?> GetAsync(IconEvidenceKey key, CancellationToken cancellationToken) =>
            Task.FromResult<IconContentEvidenceAsset?>(null);

        public Task<IReadOnlyList<IconContentEvidence>> ListEvidenceAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_evidence);
    }

    private sealed class StubItemRepository(IReadOnlyDictionary<string, ItemDefinition>? byId = null) : IItemRepository
    {
        private readonly IReadOnlyDictionary<string, ItemDefinition> _byId = byId ?? new Dictionary<string, ItemDefinition>();

        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(_byId.TryGetValue(itemId, out var item) ? item : null);

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(null);
    }

    private sealed class UnavailableOcrEngine : IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(CapturedImage image, OcrRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrResult([], TimeSpan.Zero, "fixture-ocr", IsAvailable: false, DiagnosticCode: "fixture_ocr_unavailable"));
    }
}
