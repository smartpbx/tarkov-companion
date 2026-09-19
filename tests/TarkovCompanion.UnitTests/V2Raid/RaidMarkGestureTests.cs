using System.Globalization;
using System.Text.RegularExpressions;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// [V2 rough package 46] Marks are placed by gesture, and removal beats placement.
/// </summary>
/// <remarks>
/// Clayton: "We can also remove the ping and waypoint buttons. They need to be easier. You can
/// make right click do a ping, and shift right click do a waypoint. Right click on them should
/// remove them." So there is no armed state to test any more; what has to hold is that one
/// gesture means exactly one thing.
///
/// The branch that decides it is in the renderer view, which needs a rendering platform the unit
/// suite does not have, so it is tested at its two seams instead: the hit test the view asks
/// (does the gesture land on an object?) and the mapping the host applies (which mark does the
/// modifier mean?). The markup between them is checked by reading it, the way the person-cone
/// tests do, because "the view still raises the event the host handles" is a markup fact.
/// </remarks>
public sealed class RaidMarkGestureTests
{
    private static readonly MapSceneRendererPresentation Presentation =
        MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc);

    private static readonly DateTimeOffset NowUtc = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private const string ViewPath = "src/TarkovCompanion.App/Views/V2/Raid/RaidCockpitView.axaml";
    private const string RendererPath = "src/TarkovCompanion.App/Views/V2/MapRenderer/MapSceneRendererView.axaml.cs";
    private const string TabletPath = "src/TarkovCompanion.GroupServer/Tablet/index.html";

    [Fact]
    public void A_plain_gesture_is_a_ping_and_the_secondary_one_is_a_waypoint()
    {
        Assert.Equal(RaidMarkKind.Ping, RaidCockpitViewModel.MarkKindFor(isSecondary: false));
        Assert.Equal(RaidMarkKind.Waypoint, RaidCockpitViewModel.MarkKindFor(isSecondary: true));
    }

    [Fact]
    public void A_gesture_over_a_mark_finds_that_mark_and_one_over_bare_map_finds_nothing()
    {
        // Removal beats placement, and this is the test of it: the view places a mark only when
        // the hit test finds nothing, so a gesture that lands on a ping can never also ping.
        var pingId = Guid.NewGuid();
        var renderer = RendererWith(new RaidMark(pingId, RaidMarkKind.Ping, new("customs", null, 50, 50, null, null), NowUtc));
        var onTheMark = renderer.ProjectedFor($"mark:{pingId}");

        Assert.True(renderer.TryHitObjectAt(onTheMark.X, onTheMark.Y, out var hit));
        Assert.Equal($"mark:{pingId}", hit.Value);
        Assert.True(RaidCockpitViewModel.TryParseMarkId(hit, out var markId));
        Assert.Equal(pingId, markId);

        // Far enough away to be outside the marker's own hit area, still inside the plan.
        Assert.False(renderer.TryHitObjectAt(onTheMark.X + 200, onTheMark.Y + 120, out _));
        Assert.True(renderer.TryScenePointAt(onTheMark.X + 200, onTheMark.Y + 120, out var bare));
        Assert.NotEqual(0, bare.X);
    }

    [Fact]
    public void A_gesture_over_a_group_members_mark_finds_it_the_same_way()
    {
        // The marks list already removes a group waypoint as readily as one of ours; the gesture
        // uses the same id, so it inherits the same rule rather than a second one.
        var groupWaypointId = Guid.NewGuid();
        var renderer = RendererWith(
            new RaidMark(groupWaypointId, RaidMarkKind.Waypoint, new("customs", null, 60, 40, "Riley", null), NowUtc));
        var onTheMark = renderer.ProjectedFor($"mark:{groupWaypointId}");

        Assert.True(renderer.TryHitObjectAt(onTheMark.X, onTheMark.Y, out var hit));
        Assert.True(RaidCockpitViewModel.TryParseMarkId(hit, out var markId));
        Assert.Equal(groupWaypointId, markId);
    }

    [Fact]
    public void A_gesture_over_something_that_is_not_a_mark_removes_nothing()
    {
        Assert.False(RaidCockpitViewModel.TryParseMarkId(new("catalog:extract-zb-1011"), out _));
    }

    [Fact]
    public void The_arm_buttons_and_the_sentence_that_explained_them_are_gone()
    {
        var view = File.ReadAllText(RepositoryFile(ViewPath));
        foreach (var gone in new[]
                 {
                     "v2-raid-place-ping", "v2-raid-place-waypoint", "v2-raid-cancel-placing",
                     "Arm Ping or Waypoint", "Click the map to place it",
                 })
        {
            Assert.DoesNotContain(gone, view, StringComparison.Ordinal);
        }

        // And the gesture that replaced them is wired to the plan.
        Assert.Contains("PlanRightClicked=\"RendererPlanRightClicked\"", view, StringComparison.Ordinal);
        Assert.Contains("MarkerRightClicked=\"RendererMarkerRightClicked\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void A_right_click_raises_exactly_one_of_the_two_events_and_is_always_handled()
    {
        // Why by reading: the branch is in a pointer handler, and a pointer handler needs a
        // rendering platform. What matters is that the placement arm is the else of the removal
        // arm — so one press cannot do both — and that neither arm leaves the gesture unhandled
        // for something further up to turn into a context menu.
        var renderer = File.ReadAllText(RepositoryFile(RendererPath));
        var block = Regex.Match(
            renderer,
            @"if \(current\.Properties\.IsRightButtonPressed\).*?\n            return;",
            RegexOptions.Singleline);
        Assert.True(block.Success, "The right-button branch is not where this test expects it.");
        Assert.Contains("MarkerRightClicked?.Invoke", block.Value, StringComparison.Ordinal);
        Assert.Contains("else if", block.Value, StringComparison.Ordinal);
        Assert.Contains("PlanRightClicked?.Invoke", block.Value, StringComparison.Ordinal);
        Assert.Contains("KeyModifiers.Shift", block.Value, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(block.Value, "eventArgs.Handled = true;").Count);
    }

    [Fact]
    public void The_tablet_tells_a_tap_from_a_pan_by_numbers_it_states()
    {
        // The tablet has no second button, so the same three outcomes ride on tap, hold and
        // "the finger moved". The thresholds are the whole of it: a stray ping on every pan
        // would be worse than no pinging at all, so they are asserted rather than assumed.
        // Newlines normalised because one assertion below spans two lines, and the file is
        // checked out with CRLF on Windows and LF here: the literal matched on Linux, failed in
        // the Windows job, and took main red with it.
        var tablet = File.ReadAllText(RepositoryFile(TabletPath)).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("const SLOP_PX = 12;", tablet, StringComparison.Ordinal);
        Assert.Contains("const TAP_MS = 350;", tablet, StringComparison.Ordinal);
        Assert.Contains("const HOLD_MS = 500;", tablet, StringComparison.Ordinal);
        // Straight-line from where it went down, not summed along the path: a still finger's
        // jitter must not add up to a drag over a 500 ms hold.
        Assert.Contains("Math.hypot(event.clientX - press.startX, event.clientY - press.startY) > SLOP_PX", tablet, StringComparison.Ordinal);
        // A second finger is a pinch and can never be a tap.
        Assert.Contains("gesture = pinchState();\n      cancelPress();", tablet, StringComparison.Ordinal);
        // Tap pings, hold makes a waypoint, hold on one of ours removes it.
        Assert.Contains("addMark(target.x, target.y, \"Ping\");", tablet, StringComparison.Ordinal);
        Assert.Contains("addMark(target.x, target.y, \"Waypoint\");", tablet, StringComparison.Ordinal);
        Assert.Contains("removeMark(mark);", tablet, StringComparison.Ordinal);
        // Something under the finger wins, exactly as removal beats placement on the desktop.
        Assert.Contains("if (target.hit) {\n      selectObject(target.hit);", tablet, StringComparison.Ordinal);
        // And a press that did something says so where the finger is.
        Assert.Contains("flash(target.px, target.py", tablet, StringComparison.Ordinal);
        // Nothing left to arm.
        Assert.DoesNotContain("markArmed", tablet, StringComparison.Ordinal);
    }

    private static TestRenderer RendererWith(RaidMark mark)
    {
        var bounds = new MapSceneBounds(0, 0, 200, 150);
        var variant = new MapVariant(
            "customs",
            "customs",
            MapProjectionKind.Interactive,
            "2D",
            null,
            null,
            new("https://assets.tarkov.dev/maps/svg/Customs.svg"),
            null,
            256,
            1,
            6,
            new MapCatalogBounds(new(-100, -75), new(100, 75)),
            null,
            new(1, 100, 1, 75, 0),
            null,
            null,
            null,
            "Shebuka",
            null,
            [],
            [],
            []);
        var location = new MapLocation("customs", null, "Customs", null, null, [variant]);
        var model = new MapPresentationService().Create(location, variant, "/cache/customs.svg");
        var (layer, objects) = RaidCockpitViewModel.BuildMarksLayer([mark], "customs", NowUtc);
        Assert.NotNull(layer);
        var camera = new MapSceneCamera(
            bounds.MinimumX + (bounds.Width / 2),
            bounds.MinimumY + (bounds.Height / 2),
            1,
            0,
            0);
        var result = new MapSceneAssembler().Build(new(
            1,
            model,
            bounds,
            variant.Key,
            new(MapSceneMode.Flat2D, null, camera, []),
            [],
            [layer!],
            objects,
            [new MapSceneAsset(
                new("asset:customs"),
                MapSceneAssetKind.Background2D,
                model.Background!.SourceUri,
                model.LicenseUri,
                new string('c', 64),
                "Shebuka",
                variant.Key,
                "current",
                MapSceneAssetReviewStatus.Reviewed,
                NowUtc)]));
        var scene = Assert.IsType<MapSceneSnapshot>(result.Scene);
        var renderer = new MapSceneRendererViewModel(scene, Presentation);
        renderer.SetViewportSize(1920, 1080);
        return new(renderer);
    }

    private static string RepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TarkovCompanion.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, relativePath);
        Assert.True(File.Exists(path), $"{relativePath} is not where this test expects it.");
        return path;
    }

    /// <summary>The renderer plus "where on the viewport is this object drawn", which the view knows and it does not.</summary>
    private sealed class TestRenderer(MapSceneRendererViewModel renderer)
    {
        public bool TryHitObjectAt(double x, double y, out MapSceneObjectId objectId) =>
            renderer.TryHitObjectAt(x, y, out objectId);

        public bool TryScenePointAt(double x, double y, out MapScenePoint point) =>
            renderer.TryScenePointAt(x, y, out point);

        public (double X, double Y) ProjectedFor(string objectId)
        {
            var marker = Assert.Single(
                renderer.SpatialObjects,
                item => item.SceneObject?.Id.Value == objectId);
            return (
                marker.AnchorLeft + (MapSceneRendererViewModel.MarkerExtent / 2),
                marker.AnchorTop + (MapSceneRendererViewModel.MarkerExtent / 2));
        }
    }
}
