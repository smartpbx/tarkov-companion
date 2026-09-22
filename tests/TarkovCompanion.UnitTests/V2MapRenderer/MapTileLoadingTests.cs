using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// The two pieces of arithmetic behind a photographed map that appears at once: where a coarse
/// tile goes on a sharper level's canvas, and which decoded tiles are kept between visits.
/// </summary>
public sealed class MapTileLoadingTests
{
    [Fact]
    public void Decoded_tile_cache_is_bounded_to_two_maximum_size_sharp_tile_sets()
    {
        const long decodedTileBytes = 256L * 256 * 4;

        Assert.Equal(
            2L * MapTilePlanner.MaximumTilesPerView * decodedTileBytes,
            MapViewModel.DecodedTileCacheCapacityBytes);
    }

    [Fact]
    public void Every_photographed_map_has_a_soft_picture_of_a_few_tiles_that_covers_it()
    {
        var catalog = RealMapCatalog.Load();
        var checkedMaps = 0;
        foreach (var location in catalog.Locations)
        {
            var variant = catalog.Variant(location);
            if (variant.TilePath is null)
            {
                continue;
            }

            var zoom = MapCanvasCoordinateMapper.ChooseTileZoom(variant);
            var sharp = MapTilePlanner.Plan(variant, zoom, MapTilePlanner.MaximumTilesPerView);
            var coarse = MapTileUnderlay.Plan(variant, zoom);
            if (!sharp.IsValid || sharp.Tiles.Count <= MapTileUnderlay.MaximumTiles)
            {
                continue;
            }

            var levels = string.Join(", ", Enumerable.Range(variant.MinimumZoom ?? 0, (variant.MaximumZoom ?? 0) - (variant.MinimumZoom ?? 0) + 1)
                .Select(level => $"z{level}={MapTilePlanner.Plan(variant, level, 100000).Tiles.Count}"));
            Assert.True(coarse is { IsValid: true }, $"{location.Id} has no level coarse enough to show first (sharp z{zoom}; {levels}).");
            var placed = MapTileUnderlay.Place(coarse, sharp);
            Assert.InRange(placed.Count, 1, MapTileUnderlay.MaximumTiles);

            // Together the soft tiles cover the whole sharp canvas, and each one lands on the sharp
            // level's own tile lines: a soft picture half a tile out is a map that visibly jumps
            // when the sharp tiles land on it.
            Assert.True(placed.Min(tile => tile.Left) <= 0 && placed.Min(tile => tile.Top) <= 0, location.Id);
            Assert.True(placed.Max(tile => tile.Left + tile.Size) >= sharp.Width, location.Id);
            Assert.True(placed.Max(tile => tile.Top + tile.Size) >= sharp.Height, location.Id);
            var tileSize = sharp.Tiles[0].Size;
            Assert.All(placed, tile =>
            {
                Assert.Equal(0, tile.Left % tileSize, 9);
                Assert.Equal(0, tile.Top % tileSize, 9);
            });
            checkedMaps++;
        }

        Assert.True(checkedMaps >= 8, $"only {checkedMaps} photographed maps were checked");
    }

    [Fact]
    public void A_soft_tile_covers_exactly_the_sharp_tiles_it_is_a_picture_of()
    {
        var catalog = RealMapCatalog.Load();
        var customs = catalog.Variant(catalog.Locations.Single(location => location.Id == "customs"));
        var zoom = MapCanvasCoordinateMapper.ChooseTileZoom(customs);
        var sharp = MapTilePlanner.Plan(customs, zoom, MapTilePlanner.MaximumTilesPerView);
        var coarse = MapTileUnderlay.Plan(customs, zoom)!;
        var factor = 1 << (zoom - coarse.Tiles[0].Zoom);

        foreach (var soft in MapTileUnderlay.Place(coarse, sharp))
        {
            // In a tile pyramid, tile (x, y) one level down is tiles (2x..2x+1, 2y..2y+1) here.
            var covered = sharp.Tiles.Where(tile =>
                tile.X / factor == soft.Tile.X && tile.Y / factor == soft.Tile.Y && tile.X >= 0 && tile.Y >= 0);
            Assert.All(covered, tile =>
            {
                Assert.InRange(tile.Left, soft.Left, soft.Left + soft.Size - tile.Size);
                Assert.InRange(tile.Top, soft.Top, soft.Top + soft.Size - tile.Size);
            });
        }
    }

    [Fact]
    public void The_least_recently_used_tiles_go_first_when_the_budget_is_full()
    {
        var evicted = new List<string>();
        var cache = new BoundedLruCache<string, string>(30, _ => 10, evicted.Add);
        HashSet<string> none = [];

        cache.GetOrAdd("a", "A", none);
        cache.GetOrAdd("b", "B", none);
        cache.GetOrAdd("c", "C", none);
        Assert.True(cache.TryGet("a", out _));
        cache.GetOrAdd("d", "D", none);

        Assert.Equal(["B"], evicted);
        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.Equal(30, cache.Bytes);
    }

    [Fact]
    public void A_tile_on_screen_is_never_evicted_even_over_budget()
    {
        var evicted = new List<string>();
        var cache = new BoundedLruCache<string, string>(20, _ => 10, evicted.Add);
        HashSet<string> onScreen = ["a", "b", "c"];

        cache.GetOrAdd("a", "A", onScreen);
        cache.GetOrAdd("b", "B", onScreen);
        cache.GetOrAdd("c", "C", onScreen);

        Assert.Empty(evicted);
        Assert.Equal(3, cache.Count);

        // The next map's tiles push the old map's out, oldest first, and keep their own.
        HashSet<string> next = ["d"];
        cache.GetOrAdd("d", "D", next);
        Assert.Equal(["A", "B"], evicted);
    }

    [Fact]
    public void A_tile_decoded_twice_keeps_the_first_bitmap_and_drops_the_second()
    {
        var evicted = new List<string>();
        var cache = new BoundedLruCache<string, string>(100, _ => 10, evicted.Add);
        HashSet<string> none = [];

        var first = cache.GetOrAdd("a", "first", none);
        var second = cache.GetOrAdd("a", "second", none);

        Assert.Equal("first", first);
        Assert.Equal("first", second);
        Assert.Equal(["second"], evicted);
        Assert.Equal(10, cache.Bytes);
    }
}
