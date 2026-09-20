using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Raid;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// <c>--then-map</c>: change map inside one run, the way a player does, and say what was drawn and when.
/// </summary>
/// <remarks>
/// A render of one map cannot show the fault reported on build 11, where Factory opened stretched
/// into the rectangle Customs had just been drawn in: that only exists after a switch. Nor can it
/// say how long a map takes to appear. This selects each map through the picker's own command and
/// prints, per step, a timeline of what the plan was showing (so "how long until the picture is
/// complete" is a number) and then the rectangle the VIEW laid the artwork out in, read from the
/// visual tree rather than from the view model whose notifications are the thing in question.
///
/// A step is a map id, optionally followed by ":drawing" or ":photo" to choose that artwork once
/// the map is up: <c>--then-map factory,customs:drawing,factory</c>.
/// </remarks>
internal static class MapSwitchProbe
{
    /// <summary>The name of the border the flat plan's artwork is laid out in.</summary>
    private const string PlanArtworkName = "PlanArtwork";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static void Run(Window window, MainWindowViewModel viewModel, RaidCockpitViewModel raid, string steps)
    {
        foreach (var step in steps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = step.Split(':', 2);
            var mapId = parts[0];
            var artwork = parts.Length > 1 ? parts[1] : null;
            var item = raid.MapPicker.FirstOrDefault(entry => string.Equals(entry.MapId, mapId, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                Console.Error.WriteLine($"--then-map: no map '{mapId}'.");
                continue;
            }

            Time($"select {mapId}", window, viewModel, raid, mapId, () => item.SelectCommand.Execute(null));
            ReportDrawnRectangle(window, raid, mapId);
            var wantsDrawing = string.Equals(artwork, "drawing", StringComparison.OrdinalIgnoreCase);
            if (artwork is not null && raid.HasArtworkChoice && raid.PrefersDrawing != wantsDrawing)
            {
                Time($"choose {artwork} on {mapId}", window, viewModel, raid, mapId, () => raid.ToggleArtworkCommand.Execute(null));
                ReportDrawnRectangle(window, raid, mapId);
            }
        }
    }

    /// <summary>
    /// Runs one action and prints each change in what the plan shows until it has stopped changing.
    /// </summary>
    private static void Time(string label, Window window, MainWindowViewModel viewModel, RaidCockpitViewModel raid, string mapId, Action act)
    {
        Console.WriteLine($"[switch] {label}");
        var clock = Stopwatch.StartNew();
        act();
        string? last = null;
        var lastChange = TimeSpan.Zero;
        var firstPicture = TimeSpan.MinValue;
        var longestTurn = TimeSpan.Zero;
        // Settled: the map asked for is up with a picture and nothing has changed for 1.5 s. Capped
        // at 90 s, which is already an answer ("it never finished") rather than a wait.
        while (clock.Elapsed < TimeSpan.FromSeconds(90))
        {
            var turn = Stopwatch.StartNew();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (turn.Elapsed > longestTurn)
            {
                longestTurn = turn.Elapsed;
            }

            var state = Describe(viewModel, raid);
            var showsAsked = raid.Renderer is { } renderer &&
                string.Equals(renderer.Scene.LocationId, mapId, StringComparison.OrdinalIgnoreCase);
            if (state != last)
            {
                last = state;
                lastChange = clock.Elapsed;
                Console.WriteLine(string.Create(Invariant, $"[switch] {clock.Elapsed.TotalSeconds,7:F3}s {state}"));
                if (firstPicture == TimeSpan.MinValue && showsAsked && raid.Renderer!.BackgroundImage is not null)
                {
                    firstPicture = clock.Elapsed;
                }
            }

            // V1 says "Loading ..." for as long as it is fetching, rasterising or decoding, and a
            // drawing can take longer than the settle window to rasterise with nothing else moving.
            var loading = viewModel.Map.Status.StartsWith("Loading", StringComparison.Ordinal);
            if (showsAsked && !loading && raid.Renderer!.BackgroundImage is not null && clock.Elapsed - lastChange > TimeSpan.FromSeconds(1.5))
            {
                break;
            }

            Thread.Sleep(5);
        }

        Console.WriteLine(string.Create(
            Invariant,
            $"[switch] {label}: first picture {(firstPicture == TimeSpan.MinValue ? double.NaN : firstPicture.TotalSeconds):F3}s, complete {lastChange.TotalSeconds:F3}s, longest UI turn {longestTurn.TotalMilliseconds:F0} ms"));
    }

    private static string Describe(MainWindowViewModel viewModel, RaidCockpitViewModel raid)
    {
        var map = viewModel.Map;
        if (raid.Renderer is not { } renderer)
        {
            return $"no renderer; tiles {map.Tiles.Count}; '{map.Status}'";
        }

        var art = renderer.BackgroundImage?.Size;
        return string.Create(
            Invariant,
            $"{renderer.Scene.LocationId} art {art?.Width ?? 0:F0}x{art?.Height ?? 0:F0} drawn {renderer.MapWidth:F1}x{renderer.MapHeight:F1} tiles {map.Tiles.Count} drawing={raid.PrefersDrawing} '{map.Status}'");
    }

    /// <summary>
    /// The rectangle the view gave the artwork, against the artwork's own shape.
    /// </summary>
    private static void ReportDrawnRectangle(Window window, RaidCockpitViewModel raid, string mapId)
    {
        Dispatcher.UIThread.RunJobs();
        var border = window.GetVisualDescendants().OfType<Border>().FirstOrDefault(item => item.Name == PlanArtworkName);
        if (border is null || raid.Renderer is not { } renderer)
        {
            Console.Error.WriteLine($"--then-map: no plan artwork on screen for '{mapId}'.");
            return;
        }

        var laidOut = border.Bounds;
        var art = renderer.BackgroundImage?.Size ?? default;
        var viewAspect = laidOut.Height > 0 ? laidOut.Width / laidOut.Height : double.NaN;
        var artAspect = art.Height > 0 ? art.Width / art.Height : double.NaN;
        // What the width would have to be for this height to be the artwork's own shape; the
        // difference is how many pixels of stretch a player is looking at.
        var stretch = double.IsFinite(artAspect) ? laidOut.Width - (laidOut.Height * artAspect) : double.NaN;
        Console.WriteLine(string.Create(
            Invariant,
            $"[switch] {mapId} VIEW rectangle {laidOut.Width:F1}x{laidOut.Height:F1} = {viewAspect:F4}; view model {renderer.MapWidth:F1}x{renderer.MapHeight:F1}; artwork {art.Width:F0}x{art.Height:F0} = {artAspect:F4}; stretched by {stretch:F1} px"));
    }

    /// <summary>
    /// <c>--map-cache</c>: keeps downloaded map artwork between runs, which a throwaway data root never does.
    /// </summary>
    /// <remarks>
    /// Every run starts with an empty cache and downloads each tile, which is a player's first
    /// launch ever and no launch after it. A link rather than a copy, so what one run fetched the
    /// next one finds. Removed again before the data root is deleted.
    /// </remarks>
    public static void LinkMapCache(string dataRoot, string? shared)
    {
        if (shared is null)
        {
            return;
        }

        Directory.CreateDirectory(shared);
        var cache = TarkovCompanion.App.Services.AppDataPaths.Resolve(dataRoot, demoMode: true).Cache;
        Directory.CreateDirectory(cache);
        Directory.CreateSymbolicLink(Path.Combine(cache, "Maps"), Path.GetFullPath(shared));
    }

    public static void UnlinkMapCache(string dataRoot)
    {
        var link = Path.Combine(TarkovCompanion.App.Services.AppDataPaths.Resolve(dataRoot, demoMode: true).Cache, "Maps");
        if (new DirectoryInfo(link) is { Exists: true, LinkTarget: not null } info)
        {
            info.Delete();
        }
    }
}
