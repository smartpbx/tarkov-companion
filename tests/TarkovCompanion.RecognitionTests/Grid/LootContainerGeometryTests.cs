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

    [Fact]
    public void PicksOnePanelWhenSeveralShareThePitchAndNeverALatticeAcrossThem()
    {
        // A real character screen: gear slots, pockets, a backpack and the stash, all at 63
        // pixels and each at its own phase. On real frames the open search walked from one panel
        // into the next and returned 19 columns of 65-pixel cells. Clean drawn lines do not
        // reproduce that (it needs the clutter of a real interface, and the old search passes
        // this too), so this pins the outcome: one panel, the one with the most line, exactly.
        var frame = new LineFrame(3840, 1080);
        frame.Panel(originX: 1616, originY: 330, columns: 5, rows: 4);
        frame.Panel(originX: 2225, originY: 79, columns: 10, rows: 14);

        var spec = Assert.IsType<ContainerGridSpec>(new ContainerGridDetector().Detect(frame.ToImage()));

        Assert.Equal(new PixelRect(2225, 79, 630, 882), spec.Bounds);
        Assert.Equal(10, spec.Columns);
        Assert.Equal(14, spec.Rows);
    }

    [Fact]
    public void ABorderDrawnOnePixelOutsideTheCellsDoesNotMoveTheLattice()
    {
        // The stash's outer frame sits a pixel left of the lattice its cells are on. A lattice
        // anchored on it is a pixel out everywhere, which is enough to change every fingerprint:
        // on real frames that named 3 to 7 items where the right phase names 11 to 13.
        var frame = new LineFrame(3840, 1080);
        frame.Panel(originX: 2225, originY: 79, columns: 10, rows: 14);
        frame.VerticalLine(2224, 79, 882);

        var spec = Assert.IsType<ContainerGridSpec>(new ContainerGridDetector().Detect(frame.ToImage()));

        Assert.Equal(2225, spec.Bounds.X);
        Assert.Equal(630, spec.Bounds.Width);
    }

    [Fact]
    public void ARunAtTheKnownPitchCannotBorrowTheRowsOfAWindowDrawnSixPixelsOff()
    {
        // A case window open over the character screen: its frame is a long line 6 pixels above
        // the rows of the case inside it. Stepping at 65 instead of 63, a run that started on the
        // frame caught up with the real rows and was then laid back down at the frame's phase.
        var frame = new LineFrame(3840, 1080);
        frame.Panel(originX: 1518, originY: 149, columns: 14, rows: 12);
        frame.HorizontalLine(143, 1518, 882);
        frame.HorizontalLine(80, 1518, 882);

        var spec = Assert.IsType<ContainerGridSpec>(new ContainerGridDetector().Detect(frame.ToImage()));

        Assert.Equal(149, spec.Bounds.Y);
        Assert.Equal(1518, spec.Bounds.X);
    }

    [Fact]
    public async Task ABlockOfEmptyCellsWithNoLineReadBetweenThemIsNotAnItem()
    {
        // On a real panel the line between two empty cells is too faint for the border probe, so
        // a run of empty cells joins into one block. That is still nothing.
        var frame = new LineFrame(1920, 1080);
        frame.Panel(originX: 1260, originY: 180, columns: 5, rows: 4);
        frame.EraseInnerLines(originX: 1260, originY: 180, row: 2, column: 1, width: 3, height: 2);
        var builder = new GridPixelReconstructionBuilder(new NoIcons(), new NoItems(), new NoOcr());

        var request = await builder.BuildAsync(frame.ToImage(), InventoryGridSurface.VisibleLoot, DateTimeOffset.UtcNow);

        Assert.NotNull(request.Lattice);
        Assert.Empty(request.OccupiedCells);
    }

    /// <summary>A dark frame with bright one-pixel lines, which is what the game draws.</summary>
    private sealed class LineFrame(int width, int height)
    {
        private const int Pitch = 63;
        private readonly byte[] _pixels = CreateBackground(width, height);

        public void Panel(int originX, int originY, int columns, int rows)
        {
            for (var column = 0; column <= columns; column++)
            {
                VerticalLine(originX + (column * Pitch), originY, rows * Pitch);
            }

            for (var row = 0; row <= rows; row++)
            {
                HorizontalLine(originY + (row * Pitch), originX, columns * Pitch);
            }
        }

        public void VerticalLine(int x, int y, int length)
        {
            for (var offset = 0; offset <= length; offset++)
            {
                Set(x, y + offset, 80);
            }
        }

        public void HorizontalLine(int y, int x, int length)
        {
            for (var offset = 0; offset <= length; offset++)
            {
                Set(x + offset, y, 80);
            }
        }

        public void EraseInnerLines(int originX, int originY, int row, int column, int width, int height)
        {
            for (var y = originY + (row * Pitch) + 1; y < originY + ((row + height) * Pitch); y++)
            {
                for (var x = originX + (column * Pitch) + 1; x < originX + ((column + width) * Pitch); x++)
                {
                    Set(x, y, 24);
                }
            }
        }

        public CapturedImage ToImage() =>
            new(_pixels, width, height, width * 4, PixelFormat.Bgra8888, DateTimeOffset.UtcNow, "line-frame");

        private void Set(int x, int y, byte luminance)
        {
            var offset = ((y * width) + x) * 4;
            _pixels[offset] = _pixels[offset + 1] = _pixels[offset + 2] = luminance;
        }

        private static byte[] CreateBackground(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 24;
                pixels[offset + 3] = 255;
            }

            return pixels;
        }
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
