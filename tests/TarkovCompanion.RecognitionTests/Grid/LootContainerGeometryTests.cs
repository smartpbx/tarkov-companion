using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;

namespace TarkovCompanion.RecognitionTests.Grid;

/// <summary>
/// Package 37: the shapes a loot container really has, which the stash-sized fixtures never drew.
/// </summary>
public sealed class LootContainerGeometryTests
{
    [Theory]
    [InlineData(1920, 1080, 4, 3)]
    [InlineData(1920, 1080, 2, 2)]
    [InlineData(3840, 1080, 5, 4)]
    public void FindsAContainerFarNarrowerThanTheFrame(int width, int height, int columns, int rows)
    {
        // Four cells at 1080p are 13% of the frame's width. The line finder used to want 18%.
        var occupied = new HashSet<(int, int)> { (0, 0), (1, 1) };
        var image = SyntheticGrid.Build(width, height, (width / 2) + 300, 180, columns, rows, 63, occupied);

        var spec = Assert.IsType<ContainerGridSpec>(new ContainerGridDetector().Detect(image));

        Assert.Equal(columns, spec.Columns);
        Assert.Equal(rows, spec.Rows);
        // On the drawn line, not beside it: one pixel off changes every icon's fingerprint.
        Assert.Equal((width / 2) + 300, spec.Bounds.X);
        Assert.Equal(180, spec.Bounds.Y);
        Assert.Equal(columns * 63, spec.Bounds.Width);
    }

    [Fact]
    public void AssumesAColumnLineThatWideItemsHideFromTopToBottom()
    {
        // Every row covers the line between the second and third columns with an item.
        (int Row, int Column, int Width, int Height)[] items = [(0, 1, 2, 2), (2, 1, 2, 1), (3, 1, 2, 1)];
        var occupied = items
            .SelectMany(item => Enumerable.Range(item.Row, item.Height)
                .SelectMany(row => Enumerable.Range(item.Column, item.Width).Select(column => (row, column))))
            .ToHashSet();
        var image = SyntheticGrid.Build(1920, 1080, 1260, 180, columns: 5, rows: 4, 63, occupied, items);

        var spec = Assert.IsType<ContainerGridSpec>(new ContainerGridDetector().Detect(image));

        Assert.Equal(5, spec.Columns);
        Assert.Equal(4, spec.Rows);
        Assert.Equal(1260, spec.Bounds.X);
    }

    [Fact]
    public async Task KeepsTwoItemsThatTouchAsTwoFootprints()
    {
        // A container is packed. Joining every occupied cell that touched another read two
        // bandages side by side as one 2x1 item nobody has ever seen.
        var occupied = new HashSet<(int, int)> { (1, 1), (1, 2) };
        var image = SyntheticGrid.Build(1920, 1080, 1260, 180, columns: 5, rows: 4, 63, occupied);
        var builder = new GridPixelReconstructionBuilder(new NoIcons(), new NoItems(), new NoOcr());

        var request = await builder.BuildAsync(image, InventoryGridSurface.VisibleLoot, DateTimeOffset.UtcNow);

        Assert.Equal(
            [new GridCellAddress(1, 1), new GridCellAddress(1, 2)],
            request.OccupiedCells.Select(cell => cell.Anchor).OrderBy(anchor => anchor.Column).ToArray());
    }

    [Fact]
    public async Task ReadsAnItemWithNoInnerBordersAsOneFootprintEvenWhenACornerLooksEmpty()
    {
        // Only three of the four cells hold anything the segmenter would call occupied.
        var occupied = new HashSet<(int, int)> { (0, 0), (0, 1), (1, 0) };
        var image = SyntheticGrid.Build(1920, 1080, 1260, 180, columns: 5, rows: 4, 63, occupied, items: [(0, 0, 2, 2)]);
        var builder = new GridPixelReconstructionBuilder(new NoIcons(), new NoItems(), new NoOcr());

        var request = await builder.BuildAsync(image, InventoryGridSurface.VisibleLoot, DateTimeOffset.UtcNow);

        var cell = Assert.Single(request.OccupiedCells);
        Assert.Equal(new GridCellAddress(0, 0), cell.Anchor);
        Assert.Equal(126, cell.Item.Bounds!.Width);
        Assert.Equal(126, cell.Item.Bounds!.Height);
    }

    private sealed class NoIcons : IIconEvidenceCache
    {
        public Task<IconContentEvidenceAsset> StoreAsync(IconContentWriteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IconContentEvidenceAsset?> GetAsync(IconEvidenceKey key, CancellationToken cancellationToken) =>
            Task.FromResult<IconContentEvidenceAsset?>(null);

        public Task<IReadOnlyList<IconContentEvidence>> ListEvidenceAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IconContentEvidence>>([]);
    }

    private sealed class NoItems : TarkovCompanion.Core.Abstractions.IItemRepository
    {
        public Task<TarkovCompanion.Core.Domain.Items.ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<TarkovCompanion.Core.Domain.Items.ItemDefinition?>(null);

        public Task<IReadOnlyList<TarkovCompanion.Core.Domain.Items.ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TarkovCompanion.Core.Domain.Items.ItemSearchHit>>([]);

        public Task<TarkovCompanion.Core.Domain.Items.ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<TarkovCompanion.Core.Domain.Items.ItemPriceSnapshot?>(null);
    }

    private sealed class NoOcr : TarkovCompanion.Core.Abstractions.IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(CapturedImage image, OcrRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrResult([], TimeSpan.Zero, "none", IsAvailable: false, DiagnosticCode: "not_exercised"));
    }
}
