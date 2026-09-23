using System.Linq;
using System.Text.Json;
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
using TarkovCompanion.App.ViewModels.V2.ReleaseExperience;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.App.Services.V2.Appearance;
using TarkovCompanion.App.Services.Windowing;
using TarkovCompanion.Application.Services.Personalization;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Personalization;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.App.Views;
using TarkovCompanion.App.Views.Pages;
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
        // --no-demo: the demo fixture is always mid-raid on Customs, so it can never show what a
        // first launch shows, which is no raid and no map anybody chose.
        var demoMode = !args.Contains("--no-demo");
        var options = AppCommandLine.Parse(args) with { Demo = demoMode };

        var rendered = false;
        var dataRoot = Path.Combine(Path.GetTempPath(), $"v2-render-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);

        // V2 rough package 17: the demo fixture seeds one item and no quests or hideout, so a
        // Plan render showed only empty states. --seed-database copies an existing synced
        // database (any earlier real run's tarkov-companion.db) into the throwaway data root;
        // startup migrates it forward, and it is deleted with the root afterwards.
        if (StringOption(args, "--seed-database") is { } seedDatabase)
        {
            var databaseDirectory = AppDataPaths.Resolve(dataRoot, demoMode: demoMode).Database;
            Directory.CreateDirectory(databaseDirectory);
            File.Copy(seedDatabase, Path.Combine(databaseDirectory, "tarkov-companion.db"));
        }

        // [Issue 563] --seed-loot-cache copies a real durable loot-spawn publication.cache (the
        // exact file HighValueLootRuntimeSource reads on startup) into the throwaway data root, so
        // a render can show the high-value loot layer with real spawns instead of only its
        // no-data state.
        if (StringOption(args, "--seed-loot-cache") is { } seedLootCache)
        {
            var lootCacheDirectory = Path.Combine(AppDataPaths.Resolve(dataRoot, demoMode: demoMode).Cache, "LootSpawns");
            Directory.CreateDirectory(lootCacheDirectory);
            File.Copy(seedLootCache, Path.Combine(lootCacheDirectory, "publication.cache"));
        }

        // [#283] --seed-icon-cache <dir> copies an icon evidence cache (the local icon corpus's
        // icon-evidence-cache) into the throwaway data root, so a real screenshot handed to
        // --capture-image is named the way it would be on a machine whose cache has filled.
        if (StringOption(args, "--seed-icon-cache") is { } seedIconCache)
        {
            var iconDirectory = Path.Combine(AppDataPaths.Resolve(dataRoot, demoMode: demoMode).Cache, "IconEvidence");
            Directory.CreateDirectory(iconDirectory);
            foreach (var file in Directory.EnumerateFiles(seedIconCache))
            {
                File.Copy(file, Path.Combine(iconDirectory, Path.GetFileName(file)));
            }
        }

        MapSwitchProbe.LinkMapCache(dataRoot, StringOption(args, "--map-cache"), demoMode);
        MapSwitchProbe.SeedLastMap(dataRoot, demoMode, StringOption(args, "--last-map"));
        try
        {
            // Not disposed: some services' DisposeAsync continues on the UI dispatcher, which
            // nothing pumps once the frame is saved, so awaiting it hung the process after
            // "Saved" (package 17). The process exits right after the finally block instead.
            // --now <utc>: a seeded database's prices are as old as the day it was copied, and
            // anything that weighs them against the clock reads every one as expired.
            var now = StringOption(args, "--now") is { } nowText
                ? new ScanFrame.FixedClock(DateTimeOffset.Parse(
                    nowText,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal))
                : null;
            // [#454] --previous-run-died: a run that navigated to Plan, began loading Reserve and
            // never shut down, so Setup's Diagnostics can be photographed saying so.
            if (args.Contains("--previous-run-died"))
            {
                var died = Path.Combine(dataRoot, "previous-run-logs");
                CrashBreadcrumbs.Install(died);
                CrashBreadcrumbs.Drop("navigate", "#/plan");
                CrashBreadcrumbs.Drop("map", "loading reserve/reserve-2d");
                CrashBreadcrumbs.Detach();
                CrashBreadcrumbs.Install(died);
                CrashBreadcrumbs.Detach();
            }

            // [#599] Setup > Updates beside a build that was downloaded and did not apply. There is
            // no installation and no updater here, so the state is handed to the gateway directly;
            // that the gateway reports it from a real packages folder is PendingUpdateTests' job.
            if (args.Contains("--update-did-not-apply"))
            {
                TarkovCompanion.App.Services.Updates.VelopackUpdateGateway.RenderPending = new(
                    "2.0.1337",
                    "Unable to start the update, because one or more running processes prevented it.");
            }

            var monitorDemo = args.Contains("--display-demo") ? new RenderMonitorService() : null;
            var placementDemo = monitorDemo is null ? null : new RenderWindowPlacementController("\\\\.\\DISPLAY1");
            var services = AppComposition.Build(options, new AppCompositionSettings(
                DataRoot: dataRoot,
                Offline: true,
                TimeProvider: now,
                HttpMessageHandler: MapSwitchProbe.SlowNetwork(IntOption(args, "--slow-network", 0)),
                MonitorService: monitorDemo,
                WindowPlacementController: placementDemo));

            if (args.Contains("--learn-mode"))
            {
                services.GetRequiredService<LearnModeSetting>().IsEnabled = true;
            }

            if (args.Contains("--clock-skew-demo"))
            {
                services.GetRequiredService<RelayClockOffsetTracker>().ObserveOffsetSeconds(-14_400);
            }

            if (StringOption(args, "--quest-region") is { } questScreenshot)
            {
                var image = services.GetRequiredService<IScreenshotImageLoader>()
                    .LoadAsync(questScreenshot, CancellationToken.None).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException($"Could not load '{questScreenshot}'.");
                var detected = services
                    .GetRequiredService<TarkovCompanion.Application.Services.Quests.IQuestTaskColumnRegionDetector>()
                    .Detect(image);
                var region = detected.Region;
                Console.WriteLine($"Quest {detected.Layout} region: {region.X},{region.Y} {region.Width}x{region.Height} of {image.Width}x{image.Height}");
                return 0;
            }

            AppBuilder.Configure(() => new AppClass(services))
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                // Binding and layout warnings are exactly what the Windows page gallery fails on;
                // printing them here lets a Linux run catch the same faults before CI does.
                .LogToTextWriter(Console.Out, Avalonia.Logging.LogEventLevel.Warning, Avalonia.Logging.LogArea.Binding, Avalonia.Logging.LogArea.Layout)
                .SetupWithoutStarting();

            // [V2 rough package 60 — appearance] #266/#315. SetupWithoutStarting never reaches
            // OnFrameworkInitializationCompleted, so the applier the running app installs there
            // is installed here instead. Without it every render is Dark at 100%, which is the
            // one combination the appearance work does not need proving.
            var appearance = ApplyAppearance(services, args);

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

            // [#294] Photograph the waiting-build mark. This paints it directly rather than
            // simulating the updater: what it proves is that the dot is drawn, where, and at what
            // size. That the shell raises it from the real update signal is proved by
            // V2UpdateNoticeTests and the host-contract ratchet, not by this.
            if (shell is not null && args.Contains("--update-waiting"))
            {
                shell.SetupDestination.HasNotice = true;
            }

            // [#294] How large everything is drawn, so a render can show the scale actually
            // applying under V2. It used to apply only under V1: the transform lived inside the
            // legacy host, and Setup's Smaller/Larger/Reset moved a number nothing read.
            if (IntOption(args, "--interface-scale", 0) is var scalePercent and > 0)
            {
                for (var guard = 0; guard < 12 && Math.Round(viewModel.InterfaceScale * 100) < scalePercent; guard++)
                {
                    viewModel.StepInterfaceScale(1);
                }

                Console.WriteLine($"Interface scale: {viewModel.InterfaceScaleLabel}");
            }

            var window = new MainWindow { DataContext = viewModel, Width = width, Height = height };
            appearance?.Attach(window, services.GetRequiredService<WorkspacePreferenceService>().Current);
            // [#453] --ui-stalls-before-show: start up with no window, so every long turn the meter
            // reports is a view model holding the interface thread, not the first layout and paint.
            var showAfterStartup = args.Contains("--ui-stalls-before-show");
            if (!showAfterStartup)
            {
                window.Show();
            }

            // [#294] Whether the V1 shell was built at all. It used to be built on every launch
            // and hidden, so "V2 is the default" was true of what was drawn and false of what was
            // constructed. Printed rather than asserted: this tool reports, the ratchet test in
            // MainWindowShellCompositionTests is what fails.
            Console.WriteLine($"V1 chrome: {(window.GetVisualDescendants().OfType<LegacyShellView>().Any() ? "built" : "not built")}");
            // [#453] --inject-load-fault plan,hideout,keep,startup/hideout: make those loads throw,
            // so the pane's "did not load" notice and the shell's startup banner can be looked at.
            if (StringOption(args, "--inject-load-fault") is { } injected)
            {
                LoadFaultInjection.Inject(injected.Split(','));
            }

            // [#453] --ui-stalls <ms>: how long each dispatcher turn held the interface thread.
            if (IntOption(args, "--ui-stalls", 0) is var stallMs and > 0)
            {
                UiStallMeter.Enable(stallMs);
            }

            Task? initializing = null;
            UiStallMeter.Time(() => initializing = viewModel.InitializeAsync());
            DrainUntilComplete(initializing!);
            if (seeding is not null)
            {
                DrainUntilComplete(seeding);
            }

            // #284: put one catalog item in the active profile's recorded holdings so item
            // detail can render its production selling comparison. This changes only the
            // preview's throwaway profile; it does not synthesize any game or flea action.
            if (StringOption(args, "--intel-held") is { } heldItemId)
            {
                DrainUntilComplete(SeedHeldItemAsync(services, heldItemId));
            }

            // #307: unlock real catalog recipes for a chain render. Item names, prices, inputs,
            // outputs and durations still come from --seed-database; only the throwaway active
            // profile's station/trader levels are raised so the planner may actually choose them.
            if (args.Contains("--chain-ready-profile"))
            {
                DrainUntilComplete(SeedChainReadyProfileAsync(services));
            }

            UiStallMeter.Report("startup");
            if (showAfterStartup)
            {
                window.Show();
                Pump(20);
                UiStallMeter.Report("first layout and paint");
            }

            // #314: the real post-update banner and the list it opens. The render's data root is
            // new every run, so ordinary initialization correctly treats it as a first install;
            // this preview seam supplies the prior-update state without persisting fake history.
            if (args.Contains("--whats-new-banner") || args.Contains("--whats-new-list"))
            {
                services.GetRequiredService<ReleaseExperienceViewModel>()
                    .PresentForPreview(args.Contains("--whats-new-list"));
                Pump(20);
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

            if (StringOption(args, "--plan-map") is { } planMap)
            {
                var plan = services.GetRequiredService<PlanWorkspaceViewModel>();
                if (!plan.SelectMapForPreview(planMap))
                {
                    throw new ArgumentException($"The Plan workspace has no map named '{planMap}'.");
                }

                Pump(80);
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

            // [V2 rough package 61 — plan export] #288/#315: press Export and print what it
            // produced, so the document can be read rather than assumed.
            if (args.Contains("--plan-export"))
            {
                var exported = services.GetRequiredService<PlanWorkspaceViewModel>();
                exported.Clipboard = text =>
                {
                    Console.WriteLine("----- exported plan -----");
                    Console.WriteLine(text);
                    Console.WriteLine("----- end -----");
                    return Task.CompletedTask;
                };
                DrainUntilComplete(exported.ExportCommand.ExecuteAsync());
                Pump(20);
                Console.WriteLine("Export status: " + exported.ExportStatus);
            }

            if (shell is not null && route is not null)
            {
                V2NavigationResult? result = null;
                UiStallMeter.Time(() => result = shell.Router.NavigateToAddress(route));
                if (!result!.Succeeded)
                {
                    throw new ArgumentException($"The shell refused '{route}': {result.Failure}");
                }

                Pump(20);
                UiStallMeter.Report($"navigate to {route}");
            }

            // [#572] --loot-timing-demo: one finished loot scan (Setup > Diagnostics' timing) and one
            // still running (the Loot page's progress line), through the app's own stage timeline.
            if (args.Contains("--loot-timing-demo"))
            {
                var timeline = services.GetRequiredService<TarkovCompanion.Application.Services.CaptureSessions.ICaptureStageTimeline>();
                var seen = DateTimeOffset.UtcNow.AddSeconds(-20);
                var done = TarkovCompanion.Application.Services.CaptureSessions.CaptureCorrelationId.New();
                timeline.Begin(done, seen);
                foreach (var (stage, ms) in new[] { ("settle_wait", 310d), ("context_ocr", 142d), ("grid_and_icon_matching", 118d), ("grid_reconstruct", 21d), ("profile_lookup", 9d), ("recommendation", 34d), ("decide", 6d) })
                {
                    timeline.Mark(done, stage, TimeSpan.FromMilliseconds(ms));
                }

                timeline.Complete(done, seen.AddMilliseconds(702));
                var running = TarkovCompanion.Application.Services.CaptureSessions.CaptureCorrelationId.New();
                timeline.Begin(running, DateTimeOffset.UtcNow);
                timeline.Mark(running, "settle_wait", TimeSpan.FromMilliseconds(300));
                timeline.Mark(running, "context_ocr", TimeSpan.FromMilliseconds(140));
                Pump(10);
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

            // #667: fixture OCR output through the real matcher and history inference, so the
            // Setup preview can be judged without invoking a Windows-only OCR provider on dev.
            if (shell?.SetupWorkspace?.QuestSync is { } questSync && args.Contains("--quest-sync-demo"))
            {
                DrainUntilComplete(questSync.LoadFixtureAsync(
                    ["Flint", "Gunsmith Part", "CHARACTER TASKS"]));
                Pump(20);
            }

            // #703: the passive watcher groups a scroll into one global review offer. These
            // paths are never opened in the banner render; the preview flag below drives the
            // same matcher/history view with fixture OCR because dev has no Windows OCR.
            if (shell is not null && IntOption(args, "--quest-sync-offer-demo", 0) is var offerCount and > 0)
            {
                var bursts = services.GetRequiredService<QuestScreenshotBurstCollector>();
                var first = DateTimeOffset.UtcNow;
                for (var index = 0; index < offerCount; index++)
                {
                    bursts.Observe($"fixture-task-{index}.png", first.AddSeconds(index), raidActive: false);
                }

                Pump(20);
            }

            if (shell?.SetupWorkspace?.QuestSync is { } passiveQuestSync &&
                IntOption(args, "--quest-sync-passive-demo", 0) is var passiveCount and > 0)
            {
                DrainUntilComplete(passiveQuestSync.LoadPassiveFixtureAsync(
                    passiveCount,
                    ["Flint", "Gunsmith Part", "CHARACTER TASKS"]));
                Pump(20);
            }

            // [#269] Profiles made through the real management service, so Setup > Game & Profile
            // renders the list a player would have: the first profile, a PvE one made active, and an
            // archived one behind "Show archived".
            if (shell is not null && args.Contains("--profiles-demo"))
            {
                var management = services.GetRequiredService<TarkovCompanion.Application.Services.Profiles.ProfileManagementService>();
                management.CreateAsync("Old wipe", TarkovCompanion.Core.Domain.Profiles.ProfileGameMode.Pvp, "Wipe 2", default).GetAwaiter().GetResult();
                var oldWipe = management.Current.ActiveProfile!.Context.Identity.ProfileId;
                management.CreateAsync("PvE alt", TarkovCompanion.Core.Domain.Profiles.ProfileGameMode.Pve, "Wipe 3", default).GetAwaiter().GetResult();
                management.ArchiveAsync(oldWipe, default).GetAwaiter().GetResult();
                Pump(20);

                // [#292 task 3] Opens the active profile's inline mode/wipe editor, so the render
                // shows real fields bound to a real row rather than a mock of the form.
                if (args.Contains("--profiles-edit-demo") && shell.SetupWorkspace?.Profiles is { } profilesVm)
                {
                    var row = profilesVm.Profiles.First(candidate => candidate.IsActive);
                    row.BeginEditCommand.Execute(null);
                    Pump(10);
                }
            }

            // Issue 655: save through the same scoped service as Setup's progress controls. The
            // rendered summary must follow this change even though it does not create a new
            // runtime snapshot.
            if (StringOption(args, "--profile-level") is { } profileLevelText &&
                int.TryParse(profileLevelText, out var profileLevel))
            {
                var profiles = services.GetRequiredService<TarkovCompanion.Core.Abstractions.IPlayerProfileService>();
                var profile = profiles.GetActiveAsync(default).GetAwaiter().GetResult();
                profiles.SaveAsync(profile with { Level = profileLevel }, default).GetAwaiter().GetResult();
                Pump(20);
            }

            // [#269] Profile export/import: gives the active profile some progress, exports it to
            // a file through the real service, then previews importing that file as a new profile
            // (--profile-transfer-demo) or into the active one (--profile-transfer-demo into).
            if (shell?.SetupWorkspace?.Profiles?.Transfer is { } transfer && StringOption(args, "--profile-transfer-demo") is { } transferMode)
            {
                var players = services.GetRequiredService<TarkovCompanion.Core.Abstractions.IPlayerProfileService>();
                var seeded = players.GetActiveAsync(default).GetAwaiter().GetResult();
                players.SaveAsync(seeded with
                {
                    Level = 23,
                    HideoutStationLevels = new Dictionary<string, int> { ["lavatory"] = 2, ["medstation"] = 1, ["workbench"] = 2 },
                    WishlistItemIds = new HashSet<string>(["5c0530ee86f774697952d952", "5c12613b86f7743bbe2c3f76"]),
                    ItemOverrides = new Dictionary<string, string> { ["57347ca924597744596b4e71"] = "keep" },
                }, default).GetAwaiter().GetResult();
                DrainUntilComplete(transfer.ExportCommand.ExecuteAsync());
                DrainUntilComplete((transferMode == "into" ? transfer.IntoActiveCommand : transfer.AsNewCommand).ExecuteAsync());
                DrainUntilComplete(transfer.PreviewCommand.ExecuteAsync());
                Pump(20);

                // "import": confirms the as-new preview, writing through the real stores, so the
                // frame shows the new profile in the list and the outcome line.
                if (transferMode == "import")
                {
                    DrainUntilComplete(transfer.ImportCommand.ExecuteAsync());
                    Pump(40);
                }

                // "import-check": after the import, previews the same file into the profile it
                // created; "nothing differs" is the proof that every part of it landed.
                if (transferMode == "import-check")
                {
                    DrainUntilComplete(transfer.ImportCommand.ExecuteAsync());
                    Pump(40);
                    DrainUntilComplete(transfer.IntoActiveCommand.ExecuteAsync());
                    DrainUntilComplete(transfer.PreviewCommand.ExecuteAsync());
                    Pump(20);
                }
            }

            // [#292] Paths shown in full, or an About / Data & Privacy item opened as a deep link would.
            if (shell?.SetupWorkspace is { } setupPage)
            {
                // Paths are shown in full by default now; the flag only makes sure of it.
                if (args.Contains("--show-paths") && !setupPage.Paths.IsRevealed)
                {
                    setupPage.Paths.ToggleCommand.Execute(null);
                }

                // [#292 task 2] Exercises the real reset-everything preview against whatever the
                // seed database actually holds, rather than a static mock of the dialog. Changes
                // the theme first so the preview has at least one real row to show.
                if (args.Contains("--settings-reset-preview"))
                {
                    if (setupPage.Appearance is { } appearanceForReset)
                    {
                        appearanceForReset.Themes.Single(choice => choice.Id == "theme-light").ChooseCommand.Execute(null);
                        Pump(10);
                    }

                    setupPage.SettingsAdmin?.ResetAllCommand.Execute(null);
                }

                if (StringOption(args, "--setup-open") is { } opened && opened.Split(':') is [var openedSection, var openedAnchor]
                    && Enum.TryParse<V2SetupSection>(openedSection, ignoreCase: true, out var openedTarget))
                {
                    setupPage.OpenSection(openedTarget, openedAnchor);
                }

                Pump(20);
            }

            // [#309] A screenshot folder of stand-in files (empty of pictures, named the way the game names
            // them) pointed at the runtime, then Start tidying pressed: the preview and its confirm button.
            if (shell?.SetupWorkspace is { Cleanup: { } cleanup } && args.Contains("--tidy-demo"))
            {
                var shots = Path.Combine(dataRoot, "tidy-demo-screenshots");
                Directory.CreateDirectory(shots);
                for (var day = 0; day < 6; day++)
                {
                    var path = Path.Combine(shots, $"2026-09-{10 + day:D2}[14-05]_demo_{day}.png");
                    File.WriteAllText(path, new string('x', 900_000 + (day * 13_000)));
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8 + day));
                }

                File.WriteAllText(Path.Combine(shots, "notes.txt"), "not the game's");
                var runtime = services.GetRequiredService<TarkovCompanion.Application.Services.Runtime.IRuntimeStateStore>();
                runtime.Update(snapshot => snapshot with { Observation = snapshot.Observation with { ScreenshotRoot = shots } });
                cleanup.RequestToggleCommand.Execute(null);
                Pump(60);
            }

            // [#292/#309] The problem report as a player reads it before it is sent.
            if (shell?.SetupWorkspace is { Admin.Report: { } reportReview } && args.Contains("--report-demo"))
            {
                reportReview.PreviewCommand.Execute(null);
                Pump(30);
            }

            // Package 28: a Loadout with one item assigned and evaluated, and an Events page with one
            // event holding a few items, through the pages' own commands.
            // --keep-find <text>: prints the Keep rows whose name contains the text, because the list
            // is virtualised and 600 rows long, and a row below the first screen cannot be looked at.
            if (StringOption(args, "--keep-find") is { } keepFind)
            {
                var keep = services.GetRequiredService<KeepListWorkspaceViewModel>();
                DrainUntilComplete(keep.RefreshAsync());
                foreach (var row in keep.Groups.SelectMany(group => group.Items)
                             .Where(row => row.Name.Contains(keepFind, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"Keep row: {row.Name} | {row.ReasonSummary} | {row.QuestCountLabel} | {row.HeldLabel}");
                }
            }

            // [#285] --allergy-demo <item query>: an event holding the first matches, the first of
            // them recorded Allergic, before the pages that warn about it are built up below.
            if (StringOption(args, "--allergy-demo") is { } allergyQuery)
            {
                var events = viewModel.Events;
                events.NewEventName = "Halloween 2026";
                DrainUntilComplete(events.CreateCommand.ExecuteAsync());
                Pump(40);
                events.ItemQuery = allergyQuery;
                DrainUntilComplete(events.SearchCommand.ExecuteAsync());
                foreach (var match in events.Matches.Take(3).ToArray())
                {
                    match.AddCommand.Execute(null);
                    Pump(60);
                }

                if (events.Items.Count > 0)
                {
                    Console.WriteLine($"Allergy demo: {events.Items[0].ItemName} recorded Allergic");
                    events.Items[0].MarkAllergicCommand.Execute(null);
                    Pump(60);
                    if (args.Contains("--allergy-undo"))
                    {
                        DrainUntilComplete(events.UndoCommand.ExecuteAsync());
                        Pump(40);
                    }

                    // The app re-reads Plan whenever its tab is returned to; this render went
                    // there before the record above was made, so it is returned to here.
                    DrainUntilComplete(services.GetRequiredService<PlanWorkspaceViewModel>().RefreshAsync());
                    Pump(40);
                }
            }

            if (StringOption(args, "--loadout-demo") is { } loadoutQuery)
            {
                var loadout = viewModel.Loadout;
                // Several entries separated by ';' assign the first result of each, and
                // "Ammunition=9x18mm PM" names the slot it goes in, so a render can hold a weapon
                // and a round at once. Without a slot name every entry lands in the chosen slot
                // and replaces the last: the first attempt at this put a round in Weapon.
                foreach (var entry in loadoutQuery.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var query = entry;
                    if (entry.Split('=', 2, StringSplitOptions.TrimEntries) is [var slotName, var slotQuery] &&
                        Enum.TryParse<TarkovCompanion.App.ViewModels.LoadoutSlot>(slotName, ignoreCase: true, out var slot))
                    {
                        loadout.SelectedSlot = loadout.Slots.First(option => option.Slot == slot);
                        query = slotQuery;
                    }

                    loadout.SearchQuery = query;
                    DrainUntilComplete(loadout.SearchCommand.ExecuteAsync());
                    if (loadout.Results.Count > 0)
                    {
                        loadout.Results[0].AssignCommand.Execute(null);
                        Pump(60);
                    }
                }

                DrainUntilComplete(loadout.EvaluateCommand.ExecuteAsync());
                Pump(20);

                // [V2 rough package 60 — Plan] #288: the budget line and a saved kit to compare
                // against, neither of which a cold render can reach on its own.
                if (StringOption(args, "--loadout-budget") is { } budget)
                {
                    loadout.BudgetInput = budget;
                    Pump(10);
                }

                if (StringOption(args, "--loadout-preset") is { } presetName)
                {
                    loadout.PresetName = presetName;
                    DrainUntilComplete(loadout.SavePresetCommand.ExecuteAsync());
                    Pump(20);
                    loadout.Compare(presetName);
                    Pump(40);
                }
            }

            if (args.Contains("--events-demo"))
            {
                var events = viewModel.Events;
                events.NewEventName = "Halloween 2026";
                DrainUntilComplete(events.CreateCommand.ExecuteAsync());
                Pump(40);
                var eventCatalog = services.GetRequiredService<IEventCatalog>();
                var eventAuthoring = services.GetRequiredService<IEventAuthoring>();
                var definition = eventCatalog.GetAsync(default).GetAwaiter().GetResult()
                    .Single(item => item.Id == "halloween-2026");
                var plan = services.GetRequiredService<PlanWorkspaceViewModel>();
                var closedPlanMap = plan.Groups.FirstOrDefault(group =>
                    string.Equals(group.MapLabel, "Interchange", StringComparison.OrdinalIgnoreCase));
                DrainUntilComplete(eventAuthoring.SaveAsync(definition with
                {
                    RulesJson = JsonSerializer.Serialize(new
                    {
                        effects = new object[]
                        {
                            new { type = "trader-price-multiplier", traderId = "54cb50c76803fa8b248b4571", traderName = "Prapor", multiplier = 0.8 },
                            new { type = "map-availability", mapId = "laboratory", mapName = "Labs", available = false },
                            new
                            {
                                type = "map-availability",
                                mapId = closedPlanMap?.MapId ?? "interchange",
                                mapName = closedPlanMap?.MapLabel ?? "Interchange",
                                available = false,
                            },
                            new { type = "flea-availability", enabled = false },
                        },
                    }),
                }, default));
                DrainUntilComplete(events.LoadAsync(default));
                events.Selected = events.Events.Single(item => item.EventId == definition.Id);
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

                DrainUntilComplete(plan.RefreshAsync());
                Pump(40);
            }

            // Issue 645: a fresh install's honest history state is one locally recorded price.
            // Seed that exact state after migrations so the Flea render proves it does not draw
            // three identical low/average/high figures.
            if (StringOption(args, "--one-price-item") is { } onePriceItem)
            {
                var factory = services.GetRequiredService<TarkovCompanion.Infrastructure.Persistence.SqliteConnectionFactory>();
                using var connection = factory.OpenAsync(default).GetAwaiter().GetResult();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT OR REPLACE INTO price_history(item_id, timestamp_utc, flea_price, trader_value, source) " +
                    "VALUES ($itemId, $timestamp, $flea, NULL, 'render-demo');";
                command.Parameters.AddWithValue("$itemId", onePriceItem);
                command.Parameters.AddWithValue("$timestamp", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$flea", 322_222);
                command.ExecuteNonQuery();
            }

            // #287 (event state on items): creates an event, marks one item Allergic on it, and
            // (with --route intel) searches Intel for the same item so its row and detail chip
            // can be photographed.
            if (args.Contains("--intel-allergic-demo"))
            {
                var events = viewModel.Events;
                events.NewEventName = "Allergy Test";
                DrainUntilComplete(events.CreateCommand.ExecuteAsync());
                Pump(40);
                events.ItemQuery = "bandage";
                DrainUntilComplete(events.SearchCommand.ExecuteAsync());
                if (events.Matches.Count > 0)
                {
                    events.Matches[0].AddCommand.Execute(null);
                    Pump(60);
                }

                if (events.Items.Count > 0)
                {
                    events.Items[0].MarkAllergicCommand.Execute(null);
                    Pump(60);
                }

                // Intel's own event-state map is read no more than once per
                // IntelEventStateRefreshInterval; this run's own periodic tick may have already
                // cached an earlier (pre-mark) read during the pumping above, so give one more
                // full interval before asking Intel to search, rather than photographing a race.
                Pump(120);

                if (shell is not null)
                {
                    shell.SearchText = "bandage";
                    DrainUntilComplete(shell.SearchAsync());
                    Pump(120);
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

            // Package 33 (#287): --demo (always on here) preloads a "Graphics Card" search so the
            // fixture-only run has something to show; against --seed-database that default masks
            // the Intel landing page's real empty/no-search state. This clears it back out.
            if (shell is not null && args.Contains("--intel-clear-search"))
            {
                shell.ClearIntelSearchCommand.Execute(null);
                Pump(20);
            }

            // #287 (Crafts & barters tab): --route intel/crafts lands on the tab itself; this
            // additionally runs a search there (or just waits for the catalog's own first load,
            // when the query is blank) so the render shows priced rows rather than an empty list.
            if (shell?.CraftsBartersWorkspace is { } trade)
            {
                DrainUntilComplete(trade.LoadTask);
                if (StringOption(args, "--intel-trade-search") is { } tradeQuery)
                {
                    trade.SearchText = tradeQuery;
                }

                if (args.Contains("--intel-trade-ready-now"))
                {
                    trade.ReadyNowOnly = true;
                }

                if (StringOption(args, "--intel-chain-item") is { } chainItem)
                {
                    DrainUntilComplete(trade.ShowChainAsync(chainItem));
                }

                Pump(20);
            }


            if (shell?.IntelAcquisitionChain.HasSelection == true)
            {
                DrainUntilComplete(shell.IntelAcquisitionChain.LoadTask);
                Pump(20);
            }

            // Package 33 (#287): pins one item and opens a second (leaving it "recently opened"),
            // then returns to the bare Items route, so the landing page's Pinned/Recent sections
            // can be rendered with real rows instead of only Needed now/Highest value.
            if (shell is not null && args.Contains("--intel-home-demo"))
            {
                shell.SearchText = "bandage";
                DrainUntilComplete(shell.SearchAsync());
                Pump(20);
                if (shell.PinCommand.CanExecute(null))
                {
                    shell.PinCommand.Execute(null);
                }

                shell.SearchText = "screw nuts";
                DrainUntilComplete(shell.SearchAsync());
                Pump(20);
                shell.Router.Navigate(V2Routes.Items, "demo");
                shell.ClearIntelSearchCommand.Execute(null);
                // The landing page's own auto-select-first-suggestion fires one more navigation
                // once its async load resolves (adding that item to Recents in turn); give it
                // room to settle before anything downstream reads Recents or takes the shot.
                Pump(80);
            }

            // #287 (side-by-side comparison): --intel-compare "M855;M856;M855A1" searches each
            // query, opens its first hit and adds it to the compare tray, then opens the table.
            // --intel-compare-tray stops at the tray, with the last item's detail still showing.
            if (shell is not null && StringOption(args, "--intel-compare") is { } compareQueries)
            {
                foreach (var query in compareQueries.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    shell.SearchText = query;
                    DrainUntilComplete(shell.SearchAsync());
                    Pump(60);
                    shell.IntelCompare.ToggleCurrentCommand.Execute(null);
                    Pump(10);
                }

                if (!args.Contains("--intel-compare-tray"))
                {
                    DrainUntilComplete(shell.IntelCompare.OpenAsync());
                    Pump(20);
                }
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
                // --no-map: leave the page as a first launch finds it, with nobody having chosen anything.
                var picked = (mapId is null && options.StartPage is not null) || args.Contains("--no-map")
                    ? null
                    : mapId is null
                    ? raid.MapPicker.FirstOrDefault()
                    : raid.MapPicker.FirstOrDefault(item => string.Equals(item.MapId, mapId, StringComparison.OrdinalIgnoreCase));
                // First paint: the app opens on the raid's own map with nobody selecting it, and the tool
                // used to select it again, which measures a second load, not the one a player sees
                // every launch. --first-paint leaves the first load alone and prints how the plan's
                // drawn rectangle and the artwork's own shape stand at each step of it.
                if (args.Contains("--first-paint"))
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    string? last = null;
                    for (var i = 0; i < 1200 && clock.Elapsed < TimeSpan.FromSeconds(60); i++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        string current;
                        if (raid.Renderer is { } probe)
                        {
                            var art = probe.BackgroundImage?.Size;
                            var drawnAspect = probe.MapHeight > 0 ? probe.MapWidth / probe.MapHeight : double.NaN;
                            var artAspect = art is { Height: > 0 } size ? size.Width / size.Height : double.NaN;
                            var ci = System.Globalization.CultureInfo.InvariantCulture;
                            current = string.Create(
                                ci,
                                $"scene r{probe.Scene.Revision} {probe.Scene.LocationId} card {probe.CanvasWidth:F0}x{probe.CanvasHeight:F0} drawn {probe.MapWidth:F1}x{probe.MapHeight:F1}={drawnAspect:F4} art {art?.Width:F0}x{art?.Height:F0}={artAspect:F4} v1canvas {viewModel.Map.CanvasWidth:F0}x{viewModel.Map.CanvasHeight:F0} tiles {viewModel.Map.Tiles.Count} drawing={raid.PrefersDrawing}");
                        }
                        else
                        {
                            current = $"no renderer yet; v1 status '{viewModel.Map.Status}' tiles {viewModel.Map.Tiles.Count} canvas {viewModel.Map.CanvasWidth:F0}x{viewModel.Map.CanvasHeight:F0}";
                        }

                        if (current != last)
                        {
                            Console.WriteLine($"[{clock.Elapsed.TotalSeconds:F2}s] {current}");
                            last = current;
                        }

                        Thread.Sleep(20);
                    }

                    picked = null;
                }

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

                // [Issue 286] The other press: a layer the map opens with, switched off.
                if (StringOption(args, "--map-layers-off") is { } unwanted && raid.Renderer is { } offRenderer)
                {
                    foreach (var layerId in unwanted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (offRenderer.Layers.FirstOrDefault(item =>
                                string.Equals(item.Layer.Id.Value, layerId, StringComparison.OrdinalIgnoreCase)) is { IsVisible: true } layer)
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
                    // "none" picks the first objective with no number, which is one with no place.
                    var row = objectiveNumber == "none"
                        ? raid.QuestObjectives.FirstOrDefault(item => !item.HasNumber)
                        : raid.QuestObjectives.FirstOrDefault(item => item.Number == objectiveNumber);
                    if (row is null)
                    {
                        Console.Error.WriteLine($"No objective is numbered '{objectiveNumber}'.");
                    }
                    else
                    {
                        row.SelectCommand.Execute(null);
                        Pump(40);
                        Console.WriteLine($"Selected: objective {raid.SelectedObjective?.Number}, map marker '{raid.Renderer?.SelectedObject?.Label}' ({raid.Renderer?.SelectedObject?.SceneObject?.Id.Value})");
                        // Issue 379: put the selected objective on the middle of the plan, the way a click on
                        // the map after "Place on map" would, so the "Placed by you" marker can be seen.
                        if (args.Contains("--place-selected-objective") && raid.SelectedObjective is { CanPlace: true } selected &&
                            raid.Renderer is { } placing)
                        {
                            selected.PlaceCommand.Execute(null);
                            var bounds = placing.Scene.Bounds;
                            raid.PlaceMarkAt(new(bounds.MinimumX + (bounds.Width / 2), bounds.MinimumY + (bounds.Height / 2)), TarkovCompanion.App.ViewModels.V2.Raid.RaidCockpitViewModel.MarkKindFor(false));
                            Pump(80);
                            Console.WriteLine($"Placed: {raid.SelectedObjective?.Where} #{raid.SelectedObjective?.Number}");
                        }
                    }
                }

                // [Issue 594] Selects an extract or transit by name, the same as pressing its row
                // in Extract options would: selects and centres its marker, and draws its route if
                // one was planned. A render can then show the hover-free name label, the ring, and
                // the "Selected" card without a live click.
                if (StringOption(args, "--select-extract") is { } selectExtract)
                {
                    // Map selection and catalog features finish on sibling async paths. Wait for
                    // the named row itself; otherwise a real-map render can ask for the extract
                    // a moment before the rows arrive, then photograph it present but unselected.
                    for (var i = 0; i < 400 && !raid.MapExtracts.Any(item =>
                             string.Equals(item.Name, selectExtract, StringComparison.OrdinalIgnoreCase)); i++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        Thread.Sleep(25);
                    }

                    var extractRow = raid.MapExtracts.FirstOrDefault(item =>
                        string.Equals(item.Name, selectExtract, StringComparison.OrdinalIgnoreCase));
                    if (extractRow is null)
                    {
                        Console.Error.WriteLine($"No extract or transit named '{selectExtract}'.");
                    }
                    else
                    {
                        extractRow.SelectCommand.Execute(null);
                        Pump(40);
                        Console.WriteLine(
                            $"Selected extract: '{raid.Renderer?.SelectedObject?.Label}', detail '{raid.Renderer?.SelectedObject?.Detail}', " +
                            $"HasGenericSelection={raid.Renderer?.HasGenericSelection}, ShowsSelectedExtract={raid.ShowsSelectedExtract}, HasRenderer={raid.HasRenderer}.");
                    }
                }

                // Keep a selected extract and its revealed switch chain while returning to the
                // whole-plan view, so close switch steps can be judged together at 1920x1080.
                if (args.Contains("--map-fit") && raid.Renderer is { } fitRenderer)
                {
                    if (raid.FollowsPlayer)
                    {
                        raid.ToggleFollowCommand.Execute(null);
                    }

                    fitRenderer.FitPlanCommand.Execute(null);
                    Pump(40);
                    Console.WriteLine("Numbered switch steps: " + string.Join(
                        " | ",
                        fitRenderer.SpatialObjects
                            .Where(item => item.IsSwitchMark && item.HasMarkerNumber)
                            .Select(item => $"{item.MarkerGlyph} {item.Label} ({item.PinOffsetX:0.#},{item.PinOffsetY:0.#})")));
                }

                // [Issue 571] Marks an objective done by hand for the render — the same "Done" the
                // Objectives list offers — so a before/after render can show it leaving the map and
                // the list without driving a live app through the gesture.
                if (StringOption(args, "--mark-objective-done") is { } markObjectiveDone)
                {
                    var doneRow = raid.QuestObjectives.FirstOrDefault(item => item.Number == markObjectiveDone);
                    if (doneRow is null)
                    {
                        Console.Error.WriteLine($"No objective is numbered '{markObjectiveDone}'.");
                    }
                    else if (!doneRow.CanToggleDone)
                    {
                        Console.Error.WriteLine("Done is not offered on this row (no hand-done store wired up).");
                    }
                    else
                    {
                        doneRow.ToggleDoneCommand.Execute(null);
                        Pump(80);
                        Console.WriteLine($"Marked done: objective {markObjectiveDone}");
                    }
                }

                // [Issue 571] Brings every done objective back, dimmed with a check, the same as
                // pressing "Show completed" on the Objectives card.
                if (args.Contains("--show-completed-objectives"))
                {
                    raid.ShowCompletedObjectives = true;
                    Pump(40);
                }

                // [Issue 508] Waypoints (and a ping) on the raid map, so a render can show pins
                // next to quest objectives. --seed-marks N drops N waypoints spread across the
                // plan and one ping; --seed-marks-collide additionally drops two more waypoints
                // both exactly on the plan's own centre — the default camera's own centre too, so
                // a render can show the collision without having to be panned onto it — so a
                // render can show three pins landing on the exact same spot.
                if (IntOption(args, "--seed-marks", 0) is var markCount and > 0 && raid.Renderer is { } marksRenderer)
                {
                    var bounds = marksRenderer.Scene.Bounds;
                    double[] fractions = [0.22, 0.38, 0.5, 0.64, 0.78];
                    for (var i = 0; i < markCount; i++)
                    {
                        var fx = fractions[i % fractions.Length];
                        var fy = 0.28 + (0.12 * (i % 3));
                        raid.PlaceMarkAt(
                            new(bounds.MinimumX + (bounds.Width * fx), bounds.MinimumY + (bounds.Height * fy)),
                            TarkovCompanion.App.ViewModels.V2.Raid.RaidCockpitViewModel.MarkKindFor(true));
                    }

                    raid.PlaceMarkAt(
                        new(bounds.MinimumX + (bounds.Width * 0.5), bounds.MinimumY + (bounds.Height * 0.62)),
                        TarkovCompanion.App.ViewModels.V2.Raid.RaidCockpitViewModel.MarkKindFor(false));

                    if (args.Contains("--seed-marks-collide"))
                    {
                        var centre = new TarkovCompanion.Core.Domain.Maps.Scene.MapScenePoint(
                            bounds.MinimumX + (bounds.Width / 2),
                            bounds.MinimumY + (bounds.Height / 2));
                        raid.PlaceMarkAt(centre, TarkovCompanion.App.ViewModels.V2.Raid.RaidCockpitViewModel.MarkKindFor(true));
                        raid.PlaceMarkAt(centre, TarkovCompanion.App.ViewModels.V2.Raid.RaidCockpitViewModel.MarkKindFor(true));
                    }

                    Pump(80);
                    Console.WriteLine("Marks: " + string.Join(" | ", raid.Marks.Select(mark => $"{mark.KindLabel} {mark.Label}")));
                }

                // [Issue 508] Zoom and rotate the plan the way the zoom buttons and the keyboard
                // rotate gesture do, so a render can show a pin's tip staying on the spot at a
                // closer zoom and with the map turned. --map-zoom N presses "zoom in" N times
                // (each press is 1.25x, the same as RequestZoom); --map-bearing DEG turns the
                // plan to that absolute bearing. Last, so selecting an objective or placing marks
                // above does not recentre the view and undo it.
                if (IntOption(args, "--map-zoom", 0) is var zoomSteps and > 0 && raid.Renderer is { } zoomRenderer)
                {
                    for (var i = 0; i < zoomSteps; i++)
                    {
                        zoomRenderer.RequestZoom(1);
                        Pump(10);
                    }

                    Console.WriteLine($"Zoom: {zoomRenderer.Scene.View.Camera.Zoom:F2}");
                }

                if (DoubleOption(args, "--map-bearing") is { } bearingDegrees && raid.Renderer is { } bearingRenderer)
                {
                    bearingRenderer.SetBearing(bearingDegrees);
                    Pump(20);
                    Console.WriteLine($"Bearing: {bearingRenderer.Scene.View.Camera.BearingDegrees:F1}");
                }

                // [Issue 551] What clips a mark, read off the visual tree. See MarkClipProbe.
                if (args.Contains("--mark-clip-probe"))
                {
                    MarkClipProbe.Run(window);
                }

                // Change map inside the run, and say what the view drew and how long it took.
                if (StringOption(args, "--then-map") is { } thenMaps)
                {
                    MapSwitchProbe.FirstPicturePath = StringOption(args, "--then-map-first");
                    MapSwitchProbe.Run(
                        window,
                        viewModel,
                        raid,
                        thenMaps,
                        measureMemory: args.Contains("--map-switch-memory"));
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
            // Not after --then-map: resizing the card is exactly what used to put a stale plan
            // rectangle right, so the nudge would hide the fault that option exists to show.
            if (StringOption(args, "--then-map") is null)
            {
                window.Width = width - 1;
                Pump(5);
                window.Width = width;
                Pump(10);
            }

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
            // [V2 rough package 60 — Intel scan] #287: the capture dialog holding a real decision.
            // Nothing can reach these states in a render without a game writing a screenshot, so
            // the shell's own projection is set to each one and the real view draws it.
            // "disagreement" is the recognizer reading a different screen than the armed intent;
            // "identified" is a finished read with its alternates.
            if (shell is not null && StringOption(args, "--capture-demo") is { } captureDemo)
            {
                var captureSession = new CaptureSessionId(Guid.Parse("30000000-0000-0000-0000-000000000287"));
                var captureNow = DateTimeOffset.UtcNow;
                shell.CaptureCommand.Execute(null);
                shell.UpdateCaptureState(captureDemo switch
                {
                    "disagreement" => new V2CaptureShellState(
                        ScanIntent.Stash,
                        new StateRevision(1),
                        V2NavigationContext.ThisDesktop,
                        attention: new V2CaptureAttention(
                            V2CaptureAttentionKind.IntentMismatch,
                            captureSession,
                            "shot-1",
                            0,
                            ScanIntent.Stash,
                            new StateRevision(1),
                            V2NavigationContext.ThisDesktop,
                            RecognizedContext.Flea)),
                    "unknown" => new V2CaptureShellState(
                        ScanIntent.Auto,
                        new StateRevision(1),
                        V2NavigationContext.ThisDesktop,
                        attention: new V2CaptureAttention(
                            V2CaptureAttentionKind.UnknownContext,
                            captureSession,
                            "shot-1",
                            0,
                            ScanIntent.Auto,
                            new StateRevision(1),
                            V2NavigationContext.ThisDesktop)),
                    "identified" => new V2CaptureShellState(
                        ScanIntent.Auto,
                        new StateRevision(1),
                        V2NavigationContext.ThisDesktop,
                        review: new V2CaptureReview(
                            captureSession,
                            "shot-1",
                            0,
                            ScanIntent.Auto,
                            RecognizedContext.Item,
                            captureNow,
                            "Graphics card · 82% sure · also Graphics tablet, GPU crate",
                            "Screenshot · ambiguous_runner_up",
                            canCorrect: false)
                        {
                            // [f920 capture] The candidate list that replaced "Correct result".
                            Candidates =
                            [
                                new("demo-graphics-card", "Graphics card", 0.82),
                                new("demo-graphics-tablet", "Graphics tablet", 0.61),
                                new("demo-gpu-crate", "GPU crate", 0.44),
                            ],
                            ChosenCandidateId = "demo-graphics-card",
                        }),
                    _ => throw new ArgumentException($"No capture demo is named '{captureDemo}'."),
                });
                Pump(20);
            }

            if (shell is not null && args.Contains("--capture-batch-demo"))
            {
                shell.CaptureCommand.Execute(null);
                shell.ShowManualImageBatchPreview(
                    ["loot-west-wing.png", "stash-scroll-02.png", "flea-listing-euros.png"]);
                Pump(20);
            }

            // [#283] --seed-owned id=n,id=n records owned counts as a stash or case scan would, and
            // --stash-subscan ammo|keys starts a guided case scan from the Stash page's chips, so
            // a following --capture-image with the same --capture-intent lands in it.
            if (StringOption(args, "--seed-owned") is { } ownedText)
            {
                var players = services.GetRequiredService<TarkovCompanion.Core.Abstractions.IPlayerProfileService>();
                var profile = players.GetActiveAsync(default).GetAwaiter().GetResult();
                var owned = new Dictionary<string, int>(profile.OwnedItemCounts, StringComparer.Ordinal);
                foreach (var pair in ownedText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var (id, count) = pair.Split('=', 2) is [var key, var value] ? (key, int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)) : (pair, 1);
                    owned[id] = count;
                }

                players.SaveAsync(profile with { OwnedItemCounts = owned }, default).GetAwaiter().GetResult();
                DrainUntilComplete(viewModel.Ammo.RefreshOwnedAsync(CancellationToken.None));
                DrainUntilComplete(viewModel.Keys.RefreshOwnedAsync(CancellationToken.None));
                Pump(20);
            }

            // [#283] --ammo-caliber <text> filters the Ammo page's calibers and opens the first.
            if (StringOption(args, "--ammo-caliber") is { } caliberText)
            {
                DrainUntilComplete(viewModel.Ammo.LoadAsync(CancellationToken.None));
                viewModel.Ammo.SearchQuery = caliberText;
                viewModel.Ammo.SelectedCaliber = viewModel.Ammo.Calibers.FirstOrDefault();
                for (var turn = 0; turn < 200 && viewModel.Ammo.Rounds.Count == 0; turn++)
                {
                    Pump(1);
                    Thread.Sleep(10);
                }

                // --ammo-round <name> then opens the round whose name ends with it, once the
                // caliber has finished ranking (which clears the choice).
                Pump(20);
                if (StringOption(args, "--ammo-round") is { } roundText &&
                    viewModel.Ammo.Rounds.FirstOrDefault(round => round.Name.EndsWith(roundText, StringComparison.OrdinalIgnoreCase)) is { } round)
                {
                    viewModel.Ammo.SelectedRound = round;
                }

                Pump(20);
            }

            // [#283] --keys-filter owned presses the Keys page's "You own" chip.
            if (shell?.KeysWorkspace is { } keysWorkspace && StringOption(args, "--keys-filter") is { } keysFilter)
            {
                var filter = Enum.Parse<TarkovCompanion.App.ViewModels.V2.Intel.KeyVerdictFilter>(keysFilter, ignoreCase: true);
                keysWorkspace.VerdictFilters.Single(chip => chip.Filter == filter).SelectCommand.Execute(null);
                Pump(20);
            }

            if (shell is not null && StringOption(args, "--stash-subscan") is { } subScanText)
            {
                var intent = Enum.Parse<ScanIntent>(subScanText, ignoreCase: true);
                var stashWorkspace = services.GetRequiredService<TarkovCompanion.App.ViewModels.V2.StashScan.StashScanWorkspaceViewModel>();
                DrainUntilComplete(stashWorkspace.LoadAsync());
                stashWorkspace.ScanTargets.Single(target => target.Intent == intent).SelectCommand.Execute(null);
                DrainUntilComplete(((TarkovCompanion.App.ViewModels.AsyncDelegateCommand)stashWorkspace.StartSelectedScanCommand).ExecuteAsync());
                Pump(20);
            }

            // [f920 capture] --capture-image <file> [--capture-intent loot|stash|auto]: a picture
            // handed to the shell the way the file picker hands one over, through the composed
            // bridge, intake and pipeline. The panel is left open on whatever came of it.
            if (shell is not null && StringOption(args, "--capture-image") is { } captureImage)
            {
                var wanted = Enum.Parse<ScanIntent>(StringOption(args, "--capture-intent") ?? "Auto", ignoreCase: true);
                shell.CaptureCommand.Execute(null);
                shell.CaptureIntents.Single(offered => offered.Intent == wanted).SelectCommand.Execute(null);
                shell.SubmitManualImage(TarkovCompanion.App.ViewModels.V2.Shell.V2ManualImageOrigin.Picker, captureImage, null);
                var sessions = services.GetRequiredService<TarkovCompanion.Application.Services.CaptureSessions.ICaptureSessionService>();
                for (var turn = 0; turn < 1200 && !sessions.Snapshot.Sessions.Any(session => session.IsTerminal) && !shell.HasCaptureAttention; turn++)
                {
                    Pump(1);
                    Thread.Sleep(25);
                }

                // --capture-analyse-as-armed presses the button a screen nobody could place offers,
                // which on this host is every screen: OCR is Windows-only.
                Pump(40);
                if (args.Contains("--capture-analyse-as-armed") &&
                    shell.CaptureAttentionActions.FirstOrDefault(action => action.Resolution == V2CaptureResolutionKind.AnalyzeAsArmed) is { } asArmed)
                {
                    asArmed.InvokeCommand.Execute(null);
                    for (var turn = 0; turn < 2400 && !sessions.Snapshot.Sessions.Any(session => session.IsTerminal); turn++)
                    {
                        Pump(1);
                        Thread.Sleep(25);
                    }
                }

                // --capture-close shows what the capture left behind instead of the panel.
                if (args.Contains("--capture-close"))
                {
                    if (shell.IsCaptureOpen)
                    {
                        shell.CaptureCommand.Execute(null);
                    }

                    DrainUntilComplete(services.GetRequiredService<TarkovCompanion.App.ViewModels.V2.StashScan.StashScanWorkspaceViewModel>().LoadAsync());
                }

                // [#283] --stash-subscan-finish finishes the case scan the capture joined.
                if (args.Contains("--stash-subscan-finish"))
                {
                    var stashWorkspace = services.GetRequiredService<TarkovCompanion.App.ViewModels.V2.StashScan.StashScanWorkspaceViewModel>();
                    DrainUntilComplete(((TarkovCompanion.App.ViewModels.AsyncDelegateCommand)stashWorkspace.FinishScanCommand).ExecuteAsync());
                    Console.WriteLine($"Case scan: {stashWorkspace.Status}");
                }

                Pump(40);
                Console.WriteLine($"Capture image: {shell.CaptureManualStatus} | armed {shell.CaptureArmedStatus} | route {shell.Router.CurrentAddress}");
                foreach (var notice in sessions.Snapshot.Notices.TakeLast(8))
                {
                    Console.WriteLine($"Capture notice: {notice.Kind} {notice.Code}");
                }
            }

            // [#289] One mark of each scope and lifetime; see MarkScopeDemo. Before the team demo,
            // whose group the running group service would otherwise replace while this pumps.
            if (shell is not null && args.Contains("--mark-scopes-demo"))
            {
                MarkScopeDemo.Run(window, services, viewModel.Map.RenderModel?.Location.Id, outputPath, args.Contains("--open-mark-menu"), Pump);
            }

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
                        // [#553] Nothing is claimed any more; these are a desktop with no group key...
                        pairing.PresentForPreview(
                            RelayOwnerClaimState.NotClaimed,
                            CompanionPairingStage.Idle,
                            claimMessage: CompanionPairingViewModel.GroupKeyNeededMessage);
                        break;
                    case "unclaimable":
                        // ...one whose group key the relay refused...
                        pairing.PresentForPreview(
                            RelayOwnerClaimState.NotClaimed,
                            CompanionPairingStage.Idle,
                            claimMessage: "This relay did not accept your group key.");
                        break;
                    case "claimed":
                        // ...and one that has just registered itself.
                        pairing.PresentForPreview(
                            RelayOwnerClaimState.ClaimedByThisDesktop,
                            CompanionPairingStage.Idle,
                            claimMessage: "Connected to the relay.");
                        break;
                    case "restored":
                        // [#289] Claimed on an earlier run and picked back up at startup: nothing
                        // was attempted this run, so there is no attempt message to show.
                        pairing.PresentForPreview(
                            RelayOwnerClaimState.ClaimedByThisDesktop,
                            CompanionPairingStage.Idle,
                            devices: [DemoPairedDevice("Kitchen tablet")]);
                        break;
                    case "pairing":
                        pairing.PresentForPreview(
                            RelayOwnerClaimState.ClaimedByThisDesktop,
                            CompanionPairingStage.AwaitingTablet,
                            pairingCode: "K7M2-9QRT-4B",
                            // [V2 rough package 60 — Team] #289: far enough out that the
                            // countdown reads in minutes, which is the state it spends most of
                            // its life in.
                            codeExpiresUtc: DateTimeOffset.UtcNow.AddMinutes(4).AddSeconds(37));
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
                        // #290: what the app wires at startup, so "Send to tablet" draws enabled.
                        pairing.SendMapToTablet = _ => Task.FromResult(true);
                        break;
                    // [#562] The shell-level prompt (ControlRequestPromptView, V2ShellViewModel.
                    // ControlRequestPrompt) draws from the same CompanionPairingViewModel Team's
                    // own Allow/Deny row does, so putting it into this state here proves it shows
                    // up on whatever --route is rendered, not only team/tablet.
                    case "control-requested":
                        pairing.PresentForPreview(
                            RelayOwnerClaimState.ClaimedByThisDesktop,
                            CompanionPairingStage.Idle,
                            claimMessage: "Claimed. This desktop is now the relay's owner.",
                            devices: [DemoPairedDevice("Kitchen tablet")],
                            controlRequestMessage: "Kitchen tablet is asking to control this desktop.");
                        break;
                    // [#601] A tablet whose Control was refused for a grant from an older build.
                    case "out-of-date":
                        pairing.PresentForPreview(
                            RelayOwnerClaimState.ClaimedByThisDesktop,
                            CompanionPairingStage.Idle,
                            devices: [DemoPairedDevice("Kitchen tablet", pairingOutOfDate: true)]);
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
                var demo = RaidDemo(viewModel.Map.RenderModel, IntOption(args, "--raid-minutes", 14));
                if (args.Contains("--raid-left"))
                {
                    demo = SquadAfterRaidDemo.Apply(demo);
                }

                for (var i = 0; i < 8; i++)
                {
                    store.Update(snapshot => snapshot with { Raid = demo.Raid, Group = demo.Group });
                    Pump(2);
                }

                Pump(20);
            }

            // [#780] The demo squad shares real catalog quests on this map, by id.
            if (shell is not null && args.Contains("--squad-quests"))
            {
                // The render has no relay: stop the session so its "not sharing" stops replacing the demo squad.
                DrainUntilComplete(services.GetRequiredService<TarkovCompanion.Application.Services.Group.GroupSessionService>().DisposeAsync().AsTask());
                var squadBase = args.Contains("--raid-demo")
                    ? RaidDemo(viewModel.Map.RenderModel, IntOption(args, "--raid-minutes", 14)).Group
                    : TeamDemoGroup(viewModel.Map.RenderModel);
                DrainUntilComplete(SquadQuestsDemo.ApplyAsync(services, viewModel.Map, squadBase));
                services.GetRequiredService<TarkovCompanion.App.ViewModels.V2.Team.TeamWorkspaceViewModel>()
                    .Apply(services.GetRequiredService<TarkovCompanion.Application.Services.Runtime.IRuntimeStateStore>().Current);
                Pump(40);
                SquadQuestsDemo.ShowOnRaid(services, args.Contains("--route-squad"));
                Pump(80);
            }

            if (args.Contains("--objective-route-demo"))
            {
                var plan = services.GetRequiredService<PlanWorkspaceViewModel>();
                plan.RefreshMapPreview();
                Pump(80);
                plan.RefreshMapPreview();
                plan.SendSelectedObjectiveRouteToRaidForPreview();
                Pump(40);
            }

            // [Issue 701] Exercise the same choices exposed beside the gem and in Layers. This
            // runs before --loot-preset so the resulting frame and object count use the choice.
            var lootThreshold = StringOption(args, "--loot-threshold");
            var lootBasis = StringOption(args, "--loot-basis");
            if (StringOption(args, "--loot-tour") is null &&
                (lootThreshold is not null || lootBasis is not null) &&
                shell?.RaidCockpit is TarkovCompanion.App.ViewModels.V2.Raid.RaidCockpitViewModel lootFilterRaid)
            {
                ApplyLootValueFilter(
                    lootFilterRaid,
                    lootThreshold is null ? null : ParseLootThreshold(lootThreshold),
                    lootBasis);
                Pump(40);
            }

            // [Issue 286] Press a step of the traffic phase control: auto, early, mid or late.
            if (StringOption(args, "--traffic-phase") is { } trafficPhase &&
                shell?.RaidCockpit is TarkovCompanion.App.ViewModels.V2.Raid.RaidCockpitViewModel phasedRaid)
            {
                phasedRaid.TrafficPhases
                    .FirstOrDefault(choice => string.Equals(choice.Label, trafficPhase, StringComparison.OrdinalIgnoreCase))
                    ?.SelectCommand.Execute(null);
                Pump(40);
            }

            // [Issue 286] Press an extract's row, so the routes drawn are the ones to it.
            if (StringOption(args, "--route-extract") is { } routeExtract &&
                shell?.RaidCockpit is TarkovCompanion.App.ViewModels.V2.Raid.RaidCockpitViewModel routedRaid)
            {
                if (routedRaid.MapExtracts.FirstOrDefault(row =>
                        row.Name.Contains(routeExtract, StringComparison.OrdinalIgnoreCase)) is { RouteCommand: { } press })
                {
                    press.Execute(null);
                    Pump(40);
                }
                else
                {
                    Console.Error.WriteLine(
                        $"No routed extract matches '{routeExtract}'. Routed: " +
                        string.Join(", ", routedRaid.MapExtracts.Where(row => row.HasEstimate).Select(row => row.Name)));
                }
            }

            // [Issue 563] Press "High-value loot only" the way the gem button on the map does, so
            // a render can show what it leaves on the map with and without loot-spawn data.
            if (args.Contains("--loot-preset") &&
                shell?.RaidCockpit is TarkovCompanion.App.ViewModels.V2.Raid.RaidCockpitViewModel lootPresetRaid)
            {
                Console.WriteLine($"Loot before preset: unavailable {lootPresetRaid.Renderer?.HighValueLoot?.IsUnavailable}, layer on {LootLayerOn(lootPresetRaid)}");
                lootPresetRaid.Renderer?.HighValueLootPresetCommand.Execute(null);
                Pump(40);
                Console.WriteLine($"Loot after preset: layer on {LootLayerOn(lootPresetRaid)}");
                if (lootPresetRaid.Renderer?.Scene is { } lootScene)
                {
                    var lootProbe = services.GetRequiredService<TarkovCompanion.Application.Services.LootSpawns.IHighValueLootRuntimeSource>().Build(new(
                        lootScene.LocationId,
                        lootScene.TransformVersion,
                        lootScene.Bounds,
                        DateTimeOffset.UtcNow,
                        TarkovCompanion.Core.Domain.LootSpawns.HighValueLootFilter.Default,
                        lootScene.FloorIds));
                    Console.WriteLine(
                        $"Loot probe: bounds {lootScene.Bounds} · {lootProbe.Status.Completeness} {lootProbe.Status.Code} · {lootProbe.CompactLegend} · entries {lootProbe.Entries.Count}, objects {lootProbe.Objects.Count} · " +
                        string.Join(", ", lootProbe.Diagnostics.GroupBy(d => d.Code).Select(g => $"{g.Key} x{g.Count()}")));
                }
                if (lootPresetRaid.Renderer?.HighValueLoot is { } lootPanel)
                {
                    Console.WriteLine(
                        $"Loot: {lootPanel.StateMessage} | {lootPanel.FreshnessMessage} | {lootPanel.CoverageLabel} | rows {lootPanel.Rows.Count}, filtered {lootPanel.FilteredCount}, drawn {lootPanel.VisibleObjectIds.Count} | notice {lootPresetRaid.Renderer.RendererNotice}");
                }
            }

            // #286: the Corrections card as a player leaves it: a side and a time left set by hand,
            // one exit marked offered, the card open. After
            // --raid-demo, because that publishes a new raid and a new raid drops every correction. "return" then undoes the side, to show both states.
            if (StringOption(args, "--raid-corrections-demo") is { } correctionsDemo &&
                shell?.RaidCockpit is TarkovCompanion.App.ViewModels.V2.Raid.RaidCockpitViewModel correcting)
            {
                var card = correcting.Corrections;
                card.IsOpen = true;
                card.SetScavCommand.Execute(null);
                card.TimeLeftInput = "12:34";
                card.SetTimeLeftCommand.Execute(null);
                Pump(40);
                card.ExtractChoices.FirstOrDefault()?.ToggleCommand.Execute(null);
                Pump(40);
                if (string.Equals(correctionsDemo, "return", StringComparison.OrdinalIgnoreCase))
                {
                    card.ReturnSideCommand.Execute(null);
                    Pump(40);
                }

                Console.WriteLine(
                    $"Corrections: side {card.SideText} ({card.SideSource}), clock {card.ClockText} ({card.ClockSource}), " +
                    $"extracts {card.ExtractsSummary} ({card.ExtractsSource}); strip clock '{correcting.RaidPhaseLabel}'");
            }

            // Package 29 (parity): raids written through the real history service, so Debrief lists
            // and selects them the way it does for a player's own. The newest carries a trail on the
            // shown map; --watch then presses "Watch on map" and the render lands on the Raid map.
            if (shell is not null && args.Contains("--debrief-demo"))
            {
                var shownRaid = RaidDemo(viewModel.Map.RenderModel).Raid;
                var seeded = SeedDebriefAsync(services, shownRaid);
                DrainUntilComplete(seeded);
                if (args.Contains("--debrief-tags-demo"))
                {
                    var history = services.GetRequiredService<TarkovCompanion.Infrastructure.Persistence.Repositories.SqliteRaidHistoryService>();
                    DrainUntilComplete(history.RecordEventAsync(
                        seeded.Result,
                        "tag",
                        DateTimeOffset.UtcNow,
                        "{\"tag\":\"Tasks\",\"present\":true}",
                        CancellationToken.None));
                }

                if (args.Contains("--debrief-wrong-scan-demo"))
                {
                    var history = services.GetRequiredService<TarkovCompanion.Infrastructure.Persistence.Repositories.SqliteRaidHistoryService>();
                    var scans = history.ListEventsAsync(seeded.Result, "scan", CancellationToken.None);
                    DrainUntilComplete(scans);
                    var correctedUtc = DateTimeOffset.UtcNow;
                    DrainUntilComplete(history.RecordEventAsync(
                        seeded.Result,
                        TarkovCompanion.Application.Services.Raids.RaidScanCorrection.EventType,
                        correctedUtc,
                        new TarkovCompanion.Application.Services.Raids.RaidScanCorrection(
                            scans.Result[0].Id,
                            true,
                            correctedUtc).ToPayload(),
                        CancellationToken.None));
                }

                if (args.Contains("--debrief-loot-demo"))
                {
                    LootHistoryDemo.SeedDebrief(services, seeded.Result, DrainUntilComplete, args.Contains("--debrief-loot-wrong"));
                }

                if (args.Contains("--debrief-route-demo"))
                {
                    var history = services.GetRequiredService<TarkovCompanion.Infrastructure.Persistence.Repositories.SqliteRaidHistoryService>();
                    var plannedUtc = DateTimeOffset.UtcNow.AddHours(-2).AddMinutes(1);
                    var points = shownRaid.PositionTrail
                        .Select(step => new TarkovCompanion.Core.Domain.Maps.WorldPosition(
                            step.Position.X + 35,
                            step.Position.Y,
                            step.Position.Z))
                        .ToArray();
                    DrainUntilComplete(history.RecordEventAsync(
                        seeded.Result,
                        TarkovCompanion.Application.Services.Raids.RaidPlannedRoute.EventType,
                        plannedUtc,
                        new TarkovCompanion.Application.Services.Raids.RaidPlannedRoute(
                            shownRaid.MapId ?? "customs",
                            "Crossroads",
                            plannedUtc,
                            points).ToPayload(),
                        CancellationToken.None));
                }

                var debrief = services.GetRequiredService<TarkovCompanion.App.ViewModels.V2.Debrief.DebriefWorkspaceViewModel>();
                DrainUntilComplete(debrief.LoadAsync());
                // The demo composition records a live raid of its own, which is the newest and so
                // the one Debrief selects; pick the seeded one, the one with a trail to look at.
                DrainUntilComplete(debrief.SelectRaidAsync(seeded.Result, CancellationToken.None));
                Pump(20);
                if (args.Contains("--debrief-tags-demo"))
                {
                    debrief.SelectedTagFilterOption = debrief.TagFilterOptions.First(option => option.Tag == "Tasks");
                    debrief.SavedViewName = "Task raids";
                    debrief.SaveViewCommand.Execute(null);
                    Pump(20);
                }

                if (args.Contains("--watch"))
                {
                    services.GetRequiredService<TarkovCompanion.App.ViewModels.V2.Debrief.DebriefWorkspaceViewModel>()
                        .WatchOnMapCommand.Execute(null);
                    // The shell picks the raid's map, opens the replay and navigates: three async steps.
                    Pump(120);
                    if (args.Contains("--watch-end"))
                    {
                        viewModel.Raid.Replay.Step = viewModel.Raid.Replay.LastStep;
                        Pump(30);
                    }
                }

                // #291 package 2: put the raid list on a search or a filter before the frame, the
                // same way Plan does, so a render can show what a filtered history looks like.
                var debriefSearch = StringOption(args, "--debrief-search");
                var debriefOutcome = StringOption(args, "--debrief-filter-outcome");
                var debriefSide = StringOption(args, "--debrief-filter-side");
                if (debriefSearch is not null || debriefOutcome is not null || debriefSide is not null)
                {
                    if (debriefSearch is not null)
                    {
                        debrief.SearchText = debriefSearch;
                    }

                    if (debriefOutcome is not null)
                    {
                        debrief.OutcomeFilter = Enum.Parse<TarkovCompanion.App.ViewModels.V2.Debrief.DebriefOutcomeFilter>(debriefOutcome, ignoreCase: true);
                    }

                    if (debriefSide is not null)
                    {
                        debrief.SideFilter = Enum.Parse<TarkovCompanion.App.ViewModels.V2.Debrief.DebriefSideFilter>(debriefSide, ignoreCase: true);
                    }

                    Pump(20);
                }

                // #291 package 3: show the delete preview, the undo banner after a delete, or the
                // delete-before preview, so a render can show each state.
                if (args.Contains("--debrief-delete-preview"))
                {
                    debrief.BeginDeleteCommand.Execute(null);
                    Pump(20);
                }
                else if (args.Contains("--debrief-delete-confirm"))
                {
                    debrief.BeginDeleteCommand.Execute(null);
                    Pump(10);
                    debrief.ConfirmDeleteCommand.Execute(null);
                    Pump(40);
                }

                if (StringOption(args, "--debrief-delete-before") is { } deleteBefore)
                {
                    debrief.DeleteBeforeDate = DateTimeOffset.Parse(deleteBefore, System.Globalization.CultureInfo.InvariantCulture);
                    debrief.BeginBulkDeleteCommand.Execute(null);
                    Pump(20);
                }

                // #291 package 4: kills and the value brought out, typed on the selected raid, so a
                // render can show the manual facts and the per-map stats that follow from them.
                if (args.Contains("--debrief-manual-demo"))
                {
                    debrief.ManualPmcKills = 2;
                    debrief.ManualScavKills = 1;
                    debrief.ManualBossKills = 0;
                    debrief.ManualValueRoubles = 450_000;
                    debrief.SaveManualMetadataCommand.Execute(null);
                    Pump(40);
                }
            }

            // Package 17 (scan): render-only fixtures so the Loot decision and Stash scan
            // workspaces can be seen populated. Both go through the real services (the loot
            // planner, the snapshot store), so nothing here invents presentation state.
            // #572: "Show loot results on the tablet only", ticked, so a render can show the desk
            // staying on the map (needs --pairing-demo paired for the paired tablet).
            if (args.Contains("--loot-tablet-only") &&
                services.GetService<TarkovCompanion.App.ViewModels.V2.Setup.SetupAdminViewModel>()?.LootScan is { } lootSettings)
            {
                lootSettings.TabletOnly = true;
            }

            if (shell is not null && (args.Contains("--loot-demo") || args.Contains("--loot-rig-demo")))
            {
                var profile = services.GetRequiredService<TarkovCompanion.Application.Services.Runtime.IRuntimeStateStore>()
                    .Current.Profile ?? throw new InvalidOperationException("The demo composition has no profile.");
                var scope = new TarkovCompanion.Core.Domain.Inventory.InventoryProfileScope(
                    profile.Id, profile.ProfileGeneration, profile.GameMode.ToString());
                var lootResult = ScanDemo.LootResult(scope, args.Contains("--loot-rig-demo"));
                if (StringOption(args, "--loot-progress") is { } progress)
                {
                    LootProgressDemo.Show(shell, lootResult, string.Equals(progress, "final", StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    shell.ShowLootScanResult(new TarkovCompanion.App.ViewModels.V2.LootScan.LootScanViewModel(
                        lootResult,
                        openWiki: _ => Task.CompletedTask));
                }
                Pump(20);
            }

            // #274: "Last scans" and a reopened saved scan. See LootHistoryDemo.
            if (shell is not null && args.Contains("--loot-history-demo"))
            {
                LootHistoryDemo.RunLoot(services, shell, DrainUntilComplete, Pump, StringOption(args, "--loot-history-open"));
            }

            // [f920 capture] The same workspace decided by the composed application from a
            // seeded profile: an active quest, a pin, an Allergic event result. See SeededLootScan.
            if (shell is not null && args.Contains("--loot-seeded"))
            {
                SeededLootScan.Run(
                    services,
                    viewModel,
                    DrainUntilComplete,
                    Pump,
                    StringOption(args, "--loot-scan-flea-rates"),
                    StringOption(args, "--loot-scan-phase"),
                    StringOption(args, "--seed-database"));
                Pump(20);
            }

            // [f920 capture] #284: Intel > Flea over a photographed flea screen. See FleaScanDemo.
            if (shell is not null && StringOption(args, "--flea-scan-demo") is { } fleaScanItem)
            {
                FleaScanDemo.Run(services, DrainUntilComplete, Pump, fleaScanItem, StringOption(args, "--loot-scan-flea-rates"));
            }

            // Package 37: the same workspace over a picture the shipped recognizer actually read.
            if (shell is not null && StringOption(args, "--loot-scan-frame") is { } lootFrame)
            {
                var scan = ScanFrame.EvaluateAsync(
                    services,
                    lootFrame,
                    StringOption(args, "--icon-cache"),
                    StringOption(args, "--loot-scan-now"),
                    StringOption(args, "--loot-scan-flea-rates"),
                    StringOption(args, "--loot-scan-phase"));
                DrainUntilComplete(scan);
                shell.ShowLootScanResult(new TarkovCompanion.App.ViewModels.V2.LootScan.LootScanViewModel(
                    scan.Result.Result,
                    controls: scan.Result.Controls,
                    openWiki: _ => Task.CompletedTask));
                Pump(20);
            }

            if (shell is not null && args.Contains("--stash-demo"))
            {
                var profile = services.GetRequiredService<TarkovCompanion.Application.Services.Runtime.IRuntimeStateStore>()
                    .Current.Profile ?? throw new InvalidOperationException("The demo composition has no profile.");
                var scope = new TarkovCompanion.Core.Domain.Inventory.InventoryProfileScope(
                    profile.Id, profile.ProfileGeneration, profile.GameMode.ToString());
                var store = services.GetRequiredService<TarkovCompanion.Core.Domain.Stash.IStashSnapshotStore>();
                // The demo's items carry invented ids. Over a seeded catalog each is looked up by
                // name, so the sort plan is made from real prices and real needs or not at all.
                var repository = services.GetRequiredService<TarkovCompanion.Core.Abstractions.IItemRepository>();
                string Resolve(string id, string name)
                {
                    var search = repository.SearchAsync(name, 1, CancellationToken.None);
                    DrainUntilComplete(search);
                    return search.Result.FirstOrDefault()?.Item.Id ?? id;
                }

                var seed = ScanFrame.SeedFleaRatesAsync(
                    services,
                    StringOption(args, "--loot-scan-flea-rates"),
                    services.GetRequiredService<TimeProvider>().GetUtcNow());
                DrainUntilComplete(seed);
                var stashRecord = ScanDemo.StashRecord(scope, Resolve);
                DrainUntilComplete(store.SaveAsync(stashRecord, CancellationToken.None));
                if (args.Contains("--stash-review-demo"))
                {
                    var recognitionId = stashRecord.Recognition.Result.Value?.SnapshotId
                        ?? throw new InvalidOperationException("The stash demo has no recognition snapshot id.");
                    var reviews = services.GetRequiredService<TarkovCompanion.Core.Domain.Stash.IStashReviewCommandSink>();
                    DrainUntilComplete(reviews.AppendAsync(
                        new TarkovCompanion.Core.Domain.Stash.StashReviewCommand(
                            Guid.Parse("3ef70b2a-2aac-4ba1-bab8-b24dad6c1eb6"),
                            recognitionId,
                            TarkovCompanion.Core.Domain.Stash.StashReviewActionKind.CorrectQuantity,
                            ["stash/6/8"],
                            new DateTimeOffset(2026, 9, 22, 12, 34, 56, TimeSpan.Zero),
                            "v2.stash-workspace",
                            correctedQuantity: 3,
                            reason: "Counted on review."),
                        CancellationToken.None));
                }
                var stashWorkspace = services.GetRequiredService<TarkovCompanion.App.ViewModels.V2.StashScan.StashScanWorkspaceViewModel>();
                DrainUntilComplete(stashWorkspace.LoadAsync());
                if (args.Contains("--stash-export-demo"))
                {
                    stashWorkspace.ExportJsonCommand.Execute(null);
                }
                if (args.Contains("--stash-list"))
                {
                    stashWorkspace.ShowListCommand.Execute(null);
                }

                if (args.Contains("--stash-command-demo") && stashWorkspace.Items.FirstOrDefault() is { } selectedItem)
                {
                    stashWorkspace.SelectedItem = selectedItem;
                    DrainUntilComplete(((TarkovCompanion.App.ViewModels.AsyncDelegateCommand)stashWorkspace.PinSelectedCommand).ExecuteAsync());
                }

                if (args.Contains("--stash-open-loadout-demo") &&
                    stashWorkspace.Items.FirstOrDefault(item => item.HasLoadoutLink) is { OpenLoadoutCommand: TarkovCompanion.App.ViewModels.AsyncDelegateCommand openLoadout })
                {
                    DrainUntilComplete(openLoadout.ExecuteAsync());
                    Pump(40);
                }

                Pump(20);
            }

            if (shell is not null && args.Contains("--stash-no-profile-skip-demo"))
            {
                var captureStatus = services.GetRequiredService<TarkovCompanion.App.Services.V2.Capture.StashScanCaptureStatus>();
                captureStatus.ReportNoActiveProfile();
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

            if (shell is not null && StringOption(args, "--stash-real-burst") is { } stashRealBurst)
            {
                var stashScan = services.GetRequiredService<TarkovCompanion.App.ViewModels.V2.StashScan.StashScanWorkspaceViewModel>();
                var guided = services.GetRequiredService<TarkovCompanion.Application.Services.StashScan.GuidedStashScanService>();
                DrainUntilComplete(stashScan.LoadAsync());
                stashScan.StartSelectedScanCommand.Execute(null);
                Pump(10);
                DrainUntilComplete(StashScanDemo.AddRealBurstAsync(
                    guided,
                    services.GetRequiredService<TarkovCompanion.Core.Abstractions.IScreenshotImageLoader>(),
                    services.GetRequiredService<TarkovCompanion.Infrastructure.Recognition.Grid.GridPixelReconstructionBuilder>(),
                    services.GetRequiredService<TarkovCompanion.Infrastructure.Recognition.Grid.InventoryGridReconstructor>(),
                    stashRealBurst));
                stashScan.FinishScanCommand.Execute(null);
                Pump(50);
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

            // [#453] --hang-demo: the application's own watchdog, on this real dispatcher, against a
            // dispatcher job that does not return for 2.5 s. Prints what it wrote to the crash log.
            if (args.Contains("--hang-demo"))
            {
                var hangLog = Path.Combine(dataRoot, "hang-demo-logs");
                CrashLog.Install(hangLog);
                using var watchdog = UiHangWatchdog.ForApplication(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100));
                watchdog.Start();
                Pump(20);
                Dispatcher.UIThread.Post(() => Thread.Sleep(2500));
                Pump(40);
                Console.WriteLine($"Hang demo: {watchdog.HangsRecorded} hang(s) recorded.");
                foreach (var line in File.ReadAllLines(CrashLog.FilePath!).Where(line => line.Contains("ui-hang", StringComparison.Ordinal)))
                {
                    Console.WriteLine("  " + line);
                }

                CrashLog.Detach();
            }

            if (StringOption(args, "--open-flyout") is { } flyoutId)
            {
                FlyoutProbe.Save(window, flyoutId, outputPath, Pump);
            }

            // [#453] --stall-tour / --memory-tour N: walk the app and report stalls or memory.
            if (shell is not null && args.Contains("--stall-tour"))
            {
                StallTour.Run(window, viewModel, shell, services, args);
            }

            if (shell is not null && IntOption(args, "--raid-soak", 0) is var soakSeconds and > 0)
            {
                StallTour.RunRaidSoak(services, viewModel, shell, soakSeconds, IntOption(args, "--raid-soak-screenshot", 20), IntOption(args, "--raid-soak-group-ms", 2000));
            }

            // [#657] --loot-tour on|off: pan, zoom and idle per map with the loot layer on or off.
            if (shell is not null && StringOption(args, "--loot-tour") is { } lootTour)
            {
                LootTour.Run(
                    services,
                    viewModel,
                    shell,
                    lootTour == "on",
                    (StringOption(args, "--loot-tour-maps") ?? "customs,interchange,streets-of-tarkov").Split(','),
                    IntOption(args, "--loot-tour-idle", 60),
                    StringOption(args, "--loot-threshold") is { } threshold ? ParseLootThreshold(threshold) : null,
                    StringOption(args, "--loot-basis"));
            }

            if (shell is not null && IntOption(args, "--memory-tour", 0) is var memorySwitches and > 0)
            {
                StallTour.RunMemory(viewModel, shell, memorySwitches);
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
                MapSwitchProbe.UnlinkMapCache(dataRoot, demoMode);
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

    private static async Task SeedHeldItemAsync(IServiceProvider services, string itemId)
    {
        var profiles = services.GetRequiredService<IPlayerProfileService>();
        var profile = await profiles.GetActiveAsync(CancellationToken.None);
        var holdings = new Dictionary<string, int>(profile.OwnedItemCounts, StringComparer.Ordinal)
        {
            [itemId] = 2,
        };
        await profiles.SaveAsync(profile with { OwnedItemCounts = holdings }, CancellationToken.None);
        Console.WriteLine($"Recorded 2 held for {itemId} in the preview profile.");
    }

    private static async Task SeedChainReadyProfileAsync(IServiceProvider services)
    {
        var requirementCatalog = services.GetRequiredService<IRequirementCatalog>();
        var traderCatalog = services.GetRequiredService<ITraderCatalog>();
        var barterCatalog = services.GetRequiredService<IBarterCatalog>();
        var stations = await requirementCatalog.GetStationsAsync(CancellationToken.None);
        var traderNames = await traderCatalog.GetNamesAsync(CancellationToken.None);
        var barters = await barterCatalog.GetAsync(CancellationToken.None);
        var profiles = services.GetRequiredService<IPlayerProfileService>();
        var profile = await profiles.GetActiveAsync(CancellationToken.None);
        await profiles.SaveAsync(profile with
        {
            HideoutStationLevels = stations.ToDictionary(station => station.StationId, _ => 99, StringComparer.Ordinal),
            TraderLevels = traderNames.Keys.ToDictionary(traderId => traderId, _ => 4, StringComparer.Ordinal),
            CompletedTaskIds = new HashSet<string>(
                profile.CompletedTaskIds.Concat(barters.Select(barter => barter.TaskUnlock).OfType<string>()),
                StringComparer.Ordinal),
        }, CancellationToken.None);
        Console.WriteLine($"Chain profile: {stations.Count} stations ready, {traderNames.Count} traders LL4.");
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
                new(Guid.NewGuid(), profile.Id, map, "Regular", started, null, null, null),
                CancellationToken.None);
            await history.EndAsync(id, started + length, outcome, notes, CancellationToken.None);
            return id;
        }

        await Raid("factory", TimeSpan.FromDays(3), TimeSpan.FromMinutes(21), "Survived", null);
        // The companion's own words for a raid it found closed on restart, so the preview shows an
        // inferred end beside the hand-typed and observed ones.
        await Raid(
            "woods",
            TimeSpan.FromDays(1),
            TimeSpan.FromMinutes(38),
            TarkovCompanion.Core.Domain.Raids.RaidClosure.ClosedOnRestartOutcome,
            TarkovCompanion.Core.Domain.Raids.RaidClosure.ClosedOnRestartNotes);
        var newest = await Raid(shown.MapId ?? "customs", TimeSpan.FromHours(2), TimeSpan.FromMinutes(27), null, null);
        // A player's correction, through the same call Debrief's Save button makes, so it is stored
        // as a correction event and reads as manual.
        await history.CorrectAsync(newest, "Survived", "Dorms then RUAF roadblock", CancellationToken.None);
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

        // Scans taken during that raid, written as the runtime writes them (default JSON options):
        // two items it recognised, one screenshot that showed nothing it could name.
        var scanned = raidStart + TimeSpan.FromMinutes(6);
        foreach (var (name, id, value, confidence, action) in new[]
        {
            ("Graphics card", "57347ca924597744596b4e71", 232_000L, 0.94, "Take"),
            ("Salewa first aid kit", "544fb45d4bdc2dee738b4568", 27_500L, 0.81, "Sell"),
        })
        {
            scanned += TimeSpan.FromMinutes(4);
            await history.RecordEventAsync(
                newest,
                "scan",
                scanned,
                System.Text.Json.JsonSerializer.Serialize(new TarkovCompanion.Application.Services.Runtime.ScanExecutionResult(
                    true, true, id, name, value, value, action, new(confidence), scanned, "screenshot", "preview")),
                CancellationToken.None);
        }

        scanned += TimeSpan.FromMinutes(3);
        await history.RecordEventAsync(
            newest,
            "scan",
            scanned,
            System.Text.Json.JsonSerializer.Serialize(new TarkovCompanion.Application.Services.Runtime.ScanExecutionResult(
                true, false, null, null, null, null, null, new(0), scanned, "screenshot", "preview")),
            CancellationToken.None);

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
                new(null, null, "Sam", "Usec", 29, true, false, null, []),
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
    internal static (TarkovCompanion.Core.Domain.Raids.RaidSnapshot Raid, TarkovCompanion.Application.Services.Group.GroupSnapshot Group) RaidDemo(
        TarkovCompanion.Application.Services.Maps.MapRenderModel? model,
        int minutesAgo = 14)
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
            now.AddMinutes(-Math.Max(0, minutesAgo)),
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

        // [Issue 581] Three, not two: enough to prove a squadmate's colour is their own and not
        // shared with the nearest other teammate by coincidence.
        var group = new TarkovCompanion.Application.Services.Group.GroupSnapshot(
            true,
            [Mate("Geo", 72, 34, 300, 6), Mate("Riley", 44, 71, 120, 25), Mate("Sam", 58, 52, 30, 12)],
            "Sharing as Clay · 3 others here",
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

    private static bool? LootLayerOn(TarkovCompanion.App.ViewModels.V2.Raid.RaidCockpitViewModel raid) =>
        raid.Renderer?.Scene.View.Layers
            .FirstOrDefault(layer => layer.LayerId == TarkovCompanion.Application.Services.LootSpawns.HighValueLootLayerService.LayerId)
            ?.IsVisible;

    private static void Pump(int turns)
    {
        for (var i = 0; i < turns; i++)
        {
            UiStallMeter.RunJobs();
            Thread.Sleep(25);
        }

        Settle();
    }

    /// <summary>
    /// Keeps pumping until the page has stopped reading, or ten seconds have gone.
    /// </summary>
    /// <remarks>
    /// [#453] A fixed number of turns was enough while every database read ran inside the turn
    /// that asked for it. Reads now happen on the pool and come back in later turns, and the first
    /// render after that change photographed Keep saying "Loading the keep list…". Settled means
    /// six turns in a row with no database call in flight, no workspace load unfinished, and
    /// nothing for the dispatcher to do.
    /// </remarks>
    private static void Settle()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var quiet = 0;
        while (quiet < 6 && System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
        {
            var turn = System.Diagnostics.Stopwatch.GetTimestamp();
            UiStallMeter.RunJobs();
            var idle = System.Diagnostics.Stopwatch.GetElapsedTime(turn) < TimeSpan.FromMilliseconds(2)
                && TarkovCompanion.Infrastructure.Persistence.SqliteConnectionFactory.OpenConnectionCount == 0
                && !UiActivity.IsLoading;
            quiet = idle ? quiet + 1 : 0;
            Thread.Sleep(10);
        }
    }

    private static void DrainUntilComplete(Task task)
    {
        while (!task.IsCompleted)
        {
            UiStallMeter.RunJobs();
            Thread.Sleep(5);
        }

        task.GetAwaiter().GetResult();
    }

    /// <summary>One paired device, so the list has something in it to photograph.</summary>
    private static TarkovCompanion.App.ViewModels.V2.Tablet.PairedDeviceRowViewModel DemoPairedDevice(
        string name,
        bool pairingOutOfDate = false)
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
        return new(device, _ => Task.CompletedTask, pairingOutOfDate);
    }

    private static int IntOption(string[] args, string name, int fallback)
    {
        var value = StringOption(args, name);
        return value is null ? fallback : int.Parse(value);
    }

    private static long ParseLootThreshold(string value) => value.Equals("any", StringComparison.OrdinalIgnoreCase)
        ? 0
        : long.TryParse(value, out var threshold) && threshold is 50_000 or 100_000 or 250_000 or 500_000
            ? threshold
            : throw new ArgumentException($"No loot threshold is named '{value}'.");

    private static void ApplyLootValueFilter(
        TarkovCompanion.App.ViewModels.V2.Raid.RaidCockpitViewModel raid,
        long? threshold,
        string? basis)
    {
        var loot = raid.Renderer?.HighValueLoot ??
            throw new InvalidOperationException("The Raid map has no potential-loot filter.");
        if (threshold is { } minimum)
        {
            loot.ValueThresholdChoices.Single(choice => choice.Id == $"threshold-{(minimum == 0 ? "any" : minimum)}")
                .SelectCommand.Execute(null);
            loot = raid.Renderer?.HighValueLoot ?? loot;
        }

        if (basis is not null)
        {
            var id = basis switch
            {
                "per-item" => "compact-basis-BestNet",
                "per-slot" => "compact-basis-ValuePerSquare",
                _ => throw new ArgumentException($"No loot value basis is named '{basis}'."),
            };
            loot.CompactValueBasisChoices.Single(choice => choice.Id == id).SelectCommand.Execute(null);
        }
    }

    private static double? DoubleOption(string[] args, string name)
    {
        var value = StringOption(args, name);
        return value is null ? null : double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? StringOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>Two ordinary displays for judging Setup's monitor rows without Windows APIs.</summary>
    private sealed class RenderMonitorService : IMonitorService
    {
        public Task<IReadOnlyList<DisplayDescriptor>> GetDisplaysAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<DisplayDescriptor> displays =
            [
                new("\\\\.\\DISPLAY1", "Display 1", new(0, 0, 1920, 1080), true, 1,
                    new(0, 0, 1920, 1040)),
                new("\\\\.\\DISPLAY2", "Display 2", new(1920, 0, 2560, 1440), false, 1.25,
                    new(1920, 0, 2560, 1400)),
            ];
            return Task.FromResult(displays);
        }
    }

    private sealed class RenderWindowPlacementController(string currentDisplayId) : IDesktopWindowPlacementController
    {
        public event EventHandler? CurrentDisplayChanged;

        public string? CurrentDisplayId { get; private set; } = currentDisplayId;

        public Task MoveToAsync(string displayId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurrentDisplayId = displayId;
            CurrentDisplayChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Applies the appearance a render asks for, and returns the applier so a window can be
    /// given the reduced-motion class the running app gives it.
    /// </summary>
    /// <remarks>
    /// Named the way the Setup page names them ("light", "high-contrast", "red-green") rather
    /// than by enum spelling, so a render command reads like the choice it is proving.
    /// </remarks>
    private static V2AppearanceApplier? ApplyAppearance(IServiceProvider services, string[] args)
    {
        var theme = StringOption(args, "--appearance");
        var vision = StringOption(args, "--color-vision");
        var density = StringOption(args, "--density");
        var scale = IntOption(args, "--text-scale", 0);
        var reduceMotion = args.Contains("--reduce-motion");
        if (theme is null && vision is null && density is null && scale == 0 && !reduceMotion)
        {
            return null;
        }

        var preferences = new WorkspacePreferences(
            theme switch
            {
                "light" => AppearanceTheme.Light,
                "high-contrast" => AppearanceTheme.HighContrast,
                "system" => AppearanceTheme.System,
                null or "dark" => AppearanceTheme.Dark,
                _ => throw new ArgumentException($"No appearance is named '{theme}'."),
            },
            vision switch
            {
                "red-green" => ColorVisionMode.RedGreenSafe,
                "blue-yellow" => ColorVisionMode.BlueYellowSafe,
                "mono" => ColorVisionMode.Monochrome,
                null or "standard" => ColorVisionMode.Standard,
                _ => throw new ArgumentException($"No colour-vision palette is named '{vision}'."),
            },
            scale == 0 ? 100 : scale,
            density switch
            {
                "compact" => InterfaceDensity.Compact,
                "comfortable" => InterfaceDensity.Comfortable,
                null or "standard" => InterfaceDensity.Standard,
                _ => throw new ArgumentException($"No density is named '{density}'."),
            },
            reduceMotion);

        var service = services.GetRequiredService<WorkspacePreferenceService>();
        DrainUntilComplete(service.UpdateAsync(preferences, CancellationToken.None));
        var applier = new V2AppearanceApplier(
            Avalonia.Application.Current ?? throw new InvalidOperationException("No application was built."),
            // Headless has no platform colour values; "system" would otherwise mean "dark"
            // silently and a --appearance system render would prove nothing.
            () => new Avalonia.Platform.PlatformColorValues
            {
                ThemeVariant = Avalonia.Platform.PlatformThemeVariant.Light,
            });
        applier.Apply(preferences);
        Console.WriteLine($"Appearance: {applier.Applied?.Key} text {preferences.TextScalePercent}% {preferences.Density}" +
            (preferences.ReduceMotion ? " reduced-motion" : string.Empty));
        return applier;
    }
}
