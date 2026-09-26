using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>What <c>--gallery-scene</c> seeds before the Windows page gallery photographs the Raid map.</summary>
public enum GallerySceneKind
{
    /// <summary>Nothing seeded: readiness only (the named map is drawn and has its extracts).</summary>
    Map,

    /// <summary>Active quests on the map, a selected spawn, and Plan's objective route opened on Raid (#779).</summary>
    Route,

    /// <summary>A demo squad in raid with positions and shared quests, Squad on (#783).</summary>
    Squad,

    /// <summary>A mark of each scope and lifetime, and a tablet's short route (#785/#787).</summary>
    Marks,

    /// <summary>The player in raid with the raid clock running, and nobody else (#838).</summary>
    /// <remarks>
    /// The strip under the map carries the clock only in raid, and on Customs that was the width
    /// that wrapped it and took 32 pixels from the map. No other scene had a clock without a squad.
    /// </remarks>
    InRaid,

    /// <summary>[#286] Draw mode on, two lines of the player's and one of a squadmate's.</summary>
    Draw,

    /// <summary>
    /// [#279] Nothing seeded and no map: ready once the page on screen has read its data
    /// (<see cref="GalleryPageReadiness"/>), so Ammo is not photographed while it is still reading.
    /// </summary>
    Page,

    /// <summary>[#279] A fresh profile: ready once the empty database is open and the page has said so.</summary>
    Empty,

    /// <summary>[#279] The page's own load held at its first line (<see cref="LoadHold"/>); "release" lets it finish.</summary>
    Loading,

    /// <summary>[#279] Cached data six days old and a relay that stopped answering; the gallery launches it offline.</summary>
    Degraded,

    /// <summary>[#279] The page's own load made to fail (<see cref="LoadFaultInjection"/>); "release" stops failing it.</summary>
    Error,

    /// <summary>[#286] In raid on the map, Inspect mode, a spot clicked: the popover.</summary>
    Inspect,

    /// <summary>[#286] In raid on the map, Route mode, five stops clicked: the numbered route.</summary>
    RouteStops,

    /// <summary>[#914] A PMC raid one minute old, the first screenshot near the player: the spawn lines.</summary>
    SpawnLines,
}

public static class GallerySceneKinds
{
    public static GallerySceneKind Parse(string value) =>
        // By name only: Enum.TryParse also takes "7", which is whichever scene happens to be seventh.
        !int.TryParse(value, out _) && Enum.TryParse<GallerySceneKind>(value, ignoreCase: true, out var kind) && Enum.IsDefined(kind)
            ? kind
            : throw new ArgumentException($"--gallery-scene must be one of map, route, squad, marks, inraid, draw, page, empty, loading, degraded, error, inspect, routestops or spawnlines, not '{value}'.");
}

/// <summary>
/// [#279] The answer to the diagnostic channel's "ready" command: the gallery waits on this
/// instead of a fixed sleep, so a capture is taken once the map and its seeded state are drawn.
/// </summary>
public sealed class GalleryReadiness
{
    private readonly TaskCompletionSource<string> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Ready(string detail) => _ready.TrySetResult(detail);

    public void Failed(string detail) => _ready.TrySetException(new InvalidOperationException(detail));

    /// <summary>
    /// [#279] Set by the scene once it is ready: waits for a named condition after a gallery step
    /// (an extract pressed, the map zoomed, the loot layer switched on). The scene's own "ready"
    /// answers once; this is what replaced the fixed sleeps the gallery took after each step.
    /// </summary>
    public Func<string, CancellationToken, Task<string>>? AfterStep { get; set; }

    /// <summary>Null detail and a reason when it did not become ready within the timeout.</summary>
    /// <param name="condition">Null for the scene itself; otherwise a condition <see cref="AfterStep"/> knows.</param>
    public async Task<(bool Ready, string Detail)> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken, string? condition = null)
    {
        try
        {
            var detail = await _ready.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (condition is null)
            {
                return (true, detail);
            }

            if (AfterStep is not { } afterStep)
            {
                return (false, $"the scene cannot wait for '{condition}'");
            }

            return (true, await afterStep(condition, cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false));
        }
        catch (TimeoutException)
        {
            return (false, $"not ready after {timeout.TotalSeconds:0} s");
        }
        catch (InvalidOperationException exception)
        {
            return (false, exception.Message);
        }
    }
}

/// <summary>
/// [#279] Seeds one <see cref="GallerySceneKind"/> in the running app, through the same services
/// and view models a player's actions reach, then reports readiness.
/// </summary>
/// <remarks>
/// Developer mode only (see <see cref="AppCommandLine.GalleryScene"/>): it marks quests active,
/// writes marks, and replaces the squad with made-up people, all of which a player must never get
/// from a launch argument. Everything is synthetic and built from the public catalog the app has
/// already downloaded; the gallery restores the database and config it changes afterwards.
/// </remarks>
internal sealed class GallerySceneRunner(IServiceProvider services, MainWindowViewModel main, string mapId, GallerySceneKind scene)
{
    private static readonly TimeSpan MapTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(45);

    public async Task RunAsync(GalleryReadiness readiness, CancellationToken cancellationToken)
    {
        if (scene == GallerySceneKind.Page)
        {
            await RunPageAsync(readiness, cancellationToken).ConfigureAwait(true);
            return;
        }

        if (GalleryStateScene.IsState(scene))
        {
            await new GalleryStateScene(services, main, scene).RunAsync(readiness, cancellationToken).ConfigureAwait(true);
            return;
        }

        try
        {
            var raid = services.GetRequiredService<RaidCockpitViewModel>();
            await WaitForAsync(
                () => raid.Renderer is { } renderer &&
                    string.Equals(renderer.Scene.LocationId, mapId, StringComparison.OrdinalIgnoreCase) &&
                    renderer.BackgroundImage is not null &&
                    raid.MapExtracts.Count > 0,
                MapTimeout,
                $"the {mapId} map, its picture and its extracts",
                cancellationToken).ConfigureAwait(true);
            var detail = scene switch
            {
                GallerySceneKind.Route => await RouteAsync(raid, cancellationToken).ConfigureAwait(true),
                GallerySceneKind.Squad => await SquadAsync(raid, cancellationToken).ConfigureAwait(true),
                GallerySceneKind.Marks => await MarksAsync(raid, cancellationToken).ConfigureAwait(true),
                GallerySceneKind.InRaid => await InRaidAsync(raid, cancellationToken).ConfigureAwait(true),
                GallerySceneKind.Draw => await DrawAsync(raid, cancellationToken).ConfigureAwait(true),
                GallerySceneKind.Inspect => await InspectAsync(raid, cancellationToken).ConfigureAwait(true),
                GallerySceneKind.RouteStops => await RouteStopsAsync(raid, cancellationToken).ConfigureAwait(true),
                GallerySceneKind.SpawnLines => await SpawnLinesAsync(raid, cancellationToken).ConfigureAwait(true),
                _ => $"{raid.MapExtracts.Count} extracts",
            };

            // The state above is the view model's; the map redraws its markers on a paced rebuild
            // after it. The first Windows run photographed the squad and marks scenes between the
            // two: route line drawn, every pin (extracts included) missing. So wait for the pins
            // this scene is about, then for the scene to stop changing.
            await WaitForAsync(() => MarkersDrawn(raid), StepTimeout, $"the {scene.ToString().ToLowerInvariant()} pins on the map", cancellationToken)
                .ConfigureAwait(true);
            await SettledAsync(raid, cancellationToken).ConfigureAwait(true);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            readiness.AfterStep = (condition, token) =>
                Dispatcher.UIThread.InvokeAsync(() => AfterStepAsync(raid, condition, token));
            readiness.Ready($"{scene.ToString().ToLowerInvariant()} on {mapId}: {detail}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            readiness.Failed($"{scene.ToString().ToLowerInvariant()} on {mapId}: {exception.Message}");
        }
    }

    /// <summary>
    /// [#279] The <see cref="GallerySceneKind.Page"/> scene: waits until the page has read its
    /// data and stayed that way for a second (a catalog sync finishing just after the first read
    /// starts a second one), then answers. "settled" after a gallery step asks the same again.
    /// </summary>
    private async Task RunPageAsync(GalleryReadiness readiness, CancellationToken cancellationToken)
    {
        try
        {
            if (main.PreviewShell is not { } shell)
            {
                throw new InvalidOperationException("no V2 shell");
            }

            var what = await PageLoadedAsync(shell, cancellationToken).ConfigureAwait(true);
            readiness.AfterStep = (_, token) => Dispatcher.UIThread.InvokeAsync(() => PageLoadedAsync(shell, token));
            readiness.Ready($"page {shell.CurrentAddress}: {what}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            readiness.Failed($"page {main.PreviewShell?.CurrentAddress}: {exception.Message}");
        }
    }

    internal static async Task<string> PageLoadedAsync(V2ShellViewModel shell, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + MapTimeout;
        var quiet = 0;
        var what = string.Empty;
        while (quiet < 10)
        {
            var (loaded, page) = GalleryPageReadiness.Of(shell);
            what = page;
            quiet = loaded ? quiet + 1 : 0;
            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException($"{page} still loading after {MapTimeout.TotalSeconds:0} s");
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(true);
        }

        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        return $"{what} loaded";
    }

    private int _placedMarks;

    /// <summary>
    /// [#279] What the gallery waits for after one of its own steps, on the UI thread.
    /// "settled": the scene stopped changing (an extract selected, the map zoomed). "loot": the
    /// high-value loot layer has drawn its pins, or has none to draw, and settled; a fixed four
    /// seconds after the button was a guess at how long that takes on a runner.
    /// </summary>
    private async Task<string> AfterStepAsync(RaidCockpitViewModel raid, string condition, CancellationToken cancellationToken)
    {
        switch (condition)
        {
            case "settled":
                break;
            case "loot":
                // A runner has no loot publication (the layer says "no loot data"), so the answer is
                // either the pins of the rows it lists or that there are no rows to draw.
                await WaitForAsync(
                    () => raid.Renderer is { HighValueLoot: { } loot } renderer &&
                        (loot.IsUnavailable || !loot.HasEntries ||
                         renderer.PointMarkers.Any(item => item.SceneObject?.Kind is MapSceneObjectKind.LootSpawn or MapSceneObjectKind.LootContainer)),
                    StepTimeout,
                    "the loot layer's pins or its empty state",
                    cancellationToken).ConfigureAwait(true);
                break;
            case "stack":
                // [#923] Stack over a photograph loads the drawing first; "settled" answered while
                // the drawing was still downloading and photographed the flat picture.
                await WaitForAsync(
                    () => raid.Renderer is { HasFloorStack: true },
                    StepTimeout,
                    "the floor stack drawn",
                    cancellationToken).ConfigureAwait(true);
                break;
            default:
                throw new InvalidOperationException($"no gallery condition named '{condition}'");
        }

        await SettledAsync(raid, cancellationToken).ConfigureAwait(true);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        var pins = raid.Renderer?.PointMarkers.Count ?? 0;
        return $"{condition}: {pins} pins";
    }

    private bool MarkersDrawn(RaidCockpitViewModel raid)
    {
        if (raid.Renderer is not { } renderer)
        {
            return false;
        }

        var kinds = renderer.PointMarkers.Select(item => item.SceneObject?.Kind).ToArray();
        return kinds.Any(kind => kind is MapSceneObjectKind.Extract or MapSceneObjectKind.Transit) && scene switch
        {
            GallerySceneKind.Route => kinds.Contains(MapSceneObjectKind.QuestObjective),
            GallerySceneKind.Squad => kinds.Contains(MapSceneObjectKind.TeammateLastKnown),
            GallerySceneKind.Marks => kinds.Count(kind => kind is MapSceneObjectKind.Ping or MapSceneObjectKind.Waypoint) >= _placedMarks,
            GallerySceneKind.RouteStops => kinds.Count(kind => kind is MapSceneObjectKind.Waypoint) >= _placedMarks,
            GallerySceneKind.Draw => renderer.GeometryObjects.Count(item => item.SceneObject.Id.Value.Contains("drawing:", StringComparison.Ordinal)) >= 3,
            _ => true,
        };
    }

    /// <summary>The scene's revision and its pin count unchanged for a second: no rebuild still to land.</summary>
    private static async Task SettledAsync(RaidCockpitViewModel raid, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        (long, int)? last = null;
        var quiet = 0;
        while (quiet < 10 && DateTime.UtcNow < deadline)
        {
            var now = (raid.Renderer?.Scene.Revision ?? -1, raid.Renderer?.PointMarkers.Count ?? -1);
            quiet = now == last ? quiet + 1 : 0;
            last = now;
            await Task.Delay(100, cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task<string> RouteAsync(RaidCockpitViewModel raid, CancellationToken cancellationToken)
    {
        // The origin: a PMC spawn chosen on the map, the same as a player clicking one.
        var renderer = raid.Renderer!;
        var spawn = renderer.Scene.Objects
            .Where(item => item.Kind == MapSceneObjectKind.SpawnArea && item.Geometry.Points.Count > 0)
            // A player spawn ("Spawn · …"), not a boss's or a Scav's.
            .OrderBy(item => item.Label.StartsWith("Spawn", StringComparison.Ordinal) && item.Faction != MapFeatureFaction.Scav ? 0 : 1)
            .ThenBy(item => item.Faction == MapFeatureFaction.Pmc ? 0 : 1)
            .ThenBy(item => item.Label, StringComparer.Ordinal)
            .ThenBy(item => item.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault() ?? throw new InvalidOperationException("the map has no spawn to start from");
        // Spawns start hidden, and only a drawn object can be selected: the Spawns layer goes on first.
        renderer.SetLayerVisibility(spawn.LayerId, true);
        await WaitForAsync(
            () =>
            {
                raid.Renderer?.SelectObject(spawn.Id);
                return raid.ObjectiveRouteOrigin() is not null;
            },
            StepTimeout,
            "selected spawn",
            cancellationToken).ConfigureAwait(true);

        var tasks = await QuestsOnMapAsync(3, cancellationToken).ConfigureAwait(true);
        var profile = await services.GetRequiredService<IPlayerProfileService>().GetActiveAsync(cancellationToken).ConfigureAwait(true);
        var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
        var commands = services.GetRequiredService<IQuestProgressCommandService>();
        foreach (var task in tasks)
        {
            await commands.SetTaskStateAsync(scope, task.Id, RecordedTaskState.Active, cancellationToken).ConfigureAwait(true);
        }

        var plan = services.GetRequiredService<PlanWorkspaceViewModel>();
        await plan.RefreshAsync(cancellationToken).ConfigureAwait(true);
        var label = raid.MapPicker.FirstOrDefault(item => string.Equals(item.MapId, mapId, StringComparison.OrdinalIgnoreCase))?.Name;
        if (!plan.SelectMapForPreview(mapId) && (label is null || !plan.SelectMapForPreview(label)))
        {
            throw new InvalidOperationException($"Plan has no {label ?? mapId} group for the seeded quests");
        }

        // Plan orders the objectives once their projection arrives; it is asked again until it has.
        var group = plan.SelectedGroup!;
        try
        {
            await WaitForAsync(
                () =>
                {
                    plan.RefreshMapPreview();
                    return group.Route is { Steps.Count: > 0 };
                },
                StepTimeout,
                "Plan's objective route",
                cancellationToken).ConfigureAwait(true);
        }
        catch (InvalidOperationException exception)
        {
            // Plan says why it could not order them; that is the useful half of the failure.
            throw new InvalidOperationException($"{exception.Message} (Plan: '{plan.MapNote}' '{group.RouteHint}', origin {(raid.ObjectiveRouteOrigin() is null ? "none" : "set")})");
        }

        // Counted before "Open in Raid": since #807 the button marks the quests active, Plan rebuilds
        // its groups, and this group's route is gone by the time the Raid map draws it.
        var steps = group.Route!.Steps.Count;

        // "Open in Raid", the button a player presses.
        if (group.OpenInRaidCommand is AsyncDelegateCommand open)
        {
            await open.ExecuteAsync().ConfigureAwait(true);
        }
        else
        {
            group.OpenInRaidCommand.Execute(null);
        }

        await WaitForAsync(
            () => raid.Renderer?.Scene.Objects.Any(item => item.LayerId == ObjectiveRouteSceneBuilder.LayerId && item.Kind == MapSceneObjectKind.Route) == true,
            StepTimeout,
            "the objective route on the Raid map",
            cancellationToken).ConfigureAwait(true);
        return $"{tasks.Count} quests, spawn '{spawn.Label}', {steps} route steps";
    }

    private async Task<string> SquadAsync(RaidCockpitViewModel raid, CancellationToken cancellationToken)
    {
        // No relay on a verification machine: the session's "not sharing" would replace the demo squad.
        await services.GetRequiredService<GroupSessionService>().DisposeAsync().ConfigureAwait(true);
        var model = main.Map.RenderModel ?? throw new InvalidOperationException("the map has no render model");
        var demo = GallerySquad.Build(model, services.GetRequiredService<TimeProvider>().GetUtcNow());
        var store = services.GetRequiredService<IRuntimeStateStore>();
        store.Update(snapshot => snapshot with { Raid = demo.Raid, Group = demo.Group });
        var shared = await GallerySquad.ShareQuestsAsync(services, main.Map, demo.Group, cancellationToken).ConfigureAwait(true);
        raid.ShowSquadObjectives = true;
        await WaitForAsync(() => raid.HasSquadObjectivesHere, StepTimeout, "the squad's objectives on the map", cancellationToken)
            .ConfigureAwait(true);
        return $"{demo.Group.Members.Count} squadmates, {shared}";
    }

    private async Task<string> InRaidAsync(RaidCockpitViewModel raid, CancellationToken cancellationToken)
    {
        // The squad scene's own raid, minus the squad: the store's group is left as it is.
        var model = main.Map.RenderModel ?? throw new InvalidOperationException("the map has no render model");
        var demo = GallerySquad.Build(model, services.GetRequiredService<TimeProvider>().GetUtcNow());
        services.GetRequiredService<IRuntimeStateStore>().Update(snapshot => snapshot with { Raid = demo.Raid });
        await WaitForAsync(() => raid.ShowsStripPhase, StepTimeout, "the raid clock on the strip", cancellationToken)
            .ConfigureAwait(true);
        return $"clock '{raid.RaidPhaseLabel}'";
    }

    /// <summary>
    /// [#914] The inraid scene's raid, one minute in and with only its last two screenshots, so the
    /// nearby spawn areas are measured from near the player, as early in a real raid; 300 m so a
    /// few lines show whatever the demo walk passes. Waits for the lines themselves.
    /// </summary>
    private async Task<string> SpawnLinesAsync(RaidCockpitViewModel raid, CancellationToken cancellationToken)
    {
        var model = main.Map.RenderModel ?? throw new InvalidOperationException("the map has no render model");
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var demo = GallerySquad.Build(model, now);
        var early = demo.Raid with
        {
            StartedUtc = now.AddMinutes(-1),
            PositionTrail = [.. demo.Raid.PositionTrail.Skip(Math.Max(0, demo.Raid.PositionTrail.Count - 2))],
        };
        raid.SetSpawnRadius(model.Location.Id, 300);
        services.GetRequiredService<IRuntimeStateStore>().Update(snapshot => snapshot with { Raid = early });
        await WaitForAsync(
                () => raid.Renderer?.Scene.Objects.Any(item => item.LayerId == RaidCockpitViewModel.SpawnLinesLayerId) == true,
                StepTimeout,
                "the spawn lines on the map",
                cancellationToken)
            .ConfigureAwait(true);
        return $"{raid.Renderer!.Scene.Objects.Count(item => item.LayerId == RaidCockpitViewModel.SpawnLinesLayerId && item.Kind == MapSceneObjectKind.Route)} spawn lines";
    }

    /// <summary>
    /// [#286] The squad scene's raid, Draw mode on, two lines drawn the way a drag hands them over
    /// and one squadmate's line as the relay would: the three kinds of line a player sees.
    /// </summary>
    private async Task<string> DrawAsync(RaidCockpitViewModel raid, CancellationToken cancellationToken)
    {
        // No relay on a verification machine: the session's "not sharing" would replace the demo squad.
        await services.GetRequiredService<GroupSessionService>().DisposeAsync().ConfigureAwait(true);
        var model = main.Map.RenderModel ?? throw new InvalidOperationException("the map has no render model");
        var demo = GallerySquad.Build(model, services.GetRequiredService<TimeProvider>().GetUtcNow());
        var members = demo.Group.Members.ToArray();
        var index = Array.FindIndex(members, member => member.Position is not null);
        if (index >= 0)
        {
            var at = members[index].Position!.Value;
            members[index] = members[index] with
            {
                Drawings =
                [
                    new("gallery", members[index].MapId ?? mapId, null,
                        [.. Enumerable.Range(0, 12).Select(step => (at.X + (step * 12.0), at.Z + (25 * Math.Sin(step / 3.0))))]),
                ],
            };
        }

        services.GetRequiredService<IRuntimeStateStore>().Update(snapshot => snapshot with
        {
            Raid = demo.Raid,
            Group = demo.Group with { Members = members },
        });
        var bounds = raid.Renderer!.Scene.Bounds;
        MapScenePoint At(double fx, double fy) => new(bounds.MinimumX + (bounds.Width * fx), bounds.MinimumY + (bounds.Height * fy));
        raid.SetInteractionMode(MapInteractionMode.Draw);
        raid.AddDrawing([.. Enumerable.Range(0, 240).Select(step => At(0.25 + (step / 240.0 * 0.4), 0.45 + (0.08 * Math.Sin(step / 18.0))))]);
        raid.AddDrawing([.. Enumerable.Range(0, 180).Select(step =>
            At(0.6 + (0.07 * Math.Cos(step / 180.0 * 2 * Math.PI)), 0.3 + (0.1 * Math.Sin(step / 180.0 * 2 * Math.PI))))]);
        await WaitForAsync(() => raid.HasOwnDrawings && raid.IsDrawMode, StepTimeout, "two lines and Draw mode", cancellationToken)
            .ConfigureAwait(true);
        return $"{raid.DrawingStore.Drawings.Count} lines, {(index >= 0 ? "a squad line" : "no squad line")}";
    }

    /// <summary>
    /// [#286] The inraid scene, then Inspect mode and one click near the middle of the plan, on an
    /// objective or a place name where there is one: the popover #868 added, which no Windows
    /// capture had shown. The click goes through <see cref="RaidCockpitViewModel.ModeClicked"/>,
    /// the call a real click in Inspect mode makes.
    /// </summary>
    private async Task<string> InspectAsync(RaidCockpitViewModel raid, CancellationToken cancellationToken)
    {
        var clock = await InRaidAsync(raid, cancellationToken).ConfigureAwait(true);
        var renderer = FitForModes(raid);
        var bounds = renderer.Scene.Bounds;
        var middle = new MapScenePoint(bounds.MinimumX + (bounds.Width * 0.5), bounds.MinimumY + (bounds.Height * 0.5));
        var target = renderer.Scene.VisibleObjects
            .Where(item => item.Kind is MapSceneObjectKind.QuestObjective or MapSceneObjectKind.Label && item.Geometry.Points.Count > 0)
            .OrderBy(item => item.Kind == MapSceneObjectKind.QuestObjective ? 0 : 1)
            .ThenBy(item => Math.Pow(item.Geometry.Points[0].X - middle.X, 2) + Math.Pow(item.Geometry.Points[0].Y - middle.Y, 2))
            .Select(item => (MapScenePoint?)item.Geometry.Points[0])
            .FirstOrDefault() ?? middle;
        raid.SetInteractionMode(MapInteractionMode.Inspect);
        raid.ModeClicked(new(target.X + (bounds.Width * 0.01), target.Y + (bounds.Height * 0.01)));
        await WaitForAsync(() => raid.HasInspection && raid.InspectSections.Count > 0, StepTimeout, "the Inspect popover", cancellationToken)
            .ConfigureAwait(true);
        return $"{clock}, inspect '{raid.InspectTitle}' with {raid.InspectSections.Count} sections";
    }

    /// <summary>[#286] The inraid scene, then Route mode and five clicks across the plan: five numbered stops.</summary>
    private async Task<string> RouteStopsAsync(RaidCockpitViewModel raid, CancellationToken cancellationToken)
    {
        var clock = await InRaidAsync(raid, cancellationToken).ConfigureAwait(true);
        var bounds = FitForModes(raid).Scene.Bounds;
        raid.SetInteractionMode(MapInteractionMode.Route);
        (double X, double Y)[] stops = [(0.30, 0.62), (0.38, 0.52), (0.47, 0.56), (0.56, 0.47), (0.63, 0.36)];
        foreach (var (fx, fy) in stops)
        {
            raid.ModeClicked(new(bounds.MinimumX + (bounds.Width * fx), bounds.MinimumY + (bounds.Height * fy)));
        }

        _placedMarks = stops.Length;
        await WaitForAsync(() => raid.PlannedRouteStopCount == stops.Length && raid.Marks.Count >= stops.Length, StepTimeout,
            $"{stops.Length} route stops", cancellationToken).ConfigureAwait(true);
        return $"{clock}, route '{raid.PlannedRouteSummary}'";
    }

    /// <summary>The whole plan in view and not following the player, so a click lands where it is aimed.</summary>
    private static MapSceneRendererViewModel FitForModes(RaidCockpitViewModel raid)
    {
        if (raid.FollowsPlayer)
        {
            raid.ToggleFollowCommand.Execute(null);
        }

        var renderer = raid.Renderer ?? throw new InvalidOperationException("the map is not open");
        renderer.FitPlanCommand.Execute(null);
        return renderer;
    }

    private async Task<string> MarksAsync(RaidCockpitViewModel raid, CancellationToken cancellationToken)
    {
        var placed = _placedMarks = await GalleryMarks.PlaceAsync(
            services.GetRequiredService<IRaidMarkStore>(),
            mapId,
            raid.Renderer!.Scene.Bounds).ConfigureAwait(true);

        // Only the Marks card open, so the rows and their countdowns are on screen at 1080 lines.
        foreach (var card in new[] { raid.Cards.Summary, raid.Cards.Squad, raid.Cards.Objectives, raid.Cards.Extracts, raid.Cards.ExtractSelection, raid.Cards.Route, raid.Cards.Tasks, raid.Cards.LootSelection })
        {
            card.IsExpanded = false;
        }

        raid.Cards.Marks.IsExpanded = true;
        await WaitForAsync(() => raid.Marks.Count >= placed, StepTimeout, $"{placed} marks in the Marks card", cancellationToken)
            .ConfigureAwait(true);
        return $"{raid.Marks.Count} marks";
    }

    private async Task<IReadOnlyList<QuestTaskDefinition>> QuestsOnMapAsync(int count, CancellationToken cancellationToken)
    {
        var tasks = await GallerySquad.QuestsOnMapAsync(services, main.Map, count, cancellationToken).ConfigureAwait(true);
        return tasks.Count > 0 ? tasks : throw new InvalidOperationException("the catalog has no quest placed on this map");
    }

    /// <summary>Checks on the UI thread every 100 ms; the app keeps drawing in between.</summary>
    internal static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string what, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException($"no {what} after {timeout.TotalSeconds:0} s");
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(true);
        }
    }
}

/// <summary>The demo squad the gallery and <c>tools/V2RenderPreview</c> share.</summary>
internal static class GallerySquad
{
    /// <summary>
    /// Quests with an objective at one spot on this map, by name: the ones a map picture shows.
    /// </summary>
    public static async Task<IReadOnlyList<QuestTaskDefinition>> QuestsOnMapAsync(
        IServiceProvider services,
        MapViewModel map,
        int count,
        CancellationToken cancellationToken)
    {
        var profile = await services.GetRequiredService<IPlayerProfileService>().GetActiveAsync(cancellationToken).ConfigureAwait(true);
        var language = services.GetRequiredService<QuestTrackingOptions>().NormalizedLanguage;
        var catalog = await services.GetRequiredService<IQuestCatalog>().GetAsync(profile.GameMode, language, cancellationToken).ConfigureAwait(true);
        IReadOnlyCollection<string> mapIds = [];
        await map.ProjectOtherObjectivesAsync(ids => { mapIds = ids; return null; }, cancellationToken).ConfigureAwait(true);
        if (catalog is null || mapIds.Count == 0)
        {
            return [];
        }

        return catalog.Tasks
            .Where(task => task.Objectives.Any(objective => objective.Zones.Any(zone =>
                zone.Position is not null && zone.MapId is { } id && mapIds.Contains(id, StringComparer.OrdinalIgnoreCase))))
            .OrderBy(task => task.Name, StringComparer.Ordinal)
            .Take(count)
            .ToArray();
    }

    /// <summary>
    /// [#780] Three squadmates share real catalog quests on this map, by id, as the relay would deliver them.
    /// </summary>
    public static async Task<string> ShareQuestsAsync(
        IServiceProvider services,
        MapViewModel map,
        GroupSnapshot squad,
        CancellationToken cancellationToken)
    {
        var onMap = await QuestsOnMapAsync(services, map, 6, cancellationToken).ConfigureAwait(true);
        if (onMap.Count < 4)
        {
            return $"only {onMap.Count} quests on this map";
        }

        GroupObjectiveView[] Open(QuestTaskDefinition task, int skip) =>
            [.. task.Objectives.Where(objective => objective.Optional != true).Skip(skip)
                .Select(objective => new GroupObjectiveView(task.Id, objective.Id, null))];
        var plan = new Dictionary<string, QuestTaskDefinition[]>(StringComparer.Ordinal)
        {
            ["Geo"] = [onMap[0], onMap[1]],
            ["Riley"] = [onMap[1], onMap[2]],
            ["Sam"] = [onMap[3]],
        };
        services.GetRequiredService<IRuntimeStateStore>().Update(snapshot => snapshot with
        {
            Group = squad with
            {
                Members = [.. squad.Members.Select(member => plan.TryGetValue(member.Name, out var tasks)
                    ? member with
                    {
                        QuestIds = [.. tasks.Select(task => task.Id)],
                        Objectives = [.. tasks.SelectMany((task, index) => Open(task, index))],
                    }
                    : member)],
            },
        });
        return string.Join(", ", plan.Select(pair => $"{pair.Key}={string.Join('+', pair.Value.Select(task => task.Name))}"));
    }

    /// <summary>
    /// The player mid-raid on this map with a short trail, and three squadmates around them. The
    /// positions are world positions probed from the map's own transform, so they land on the plan.
    /// </summary>
    /// <param name="now">The app's own clock, which is what reads the raid clock and the ages back.</param>
    public static (RaidSnapshot Raid, GroupSnapshot Group) Build(MapRenderModel model, DateTimeOffset now)
    {
        var mapId = model.Location.Id;
        var candidates = new List<(double X, double Z, double PlanX, double PlanY)>();
        if (MapPlanProjection.For(model) is { IsValid: true } rect)
        {
            for (var x = -1200.0; x <= 1200; x += 10)
            {
                for (var z = -1200.0; z <= 1200; z += 10)
                {
                    if (model.TryMapPosition(new(x, 0, z), out var point))
                    {
                        var planX = (point.X - rect.MinimumX) / rect.Width * 100;
                        var planY = (point.Y - rect.MinimumY) / rect.Height * 100;
                        if (planX is > 4 and < 96 && planY is > 4 and < 96)
                        {
                            candidates.Add((x, z, planX, planY));
                        }
                    }
                }
            }
        }

        WorldPosition At(double planX, double planY)
        {
            if (candidates.Count == 0)
            {
                return new(0, 0, 0);
            }

            var best = candidates.MinBy(item => Math.Pow(item.PlanX - planX, 2) + Math.Pow(item.PlanY - planY, 2));
            return new(best.X, 0, best.Z);
        }

        ScreenshotPosition Step(double planX, double planY, double heading, int secondsAgo) =>
            new(now.AddSeconds(-secondsAgo), At(planX, planY), default, heading, null, null, $"gallery-{secondsAgo}.png");

        var trail = new[] { Step(36, 64, 80, 250), Step(46, 66, 95, 210), Step(52, 58, 20, 170), Step(54, 44, 40, 90), Step(62, 42, 80, 50), Step(64, 34, 10, 15) };
        var raid = new RaidSnapshot(Guid.NewGuid(), RaidLifecycleState.InRaid, mapId, now.AddMinutes(-14), now, new(0.9), trail[^1], [], false)
        {
            Side = "PMC",
            PositionTrail = trail,
        };

        GroupTrailPointView Leg(double planX, double planY, int secondsAgo)
        {
            var at = At(planX, planY);
            return new(at.X, at.Z, TimeSpan.FromSeconds(secondsAgo));
        }

        GroupMemberView Mate(string name, double planX, double planY, double heading, int secondsAgo) =>
            new(name, mapId, RaidLifecycleState.InRaid, "PMC", At(planX, planY), heading, TimeSpan.FromSeconds(secondsAgo), [], [])
            {
                Since = TimeSpan.FromSeconds(secondsAgo),
                Trail = [Leg(planX - 9, planY + 7, 90), Leg(planX - 5, planY + 4, 45), Leg(planX, planY, secondsAgo)],
            };

        var group = new GroupSnapshot(
            true,
            [Mate("Geo", 72, 34, 300, 6), Mate("Riley", 44, 71, 120, 25), Mate("Sam", 58, 52, 30, 12)],
            "Sharing as Clay · 3 others here",
            now);
        return (raid, group);
    }
}

/// <summary>[#289/#290] One mark of each scope and several lifetimes, and a tablet's three-stop route.</summary>
internal static class GalleryMarks
{
    /// <summary>Places them through the store's own path (the one the Ctrl menu takes); returns how many.</summary>
    public static async Task<int> PlaceAsync(IRaidMarkStore store, string mapId, MapSceneBounds bounds)
    {
        MapScenePoint At(double fx, double fy) => new(bounds.MinimumX + (bounds.Width * fx), bounds.MinimumY + (bounds.Height * fy));
        // #290: two of them in a chosen colour, so the pictures show a coloured pin and ping too.
        (string? Label, RaidMarkScope Scope, RaidMarkLifetime Lifetime, MapScenePoint Point, string? Colour)[] marks =
        [
            ("Stash", RaidMarkScope.Private, RaidMarkLifetime.UntilRemoved, At(0.36, 0.36), null),
            ("Dorms", RaidMarkScope.Squad, RaidMarkLifetime.FiveMinutes, At(0.50, 0.34), "#E69F00"),
            (null, RaidMarkScope.Squad, RaidMarkLifetime.ThisRaid, At(0.62, 0.44), null),
            (null, RaidMarkScope.Private, RaidMarkLifetime.Ping, At(0.44, 0.42), null),
            (null, RaidMarkScope.Squad, RaidMarkLifetime.Ping, At(0.56, 0.40), "#CC79A7"),
        ];
        foreach (var (label, scope, lifetime, point, colour) in marks)
        {
            await store.PlaceAsync(mapId, null, point.X, point.Y, label, scope, lifetime, colour: colour).ConfigureAwait(true);
        }

        var routeId = Guid.NewGuid();
        var step = 0;
        foreach (var point in new[] { At(0.40, 0.48), At(0.48, 0.52), At(0.56, 0.49) })
        {
            await store.PlaceAsync(mapId, null, point.X, point.Y, null, RaidMarkScope.Squad, RaidMarkLifetime.UntilRemoved, default, new RaidMarkRoute(routeId, ++step))
                .ConfigureAwait(true);
        }

        return marks.Length + step;
    }
}
