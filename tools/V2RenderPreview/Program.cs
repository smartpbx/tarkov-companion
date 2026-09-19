using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.Services.V2.Profile;
using TarkovCompanion.App.ViewModels.V2.Tablet;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.Services.V2.SelfTest;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.App.Views;
using AppClass = TarkovCompanion.App.App;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// V2 rough package 15: a dev-only tool that boots the real composition (demo fixture data, the
/// real MainWindow/V2ShellView, no game or network touched) under Avalonia's headless platform
/// and saves one PNG of the rendered window. It exists so a V2 UI change can be compared against
/// docs/design/v2's concept renders on Linux, where there is no display and the Windows-only
/// windows-page-gallery.ps1 script cannot run. Not part of scripts/build.sh or scripts/test.sh.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var width = IntOption(args, "--width", 1920);
        var height = IntOption(args, "--height", 1080);
        var outputPath = StringOption(args, "--out") ?? throw new ArgumentException("--out <path.png> is required.");
        var mapId = StringOption(args, "--map");
        // V2 rough package 17: land on a workspace other than the variant's landing page
        // ("intel", "intel/item/<id>"), and optionally run a search there first.
        var route = StringOption(args, "--route");
        var search = StringOption(args, "--search");
        // Package 28: run a Flea lookup, so the Flea workspace can be rendered with results.
        var fleaQuery = StringOption(args, "--flea-query");
        var options = AppCommandLine.Parse(args) with { Demo = true };

        var rendered = false;
        var dataRoot = Path.Combine(Path.GetTempPath(), $"v2-render-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);

        // V2 rough package 17: the demo fixture seeds one item and no quests or hideout, so a
        // Plan render showed only empty states. --seed-database copies an existing synced
        // database (any earlier real run's tarkov-companion.db) into the throwaway data root;
        // startup migrates it forward, and it is deleted with the root afterwards.
        if (StringOption(args, "--seed-database") is { } seedDatabase)
        {
            var databaseDirectory = AppDataPaths.Resolve(dataRoot, demoMode: true).Database;
            Directory.CreateDirectory(databaseDirectory);
            File.Copy(seedDatabase, Path.Combine(databaseDirectory, "tarkov-companion.db"));
        }
        try
        {
            // Not disposed: some services' DisposeAsync continues on the UI dispatcher, which
            // nothing pumps once the frame is saved, so awaiting it hung the process after
            // "Saved" (package 17). The process exits right after the finally block instead.
            var services = AppComposition.Build(options, new AppCompositionSettings(DataRoot: dataRoot, Offline: true));

            AppBuilder.Configure(() => new AppClass(services))
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                // Binding and layout warnings are exactly what the Windows page gallery fails on;
                // printing them here lets a Linux run catch the same faults before CI does.
                .LogToTextWriter(Console.Out, Avalonia.Logging.LogEventLevel.Warning, Avalonia.Logging.LogArea.Binding, Avalonia.Logging.LogArea.Layout)
                .SetupWithoutStarting();

            if (options.MapRendererGallery)
            {
                var gallery = new TarkovCompanion.App.Views.V2.MapRenderer.MapSceneRendererGalleryWindow(
                    options.MapRendererLargeText, options.MapRendererLootOffline) { Width = width, Height = height };
                gallery.Show();
                Pump(40);
                var ids = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(gallery)
                    .Select(Avalonia.Automation.AutomationProperties.GetAutomationId)
                    .Where(id => id is not null && (id.Contains("cluster", StringComparison.Ordinal) || id is "v2-map-zoom-in" or "v2-map-loot-preset"))
                    .Distinct();
                Console.WriteLine("Automation ids: " + string.Join(", ", ids));
                // The Windows gallery toggles layer switches through UI Automation, and Toggle
                // on a disabled control throws rather than doing nothing. Printing each switch's
                // state lets a Linux run see that before the 30-minute Windows run does.
                var switches = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(gallery)
                    .OfType<Avalonia.Controls.Primitives.ToggleButton>()
                    .Select(toggle => (Id: Avalonia.Automation.AutomationProperties.GetAutomationId(toggle), toggle.IsEffectivelyEnabled))
                    .Where(toggle => toggle.Id?.StartsWith("v2-map-layer-", StringComparison.Ordinal) == true)
                    .Select(toggle => $"{toggle.Id} {(toggle.IsEffectivelyEnabled ? "enabled" : "DISABLED")}");
                Console.WriteLine("Layer switches: " + string.Join(", ", switches));

                // --map-renderer-toggle-layer <automation id> presses one switch the way the
                // gallery's toggle step does, and reports what the switch is called afterwards.
                if (StringOption(args, "--map-renderer-toggle-layer") is { } toggleId)
                {
                    Avalonia.Controls.Primitives.ToggleButton? Find() =>
                        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(gallery)
                            .OfType<Avalonia.Controls.Primitives.ToggleButton>()
                            .FirstOrDefault(toggle => Avalonia.Automation.AutomationProperties.GetAutomationId(toggle) == toggleId);
                    var target = Find() ?? throw new InvalidOperationException($"No switch is named '{toggleId}'.");
                    if (!target.IsEffectivelyEnabled)
                    {
                        throw new InvalidOperationException($"'{toggleId}' is disabled; UI Automation's Toggle would throw on it.");
                    }

                    target.Command?.Execute(target.CommandParameter);
                    Pump(20);
                    var after = Find();
                    Console.WriteLine(
                        $"After toggle: name '{(after is null ? null : Avalonia.Automation.AutomationProperties.GetName(after))}', " +
                        $"{(after?.IsEffectivelyEnabled == true ? "enabled" : "disabled")}");
                }

                SaveFrame(gallery, outputPath, width, height);
                rendered = true;
                return 0;
            }

            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            V2ShellViewModel? shell = null;
            Task? seeding = null;
            if (options.UiShell.IsPreview())
            {
                shell = services.GetRequiredService<V2ShellViewModel>();
                viewModel.PreviewShell = shell;
                services.GetRequiredService<V2ShellCaptureBridge>();
                // [V2 rough package 22] Started, not awaited: since #9f7c369 this waits for the
                // database feature, which only becomes ready inside MainWindowViewModel
                // .InitializeAsync below. Draining it here deadlocked every render run — the
                // drain loop pumped the dispatcher forever for a task whose gate had not been
                // opened yet. It is drained after initialization instead.
                seeding = services.GetRequiredService<LegacyProfileContextBootstrap>().EnsureSeededAsync(CancellationToken.None);
            }
            else if (options.StartPage is { } startPage && !viewModel.Navigate(startPage))
            {
                throw new ArgumentException($"No destination is named '{startPage}'.");
            }

            var window = new MainWindow { DataContext = viewModel, Width = width, Height = height };
            window.Show();
            DrainUntilComplete(viewModel.InitializeAsync());
            if (seeding is not null)
            {
                DrainUntilComplete(seeding);
            }

            // A fresh profile has no quest recorded as active, so the Plan page has nothing to
            // plan. --seed-active-quests marks that many available quests active through the same
            // command service the page itself uses, then reloads the page.
            if (IntOption(args, "--seed-active-quests", 0) is var questCount and > 0)
            {
                DrainUntilComplete(SeedActiveQuestsAsync(services, questCount));
                DrainUntilComplete(services.GetRequiredService<PlanWorkspaceViewModel>().RefreshAsync());
            }

            // Package 35: name the quests to mark active, by task id, where the first few available
            // quests are not the ones a render is about (a map's objectives with zones, say).
            if (StringOption(args, "--seed-quest-tasks") is { } taskIds)
            {
                DrainUntilComplete(SeedTasksAsync(services, taskIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
                DrainUntilComplete(services.GetRequiredService<PlanWorkspaceViewModel>().RefreshAsync());
            }

            // Package 28: put the Plan workspace on a filter, a search and a level before the frame.
            var planFilter = StringOption(args, "--plan-filter");
            var planSearch = StringOption(args, "--plan-search");
            var planLevel = IntOption(args, "--plan-level", 0);
            if (planFilter is not null || planSearch is not null || planLevel > 0)
            {
                var plan = services.GetRequiredService<PlanWorkspaceViewModel>();
                if (planLevel > 0)
                {
                    DrainUntilComplete(plan.SetPlayerLevelAsync(planLevel));
                }

                if (planFilter is not null)
                {
                    plan.Filter = Enum.Parse<PlanQuestFilter>(planFilter, ignoreCase: true);
                }

                if (planSearch is not null)
                {
                    plan.SearchText = planSearch;
                }

                Pump(40);
            }

            // [V2 rough package 46] The chrome the player can now collapse, so a render can show
            // the map at each of the widths it can have.
            if (shell is not null && StringOption(args, "--nav-rail") is { } railMode)
            {
                var wanted = TarkovCompanion.App.Services.V2.Shell.V2NavigationRailTokens.Parse(railMode);
                for (var guard = 0; guard < 3 && shell.NavigationRail != wanted; guard++)
                {
                    shell.CycleNavigationRail();
                }

                Pump(10);
                Console.WriteLine($"Nav rail: {shell.NavigationRail}");
            }

            if (shell?.RaidCockpit is TarkovCompanion.App.ViewModels.V2.Raid.RaidCockpitViewModel panelCockpit)
            {
                if (args.Contains("--hide-raid-panel") && panelCockpit.ShowsContextPanel)
                {
                    panelCockpit.ToggleContextPanel();
                    Pump(10);
                }

                if (IntOption(args, "--raid-panel-width", 0) is var panelWidth and > 0)
                {
                    panelCockpit.ResizeContextPanel(panelWidth);
                    Pump(10);
                }

                Console.WriteLine(
                    $"Raid panel: {(panelCockpit.ShowsContextPanel ? $"{panelCockpit.ContextPanelWidth:F0}px" : "hidden")}");
            }

            if (shell is not null && route is not null)
            {
                var result = shell.Router.NavigateToAddress(route);
                if (!result.Succeeded)
                {
                    throw new ArgumentException($"The shell refused '{route}': {result.Failure}");
                }

                Pump(20);
            }

            // Package 29 (parity): Setup is one route with sections inside it, so a render names the
            // section the same way its tab does ("progress", "privacy", "diagnostics", ...).
            if (shell?.SetupWorkspace is { } setup && StringOption(args, "--setup-section") is { } sectionName)
            {
                if (!Enum.TryParse<V2SetupSection>(sectionName, ignoreCase: true, out var section))
                {
                    throw new ArgumentException($"No Setup section is named '{sectionName}'.");
                }

                setup.Select(section);
                Pump(20);
            }

            // Package 28: a Loadout with one item assigned and evaluated, and an Events page with one
            // event holding a few items, through the pages' own commands.
            if (StringOption(args, "--loadout-demo") is { } loadoutQuery)
            {
                var loadout = viewModel.Loadout;
                loadout.SearchQuery = loadoutQuery;
                DrainUntilComplete(loadout.SearchCommand.ExecuteAsync());
                if (loadout.Results.Count > 0)
                {
                    loadout.Results[0].AssignCommand.Execute(null);
                    Pump(60);
                }

                DrainUntilComplete(loadout.EvaluateCommand.ExecuteAsync());
                Pump(20);
            }

            if (args.Contains("--events-demo"))
            {
                var events = viewModel.Events;
                events.NewEventName = "Halloween 2026";
                DrainUntilComplete(events.CreateCommand.ExecuteAsync());
                Pump(40);
                events.ItemQuery = "bandage";
                DrainUntilComplete(events.SearchCommand.ExecuteAsync());
                foreach (var match in events.Matches.Take(3).ToArray())
                {
                    match.AddCommand.Execute(null);
                    Pump(60);
                }

                if (events.Items.Count > 0)
                {
                    events.Items[0].MarkSafeCommand.Execute(null);
                    Pump(60);
                }
            }

            if (fleaQuery is not null)
            {
                viewModel.Flea.SearchQuery = fleaQuery;
                DrainUntilComplete(viewModel.Flea.SearchAsync());
                Pump(20);
            }

            if (shell is not null && search is not null)
            {
                shell.SearchText = search;
                DrainUntilComplete(shell.SearchAsync());
                Pump(20);
            }

            // The Raid workspace's map follows whatever the legacy MapViewModel is already
            // showing; a headless run has nobody at the V1 Raid page to have selected one, so
            // pick a map here the same way the map picker's own SelectCommand does, once the
            // map catalog itself has finished loading.
            if (shell?.RaidCockpit is TarkovCompanion.App.ViewModels.V2.Raid.RaidCockpitViewModel raid)
            {
                for (var i = 0; i < 200 && raid.MapPicker.Count == 0; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(25);
                }

                // A page other than Raid picks its own map (Plan follows its selected map group),
                // so only a Raid render, or an explicit --map, chooses one here.
                var picked = mapId is null && options.StartPage is not null
                    ? null
                    : mapId is null
                    ? raid.MapPicker.FirstOrDefault()
                    : raid.MapPicker.FirstOrDefault(item => string.Equals(item.MapId, mapId, StringComparison.OrdinalIgnoreCase));
                if (picked is not null)
                {
                    picked.SelectCommand.Execute(null);
                    // V2 rough package 20: the scene rebuild and the artwork decode are async, so
                    // 40 dispatcher turns was enough for the catalog's first map and not for a
                    // switch to another one — a --map streets run rendered Customs. Wait for the
                    // renderer to actually be showing the map that was asked for.
                    for (var i = 0; i < 400; i++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        if (string.Equals(raid.Renderer?.Scene.LocationId, picked.MapId, StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        Thread.Sleep(25);
                    }

                    Pump(40);
                    if (!string.Equals(raid.Renderer?.Scene.LocationId, picked.MapId, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Error.WriteLine(
                            $"The Raid map never became '{picked.MapId}' (showing '{raid.Renderer?.Scene.LocationId}').");
                    }
                }
                else if (raid.MapPicker.Count == 0)
                {
                    Console.Error.WriteLine("No map available to select; the map picker stayed empty.");
                }
                else
                {
                    Pump(80);
                }

                // [V2 rough package 39] Layers the map does not open with, by scene layer id,
                // so a render can show what a player would after one press each.
                if (StringOption(args, "--map-layers") is { } wanted && raid.Renderer is { } layerRenderer)
                {
                    foreach (var layerId in wanted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var layer = layerRenderer.Layers.FirstOrDefault(item =>
                            string.Equals(item.Layer.Id.Value, layerId, StringComparison.OrdinalIgnoreCase));
                        if (layer is null)
                        {
                            Console.Error.WriteLine($"No map layer '{layerId}'.");
                        }
                        else if (!layer.IsVisible)
                        {
                            layer.ToggleCommand.Execute(null);
                            Pump(10);
                        }
                    }

                    Pump(20);
                }

                // [V2 rough package 39] Two render-only presses, both of them the app's own
                // controls rather than a fixture: choose the drawing (the stack needs it — a
                // tile grid and a drawing cover different rectangles), then stack the floors.
                if (args.Contains("--map-drawing") && raid.HasArtworkChoice && !raid.PrefersDrawing)
                {
                    raid.ToggleArtworkCommand.Execute(null);
                    for (var i = 0; i < 400 && !raid.PrefersDrawing; i++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        Thread.Sleep(25);
                    }

                    Pump(40);
                }

                if (args.Contains("--map-stacked"))
                {
                    if (!raid.CanStack)
                    {
                        Console.Error.WriteLine("This map has no floors to stack.");
                    }
                    else
                    {
                        raid.ToggleStackCommand.Execute(null);
                        for (var i = 0; i < 400 && !raid.HasFloorStack; i++)
                        {
                            Dispatcher.UIThread.RunJobs();
                            Thread.Sleep(25);
                        }

                        Pump(40);
                        Console.WriteLine("Stack: " + raid.StackStatus);
                    }
                }

                // V2 rough package 20: which maps a --map value can name, so a render run that
                // asks for one that is not in this install's catalog says so instead of quietly
                // rendering whichever map came first.
                Console.WriteLine("Maps: " + string.Join(", ", raid.MapPicker.Select(item => item.MapId)));

                // Package 35: a floor by name, then an objective by its number, the way the floor
                // chooser and the objective list are used.
                if (StringOption(args, "--floor") is { } floorName)
                {
                    var floor = raid.Renderer?.Floors.FirstOrDefault(item => string.Equals(item.Name, floorName, StringComparison.OrdinalIgnoreCase));
                    if (floor is null)
                    {
                        Console.Error.WriteLine($"No floor named '{floorName}'; the map has: {string.Join(", ", raid.Renderer?.Floors.Select(item => item.Name) ?? [])}.");
                    }
                    else
                    {
                        floor.SelectCommand.Execute(null);
                        // A floor is its own artwork, fetched and drawn asynchronously: wait until the
                        // plan is showing it rather than photographing the map mid-swap.
                        for (var i = 0; i < 400; i++)
                        {
                            Dispatcher.UIThread.RunJobs();
                            if (raid.HasRenderer &&
                                string.Equals(raid.Renderer!.Scene.View.SelectedFloorId, floor.Id, StringComparison.OrdinalIgnoreCase) &&
                                i > 20)
                            {
                                break;
                            }

                            Thread.Sleep(25);
                        }

                        Pump(80);
                    }
                }

                Console.WriteLine("Quest layer: " + viewModel.Map.QuestLayerStatus);
                Console.WriteLine("Objectives: " + string.Join(" | ", raid.QuestObjectives.Select(row => $"{(row.HasNumber ? row.Number : "-")} {row.Where}")));
                if (StringOption(args, "--select-objective") is { } objectiveNumber)
                {
                    var row = raid.QuestObjectives.FirstOrDefault(item => item.Number == objectiveNumber);
                    if (row is null)
                    {
                        Console.Error.WriteLine($"No objective is numbered '{objectiveNumber}'.");
                    }
                    else
                    {
                        row.SelectCommand.Execute(null);
                        Pump(40);
                        Console.WriteLine($"Selected: objective {raid.SelectedObjective?.Number}, map marker '{raid.Renderer?.SelectedObject?.Label}' ({raid.Renderer?.SelectedObject?.SceneObject?.Id.Value})");
                    }
                }
                // [V2 rough package 39] Which artwork this map actually publishes, so a render
                // that shows no chooser says whether that is a bug or a one-variant map.
                Console.WriteLine("Artwork: " + string.Join(
                    ", ",
                    raid.ArtworkVariants.Select(item => item.Key + (item.IsSelected ? "*" : string.Empty))));

                // [V2 rough package 46] How much of the map card the floating pill in its
                // top-left corner covers, which is artwork nobody can see.
                if (window.GetVisualDescendants()
                        .OfType<Avalonia.Controls.Border>()
                        .FirstOrDefault(border => border.Classes.Contains("v2-map-float")) is { } pill &&
                    raid.Renderer is { } pillRenderer)
                {
                    var card = pillRenderer.CanvasWidth * pillRenderer.CanvasHeight;
                    var covered = pill.Bounds.Width * pill.Bounds.Height;
                    Console.WriteLine(
                        $"Top-left block: {pill.Bounds.Width:F0}x{pill.Bounds.Height:F0} = {covered:F0} px, " +
                        $"{(card > 0 ? covered / card : 0):P1} of the map card");
                }

                // [V2 rough package 46] The two numbers the aspect-ratio bug lives between: the
                // rectangle the plan is actually drawn into, and the artwork's own pixels. A
                // render that looks plausible can still be stretched by a per-cent nobody sees.
                if (raid.Renderer is { } aspectRenderer)
                {
                    var art = aspectRenderer.BackgroundImage?.Size;
                    var drawn = aspectRenderer.MapHeight > 0 ? aspectRenderer.MapWidth / aspectRenderer.MapHeight : double.NaN;
                    var intrinsic = art is { Width: > 0, Height: > 0 } size ? size.Width / size.Height : double.NaN;
                    Console.WriteLine(
                        $"Plan aspect: card {aspectRenderer.CanvasWidth:F1}x{aspectRenderer.CanvasHeight:F1}, " +
                        $"drawn {aspectRenderer.MapWidth:F1}x{aspectRenderer.MapHeight:F1} = {drawn:F5}, " +
                        $"artwork {art?.Width ?? 0:F0}x{art?.Height ?? 0:F0} = {intrinsic:F5}, " +
                        $"error {(double.IsFinite(drawn) && double.IsFinite(intrinsic) ? (drawn / intrinsic) - 1 : double.NaN):P3}");
                }
            }

            // A handful of extra dispatcher turns for layout, DynamicResource resolution, and
            // the map scene's own async asset resolution to settle after data arrives.
            Pump(20);

            // The map canvas sizes itself from PlanViewport.Bounds via a SizeChanged handler.
            // At larger widths that handler's first firing can land before the window's own
            // layout has settled at its final requested size, leaving the canvas sized to an
            // earlier, smaller pass. A nudge-and-restore forces one more SizeChanged once
            // everything else (map data, the details-panel toggle) has already settled.
            window.Width = width - 1;
            Pump(5);
            window.Width = width;
            Pump(10);

            // [V2 rough package 39] The Raid workspace's context panel is a scroller taller than
            // any screen, so a card further down it cannot be photographed without scrolling to
            // it — which is exactly what a player does.
            if (IntOption(args, "--raid-panel-scroll", 0) is var panelScroll and > 0)
            {
                var panel = window.GetVisualDescendants()
                    .OfType<ScrollViewer>()
                    .FirstOrDefault(scroller => scroller.Name == "RaidPanelScroll");
                if (panel is null)
                {
                    Console.Error.WriteLine("No Raid context panel to scroll.");
                }
                else
                {
                    panel.Offset = panel.Offset.WithY(panelScroll);
                    Pump(10);
                }
            }

            // Package 17 (team): a render-only group, so the Team workspace can be seen populated.
            // A headless run has no relay to join, and the offline group session republishes
            // "not sharing" on its own tick, so this goes straight to the view model last.
            if (shell is not null && args.Contains("--team-demo"))
            {
                var store = services.GetRequiredService<TarkovCompanion.Application.Services.Runtime.IRuntimeStateStore>();
                var demo = TeamDemoGroup(viewModel.Map.RenderModel);
                var party = DemoParty();
                for (var i = 0; i < 6; i++)
                {
                    // The shell re-applies the store's snapshot on every refresh (the raid clock
                    // alone ticks once a second), so the store carries the demo group too.
                    store.Update(snapshot => snapshot with { Group = demo, Squad = party });
                    services.GetRequiredService<TarkovCompanion.App.ViewModels.V2.Team.TeamWorkspaceViewModel>()
                        .Apply(store.Current);
                    Pump(1);
                }
            }

            // [V2 rough package 48] The pairing panel's four states, inside Team > Devices. None of
            // them can be reached in a render without a relay and a tablet on the other end, so the
            // view model is put into each one through its own preview seam and the real view draws
            // it. What is being photographed is the layout and the wording, which is what was wrong.
            if (shell is not null && StringOption(args, "--pairing-demo") is { } pairingState)
            {
                var pairing = services.GetRequiredService<CompanionPairingViewModel>();
                switch (pairingState)
                {
                    case "unclaimed":
                        pairing.PresentForPreview(
                            RelayOwnerClaimState.NotClaimed,
                            CompanionPairingStage.Idle);
                        break;
                    case "unclaimable":
                        pairing.PresentForPreview(
                            RelayOwnerClaimState.NotConfiguredForClaiming,
                            CompanionPairingStage.Idle,
                            claimMessage: CompanionPairingViewModel.NotConfiguredForClaimingMessage);
                        break;
                    case "claimed":
                        pairing.PresentForPreview(
                            RelayOwnerClaimState.ClaimedByThisDesktop,
                            CompanionPairingStage.Idle,
                            claimMessage: "Claimed. This desktop is now the relay's owner.");
                        break;
                    case "pairing":
                        pairing.PresentForPreview(
                            RelayOwnerClaimState.ClaimedByThisDesktop,
                            CompanionPairingStage.AwaitingTablet,
                            pairingCode: "K7M2-9QRT-4B");
                        break;
                    case "approving":
                        pairing.PresentForPreview(
                            RelayOwnerClaimState.ClaimedByThisDesktop,
                            CompanionPairingStage.AwaitingApproval,
                            verificationCode: "48 15 62",
                            requestedDisplayName: "Kitchen tablet");
                        break;
                    case "paired":
                        pairing.PresentForPreview(
                            RelayOwnerClaimState.ClaimedByThisDesktop,
                            CompanionPairingStage.Idle,
                            claimMessage: "Claimed. This desktop is now the relay's owner.",
                            devices: [DemoPairedDevice("Kitchen tablet")]);
                        break;
                    default:
                        throw new ArgumentException($"No pairing demo state is named '{pairingState}'.");
                }

                Pump(40);
            }

            // [V2 rough package 22] A render-only raid: a player position with a heading, the
            // trail behind it, and a squad standing around. Everything downstream of it is the
            // real path — the runtime store, MainWindowViewModel.Apply, MapViewModel.ShowPlayer,
            // the cockpit's scene build — so a render proves the wiring, not a fixture.
            if (shell is not null && args.Contains("--raid-demo"))
            {
                var store = services.GetRequiredService<TarkovCompanion.Application.Services.Runtime.IRuntimeStateStore>();
                var demo = RaidDemo(viewModel.Map.RenderModel);
                for (var i = 0; i < 8; i++)
                {
                    store.Update(snapshot => snapshot with { Raid = demo.Raid, Group = demo.Group });
                    Pump(2);
                }

                Pump(20);
            }

            // Package 29 (parity): raids written through the real history service, so Debrief lists
            // and selects them the way it does for a player's own. The newest carries a trail on the
            // shown map; --watch then presses "Watch on map" and the render lands on the Raid map.
            if (shell is not null && args.Contains("--debrief-demo"))
            {
                var seeded = SeedDebriefAsync(services, RaidDemo(viewModel.Map.RenderModel).Raid);
                DrainUntilComplete(seeded);
                var debrief = services.GetRequiredService<TarkovCompanion.App.ViewModels.V2.Debrief.DebriefWorkspaceViewModel>();
                DrainUntilComplete(debrief.LoadAsync());
                // The demo composition records a live raid of its own, which is the newest and so
                // the one Debrief selects; pick the seeded one, the one with a trail to look at.
                DrainUntilComplete(debrief.SelectRaidAsync(seeded.Result, CancellationToken.None));
                Pump(20);
                if (args.Contains("--watch"))
                {
                    services.GetRequiredService<TarkovCompanion.App.ViewModels.V2.Debrief.DebriefWorkspaceViewModel>()
                        .WatchOnMapCommand.Execute(null);
                    // The shell picks the raid's map, opens the replay and navigates: three async steps.
                    Pump(120);
                }
            }

            // Package 17 (scan): render-only fixtures so the Loot decision and Stash scan
            // workspaces can be seen populated. Both go through the real services (the loot
            // planner, the snapshot store), so nothing here invents presentation state.
            if (shell is not null && args.Contains("--loot-demo"))
            {
                var profile = services.GetRequiredService<TarkovCompanion.Application.Services.Runtime.IRuntimeStateStore>()
                    .Current.Profile ?? throw new InvalidOperationException("The demo composition has no profile.");
                var scope = new TarkovCompanion.Core.Domain.Inventory.InventoryProfileScope(
                    profile.Id, profile.ProfileGeneration, profile.GameMode.ToString());
                shell.ShowLootScanResult(new TarkovCompanion.App.ViewModels.V2.LootScan.LootScanViewModel(
                    ScanDemo.LootResult(scope)));
                Pump(20);
            }

            // Package 37: the same workspace over a picture the shipped recognizer actually read.
            if (shell is not null && StringOption(args, "--loot-scan-frame") is { } lootFrame)
            {
                var scan = ScanFrame.EvaluateAsync(
                    services,
                    lootFrame,
                    StringOption(args, "--icon-cache"),
                    StringOption(args, "--loot-scan-now"));
                DrainUntilComplete(scan);
                shell.ShowLootScanResult(new TarkovCompanion.App.ViewModels.V2.LootScan.LootScanViewModel(scan.Result));
                Pump(20);
            }

            if (shell is not null && args.Contains("--stash-demo"))
            {
                var profile = services.GetRequiredService<TarkovCompanion.Application.Services.Runtime.IRuntimeStateStore>()
                    .Current.Profile ?? throw new InvalidOperationException("The demo composition has no profile.");
                var scope = new TarkovCompanion.Core.Domain.Inventory.InventoryProfileScope(
                    profile.Id, profile.ProfileGeneration, profile.GameMode.ToString());
                var store = services.GetRequiredService<TarkovCompanion.Core.Domain.Stash.IStashSnapshotStore>();
                DrainUntilComplete(store.SaveAsync(ScanDemo.StashRecord(scope), CancellationToken.None));
                DrainUntilComplete(services.GetRequiredService<TarkovCompanion.App.ViewModels.V2.StashScan.StashScanWorkspaceViewModel>()
                    .LoadAsync());
                Pump(20);
            }

            // Package 40: the guided full-stash scan, driven through the composed services from
            // painted screenshots. "mid" stops after two of three screens; "complete" finishes;
            // "unnamed" is the application as it ships, where no tile can be named yet.
            if (shell is not null && StringOption(args, "--stash-scan-demo") is { } stashScanDemo)
            {
                var stashScan = services.GetRequiredService<TarkovCompanion.App.ViewModels.V2.StashScan.StashScanWorkspaceViewModel>();
                var guided = services.GetRequiredService<TarkovCompanion.Application.Services.StashScan.GuidedStashScanService>();
                DrainUntilComplete(stashScan.LoadAsync());
                stashScan.StartSelectedScanCommand.Execute(null);
                Pump(10);
                var complete = stashScanDemo.StartsWith("complete", StringComparison.Ordinal);
                DrainUntilComplete(StashScanDemo.AddScreensAsync(
                    guided,
                    complete ? [0, 10, 20] : [0, 10],
                    nameItems: !stashScanDemo.EndsWith("unnamed", StringComparison.Ordinal)));
                Pump(10);
                if (complete)
                {
                    stashScan.FinishScanCommand.Execute(null);
                    Pump(40);
                }

                Pump(20);
            }

            // [V2 rough package 41] Setup's self-test, run before the frame. --selftest-demo
            // substitutes fixtures at the readings seam so all three verdicts are on screen;
            // --selftest-live runs the composed readings, which on a machine with no game
            // installed is what "could not be tested" actually looks like.
            if (shell?.SetupWorkspace is { } setupWorkspace &&
                (args.Contains("--selftest-demo") || args.Contains("--selftest-live")))
            {
                setupWorkspace.Select(V2SetupSection.Diagnostics);
                if (args.Contains("--selftest-demo"))
                {
                    // [V2 rough package 43a] --selftest-waiting renders the state that used to be
                    // a red failure: every other capability settled, and the screenshot one open.
                    var waiting = args.Contains("--selftest-waiting");
                    setupWorkspace.AttachSelfTest(new SetupSelfTestViewModel(
                        () => new SelfTestDemoReadings(waiting),
                        new SelfTestJournal()));
                }

                Pump(2);
                DrainUntilComplete(setupWorkspace.SelfTest!.RunAsync());
                Pump(20);
            }

            SaveFrame(window, outputPath, width, height);
            if (StringOption(args, "--crop") is { } crop)
            {
                SaveCrop(window, outputPath, crop, IntOption(args, "--crop-scale", 4));
            }
            rendered = true;
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(dataRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: this is a throwaway temp directory for one render.
            }

            // Headless Avalonia and the composition's background services keep foreground
            // threads alive; a finished render is one frame, so end the process explicitly.
            if (rendered)
            {
                Console.Out.Flush();
                Environment.Exit(0);
            }
        }
    }

    private static async Task SeedTasksAsync(IServiceProvider services, IReadOnlyList<string> taskIds)
    {
        var profile = await services.GetRequiredService<IPlayerProfileService>().GetActiveAsync(CancellationToken.None);
        var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
        var commands = services.GetRequiredService<IQuestProgressCommandService>();
        foreach (var taskId in taskIds)
        {
            await commands.SetTaskStateAsync(scope, taskId, RecordedTaskState.Active, CancellationToken.None);
        }

        Console.WriteLine($"Marked {taskIds.Count} named quest(s) active.");
    }

    /// <summary>Three raids through the real history store; the newest of them is the one with a trail, and its id is returned.</summary>
    private static async Task<Guid> SeedDebriefAsync(IServiceProvider services, TarkovCompanion.Core.Domain.Raids.RaidSnapshot shown)
    {
        // The store itself, not IRaidHistoryService: that is the outbox, which accepts closed typed
        // commands only, and a fixture has no game to observe them from. The app still reads
        // through the outbox, so Debrief lists these exactly as it lists a player's own raids.
        IRaidHistoryService history = services.GetRequiredService<TarkovCompanion.Infrastructure.Persistence.Repositories.SqliteRaidHistoryService>();
        var profile = await services.GetRequiredService<IPlayerProfileService>().GetActiveAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

        async Task<Guid> Raid(string map, TimeSpan startedAgo, TimeSpan length, string? outcome, string? notes)
        {
            var started = now - startedAgo;
            var id = await history.StartAsync(
                new(Guid.NewGuid(), profile.Id, map, "Pmc", started, null, null, null),
                CancellationToken.None);
            await history.EndAsync(id, started + length, outcome, notes, CancellationToken.None);
            return id;
        }

        await Raid("factory4_day", TimeSpan.FromDays(3), TimeSpan.FromMinutes(21), "Survived", null);
        await Raid("woods", TimeSpan.FromDays(1), TimeSpan.FromMinutes(38), null, "Ran the sawmill");
        var newest = await Raid(shown.MapId ?? "customs", TimeSpan.FromHours(2), TimeSpan.FromMinutes(27), null, "Dorms then RUAF roadblock");
        // Re-timed to fall inside the raid they belong to: the demo trail is stamped minutes ago, and
        // the raid above started two hours back.
        var raidStart = now - TimeSpan.FromHours(2);
        var index = 0;
        foreach (var step in shown.PositionTrail)
        {
            index++;
            var stamped = step with { Timestamp = raidStart + TimeSpan.FromMinutes(2.5 * index) };
            await history.RecordEventAsync(
                newest,
                "position",
                stamped.Timestamp,
                System.Text.Json.JsonSerializer.Serialize(stamped, json),
                CancellationToken.None);
        }

        return newest;
    }

    /// <summary>A party of two as the game announces one: a leader who is ready and a member who is not.</summary>
    private static TarkovCompanion.Core.Domain.Raids.SquadSnapshot DemoParty()
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            [
                new(null, null, "Geo", "Usec", 42, true, true, null, []),
                new(null, null, "Riley", "Bear", 37, false, false, now.AddMinutes(14), []),
            ],
            now.AddSeconds(-40),
            TimeSpan.FromSeconds(38),
            now);
    }

    private static async Task SeedActiveQuestsAsync(IServiceProvider services, int count)
    {
        var profile = await services.GetRequiredService<IPlayerProfileService>().GetActiveAsync(CancellationToken.None);
        var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
        var board = await services.GetRequiredService<IQuestReadService>().GetQuestBoardAsync(scope, CancellationToken.None);
        var commands = services.GetRequiredService<IQuestProgressCommandService>();
        // Preview data only: the first few quests with map objectives, spread over three maps
        // so the page has more than one map bundle. Eligibility is ignored; a fresh profile's is
        // mostly indeterminate.
        var picked = board.Tasks
            .Where(task => task.Objectives.Any(objective => objective.MapIds.Count > 0))
            .GroupBy(task => task.Objectives.First(objective => objective.MapIds.Count > 0).MapIds[0], StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .Take(3)
            .SelectMany(group => group.Take((count + 2) / 3))
            .Take(count)
            .ToArray();
        foreach (var task in picked)
        {
            await commands.SetTaskStateAsync(scope, task.TaskId, RecordedTaskState.Active, CancellationToken.None);
        }

        Console.WriteLine($"Seeded {picked.Length} active quest(s) of {board.Tasks.Count}.");
    }

    /// <summary>
    /// [V2 rough package 22] A raid in progress on the shown map: where the player is, which way
    /// they are facing, where they have walked, and two squadmates with their own paths.
    /// </summary>
    /// <remarks>
    /// The positions are world positions found by probing the map's own transform, the same way
    /// <see cref="TeamDemoGroup"/> does, so they land on the plan rather than off its edge on
    /// whichever map is being rendered.
    /// </remarks>
    private static (TarkovCompanion.Core.Domain.Raids.RaidSnapshot Raid, TarkovCompanion.Application.Services.Group.GroupSnapshot Group) RaidDemo(
        TarkovCompanion.Application.Services.Maps.MapRenderModel? model)
    {
        var now = DateTimeOffset.UtcNow;
        var mapId = model?.Location.Id ?? "customs";
        var candidates = new List<(double X, double Z, double PlanX, double PlanY)>();
        if (model is not null)
        {
            for (var x = -1200.0; x <= 1200; x += 10)
            {
                for (var z = -1200.0; z <= 1200; z += 10)
                {
                    if (TryPlanPercent(model, x, z, out var planX, out var planY) &&
                        planX is > 4 and < 96 && planY is > 4 and < 96)
                    {
                        candidates.Add((x, z, planX, planY));
                    }
                }
            }
        }

        TarkovCompanion.Core.Domain.Maps.WorldPosition At(double planX, double planY)
        {
            if (candidates.Count == 0)
            {
                return new(0, 0, 0);
            }

            var best = candidates.MinBy(item => Math.Pow(item.PlanX - planX, 2) + Math.Pow(item.PlanY - planY, 2));
            return new(best.X, 0, best.Z);
        }

        TarkovCompanion.Core.Domain.Maps.ScreenshotPosition Step(double planX, double planY, double heading, int secondsAgo) =>
            new(now.AddSeconds(-secondsAgo), At(planX, planY), default, heading, null, null, $"demo-{secondsAgo}.png");

        // A walk across the middle of the plan, oldest first, ending where the player is now.
        // Deliberately not a straight line: a trail drawn from collinear points is
        // indistinguishable from one long segment, which tells a reviewer nothing.
        var trail = new[]
        {
            Step(30, 78, 350, 330),
            Step(28, 68, 20, 290),
            Step(36, 64, 80, 250),
            Step(46, 66, 95, 210),
            Step(52, 58, 20, 170),
            Step(48, 50, 330, 130),
            Step(54, 44, 40, 90),
            Step(62, 42, 80, 50),
            Step(64, 34, 10, 15),
        };
        var raid = new TarkovCompanion.Core.Domain.Raids.RaidSnapshot(
            Guid.NewGuid(),
            TarkovCompanion.Core.Domain.Raids.RaidLifecycleState.InRaid,
            mapId,
            now.AddMinutes(-14),
            now,
            new(0.9),
            trail[^1],
            [],
            false)
        {
            Side = "PMC",
            PositionTrail = trail,
        };

        TarkovCompanion.Application.Services.Group.GroupMemberView Mate(string name, double planX, double planY, double heading, int secondsAgo) =>
            new(
                name,
                mapId,
                TarkovCompanion.Core.Domain.Raids.RaidLifecycleState.InRaid,
                "PMC",
                At(planX, planY),
                heading,
                TimeSpan.FromSeconds(secondsAgo),
                [],
                [])
            {
                Since = TimeSpan.FromSeconds(secondsAgo),
                Trail = [Leg(planX - 9, planY + 7, 90), Leg(planX - 5, planY + 4, 45), Leg(planX, planY, secondsAgo)],
            };

        TarkovCompanion.Application.Services.Group.GroupTrailPointView Leg(double planX, double planY, int secondsAgo)
        {
            var at = At(planX, planY);
            return new(at.X, at.Z, TimeSpan.FromSeconds(secondsAgo));
        }

        var group = new TarkovCompanion.Application.Services.Group.GroupSnapshot(
            true,
            [Mate("Geo", 72, 34, 300, 6), Mate("Riley", 44, 71, 120, 25)],
            "Sharing as Clay · 2 others here",
            now);
        return (raid, group);
    }

    /// <summary>
    /// [V2 rough package 23] Where a world position sits on the plan, as a percentage of it.
    /// </summary>
    /// <remarks>
    /// The demos below want to put a marker "a third of the way across"; the transform answers in
    /// Leaflet map units, whose range is different on every map (Customs runs 54-114 across, not
    /// 0-100). Probing against 0-100 therefore picked whatever slice of the real map happened to
    /// overlap that square, which put the render's player and squad off the artwork's corner.
    /// </remarks>
    private static bool TryPlanPercent(
        TarkovCompanion.Application.Services.Maps.MapRenderModel model,
        double x,
        double z,
        out double planX,
        out double planY)
    {
        planX = 0;
        planY = 0;
        if (TarkovCompanion.Application.Services.Maps.MapPlanProjection.For(model) is not { IsValid: true } rect ||
            !model.TryMapPosition(new(x, 0, z), out var point))
        {
            return false;
        }

        planX = (point.X - rect.MinimumX) / rect.Width * 100;
        planY = (point.Y - rect.MinimumY) / rect.Height * 100;
        return double.IsFinite(planX) && double.IsFinite(planY);
    }

    private static TarkovCompanion.Application.Services.Group.GroupSnapshot TeamDemoGroup(
        TarkovCompanion.Application.Services.Maps.MapRenderModel? model)
    {
        var now = DateTimeOffset.UtcNow;
        var mapId = model?.Location.Id ?? "customs";

        // World positions that land at chosen spots on the selected map's plan, found by probing
        // its own transform, so the demo marks sit on the map rather than off its edge.
        var candidates = new List<(double X, double Z, double PlanX, double PlanY)>();
        if (model is not null)
        {
            for (var x = -1200.0; x <= 1200; x += 15)
            {
                for (var z = -1200.0; z <= 1200; z += 15)
                {
                    if (TryPlanPercent(model, x, z, out var planX, out var planY) &&
                        planX is > 5 and < 95 && planY is > 5 and < 95)
                    {
                        candidates.Add((x, z, planX, planY));
                    }
                }
            }
        }

        (double X, double Z) At(double planX, double planY) => candidates.Count == 0
            ? (0, 0)
            : candidates.MinBy(item => Math.Pow(item.PlanX - planX, 2) + Math.Pow(item.PlanY - planY, 2)) is var best ? (best.X, best.Z) : (0, 0);

        TarkovCompanion.Application.Services.Group.GroupMemberView Member(string name, TimeSpan since, params string[] quests) =>
            new(name, mapId, TarkovCompanion.Core.Domain.Raids.RaidLifecycleState.InRaid, "PMC", null, null, null, [], quests) { Since = since };
        TarkovCompanion.Application.Services.Group.GroupMemberView Sharing(string name, TimeSpan since, double planX, double planY, string[] loadout, params string[] quests)
        {
            var (x, z) = At(planX, planY);
            return new(name, mapId, TarkovCompanion.Core.Domain.Raids.RaidLifecycleState.InRaid, "PMC", new(x, 0, z), 90, TimeSpan.FromSeconds(40), loadout, quests) { Since = since };
        }
        TarkovCompanion.Application.Services.Group.GroupWaypointView Waypoint(long id, string by, double planX, double planY, string? label, string? reached, int minutesAgo)
        {
            var (x, z) = At(planX, planY);
            return new(id, by, mapId, x, 0, z, label, reached) { CreatedUtc = now.AddMinutes(-minutesAgo) };
        }

        var (pingX, pingZ) = At(55, 30);
        return new(true,
            [
                Sharing("Geo", TimeSpan.FromSeconds(4), 30, 40, ["Primary: AK-74N", "Rig: Slick"], "Delivery from the Past", "Debut"),
                Member("Riley", TimeSpan.FromSeconds(9), "Delivery from the Past"),
                Member("Sam", TimeSpan.FromMinutes(2), "Shortage"),
            ],
            "Sharing as Clay · 3 others here",
            now)
        {
            MyLoadout = ["Primary: AKMN", "Armour: 6B13"],
            MyLevel = 41,
            MySide = "Usec",
            Waypoints =
            [
                Waypoint(1, "Geo", 22, 35, "Dorms", "Riley", 6),
                Waypoint(2, "Riley", 38, 58, null, null, 4),
                Waypoint(3, "Geo", 60, 50, "Old gas station", null, 2),
                Waypoint(4, "Clay", 78, 68, "RUAF roadblock", null, 1),
            ],
            Pings = [new(5, "Sam", mapId, pingX, 0, pingZ, null, now.AddSeconds(-12))],
        };
    }

    private static void SaveFrame(Window window, string outputPath, int width, int height)
    {
        using var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("The headless platform produced no frame.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using (var stream = File.Create(outputPath))
        {
            frame.Save(stream, new PngBitmapEncoderOptions());
        }

        Console.WriteLine($"Saved {outputPath} ({width}x{height}).");
    }

    /// <summary>
    /// [V2 rough package 46] One region of the frame, magnified, as its own file.
    /// </summary>
    /// <remarks>
    /// A 1920x1080 render is read at about a third of its real size, which is enough to judge a
    /// layout and not nearly enough to judge a 44-pixel marker. `--crop x,y,w,h` with an optional
    /// `--crop-scale` writes `<out>.crop.png` alongside the full frame so a detail — a facing cone
    /// sitting on its dot, say — can actually be looked at.
    /// </remarks>
    private static void SaveCrop(Window window, string outputPath, string region, int scale)
    {
        var parts = region.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4 || !parts.All(part => int.TryParse(part, out _)))
        {
            throw new ArgumentException("--crop takes x,y,width,height in frame pixels.");
        }

        var (x, y, w, h) = (int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
        using var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("The headless platform produced no frame.");
        using var full = new MemoryStream();
        frame.Save(full, new PngBitmapEncoderOptions());
        full.Position = 0;
        using var source = SkiaSharp.SKBitmap.Decode(full)
            ?? throw new InvalidOperationException("The captured frame could not be decoded.");
        var rect = SkiaSharp.SKRectI.Intersect(
            new(x, y, x + w, y + h),
            new(0, 0, source.Width, source.Height));
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            throw new ArgumentException($"--crop {region} is outside the {source.Width}x{source.Height} frame.");
        }

        using var cropped = new SkiaSharp.SKBitmap(rect.Width, rect.Height);
        source.ExtractSubset(cropped, rect);
        using var enlarged = cropped.Resize(
            new SkiaSharp.SKImageInfo(rect.Width * scale, rect.Height * scale),
            new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Nearest, SkiaSharp.SKMipmapMode.None))
            ?? throw new InvalidOperationException("The crop could not be enlarged.");
        var cropPath = Path.ChangeExtension(outputPath, ".crop.png");
        using var data = enlarged.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        using var output = File.Create(cropPath);
        data.SaveTo(output);
        Console.WriteLine($"Saved {cropPath} ({rect.Width}x{rect.Height} at {scale}x).");
    }

    private static void Pump(int turns)
    {
        for (var i = 0; i < turns; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }
    }

    private static void DrainUntilComplete(Task task)
    {
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        task.GetAwaiter().GetResult();
    }

    /// <summary>One paired device, so the list has something in it to photograph.</summary>
    private static TarkovCompanion.App.ViewModels.V2.Tablet.PairedDeviceRowViewModel DemoPairedDevice(string name)
    {
        var now = new DateTimeOffset(2026, 9, 18, 21, 0, 0, TimeSpan.Zero);
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var point = key.ExportParameters(false).Q;
        byte[] cose =
        [
            0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20,
            .. point.X!,
            0x22, 0x58, 0x20,
            .. point.Y!,
        ];
        var thumbprint = System.Buffers.Text.Base64Url.EncodeToString(
            System.Security.Cryptography.SHA256.HashData(cose));
        var device = new TarkovCompanion.CompanionProtocol.PairedDevice(
            new TarkovCompanion.Core.Abstractions.V2.CompanionDeviceId(Guid.Parse("7a1d0000-0000-4000-8000-000000000048")),
            name,
            new TarkovCompanion.CompanionProtocol.DevicePublicKey(
                new TarkovCompanion.CompanionProtocol.DeviceKeyId(thumbprint),
                TarkovCompanion.CompanionProtocol.DeviceKeyAlgorithm.WebAuthnEs256,
                thumbprint,
                System.Buffers.Text.Base64Url.EncodeToString(cose)),
            TarkovCompanion.CompanionProtocol.DeviceAuthorizationRole.Member,
            [TarkovCompanion.CompanionProtocol.DeviceCapability.FollowDesktop],
            TarkovCompanion.CompanionProtocol.DeviceLifecycleStatus.Active,
            now,
            now,
            1,
            now.AddDays(30),
            now);
        return new(device, _ => Task.CompletedTask);
    }

    private static int IntOption(string[] args, string name, int fallback)
    {
        var value = StringOption(args, name);
        return value is null ? fallback : int.Parse(value);
    }

    private static string? StringOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
