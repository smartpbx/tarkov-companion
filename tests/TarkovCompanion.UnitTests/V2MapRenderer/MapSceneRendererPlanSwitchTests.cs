using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// The rectangle the plan is drawn into follows the map on screen, not the one before it.
/// </summary>
/// <remarks>
/// Reported on build 11: Customs opened skewed, and after Customs was put right (by toggling
/// Drawing) Factory opened drawn to Customs' aspect ratio. The renderer rebuilt its projection when
/// a scene's bounds changed but never raised MapLeft/MapTop/MapWidth/MapHeight, which the view
/// binds to; they were raised only when the card was resized. The view therefore kept the previous
/// map's rectangle, and anything that happened to resize the card "fixed" it. These read what the
/// view reads: the last value a binding was told about, never the getter it would only see by
/// asking again.
/// </remarks>
public sealed class MapSceneRendererPlanSwitchTests
{
    private static readonly MapSceneRendererPresentation Presentation =
        MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc);

    private static readonly MapSceneBounds CustomsPlan = new(0, 0, 1967, 1000);
    private static readonly MapSceneBounds FactoryPlan = new(0, 0, 1000, 800);

    [Fact]
    public void Going_from_Customs_to_Factory_draws_Factory_at_Factorys_own_shape()
    {
        var renderer = Renderer(Scene("customs", "customs-plan", CustomsPlan));
        var view = new BoundView(renderer);
        Assert.Equal(CustomsPlan.Width / CustomsPlan.Height, view.Aspect, 2);

        renderer.Present(Scene("factory", "factory-plan", FactoryPlan, revision: 2));

        view.AssertMatchesTheViewModel(renderer);
        Assert.True(
            Math.Abs(view.Height - (view.Width / (FactoryPlan.Width / FactoryPlan.Height))) < 1,
            $"Factory is {FactoryPlan.Width / FactoryPlan.Height:F3} wide-to-tall but the view was told {view.Width:F1}x{view.Height:F1}");
    }

    [Fact]
    public void Switching_back_and_forth_never_leaves_a_map_in_the_other_ones_rectangle()
    {
        var renderer = Renderer(Scene("customs", "customs-plan", CustomsPlan));
        var view = new BoundView(renderer);

        renderer.Present(Scene("factory", "factory-plan", FactoryPlan, revision: 2));
        var factoryAspect = view.Aspect;
        renderer.Present(Scene("customs", "customs-plan", CustomsPlan, revision: 3));

        view.AssertMatchesTheViewModel(renderer);
        Assert.Equal(CustomsPlan.Width / CustomsPlan.Height, view.Aspect, 2);
        Assert.NotEqual(factoryAspect, view.Aspect, 2);
    }

    [Fact]
    public void A_different_artwork_of_the_same_map_redraws_at_its_own_shape()
    {
        // The drawing and the photograph cover different rectangles (the photograph is cropped to
        // the reviewed one), so choosing between them changes the bounds without changing the map.
        var renderer = Renderer(Scene("customs", "customs-drawing", CustomsPlan));
        var view = new BoundView(renderer);
        var photo = new MapSceneBounds(0, 0, 1700, 1000);

        renderer.Present(Scene("customs", "customs-photo", photo, revision: 2));

        view.AssertMatchesTheViewModel(renderer);
        Assert.Equal(photo.Width / photo.Height, view.Aspect, 2);
    }

    [Fact]
    public void Reading_another_floor_of_a_stack_moves_the_plan_the_view_is_told_about()
    {
        var renderer = Renderer(Stack("first"));
        var view = new BoundView(renderer);
        var before = view.Height;

        // "first" is the middle floor of three and "second" the top one, so the room the plates
        // above and below need is not the same and the plan is drawn a different size.
        renderer.Present(Stack("second", revision: 2));

        view.AssertMatchesTheViewModel(renderer);
        Assert.NotEqual(before, view.Height, 3);
    }

    [Fact]
    public void Choosing_the_stack_gives_the_plan_room_for_the_plates_and_the_view_is_told()
    {
        var flat = Stack("first", MapSceneMode.Flat2D);
        var renderer = Renderer(flat);
        var view = new BoundView(renderer);
        var flatHeight = view.Height;

        renderer.Present(Stack("first", MapSceneMode.FloorStack2D, revision: 2));

        view.AssertMatchesTheViewModel(renderer);
        Assert.True(view.Height < flatHeight, "a stacked plan is drawn smaller to leave the plates somewhere to be");
    }

    private static MapSceneRendererViewModel Renderer(MapSceneSnapshot scene)
    {
        var renderer = new MapSceneRendererViewModel(
            scene,
            Presentation,
            reviewedAssetResolver: _ => new TestArtwork(new Size(400, 280)),
            floorNameResolver: id => id,
            floorElevationResolver: id => id switch { "basement" => -4, "first" => 0, "second" => 5, _ => null });
        renderer.SetViewportSize(1344, 865);
        return renderer;
    }

    private static MapSceneSnapshot Scene(string location, string variant, MapSceneBounds bounds, long revision = 1) => new(
        revision,
        location,
        variant,
        "transform-1",
        bounds,
        ["base"],
        new(MapSceneCapability.Available, MapSceneCapability.Unavailable("No floors."), MapSceneCapability.Unavailable("No interior.")),
        new(MapSceneMode.Flat2D, "base", new(bounds.MinimumX + (bounds.Width / 2), bounds.MinimumY + (bounds.Height / 2), 1, 0, 0), []),
        [new MapSceneLayer(new("extracts"), "Extracts", 10, true)],
        [],
        [Background(location)]);

    private static MapSceneSnapshot Stack(string floor, MapSceneMode mode = MapSceneMode.FloorStack2D, long revision = 1)
    {
        string[] floors = ["first", "second", "basement"];
        var bounds = new MapSceneBounds(0, 0, 200, 140);
        return new(
            revision,
            "interchange",
            "interchange-interactive",
            "transform-1",
            bounds,
            floors,
            new(MapSceneCapability.Available, MapSceneCapability.Available, MapSceneCapability.Unavailable("No interior model.")),
            new(mode, floor, new(100, 70, 1, 0, 0), []),
            [new MapSceneLayer(new("extracts"), "Extracts", 10, true)],
            [],
            [Background("interchange"), .. floors.Select(id => FloorAsset(id))]);
    }

    private static MapSceneAsset Background(string location) => new(
        new($"asset:{location}:plan"),
        MapSceneAssetKind.Background2D,
        new($"https://example.test/maps/{location}.svg"),
        new("https://example.test/licence"),
        new string('a', 64),
        "Example map author",
        "map-1",
        "game-1",
        MapSceneAssetReviewStatus.Reviewed,
        new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero));

    private static MapSceneAsset FloorAsset(string floorId) => new(
        new($"asset:interchange:floor:{floorId}"),
        MapSceneAssetKind.Floor2D,
        new("https://example.test/maps/interchange.svg"),
        new("https://example.test/licence"),
        new string('b', 64),
        "Example map author",
        "map-1",
        "game-1",
        MapSceneAssetReviewStatus.Reviewed,
        new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero),
        floorId: floorId);

    /// <summary>What a XAML binding knows: the value at attach, then only what it is told changed.</summary>
    private sealed class BoundView
    {
        private static readonly string[] Names = [nameof(MapSceneRendererViewModel.MapLeft), nameof(MapSceneRendererViewModel.MapTop), nameof(MapSceneRendererViewModel.MapWidth), nameof(MapSceneRendererViewModel.MapHeight)];
        private readonly Dictionary<string, double> _shown = [];
        private readonly MapSceneRendererViewModel _renderer;

        public BoundView(MapSceneRendererViewModel renderer)
        {
            _renderer = renderer;
            foreach (var name in Names)
            {
                _shown[name] = Read(name);
            }

            renderer.PropertyChanged += Changed;
        }

        public double Width => _shown[nameof(MapSceneRendererViewModel.MapWidth)];

        public double Height => _shown[nameof(MapSceneRendererViewModel.MapHeight)];

        public double Aspect => Width / Height;

        public void AssertMatchesTheViewModel(MapSceneRendererViewModel renderer)
        {
            foreach (var name in Names)
            {
                Assert.True(
                    Math.Abs(_shown[name] - Read(name)) < 0.5,
                    $"{name}: the view was told {_shown[name]:F2} but the plan is drawn with {Read(name):F2}");
            }
        }

        private void Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is { } name && _shown.ContainsKey(name))
            {
                _shown[name] = Read(name);
            }
        }

        private double Read(string name) => name switch
        {
            nameof(MapSceneRendererViewModel.MapLeft) => _renderer.MapLeft,
            nameof(MapSceneRendererViewModel.MapTop) => _renderer.MapTop,
            nameof(MapSceneRendererViewModel.MapWidth) => _renderer.MapWidth,
            nameof(MapSceneRendererViewModel.MapHeight) => _renderer.MapHeight,
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }

    private sealed class TestArtwork(Size size) : IImage
    {
        public Size Size { get; } = size;

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }
    }
}
