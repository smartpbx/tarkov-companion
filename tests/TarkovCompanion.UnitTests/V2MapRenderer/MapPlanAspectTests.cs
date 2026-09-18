using System.Globalization;
using Avalonia;
using Avalonia.Media;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [V2 rough package 46] Every map is drawn at its own shape.
/// </summary>
/// <remarks>
/// Reported from a live raid: "This map also is the wrong aspect ratio, it's all squished. Fix all
/// the maps." The squash was not in the fit, which has always preserved the plan rectangle's
/// ratio, and it was not in the drawings, which cover the bounds they are placed in. It was in
/// what the rectangle was for a map drawn from PNG tiles. A tile grid is snapped outwards to
/// whole tiles, so it covers up to one whole tile more ground than the map does on each of its
/// four sides, and the levels the loader picks are coarse enough that one tile is a large slice
/// of the map. The coarser the level, the squarer the grid, and at the levels the loader opens on
/// seven of the ten tiled maps came out as a literal square. Measured against the 2026-09-18
/// catalog, worst grid over the levels the tile budget admits, before the fix:
///
/// <code>
///   map             the map   worst grid   out by
///   interchange       1.188        2.000      68%
///   ground-zero       0.713        1.000      40%
///   customs           1.967        1.250      36%
///   shoreline         1.510        1.000      34%
///   icebreaker        0.617        0.800      30%
///   the-lab           1.372        1.000      27%
///   reserve           1.102        1.000       9%
///   factory           0.926        1.000       8%
///   the-labyrinth     1.076        1.000       7%
///   woods             1.038        1.000       4%
/// </code>
///
/// So these are not tests of a fit. They walk the whole catalog, take the drawn rectangle from
/// the real renderer, and compare it with the rectangle the picture covers: the ground the mosaic
/// is now cropped to, or the bounds the drawing is placed into. The tolerance is one pixel of the
/// drawn width, because a squash smaller than that is not a squash.
///
/// They run at 1920x1080 (the screen this runs on) and 3840x1080, turned and unturned, on every
/// floor, on both artworks of the seven maps that publish both, and at every level of each tile
/// pyramid the loader may pick — the fault was invisible on any map whose grid happened to land
/// near its own shape.
/// </remarks>
public sealed class MapPlanAspectTests
{
    private static readonly MapSceneRendererPresentation Presentation =
        MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc);

    private static readonly DateTimeOffset NowUtc = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly (int Width, int Height, double Bearing)[] Cards =
    [
        (1920, 1080, 0),
        (1920, 1080, 90),
        (3840, 1080, 0),
        (3840, 1080, 270),
    ];

    public static TheoryData<string> EveryMap()
    {
        var data = new TheoryData<string>();
        foreach (var location in RealMapCatalog.Load().Locations)
        {
            data.Add(location.Id);
        }

        return data;
    }

    [Fact]
    public void The_catalog_fixture_is_the_whole_catalog()
    {
        // A test that walks "every map" is only worth its name while it really does. Thirteen
        // interactive maps as of 2026-09-18; a new one must be added to the fixture deliberately.
        var catalog = RealMapCatalog.Load();
        Assert.Equal(13, catalog.Locations.Count);
        Assert.Equal(
            ["customs", "factory", "ground-zero", "icebreaker", "interchange", "lighthouse", "reserve",
             "shoreline", "streets-of-tarkov", "terminal", "the-lab", "the-labyrinth", "woods"],
            catalog.Locations.Select(location => location.Id).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EveryMap))]
    public void The_drawn_plan_is_the_shape_the_artwork_is(string mapId)
    {
        var catalog = RealMapCatalog.Load();
        var location = catalog.Locations.Single(item => item.Id == mapId);
        var variant = catalog.Variant(location);
        var checkedSomething = false;

        foreach (var (artwork, zoom) in Artworks(variant))
        {
            var model = catalog.Model(location, artwork, zoom);
            Assert.NotNull(model.Background);
            var expected = IntrinsicAspect(variant, artwork);
            foreach (var floor in Floors(model))
            {
                foreach (var (width, height, bearing) in Cards)
                {
                    var renderer = Render(model, width, height, bearing, floor?.Id);
                    Assert.True(renderer.MapWidth > 0 && renderer.MapHeight > 0);
                    var drawn = renderer.MapWidth / renderer.MapHeight;
                    var offBy = Math.Abs(renderer.MapWidth - (renderer.MapHeight * expected));
                    Assert.True(
                        offBy <= 1,
                        $"{mapId} ({artwork}{(zoom is null ? string.Empty : $" z{zoom}")}, " +
                        $"floor {floor?.Name ?? "-"}, {width}x{height}, {bearing} deg) is drawn " +
                        $"{renderer.MapWidth:F1}x{renderer.MapHeight:F1} = {drawn:F4} where the " +
                        $"artwork is {expected:F4}: {offBy:F1} px of width too {(drawn > expected ? "wide" : "narrow")}.");
                    checkedSomething = true;
                }
            }
        }

        Assert.True(checkedSomething, $"{mapId} published no artwork to check.");
    }

    [Theory]
    [MemberData(nameof(EveryMap))]
    public void The_plan_rectangle_is_the_reviewed_map_on_every_artwork(string mapId)
    {
        // The other half of #413's contract, and the reason the drawn shape can be trusted: the
        // rectangle every object projects into is the rectangle the artwork covers. Checked
        // against the reviewed bounds worked out here from the variant alone, so a change to
        // MapPlanProjection cannot move both sides of the comparison at once.
        var catalog = RealMapCatalog.Load();
        var location = catalog.Locations.Single(item => item.Id == mapId);
        var variant = catalog.Variant(location);

        foreach (var (artwork, zoom) in Artworks(variant))
        {
            var model = catalog.Model(location, artwork, zoom);
            var bounds = RaidCockpitViewModel.PlanBoundsFor(model);
            var reviewed = ReviewedRectangle(variant, artwork);
            Assert.Equal(reviewed.MinimumX, bounds.MinimumX, 6);
            Assert.Equal(reviewed.MinimumY, bounds.MinimumY, 6);
            Assert.Equal(reviewed.MaximumX, bounds.MaximumX, 6);
            Assert.Equal(reviewed.MaximumY, bounds.MaximumY, 6);
        }
    }

    [Fact]
    public void Every_tiled_map_was_drawn_the_wrong_shape_before_the_mosaic_was_cropped()
    {
        // The measurement behind the fix, kept as a test so the next person does not have to take
        // the table in this file's remarks on trust. For each tiled map: how far the tile grid's
        // own rectangle is from the map's, at the worst of the levels the tile budget admits.
        // Snapping outwards does not always move towards a square — Interchange's grid at its
        // coarsest level is 2.000 against a map of 1.188 — it simply stops describing the map.
        var catalog = RealMapCatalog.Load();
        var measured = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var location in catalog.Locations)
        {
            var variant = catalog.Variant(location);
            if (variant.TilePath is null)
            {
                continue;
            }

            var worst = 0d;
            for (var zoom = variant.MinimumZoom; zoom <= variant.MaximumZoom; zoom++)
            {
                var model = catalog.Model(location, MapBackgroundKind.TileTemplate, zoom);
                if (!MapTilePlanner.Plan(variant, zoom.Value, MapTilePlanner.MaximumTilesPerView).IsValid)
                {
                    continue;
                }

                var grid = MapPlanProjection.For(model)!;
                var map = MapPlanProjection.Reviewed(model)!;
                Assert.True(
                    grid.Width >= map.Width - 1e-9 && grid.Height >= map.Height - 1e-9,
                    $"{location.Id} z{zoom}'s grid does not contain its map.");
                worst = Math.Max(worst, Math.Abs((grid.Aspect / map.Aspect) - 1));
            }

            measured[location.Id] = worst;
        }

        var expected = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["interchange"] = 0.68,
            ["ground-zero"] = 0.40,
            ["customs"] = 0.36,
            ["shoreline"] = 0.34,
            ["icebreaker"] = 0.30,
            ["the-lab"] = 0.27,
            ["reserve"] = 0.09,
            ["factory"] = 0.08,
            ["the-labyrinth"] = 0.07,
            ["woods"] = 0.04,
        };
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), measured.Keys.Order(StringComparer.Ordinal));
        foreach (var (mapId, worst) in expected)
        {
            Assert.Equal(worst, measured[mapId], 2);
        }
    }

    [Fact]
    public void The_published_drawing_agrees_with_the_bounds_it_is_placed_in()
    {
        // The other rectangle a drawing could be judged against: its own viewBox. tarkov.dev
        // places the file into the bounds the catalog publishes, and so does this application, so
        // the bounds are where the features are and the viewBox is the author's own rounding of
        // the same rectangle. They agree to within one per cent on all ten published drawings,
        // Customs worst at 0.93% — a fifth of a pixel of stretch on a 1920-wide card. This is the
        // ratchet: a catalog change that really did stretch a drawing would break it here.
        var catalog = RealMapCatalog.Load();
        var checkedAny = 0;
        var worst = (Map: string.Empty, Error: 0d);
        foreach (var location in catalog.Locations)
        {
            var variant = catalog.Variant(location);
            if (variant.SvgPath is null)
            {
                Assert.Null(catalog.SvgAspect(location.Id));
                continue;
            }

            var published = catalog.SvgAspect(location.Id);
            Assert.NotNull(published);
            var placed = ReviewedRectangle(variant, MapBackgroundKind.Svg).Aspect;
            var error = Math.Abs((published.Value / placed) - 1);
            Assert.True(error < 0.01, $"{location.Id}'s drawing is {published:F4} and its bounds are {placed:F4}: {error:P2} apart.");
            worst = error > worst.Error ? (location.Id, error) : worst;
            checkedAny++;
        }

        Assert.Equal(10, checkedAny);
        Assert.Equal("customs", worst.Map);
    }

    /// <summary>Each artwork the map publishes, tiles at both ends of the pyramid the loader may pick.</summary>
    private static IEnumerable<(MapBackgroundKind Artwork, int? Zoom)> Artworks(MapVariant variant)
    {
        if (variant.SvgPath is not null)
        {
            yield return (MapBackgroundKind.Svg, null);
        }

        if (variant.TilePath is null)
        {
            yield break;
        }

        for (var zoom = variant.MinimumZoom; zoom <= variant.MaximumZoom; zoom++)
        {
            if (MapTilePlanner.Plan(variant, zoom.Value, MapTilePlanner.MaximumTilesPerView).IsValid)
            {
                yield return (MapBackgroundKind.TileTemplate, zoom);
            }
        }
    }

    private static IReadOnlyList<MapFloorDefinition?> Floors(MapRenderModel model) =>
        model.Floors.Count == 0 ? [null] : [.. model.Floors.Cast<MapFloorDefinition?>()];

    /// <summary>
    /// The shape the picture on screen actually is.
    /// </summary>
    /// <remarks>
    /// One rectangle answers for both artworks, and that is the point of it. The tile mosaic is
    /// composed cropped to exactly this ground, so its pixels are this shape. A drawing is placed
    /// into the bounds it is published with, the way tarkov.dev's own image overlay places it, so
    /// this is where its features are — see
    /// <see cref="The_published_drawing_agrees_with_the_bounds_it_is_placed_in"/> for how closely
    /// the published <c>viewBox</c> follows.
    /// </remarks>
    private static double IntrinsicAspect(MapVariant variant, MapBackgroundKind artwork)
    {
        var reviewed = ReviewedRectangle(variant, artwork);
        return reviewed.Width / reviewed.Height;
    }

    /// <summary>The ground the artwork covers, from the variant alone.</summary>
    private static MapPlanRect ReviewedRectangle(MapVariant variant, MapBackgroundKind artwork)
    {
        var bounds = artwork == MapBackgroundKind.TileTemplate
            ? variant.Bounds!
            : variant.SvgBounds ?? variant.Bounds!;
        var corners = new[]
        {
            new WorldPosition(bounds.First.X, 0, bounds.First.Y),
            new WorldPosition(bounds.First.X, 0, bounds.Second.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.First.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.Second.Y),
        };
        var projected = corners.Select(corner =>
        {
            Assert.True(variant.Transform!.TryProject(corner, out var point));
            return point;
        }).ToArray();
        return new(
            projected.Min(point => point.X),
            projected.Min(point => point.Y),
            projected.Max(point => point.X),
            projected.Max(point => point.Y));
    }

    private static MapSceneRendererViewModel Render(
        MapRenderModel model,
        int width,
        int height,
        double bearingDegrees,
        string? floorId)
    {
        var bounds = RaidCockpitViewModel.PlanBoundsFor(model);
        var camera = new MapSceneCamera(
            bounds.MinimumX + (bounds.Width / 2),
            bounds.MinimumY + (bounds.Height / 2),
            1,
            bearingDegrees,
            0);
        var result = new MapSceneAssembler().Build(new(
            1,
            model,
            bounds,
            model.Variant.Key,
            new(MapSceneMode.Flat2D, floorId ?? model.SelectedFloor?.Id, camera, []),
            [],
            [],
            [],
            [Asset(model)]));
        var scene = Assert.IsType<MapSceneSnapshot>(result.Scene);
        // The artwork the host would have decoded: one picture covering the plan rectangle, at
        // the size the composer or the rasterizer would have produced for it.
        var renderer = new MapSceneRendererViewModel(
            scene,
            Presentation,
            reviewedAssetResolver: _ => new TestArtwork(new(2048, 2048 / bounds.Width * bounds.Height)));
        renderer.SetViewportSize(width, height);
        return renderer;
    }

    private static MapSceneAsset Asset(MapRenderModel model) => new(
        new($"asset:{model.Location.Id}"),
        MapSceneAssetKind.Background2D,
        model.Background!.SourceUri,
        model.LicenseUri,
        new string('c', 64),
        "tarkov.dev contributors",
        model.Variant.Key,
        "current",
        MapSceneAssetReviewStatus.Reviewed,
        NowUtc);

    private sealed class TestArtwork(Size size) : IImage
    {
        public Size Size { get; } = size;

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }
    }
}
