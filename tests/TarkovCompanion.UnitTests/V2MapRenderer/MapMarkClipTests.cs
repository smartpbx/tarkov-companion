using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.Views.V2.MapRenderer;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>Alone in time: a headless Avalonia session owns process-wide state while it runs.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AvaloniaHeadlessCollection
{
    public const string Name = "Avalonia headless";
}

/// <summary>
/// [Issue 551] Nothing between a mark and the map card clips it, and nothing but the mark's own
/// shape takes the pointer.
/// </summary>
/// <remarks>
/// Reported from a build on Windows: "pins dont look right they are cut off, so is the vision
/// cone of the player ... pings are cutoff too and waypoints are even worse. and mousing over
/// them shows a highlighted square". The pin is drawn upward from its tip at the centre of the
/// 44px marker button, the button clipped to its bounds, and the square was the button's themed
/// pointer-over background. The maths tests written with the pins could not see either, because
/// both live in the real visual tree. So this one builds the real view, under the real theme, and
/// reads the tree.
/// </remarks>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class MapMarkClipTests
{
    [Fact]
    public void No_mark_is_clipped_by_anything_below_the_map_card_at_any_zoom_or_bearing()
    {
        Run(view =>
        {
            var failures = new List<string>();
            foreach (var (zoomSteps, bearing) in new[] { (0, 0d), (6, 0d), (6, 37d), (9, 90d) })
            {
                var renderer = (MapSceneRendererViewModel)view.DataContext!;
                renderer.FitPlanCommand.Execute(null);
                for (var step = 0; step < zoomSteps; step++)
                {
                    renderer.RequestZoom(1);
                }

                renderer.SetBearing(bearing);
                Dispatcher.UIThread.RunJobs();

                var marks = Marks(view);
                Assert.Equal(5, marks.Select(mark => ((MapSceneRendererObjectViewModel)mark.DataContext!).Icon).Distinct().Count());
                foreach (var mark in marks)
                {
                    var icon = ((MapSceneRendererObjectViewModel)mark.DataContext!).Icon;
                    foreach (var visual in mark.GetVisualAncestors().Prepend(mark))
                    {
                        if (visual is Control { Name: "PlanViewport" })
                        {
                            break;
                        }

                        if (visual.ClipToBounds || visual.Clip is not null)
                        {
                            failures.Add($"{icon} at zoom step {zoomSteps}, bearing {bearing}: {visual.GetType().Name} clips.");
                        }
                    }

                    // Inside the mark, only a leaf may clip (a TextBlock clips its own text). A
                    // container that clips cuts whatever is drawn past its edge: the pin's head.
                    foreach (var inner in mark.GetVisualDescendants())
                    {
                        if ((inner.ClipToBounds || inner.Clip is not null) && inner.GetVisualChildren().Any())
                        {
                            failures.Add($"{icon}: {inner.GetType().Name} inside the mark clips its children.");
                        }
                    }
                }
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Distinct()));
        });
    }

    [Fact]
    public void A_pin_is_drawn_above_its_box_so_a_clip_on_the_box_would_cut_it()
    {
        // The reason the test above matters, measured rather than assumed: the pin's drawn
        // shapes really do leave the 44px button, by the height of the head.
        Run(view =>
        {
            var pin = Marks(view).First(mark => ((MapSceneRendererObjectViewModel)mark.DataContext!).IsWaypointMark);
            var head = pin.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
                .First(path => path.Classes.Contains("v2-map-pin-round"));
            var top = head.TranslatePoint(new Point(0, 0), pin)!.Value.Y;
            Assert.Equal((MapSceneRendererViewModel.MarkerExtent / 2) - MapPinGeometry.Height, top, 3);
            Assert.True(top < 0);
        });
    }

    [Fact]
    public void Only_the_marks_own_shape_takes_the_pointer_and_the_button_draws_no_square()
    {
        Run(view =>
        {
            foreach (var mark in Marks(view))
            {
                var model = (MapSceneRendererObjectViewModel)mark.DataContext!;
                var presenter = mark.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First();
                // The hover square was this presenter's themed background.
                Assert.Null(presenter.Background);

                // The bottom-right corner of the 44px box is empty map for every kind of mark.
                var corner = mark.InputHitTest(new Point(41, 41));
                Assert.True(corner is null, $"{model.Icon}: the empty corner of the box took the pointer ({corner?.GetType().Name}).");
            }

            // Hovering a mark is seen by the mark (its own outline brightens) and still paints
            // no background behind it: the theme's pointer-over square is gone, not just hidden
            // while nothing is over it.
            var window = (Window)TopLevel.GetTopLevel(view)!;
            foreach (var mark in Marks(view))
            {
                var model = (MapSceneRendererObjectViewModel)mark.DataContext!;
                var over = model.IsPinMark ? new Point(22, 4) : new Point(22, 22);
                window.MouseMove(mark.TranslatePoint(over, window)!.Value);
                Dispatcher.UIThread.RunJobs();
                Assert.True(mark.IsPointerOver, $"{model.Icon}: the pointer over the mark's own shape did not reach it.");
                var presenter = mark.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First();
                Assert.Null(presenter.Background);
                Assert.Null(mark.Background);
            }

            // The middle of a pin's head, 20px above its tip, is the pin.
            var pin = Marks(view).First(mark => ((MapSceneRendererObjectViewModel)mark.DataContext!).IsObjectiveMark);
            Assert.NotNull(pin.InputHitTest(new Point(22, 2)));
            // And the middle of a person or a ping is that mark.
            foreach (var centred in Marks(view).Where(mark =>
                         (MapSceneRendererObjectViewModel)mark.DataContext! is { IsPinMark: false }))
            {
                Assert.NotNull(centred.InputHitTest(new Point(22, 22)));
            }
        });
    }

    private static IReadOnlyList<ToggleButton> Marks(MapSceneRendererView view) =>
        [.. view.GetVisualDescendants().OfType<ToggleButton>().Where(button => button.Classes.Contains("v2-map-marker"))];

    private static void Run(Action<MapSceneRendererView> body)
    {
        using var session = HeadlessSessions.StartNew(typeof(MarkClipApp));
        session.Dispatch(
            () =>
            {
                var view = new MapSceneRendererView { DataContext = Renderer() };
                var window = new Window { Width = 1400, Height = 900, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                try
                {
                    body(view);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None).GetAwaiter().GetResult();
    }

    private static MapSceneRendererViewModel Renderer()
    {
        var layer = new MapSceneLayer(new("marks"), "Marks", 10, true);
        MapSceneObject At(string id, MapSceneObjectKind kind, double x, double y, double? heading = null) => new(
            new($"object:{id}"),
            layer.Id,
            kind,
            MapSceneTruthKind.UserAuthored,
            "1",
            null,
            MapSceneGeometry.At(new(x, y)),
            [],
            new DataProvenance("fixture", new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero), Confidence: new Confidence(1)),
            headingDegrees: heading);
        var scene = new MapSceneSnapshot(
            1,
            "customs",
            "customs",
            "customs",
            new MapSceneBounds(0, 0, 400, 300),
            [],
            new(MapSceneCapability.Available, MapSceneCapability.Unavailable("no stack"), MapSceneCapability.Unavailable("no interior")),
            new(MapSceneMode.Flat2D, null, new(200, 150, 1, 0, 0), [new(layer.Id, true)]),
            [layer],
            [
                At("waypoint", MapSceneObjectKind.Waypoint, 190, 140),
                At("objective", MapSceneObjectKind.QuestObjective, 210, 140),
                At("ping", MapSceneObjectKind.Ping, 190, 160),
                At("player", MapSceneObjectKind.LastKnownPosition, 210, 160, 45),
                At("extract", MapSceneObjectKind.Extract, 200, 150),
            ],
            []);
        var renderer = new MapSceneRendererViewModel(
            scene,
            MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc),
            showsDetailsPanel: false);
        renderer.ViewChangeRequested += change =>
        {
            var result = MapSceneViewReducer.Apply(renderer.Scene, change);
            if (result.Status is MapSceneViewChangeStatus.Applied or MapSceneViewChangeStatus.Unchanged)
            {
                renderer.Present(result.Scene);
            }
        };
        return renderer;
    }

    /// <summary>The application's own theme and V2 styles, without the application.</summary>
    public sealed class MarkClipApp : Avalonia.Application
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<MarkClipApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

        public override void Initialize()
        {
            var root = new Uri("avares://TarkovCompanion/");
            Resources.MergedDictionaries.Add(new ResourceInclude(root) { Source = new("avares://TarkovCompanion/Themes/V2/V2Resources.axaml") });
            Styles.Add(new FluentTheme());
            Styles.Add(new StyleInclude(root) { Source = new("avares://TarkovCompanion/Themes/V2/V2PrimitiveStyles.axaml") });
            Styles.Add(new StyleInclude(root) { Source = new("avares://TarkovCompanion/Themes/V2/V2WorkspaceStyles.axaml") });
        }
    }
}
