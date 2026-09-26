using System.Runtime.InteropServices;
using SkiaSharp;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Core.Domain.Recognition.Learning;
using TarkovCompanion.Core.Domain.Stash;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Learning;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;

namespace TarkovCompanion.UnitTests.Recognition;

/// <summary>
/// #712 1-12: a correction is remembered and used by the next read, never turns a right name
/// wrong, and Delete all forgets it. Synthetic icons only; no game art or screenshot.
/// </summary>
public sealed class CorrectionMemoryTests : IAsyncLifetime
{
    private const int Pitch = 63;
    private const int OriginX = 1260;
    private const int OriginY = 180;
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "correction-memory-" + Guid.NewGuid().ToString("N"));
    private SqliteConnectionFactory _factory = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(new(Path.Combine(_directory, "companion.db")));
        await new SqliteMigrationRunner(_factory).ApplyAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_corrected_cell_is_named_by_its_learned_crop_on_the_next_read()
    {
        // Two items drawn with the same art: the catalog can only refuse, with both as lookalikes.
        var (builder, memory, _) = await ArrangeAsync(("key-a", 1), ("key-b", 1));
        var first = Assert.Single((await builder.BuildAsync(Frame(Icon(1)), InventoryGridSurface.VisibleLoot, Now)).OccupiedCells);
        Assert.Null(first.Item.Value);
        Assert.Equal(["key-a", "key-b"], first.Item.Candidates.Select(candidate => candidate.CandidateId).Order().ToArray());

        Assert.True(await memory.LearnIconAsync(Now, first.Item.Bounds!, "key-a", 1, 1, CancellationToken.None));

        var later = Now.AddMinutes(5);
        var second = Assert.Single((await builder.BuildAsync(Frame(Icon(1)), InventoryGridSurface.VisibleLoot, later)).OccupiedCells);
        Assert.Equal("key-a", second.Item.Value!.CanonicalId.Value);
        Assert.Equal("grid.cell.item.learned", second.Item.Status.Code);
    }

    [Fact]
    public async Task A_learned_crop_never_changes_a_cell_the_catalog_named()
    {
        var (builder, memory, crops) = await ArrangeAsync(("item-true", 1), ("item-other", 2), ("item-twin", 3), ("item-twin-b", 3));
        // Teach "item-other" with a crop that is exactly the picture of item-true.
        var refused = Assert.Single((await builder.BuildAsync(Frame(Icon(3)), InventoryGridSurface.VisibleLoot, Now)).OccupiedCells);
        Assert.Null(refused.Item.Value);
        crops.Put(IconCropKey.For(Now, refused.Item.Bounds!), Crop(Frame(Icon(1)), refused.Item.Bounds!));
        Assert.True(await memory.LearnIconAsync(Now, refused.Item.Bounds!, "item-other", 1, 1, CancellationToken.None));

        var read = Assert.Single((await builder.BuildAsync(Frame(Icon(1)), InventoryGridSurface.VisibleLoot, Now.AddMinutes(1))).OccupiedCells);

        Assert.Equal("item-true", read.Item.Value!.CanonicalId.Value);
        Assert.Equal("grid.cell.item.separated", read.Item.Status.Code);
    }

    [Fact]
    public async Task A_learned_crop_does_not_name_a_cell_it_does_not_look_like()
    {
        var (builder, memory, _) = await ArrangeAsync(("key-a", 1), ("key-b", 1), ("item-c", 9), ("item-d", 9));
        var first = Assert.Single((await builder.BuildAsync(Frame(Icon(1)), InventoryGridSurface.VisibleLoot, Now)).OccupiedCells);
        Assert.True(await memory.LearnIconAsync(Now, first.Item.Bounds!, "key-a", 1, 1, CancellationToken.None));

        var other = Assert.Single((await builder.BuildAsync(Frame(Icon(9)), InventoryGridSurface.VisibleLoot, Now.AddMinutes(1))).OccupiedCells);

        Assert.Null(other.Item.Value);
    }

    [Fact]
    public async Task With_crops_turned_off_nothing_is_kept()
    {
        var layout = new MemoryLayout();
        var (builder, memory, _) = await ArrangeAsync(layout, ("key-a", 1), ("key-b", 1));
        memory.KeepsIconCrops = false;
        Assert.Equal("off", layout.Get(WorkspaceLayoutKeys.LearnIconCrops));
        var first = Assert.Single((await builder.BuildAsync(Frame(Icon(1)), InventoryGridSurface.VisibleLoot, Now)).OccupiedCells);

        Assert.False(await memory.LearnIconAsync(Now, first.Item.Bounds!, "key-a", 1, 1, CancellationToken.None));
        Assert.Equal(0, (await memory.CountAsync(CancellationToken.None)).Icons);
    }

    [Fact]
    public async Task Delete_all_forgets_crops_names_and_kept_corrections_and_the_matcher_stops_using_them()
    {
        var (builder, memory, _) = await ArrangeAsync(("key-a", 1), ("key-b", 1));
        var first = Assert.Single((await builder.BuildAsync(Frame(Icon(1)), InventoryGridSurface.VisibleLoot, Now)).OccupiedCells);
        Assert.True(await memory.LearnIconAsync(Now, first.Item.Bounds!, "key-a", 1, 1, CancellationToken.None));
        await memory.RecordNamePickAsync("grophics cord", "gpu", "Graphics card", CancellationToken.None);
        await memory.RecordNamePickAsync("grophics cord", "gpu", "Graphics card", CancellationToken.None);
        await memory.RecordFrameCorrectionAsync("frame-1", "cell:cell-001-001", "key-a", CancellationToken.None);
        Assert.Equal(new CorrectionMemoryCounts(1, 1, 1), await memory.CountAsync(CancellationToken.None));

        await memory.ClearAllAsync(CancellationToken.None);

        Assert.Equal(CorrectionMemoryCounts.None, await memory.CountAsync(CancellationToken.None));
        var again = Assert.Single((await builder.BuildAsync(Frame(Icon(1)), InventoryGridSurface.VisibleLoot, Now.AddMinutes(1))).OccupiedCells);
        Assert.Null(again.Item.Value);
    }

    [Fact]
    public async Task A_name_picked_twice_becomes_an_alias_the_name_reader_uses()
    {
        var store = new SqliteCorrectionMemoryStore(_factory);
        var catalog = new FixedCatalog(new CanonicalItemReference("gpu", "Graphics card"), new CanonicalItemReference("drill", "Electric drill"));
        var resolvers = new CanonicalItemResolverCache(catalog, learned: store);
        var memory = new CorrectionMemory(store, resolvers: resolvers, clock: new FixedClock());
        const string reading = "qzxv wbnk";
        Assert.Empty((await resolvers.GetAsync(CancellationToken.None)).Resolve(reading, null).Candidates);

        Assert.False((await memory.RecordNamePickAsync(reading, "gpu", "Graphics card", CancellationToken.None))!.IsActive);
        Assert.Empty((await resolvers.GetAsync(CancellationToken.None)).Resolve(reading, null).Candidates);
        Assert.True((await memory.RecordNamePickAsync(reading, "gpu", "Graphics card", CancellationToken.None))!.IsActive);

        var resolved = (await resolvers.GetAsync(CancellationToken.None)).Resolve(reading, null);
        Assert.Equal("gpu", resolved.Candidates[0].CanonicalId);
        Assert.Equal("Graphics card", resolved.Candidates[0].DisplayName);
    }

    [Fact]
    public async Task Frame_corrections_round_trip_and_a_later_one_replaces_an_earlier()
    {
        var store = new SqliteCorrectionMemoryStore(_factory);
        await store.SetFrameCorrectionAsync("frame", "cell:a", "one", Now, CancellationToken.None);
        await store.SetFrameCorrectionAsync("frame", "cell:a", "two", Now, CancellationToken.None);
        await store.SetFrameCorrectionAsync("frame", "flea:item", "three", Now, CancellationToken.None);

        var kept = await store.ListFrameCorrectionsAsync("frame", CancellationToken.None);

        Assert.Equal("two", kept["cell:a"]);
        Assert.Equal("three", kept["flea:item"]);
        Assert.Empty(await store.ListFrameCorrectionsAsync("other", CancellationToken.None));
    }

    [Fact]
    public async Task An_item_keeps_only_its_newest_crops()
    {
        var store = new SqliteCorrectionMemoryStore(_factory);
        for (var index = 0; index < SqliteCorrectionMemoryStore.MaximumIconsPerItem + 3; index++)
        {
            await store.AddIconAsync(new(Guid.NewGuid(), "item", 1, 1, new byte[] { 1, 2, 3 }, Now.AddSeconds(index)), CancellationToken.None);
        }

        var icons = await store.ListIconsAsync(CancellationToken.None);
        Assert.Equal(SqliteCorrectionMemoryStore.MaximumIconsPerItem, icons.Count);
        Assert.Equal(Now.AddSeconds(3), icons.Min(icon => icon.CreatedUtc));
    }

    [Fact]
    public async Task Migration_0020_rolls_back_and_applies_again()
    {
        var store = new SqliteCorrectionMemoryStore(_factory);
        await store.SetFrameCorrectionAsync("frame", "cell:a", "one", Now, CancellationToken.None);
        await using (var connection = await _factory.OpenAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = SqliteMigrationRunner.ReadFixture("0020_correction_memory").RollbackSql +
                " DELETE FROM schema_migrations WHERE version = '0020_correction_memory';";
            await command.ExecuteNonQueryAsync();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'learned_%';";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }

        var again = await new SqliteMigrationRunner(_factory).ApplyAsync(CancellationToken.None);

        Assert.Equal(["0020_correction_memory"], again.Applied);
        Assert.Equal(CorrectionMemoryCounts.None, await store.CountAsync(CancellationToken.None));
    }

    [Fact]
    public void The_policy_refuses_a_tie_a_weak_crop_and_an_item_its_own_art_disowns()
    {
        var catalog = new Dictionary<string, double> { ["a"] = 0.8, ["b"] = 0.79, ["c"] = 0.2 };

        Assert.Equal("a", LearnedIconMatchPolicy.Choose(catalog, new Dictionary<string, double> { ["a"] = 0.97, ["b"] = 0.5 }));
        Assert.Null(LearnedIconMatchPolicy.Choose(catalog, new Dictionary<string, double> { ["a"] = 0.97, ["b"] = 0.95 }));
        Assert.Null(LearnedIconMatchPolicy.Choose(catalog, new Dictionary<string, double> { ["a"] = 0.97, ["b"] = 0.97 }));
        Assert.Null(LearnedIconMatchPolicy.Choose(catalog, new Dictionary<string, double> { ["a"] = 0.85 }));
        Assert.Null(LearnedIconMatchPolicy.Choose(catalog, new Dictionary<string, double> { ["c"] = 0.99 }));
        Assert.Null(LearnedIconMatchPolicy.Choose(catalog, new Dictionary<string, double> { ["unknown"] = 0.99 }));
    }

    [Fact]
    public async Task A_cell_can_only_be_named_as_one_of_its_lookalikes_and_says_the_player_named_it()
    {
        var (builder, _, _) = await ArrangeAsync(("key-a", 1), ("key-b", 1));
        var request = await builder.BuildAsync(Frame(Icon(1)), InventoryGridSurface.VisibleLoot, Now);
        var cell = Assert.Single(request.OccupiedCells);

        Assert.Null(GridCellCorrections.Apply(cell, "not-a-lookalike"));
        var named = GridCellCorrections.Apply(cell, "key-b")!;
        Assert.Equal("key-b", named.Item.Value!.CanonicalId.Value);
        Assert.Equal(EvidenceSourceClass.UserEntered, named.Item.Provenance.SourceClass);
        Assert.Equal(cell.Item.Provenance.ObservedUtc, named.Item.Provenance.ObservedUtc);

        var kept = GridCellCorrections.Apply(request, new Dictionary<string, string> { [GridCellCorrections.TargetKey(cell)] = "key-a" });
        Assert.Equal("key-a", Assert.Single(kept.OccupiedCells).Item.Value!.CanonicalId.Value);
    }

    [Fact]
    public void A_flea_correction_puts_the_chosen_item_first_and_names_the_reading_it_corrects()
    {
        var identified = new[]
        {
            new CaptureIdentifiedItem("wrong", "Wrong", new Confidence(0.9), "ocr-fuzzy; observed=grophics cord; matched=Graphic; similarity=0.8"),
            new CaptureIdentifiedItem("gpu", "Graphics card", new Confidence(0.8), "ocr-fuzzy; observed=grophics cord; matched=Graphics card"),
        };

        var picked = FleaItemCorrection.PutFirst(identified, "gpu");
        Assert.Equal(["gpu", "wrong"], picked.Select(item => item.CanonicalId));
        Assert.Equal(1, picked[0].Confidence.Value);
        Assert.Same(picked, FleaItemCorrection.PutFirst(picked, "gpu"));
        Assert.Same(identified, FleaItemCorrection.PutFirst(identified, "absent"));
        Assert.Equal("grophics cord", FleaItemCorrection.ObservedText(identified[0].Evidence));
        Assert.Null(FleaItemCorrection.ObservedText("icon-match"));
    }

    [Fact]
    public void A_stash_identity_correction_is_applied_back_and_counted()
    {
        var provenance = new EvidenceProvenance(EvidenceSourceClass.GameWrittenScreenshot, "fixture", Now, EvidenceConfidence.Unscored, new ProducerIdentity("fixture", "1"));
        var unknown = new StashReconstructedTile("stash", 0, 0, 1, 1, null, null, null, ["Graphics card?"], provenance);
        var known = new StashReconstructedTile("stash", 0, 1, 1, 1, "drill", "Electric drill", 1, [], provenance);
        var reconstruction = new StashReconstruction(
            [new StashReconstructedContainer("stash", 1, 2, [unknown, known])],
            0,
            1,
            1,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["drill"] = 1 });
        var commands = new[]
        {
            new StashReviewCommand(Guid.NewGuid(), "snap", StashReviewActionKind.CorrectItemIdentity, [unknown.ItemKey], Now, "v2.stash-workspace", correctedItemId: "gpu"),
            new StashReviewCommand(Guid.NewGuid(), "snap", StashReviewActionKind.CorrectQuantity, [unknown.ItemKey], Now.AddSeconds(1), "v2.stash-workspace", correctedQuantity: 2),
            new StashReviewCommand(Guid.NewGuid(), "snap", StashReviewActionKind.CorrectItemIdentity, [known.ItemKey], Now.AddSeconds(2), "v2.stash-workspace", correctedItemId: StashReviewCorrections.Unknown),
        };

        var applied = StashReviewCorrections.Apply(reconstruction, StashReviewCommandProjection.Project(commands));

        var tiles = applied.Containers.Single().Tiles;
        Assert.Equal("gpu", tiles[0].ItemId);
        Assert.Equal(2, tiles[0].Quantity);
        Assert.Null(tiles[1].ItemId);
        Assert.Equal(1, applied.KnownTiles);
        Assert.Equal(1, applied.UnknownTiles);
        Assert.Equal(2, applied.OwnedCounts["gpu"]);
        Assert.False(applied.OwnedCounts.ContainsKey("drill"));
    }

    private Task<(GridPixelReconstructionBuilder Builder, CorrectionMemory Memory, RecentIconCrops Crops)> ArrangeAsync(
        params (string Id, int Seed)[] references) => ArrangeAsync(new MemoryLayout(), references);

    private async Task<(GridPixelReconstructionBuilder Builder, CorrectionMemory Memory, RecentIconCrops Crops)> ArrangeAsync(
        MemoryLayout layout,
        params (string Id, int Seed)[] references)
    {
        var cache = new FileIconEvidenceCache(new FileIconEvidenceCacheOptions(Path.Combine(_directory, "icons")));
        foreach (var (id, seed) in references)
        {
            await StoreAsync(cache, id, Icon(seed));
        }

        var items = new FixedItems(references.Select(reference => reference.Id).ToArray());
        var store = new SqliteCorrectionMemoryStore(_factory);
        var index = new IconReferenceIndex(cache, items, store);
        var crops = new RecentIconCrops();
        var builder = new GridPixelReconstructionBuilder(cache, items, new NoOcr(), referenceIndex: index, recentCrops: crops);
        var memory = new CorrectionMemory(store, index, crops, layout: layout, clock: new FixedClock());
        return (builder, memory, crops);
    }

    private static CapturedImage Crop(CapturedImage image, EvidenceRegion region)
    {
        var (width, height) = (region.Width + 1, region.Height + 1);
        var buffer = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            image.Pixels.Span.Slice(((region.Y + row) * image.Stride) + (region.X * 4), width * 4).CopyTo(buffer.AsSpan(row * width * 4));
        }

        return new(buffer, width, height, width * 4, image.Format, Now, "crop");
    }

    /// <summary>A 64x64 cell picture: a bordered dark tile with a seeded blocky pattern on it.</summary>
    private static CapturedImage Icon(int seed)
    {
        const int size = Pitch + 1;
        var pixels = new byte[size * size * 4];
        var random = new Random(seed);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                Set(pixels, size, x, y, 30, 32, 34);
            }
        }

        for (var block = 0; block < 14; block++)
        {
            var left = random.Next(6, 46);
            var top = random.Next(6, 46);
            var (red, green, blue) = ((byte)random.Next(60, 255), (byte)random.Next(60, 255), (byte)random.Next(60, 255));
            for (var y = top; y < top + 12; y++)
            {
                for (var x = left; x < left + 12; x++)
                {
                    Set(pixels, size, x, y, red, green, blue);
                }
            }
        }

        for (var edge = 0; edge < size; edge++)
        {
            Set(pixels, size, edge, 0, 73, 81, 84);
            Set(pixels, size, edge, size - 1, 73, 81, 84);
            Set(pixels, size, 0, edge, 73, 81, 84);
            Set(pixels, size, size - 1, edge, 73, 81, 84);
        }

        return new(pixels, size, size, size * 4, PixelFormat.Bgra8888, Now, "test-icon");
    }

    /// <summary>A 1080p frame holding a 4x3 container with the icon in its second row.</summary>
    private static CapturedImage Frame(CapturedImage icon)
    {
        const int width = 1920;
        const int height = 1080;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Set(pixels, width, x, y, 14, 14, 14);
            }
        }

        for (var y = OriginY; y <= OriginY + (3 * Pitch); y++)
        {
            for (var x = OriginX; x <= OriginX + (4 * Pitch); x++)
            {
                var onLine = (x - OriginX) % Pitch == 0 || (y - OriginY) % Pitch == 0;
                Set(pixels, width, x, y, onLine ? (byte)73 : (byte)24, onLine ? (byte)81 : (byte)25, onLine ? (byte)84 : (byte)25);
            }
        }

        var source = icon.Pixels.Span;
        for (var y = 0; y < icon.Height; y++)
        {
            source.Slice(y * icon.Stride, icon.Width * 4)
                .CopyTo(pixels.AsSpan(((OriginY + Pitch + y) * width * 4) + ((OriginX + Pitch) * 4), icon.Width * 4));
        }

        return new(pixels, width, height, width * 4, PixelFormat.Bgra8888, Now, "test-frame");
    }

    private static void Set(byte[] pixels, int width, int x, int y, byte red, byte green, byte blue)
    {
        var offset = ((y * width) + x) * 4;
        pixels[offset] = blue;
        pixels[offset + 1] = green;
        pixels[offset + 2] = red;
        pixels[offset + 3] = 255;
    }

    private static async Task StoreAsync(FileIconEvidenceCache cache, string itemId, CapturedImage icon)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(icon.Width, icon.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(icon.Pixels.ToArray(), 0, bitmap.GetPixels(), icon.Pixels.Length);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        await cache.StoreAsync(
            new IconContentWriteRequest(
                new IconEvidenceKey(itemId, new Uri($"https://example.invalid/icons/{itemId}.png")),
                Now,
                new EvidenceProvenance(
                    EvidenceSourceClass.PublicStructuredData,
                    "fixture://icons",
                    Now,
                    EvidenceConfidence.Certain,
                    new ProducerIdentity("fixture", "1")),
                data.ToArray()),
            CancellationToken.None);
    }

    private sealed class FixedItems(string[] ids) : IItemRepository
    {
        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(ids.Contains(itemId)
                ? new ItemDefinition(
                    itemId,
                    "Name of " + itemId,
                    itemId,
                    string.Empty,
                    ItemCategory.Barter,
                    new ItemDimensions(1, 1),
                    true,
                    null,
                    null,
                    null,
                    null,
                    null,
                    new HashSet<string>(),
                    new DataProvenance("fixture", Now))
                : null);

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(null);
    }

    private sealed class FixedCatalog(params CanonicalItemReference[] items) : IRecognitionCatalogRepository
    {
        public Task<IReadOnlyList<CanonicalItemReference>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CanonicalItemReference>>(items);
    }

    private sealed class NoOcr : IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(CapturedImage image, OcrRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrResult([], TimeSpan.Zero, "none", IsAvailable: false, DiagnosticCode: "not_exercised"));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryLayout : IWorkspaceLayoutStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string value) => _values[key] = value;
    }
}
