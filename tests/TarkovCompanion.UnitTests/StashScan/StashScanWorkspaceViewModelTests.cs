using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.ViewModels.V2.StashScan;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Application.Services.Wiki;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Stash;
using TarkovCompanion.UnitTests.V2Contracts;
using TarkovCompanion.UnitTests.V2Shell;

namespace TarkovCompanion.UnitTests.StashScan;

public sealed class StashScanWorkspaceViewModelTests
{
    private static readonly Guid ProfileId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid SnapshotId = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly DataProvenance Fixture = new("fixture", DateTimeOffset.UnixEpoch);

    [Fact]
    public void One_square_uses_the_catalog_short_name_while_a_larger_tile_keeps_the_full_name()
    {
        var definition = new ItemDefinition(
            "item-gas-analyzer",
            "Gas analyzer",
            "GasAn",
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
            Fixture);
        var oneSquare = ReconstructedTile(width: 1, height: 1);
        var larger = ReconstructedTile(width: 2, height: 1);

        Assert.Equal("GasAn", StashScanWorkspaceViewModel.TileName(oneSquare, definition, definition.Name));
        Assert.Equal("Gas analyzer", StashScanWorkspaceViewModel.TileName(larger, definition, definition.Name));
    }

    [Fact]
    public async Task Loading_without_a_profile_says_so_and_stays_empty()
    {
        var store = new FakeSnapshotStore();
        var reviewCommands = new InMemoryStashReviewCommandSink();
        var viewModel = new StashScanWorkspaceViewModel(
            store,
            Workflow(store, reviewCommands),
            reviewCommands,
            new FakeItemFactCatalog([], []),
            new FakeRuntimeStateStore(V2ShellTestData.Snapshot() with { Profile = null }));

        await viewModel.LoadAsync();

        Assert.False(viewModel.HasSnapshots);
        Assert.Contains("No profile", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loading_after_a_capture_was_skipped_explains_what_to_do_next()
    {
        var store = new FakeSnapshotStore();
        var reviewCommands = new InMemoryStashReviewCommandSink();
        var captureStatus = new StashScanCaptureStatus();
        captureStatus.ReportNoActiveProfile();
        var viewModel = new StashScanWorkspaceViewModel(
            store,
            Workflow(store, reviewCommands),
            reviewCommands,
            new FakeItemFactCatalog([], []),
            new FakeRuntimeStateStore(V2ShellTestData.Snapshot() with { Profile = null }),
            captureStatus: captureStatus);

        await viewModel.LoadAsync();

        Assert.Equal(StashScanCaptureStatus.NoActiveProfileMessage, viewModel.Status);
    }

    [Fact]
    public void SelectedItemDisplayNameIsNeverNullSoTheHiddenCorrectionCardNeverBindsAgainstNull()
    {
        var store = new FakeSnapshotStore();
        var reviewCommands = new InMemoryStashReviewCommandSink();
        var viewModel = new StashScanWorkspaceViewModel(
            store,
            Workflow(store, reviewCommands),
            reviewCommands,
            new FakeItemFactCatalog([], []),
            new FakeRuntimeStateStore(RuntimeSnapshot()));

        Assert.False(viewModel.HasSelectedItem);
        Assert.Equal(string.Empty, viewModel.SelectedItemDisplayName);
    }

    /// <summary>#283: the Ammo and Keys chips start guided case sub-scans, not a one-off capture.</summary>
    [Theory]
    [InlineData(ScanIntent.Ammo, StashScanKind.Ammo, "ammo case")]
    [InlineData(ScanIntent.Keys, StashScanKind.Keys, "key tool")]
    public async Task The_ammo_and_key_chips_start_a_guided_case_scan(ScanIntent intent, StashScanKind kind, string asksFor)
    {
        var store = new FakeSnapshotStore();
        var reviewCommands = new InMemoryStashReviewCommandSink();
        var workflow = Workflow(store, reviewCommands);
        var guided = new GuidedStashScanService(
            new StashScanAssembler(),
            new StashLayoutAligner(),
            new StashReconstructionProjector(),
            workflow,
            new StashOwnedCountsApplier(new StubProfileService(RuntimeSnapshot().Profile!)),
            new MemoryPendingStore());
        var viewModel = new StashScanWorkspaceViewModel(
            store,
            workflow,
            reviewCommands,
            new FakeItemFactCatalog([], []),
            new FakeRuntimeStateStore(RuntimeSnapshot()),
            guidedScan: guided);
        var requested = new List<ScanIntent>();
        viewModel.ScanRequested += (_, requestedIntent) => requested.Add(requestedIntent);
        await viewModel.LoadAsync();

        viewModel.ScanTargets.Single(target => target.Intent == intent).SelectCommand.Execute(null);
        await ((AsyncDelegateCommand)viewModel.StartSelectedScanCommand).ExecuteAsync();

        Assert.Empty(requested);
        Assert.True(viewModel.IsScanInProgress);
        Assert.Equal(kind, guided.Current.Kind);
        Assert.Contains(asksFor, viewModel.GuidedNextStep, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_full_stash_scan_is_guided_shown_while_it_collects_and_saved_on_finish()
    {
        var store = new FakeSnapshotStore();
        var reviewCommands = new InMemoryStashReviewCommandSink();
        var workflow = Workflow(store, reviewCommands);
        var guided = new GuidedStashScanService(
            new StashScanAssembler(),
            new StashLayoutAligner(),
            new StashReconstructionProjector(),
            workflow,
            new StashOwnedCountsApplier(new StubProfileService(RuntimeSnapshot().Profile!)),
            new MemoryPendingStore());
        var viewModel = new StashScanWorkspaceViewModel(
            store,
            workflow,
            reviewCommands,
            new FakeItemFactCatalog([], []),
            new FakeRuntimeStateStore(RuntimeSnapshot()),
            guidedScan: guided);
        await viewModel.LoadAsync();
        Assert.True(viewModel.IsScanIdle);
        Assert.True(viewModel.ShowsNothingScanned);

        await ((AsyncDelegateCommand)viewModel.StartSelectedScanCommand).ExecuteAsync();

        Assert.True(viewModel.IsScanInProgress);
        Assert.False(viewModel.CanFinishScan);
        Assert.Contains("top of your stash", guided.Current.NextStep, StringComparison.Ordinal);

        // One screen: a named item, and a rectangle nothing could name.
        var image = StashScanFixtures.SyntheticStashPainter.RenderFrame(
            StashScanFixtures.SyntheticStashLayout.Of(
                14,
                new StashScanFixtures.SyntheticStashPlacement(StashScanFixtures.SyntheticStashLayout.Catalog[10], 2, 3)),
            firstRow: 0);
        var request = await new TarkovCompanion.Infrastructure.Recognition.Grid.GridPixelReconstructionBuilder(
                new StashScanMeasurement.FixedIconEvidenceCache([]),
                new StashScanMeasurement.FixedItemRepository(new Dictionary<string, ItemDefinition>()),
                new StashScanMeasurement.UnavailableOcrEngine())
            .BuildAsync(image, TarkovCompanion.Core.Domain.Recognition.Grid.InventoryGridSurface.Stash, StashScanMeasurement.ObservedUtc, cancellationToken: CancellationToken.None);
        var outcome = await guided.AddScreenshotAsync(
            "artifact-vm",
            TarkovCompanion.Application.Services.CaptureSessions.CaptureCorrelationId.New(),
            new(null, null, null, null, null, null, "desktop"),
            new string('b', 64),
            StashScanMeasurement.ObservedUtc,
            0,
            new TarkovCompanion.Infrastructure.Recognition.Grid.InventoryGridReconstructor().Reconstruct(request, CancellationToken.None),
            CancellationToken.None);
        Assert.Equal(GuidedStashFrameOutcome.Added, outcome);
        await viewModel.LoadAsync();

        Assert.True(viewModel.CanFinishScan);
        Assert.False(viewModel.ShowsNothingScanned);
        var tile = Assert.Single(Assert.Single(viewModel.Regions).Tiles);
        Assert.True(tile.IsUnresolved);
        Assert.Equal("?", tile.Name);
        Assert.Equal("Unknown item", tile.AutomationName);
        Assert.Equal((2, 3, 2, 2), (tile.Row, tile.Column, tile.WidthCells, tile.HeightCells));
        Assert.StartsWith("Scanning", viewModel.ReconstructionLabel, StringComparison.Ordinal);
        Assert.Contains("1 unknown", viewModel.ReconstructionLabel, StringComparison.Ordinal);

        await ((AsyncDelegateCommand)viewModel.FinishScanCommand).ExecuteAsync();

        Assert.True(viewModel.IsScanIdle);
        Assert.True(viewModel.HasSnapshots);
        Assert.True(viewModel.HasSelection);
        Assert.StartsWith("Scan saved.", viewModel.Status, StringComparison.Ordinal);
        Assert.True(Assert.Single(Assert.Single(viewModel.Regions).Tiles).IsUnresolved);
    }

    /// <summary>A snapshot saved outside the page (a capture) shows without leaving and coming back.</summary>
    [Fact]
    public async Task A_snapshot_saved_while_the_page_is_open_appears_without_navigating()
    {
        // On the UI thread, as in the app: the page reloads from the dispatcher, not from the saver.
        using var session = Avalonia.Headless.HeadlessUnitTestSession.StartNew(
            typeof(TarkovCompanion.UnitTests.V2MapRenderer.MapMarkClipTests.MarkClipApp));
        Assert.True(await session.Dispatch(SavedWhileOpenAsync, CancellationToken.None));
    }

    private static async Task<bool> SavedWhileOpenAsync()
    {
        var store = new FakeSnapshotStore();
        var reviewCommands = new InMemoryStashReviewCommandSink();
        var workflow = Workflow(store, reviewCommands);
        var guided = new GuidedStashScanService(
            new StashScanAssembler(),
            new StashLayoutAligner(),
            new StashReconstructionProjector(),
            workflow,
            new StashOwnedCountsApplier(new StubProfileService(RuntimeSnapshot().Profile!)),
            new MemoryPendingStore());
        var viewModel = new StashScanWorkspaceViewModel(
            store,
            workflow,
            reviewCommands,
            new FakeItemFactCatalog([], []),
            new FakeRuntimeStateStore(RuntimeSnapshot()),
            guidedScan: guided);
        await viewModel.LoadAsync();
        await ((AsyncDelegateCommand)viewModel.StartSelectedScanCommand).ExecuteAsync();
        var image = StashScanFixtures.SyntheticStashPainter.RenderFrame(
            StashScanFixtures.SyntheticStashLayout.Of(
                14,
                new StashScanFixtures.SyntheticStashPlacement(StashScanFixtures.SyntheticStashLayout.Catalog[10], 2, 3)),
            firstRow: 0);
        var request = await new TarkovCompanion.Infrastructure.Recognition.Grid.GridPixelReconstructionBuilder(
                new StashScanMeasurement.FixedIconEvidenceCache([]),
                new StashScanMeasurement.FixedItemRepository(new Dictionary<string, ItemDefinition>()),
                new StashScanMeasurement.UnavailableOcrEngine())
            .BuildAsync(image, TarkovCompanion.Core.Domain.Recognition.Grid.InventoryGridSurface.Stash, StashScanMeasurement.ObservedUtc, cancellationToken: CancellationToken.None);
        await guided.AddScreenshotAsync(
            "artifact-open-page",
            TarkovCompanion.Application.Services.CaptureSessions.CaptureCorrelationId.New(),
            new(null, null, null, null, null, null, "desktop"),
            new string('c', 64),
            StashScanMeasurement.ObservedUtc,
            0,
            new TarkovCompanion.Infrastructure.Recognition.Grid.InventoryGridReconstructor().Reconstruct(request, CancellationToken.None),
            CancellationToken.None);
        Assert.False(viewModel.HasSnapshots);

        // Saved by the capture side, not by this page's Finish button; nothing calls LoadAsync.
        Assert.NotNull(await guided.FinishAsync(CancellationToken.None));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.HasSnapshots);
        Assert.True(viewModel.HasSelection);
        Assert.Single(viewModel.Snapshots);
        return true;
    }

    [Fact]
    public async Task Selecting_a_snapshot_splits_ammo_keys_and_general_items()
    {
        var store = new FakeSnapshotStore();
        store.Seed(Record());
        var provenance = new DataProvenance("fixture", DateTimeOffset.UnixEpoch);
        var catalog = new FakeItemFactCatalog(
            [new AmmoStats("ammo-9x19", "9x19mm", 10, 20, null, null, 1, null, null, null, false, false, provenance)],
            [new KeyFacts("key-101", "customs", 20, [], ["quest-1", "quest-2"], null, 0, 0, false, 0, provenance)]);
        var reviewCommands = new InMemoryStashReviewCommandSink();
        var viewModel = new StashScanWorkspaceViewModel(
            store,
            Workflow(store, reviewCommands),
            reviewCommands,
            catalog,
            new FakeRuntimeStateStore(RuntimeSnapshot()));

        await viewModel.LoadAsync();

        Assert.True(viewModel.HasSelection);
        var ammo = Assert.Single(viewModel.AmmoSummary);
        Assert.Equal("9x19mm", ammo.Caliber);
        Assert.Equal(2, ammo.RoundCount);

        var key = Assert.Single(viewModel.KeySummary);
        Assert.Equal("customs", key.MapLabel);
        Assert.Equal("2 quest(s)", key.QuestUseLabel);

        var general = Assert.Single(viewModel.Items);
        Assert.Equal(StashPlanGroup.Review, general.Group);
        Assert.Equal("Gas analyzer", general.DisplayName);
    }

    [Fact]
    public async Task A_loaded_snapshot_is_sorted_and_the_plan_tiles_count_it()
    {
        // #283: the planner had no caller, so this row was Review and three tiles read a dash.
        // The profile context's scope (Pvp) is the one the capture handoff saves under.
        var store = new FakeSnapshotStore();
        store.Seed(Record(scope: new(ProfileId, "wipe-fixture", "Pvp")));
        var provenance = new DataProvenance("fixture", DateTimeOffset.UnixEpoch);
        var catalog = new FakeItemFactCatalog(
            [new AmmoStats("ammo-9x19", "9x19mm", 10, 20, null, null, 1, null, null, null, false, false, provenance)],
            [new KeyFacts("key-101", "customs", 20, [], ["quest-1", "quest-2"], null, 0, 0, false, 0, provenance)]);
        var reviewCommands = new InMemoryStashReviewCommandSink();
        var now = V2Capture.LootScanFactFixtures.Now;
        var profiles = new ProfileContextService(new Profiles.MemoryProfileStore(), new Profiles.ProfileClock(now));
        var profile = Profiles.ProfileV2Fixtures.Profile(
            Profiles.ProfileV2Fixtures.Context(ProfileId, "wipe-fixture", Core.Domain.Profiles.ProfileGameMode.Pvp),
            "unrelated");
        await profiles.CreateAsync(Profiles.ProfileV2Fixtures.Request(profile), CancellationToken.None);
        using var runtime = new ProfileRuntimeContextService(profiles);
        await runtime.InitializeAsync(CancellationToken.None);
        var facts = new V2Capture.LootScanFactFixtures.Catalog();
        var viewModel = new StashScanWorkspaceViewModel(
            store,
            Workflow(store, reviewCommands),
            reviewCommands,
            catalog,
            new FakeRuntimeStateStore(RuntimeSnapshot()),
            clock: new Runtime.ManualTimeProvider(now),
            profileContext: runtime,
            planSource: new StashPlanSource(new LootScanRecommendationSource(
                facts,
                facts,
                new LootScanNeedSource(
                    new V2Capture.LootScanFactFixtures.Profiles(),
                    new ProfileNeedAggregationService([], []),
                    new V2Capture.LootScanFactFixtures.Quests([]),
                    new V2Capture.LootScanFactFixtures.Requirements()))));

        await viewModel.LoadAsync();

        var general = Assert.Single(viewModel.Items);
        Assert.Equal("Gas analyzer", general.DisplayName);
        Assert.Equal(StashPlanGroup.Sell, general.Group);
        Assert.StartsWith("On the flea, about ₽", general.WhyLabel, StringComparison.Ordinal);
        Assert.EndsWith("after the fee.", general.WhyLabel, StringComparison.Ordinal);
        Assert.All(viewModel.PlanTiles, tile => Assert.True(tile.IsWired));
        Assert.Equal("1", viewModel.PlanTiles.Single(tile => tile.IsSell).CountLabel);
        Assert.Equal("0", viewModel.PlanTiles.Single(tile => tile.IsReview).CountLabel);
        Assert.StartsWith("Sorted by", viewModel.RecommendationNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Correcting_the_selected_item_records_a_pending_correction()
    {
        var store = new FakeSnapshotStore();
        store.Seed(Record());
        var reviewCommands = new InMemoryStashReviewCommandSink();
        var viewModel = new StashScanWorkspaceViewModel(
            store,
            Workflow(store, reviewCommands),
            reviewCommands,
            new FakeItemFactCatalog([], []),
            new FakeRuntimeStateStore(RuntimeSnapshot()));
        await viewModel.LoadAsync();
        viewModel.SelectedItem = Assert.Single(viewModel.Items, item => item.DisplayName == "Gas analyzer");
        viewModel.QuantityCorrection = "3";

        await ((AsyncDelegateCommand)viewModel.CorrectQuantityCommand).ExecuteAsync();

        var pending = Assert.Single(viewModel.PendingCorrections);
        Assert.Equal("CorrectQuantity", pending.Action);
    }

    [Fact]
    public async Task PinIgnoreRescanAndUndoAreReachableFromASelectedItem()
    {
        var store = new FakeSnapshotStore();
        store.Seed(Record());
        var reviewCommands = new InMemoryStashReviewCommandSink();
        var viewModel = new StashScanWorkspaceViewModel(
            store,
            Workflow(store, reviewCommands),
            reviewCommands,
            new FakeItemFactCatalog([], []),
            new FakeRuntimeStateStore(RuntimeSnapshot()));
        await viewModel.LoadAsync();
        viewModel.SelectedItem = Assert.Single(viewModel.Items, item => item.DisplayName == "Gas analyzer");

        await ((AsyncDelegateCommand)viewModel.PinSelectedCommand).ExecuteAsync();
        Assert.Equal("Pin", viewModel.PendingCorrections[^1].Action);
        Assert.True(viewModel.CanUndoReview);

        await ((AsyncDelegateCommand)viewModel.IgnoreSelectedCommand).ExecuteAsync();
        Assert.Equal("Ignore", viewModel.PendingCorrections[^1].Action);
        Assert.True(Assert.Single(viewModel.Items, item => item.DisplayName == "Gas analyzer").IsIgnored);
        Assert.Equal("2", Assert.Single(viewModel.PlanTiles, tile => tile.IsReview).CountLabel);

        await ((AsyncDelegateCommand)viewModel.UndoReviewCommand).ExecuteAsync();
        Assert.False(Assert.Single(viewModel.Items, item => item.DisplayName == "Gas analyzer").IsIgnored);

        viewModel.SelectedItem = Assert.Single(viewModel.Items, item => item.DisplayName == "Gas analyzer");
        ScanIntent? requested = null;
        viewModel.ScanRequested += (_, intent) => requested = intent;
        await ((AsyncDelegateCommand)viewModel.RescanSelectedRegionCommand).ExecuteAsync();

        Assert.Equal(ScanIntent.Stash, requested);
        Assert.Equal("Rescan", viewModel.PendingCorrections[^1].Action);
        await ((AsyncDelegateCommand)viewModel.UndoReviewCommand).ExecuteAsync();
        var projected = StashReviewCommandProjection.Project(
            await reviewCommands.ListAsync("stash-snapshot-1", CancellationToken.None));
        Assert.Empty(projected.RescanContainerPaths);
    }

    [Fact]
    public async Task MergePreviousCombinesComplementarySnapshotsAndUndoRestoresTheSelectedOne()
    {
        var previousId = Guid.Parse("30000000-0000-0000-0000-000000000003");
        var store = new FakeSnapshotStore();
        store.Seed(
            Record(),
            Record(previousId, "stash-snapshot-previous", rowOffset: 5, isCurrent: false));
        var reviewCommands = new InMemoryStashReviewCommandSink();
        var viewModel = new StashScanWorkspaceViewModel(
            store,
            Workflow(store, reviewCommands),
            reviewCommands,
            new FakeItemFactCatalog([], []),
            new FakeRuntimeStateStore(RuntimeSnapshot()));
        await viewModel.LoadAsync();

        Assert.True(viewModel.CanMergePreviousSnapshot);
        Assert.Equal(3, Assert.Single(viewModel.Regions).Tiles.Count);
        await ((AsyncDelegateCommand)viewModel.MergePreviousSnapshotCommand).ExecuteAsync();

        Assert.Equal(6, Assert.Single(viewModel.Regions).Tiles.Count);
        Assert.Equal("MergeEntries", viewModel.PendingCorrections[^1].Action);
        await ((AsyncDelegateCommand)viewModel.UndoReviewCommand).ExecuteAsync();
        Assert.Equal(3, Assert.Single(viewModel.Regions).Tiles.Count);
    }

    [Fact]
    public async Task Export_buttons_write_the_latest_stash_as_csv_and_json_with_local_times()
    {
        var root = Path.Combine(Path.GetTempPath(), $"stash-export-{Guid.NewGuid():N}");
        var store = new FakeSnapshotStore();
        var record = Record();
        store.Seed(record);
        var reviewCommands = new InMemoryStashReviewCommandSink();
        using var zone = LocalTime.UseZone(TimeZoneInfo.CreateCustomTimeZone(
            "fixture-minus-four",
            TimeSpan.FromHours(-4),
            "fixture-minus-four",
            "fixture-minus-four"));
        try
        {
            var viewModel = new StashScanWorkspaceViewModel(
                store,
                Workflow(store, reviewCommands),
                reviewCommands,
                new FakeItemFactCatalog([], []),
                new FakeRuntimeStateStore(RuntimeSnapshot()),
                clock: new Runtime.ManualTimeProvider(record.RecordedUtc.AddHours(1)),
                paths: AppDataPaths.Resolve(root));
            await viewModel.LoadAsync();

            await ((AsyncDelegateCommand)viewModel.ExportCsvCommand).ExecuteAsync();
            await ((AsyncDelegateCommand)viewModel.ExportJsonCommand).ExecuteAsync();

            var files = Directory.GetFiles(Path.Combine(root, "Exports")).Order().ToArray();
            Assert.Equal(2, files.Length);
            var csv = await File.ReadAllTextAsync(Assert.Single(files, path => path.EndsWith(".csv", StringComparison.Ordinal)));
            var json = await File.ReadAllTextAsync(Assert.Single(files, path => path.EndsWith(".json", StringComparison.Ordinal)));
            Assert.Contains(LocalTime.Iso(record.RecordedUtc), csv, StringComparison.Ordinal);
            Assert.Contains(LocalTime.Iso(record.RecordedUtc), json, StringComparison.Ordinal);
            Assert.Contains("Gas analyzer", csv, StringComparison.Ordinal);
            Assert.Contains("Gas analyzer", json, StringComparison.Ordinal);
            Assert.Contains("UTC-04:00", json, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task An_item_with_a_catalog_wiki_link_can_open_it_and_one_without_cannot()
    {
        var store = new FakeSnapshotStore();
        store.Seed(Record());
        var reviewCommands = new InMemoryStashReviewCommandSink();
        var wikiOpener = new FakeWikiLinkOpener();
        var viewModel = new StashScanWorkspaceViewModel(
            store,
            Workflow(store, reviewCommands),
            reviewCommands,
            new FakeItemFactCatalog([], []),
            new FakeRuntimeStateStore(RuntimeSnapshot()),
            itemRepository: new FakeItemRepository(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["item-gas-analyzer"] = "https://escapefromtarkov.fandom.com/wiki/Gas_analyzer",
                }),
            wikiOpener: wikiOpener);

        await viewModel.LoadAsync();

        var row = Assert.Single(viewModel.Items, item => item.DisplayName == "Gas analyzer");
        Assert.True(row.HasWikiLink);
        row.OpenWikiCommand!.Execute(null);
        Assert.Equal("https://escapefromtarkov.fandom.com/wiki/Gas_analyzer", wikiOpener.LastOpened);
    }

    [Fact]
    public async Task A_loaded_snapshot_is_drawn_as_its_containers_with_every_read_footprint()
    {
        var store = new FakeSnapshotStore();
        store.Seed(Record());
        var reviewCommands = new InMemoryStashReviewCommandSink();
        var viewModel = new StashScanWorkspaceViewModel(
            store,
            Workflow(store, reviewCommands),
            reviewCommands,
            new FakeItemFactCatalog(
                [new AmmoStats("ammo-9x19", "9x19mm", 10, 20, null, null, 1, null, null, null, false, false, Fixture)],
                [new KeyFacts("key-101", "customs", 20, [], ["quest-1"], null, 0, 0, false, 0, Fixture)]),
            new FakeRuntimeStateStore(RuntimeSnapshot()));

        await viewModel.LoadAsync();

        var region = Assert.Single(viewModel.Regions);
        Assert.Equal("Stash", region.Title);
        // Ammo and keys are folded into their own summaries, but still occupy squares on the grid.
        Assert.Equal(3, region.Tiles.Count);
        Assert.Contains(region.Tiles, tile => tile.IsAmmo);
        Assert.Contains(region.Tiles, tile => tile.IsKey);
        Assert.Equal("1 items", viewModel.ItemCountLabel);

        // Nothing is sorted into Keep/Sell/Use soon yet, and the tiles say so rather than "0".
        Assert.Equal("—", Assert.Single(viewModel.PlanTiles, tile => tile.IsKeep).CountLabel);
        Assert.Equal("1", Assert.Single(viewModel.PlanTiles, tile => tile.IsReview).CountLabel);
    }

    [Fact]
    public async Task Selecting_an_item_marks_its_square_and_the_capture_chips_choose_what_is_scanned()
    {
        var store = new FakeSnapshotStore();
        store.Seed(Record());
        var reviewCommands = new InMemoryStashReviewCommandSink();
        var viewModel = new StashScanWorkspaceViewModel(
            store,
            Workflow(store, reviewCommands),
            reviewCommands,
            new FakeItemFactCatalog([], []),
            new FakeRuntimeStateStore(RuntimeSnapshot()));
        await viewModel.LoadAsync();

        var tile = Assert.Single(Assert.Single(viewModel.Regions).Tiles, item => item.Name == "Gas analyzer");
        tile.SelectCommand.Execute(null);
        Assert.Equal("Gas analyzer", viewModel.SelectedItemDisplayName);
        Assert.True(tile.IsSelected);

        ScanIntent? requested = null;
        viewModel.ScanRequested += (_, intent) => requested = intent;
        // Ammo and Keys armed intents the stash handoff ignores, so only the full stash is offered.
        Assert.Single(viewModel.ScanTargets).SelectCommand.Execute(null);
        viewModel.StartSelectedScanCommand.Execute(null);

        Assert.Equal(ScanIntent.Stash, viewModel.ScanTarget);
        Assert.Equal(ScanIntent.Stash, requested);

        Assert.True(viewModel.IsGridView);
        viewModel.ShowListCommand.Execute(null);
        Assert.True(viewModel.IsListView);
    }

    private static StashScanWorkflow Workflow(IStashSnapshotStore store, InMemoryStashReviewCommandSink reviewCommands) =>
        new(new StashScanAssembler(), store, new StashSnapshotComparer(), reviewCommands);

    private static ApplicationRuntimeSnapshot RuntimeSnapshot() => V2ShellTestData.Snapshot() with
    {
        Profile = new PlayerProfile(
            ProfileId,
            "Fixture",
            TarkovCompanion.Core.Common.GameMode.Regular,
            24,
            Faction.Usec,
            null,
            new Dictionary<string, int>(),
            new HashSet<string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            new HashSet<string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, EventItemState>(),
            new Dictionary<string, string>(),
            DateTimeOffset.UnixEpoch,
            "wipe-fixture"),
    };

    private static InventoryProfileScope Scope() => new(ProfileId, "wipe-fixture", "Regular");

    private static StashSnapshotRecord Record(
        Guid? snapshotId = null,
        string recognitionSnapshotId = "stash-snapshot-1",
        int rowOffset = 0,
        bool isCurrent = true,
        InventoryProfileScope? scope = null)
    {
        var provenance = V2ContractTestData.ScreenshotProvenance();
        var region = new StashCaptureRegion(
            "region-1",
            "artifact-1",
            0,
            "stash",
            V2ContractTestData.Complete<GridCellAddress?>("origin", new GridCellAddress(rowOffset, 0)),
            V2ContractTestData.Grid(
                V2ContractTestData.Cell(0, 0, Item("ammo-9x19", "9x19mm PST gzh", 2)),
                V2ContractTestData.Cell(0, 1, Item("key-101", "ULTRA medical storage key", 1)),
                V2ContractTestData.Cell(0, 2, Item("item-gas-analyzer", "Gas analyzer", 1))));
        var stash = new StashRecognition(
            recognitionSnapshotId,
            [region],
            [new StashContainerCoverage(
                "stash",
                V2ContractTestData.Complete<int?>("coverage.observed", 8, provenance),
                V2ContractTestData.Complete<int?>("coverage.total", 10, provenance))],
            V2ContractTestData.Unknown<long?>("stash.value", provenance),
            V2ContractTestData.Complete<int?>("stash.unresolved", 0, provenance));
        var recognition = new RecognitionResultEnvelope<StashRecognition>(
            V2ContractTestData.Header(RecognizedContext.Stash),
            V2ContractTestData.Complete("stash", stash, provenance));
        return new StashSnapshotRecord(
            snapshotId ?? SnapshotId,
            scope ?? Scope(),
            "data-snapshot-1",
            V2ContractTestData.ObservedUtc.AddMinutes(1),
            isCurrent,
            recognition);
    }

    private static RecognizedItem Item(string id, string name, int quantity) => new(
        V2ContractTestData.Complete("item.id", id),
        V2ContractTestData.Complete("item.name", name),
        V2ContractTestData.Complete<int?>("item.quantity", quantity),
        V2ContractTestData.Complete<int?>("item.width", 1),
        V2ContractTestData.Complete<int?>("item.height", 1),
        V2ContractTestData.Complete<bool?>("item.rotated", false),
        V2ContractTestData.Complete<bool?>("item.foundInRaid", true),
        V2ContractTestData.Complete("item.condition", ItemConditionReading.NotApplicable));

    private static StashReconstructedTile ReconstructedTile(int width, int height) => new(
        "stash",
        0,
        0,
        width,
        height,
        "item-gas-analyzer",
        "Gas analyzer",
        1,
        [],
        V2ContractTestData.ScreenshotProvenance());

    private sealed class MemoryPendingStore : IGuidedStashScanPendingStore
    {
        private GuidedStashScanPending? _pending;

        public Task<GuidedStashScanPending?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_pending);

        public Task SaveAsync(GuidedStashScanPending pending, CancellationToken cancellationToken)
        {
            _pending = pending;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            _pending = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSnapshotStore : IStashSnapshotStore
    {
        private readonly Dictionary<Guid, StashSnapshotRecord> _records = [];

        public void Seed(params StashSnapshotRecord[] records)
        {
            foreach (var record in records)
            {
                _records[record.SnapshotId] = record;
            }
        }

        public Task SaveAsync(StashSnapshotRecord snapshot, CancellationToken cancellationToken)
        {
            _records[snapshot.SnapshotId] = snapshot;
            return Task.CompletedTask;
        }

        public Task<StashSnapshotRecord?> ReadCurrentAsync(InventoryProfileScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(_records.Values.FirstOrDefault(record => record.IsCurrent && record.ProfileScope == scope));

        public Task<StashSnapshotRecord?> ReadAsync(InventoryProfileScope scope, Guid snapshotId, CancellationToken cancellationToken) =>
            Task.FromResult(_records.GetValueOrDefault(snapshotId) is { } record && record.ProfileScope == scope ? record : null);

        public Task<IReadOnlyList<StashSnapshotSummary>> ListAsync(InventoryProfileScope scope, int maximumCount, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StashSnapshotSummary>>(_records.Values
                .Where(record => record.ProfileScope == scope)
                .OrderByDescending(record => record.RecordedUtc)
                .Take(maximumCount)
                .Select(record => new StashSnapshotSummary(
                    record.SnapshotId,
                    record.Recognition.Result.Value!.SnapshotId,
                    record.DataSnapshotId,
                    record.RecordedUtc,
                    record.IsCurrent,
                    record.Recognition.Result.Status,
                    record.Recognition.Result.Provenance.Coverage ?? new EvidenceCoverage(description: "fixture")))
                .ToArray());

        public Task<StashSnapshotDeleteResult> DeleteAsync(InventoryProfileScope scope, Guid snapshotId, CancellationToken cancellationToken)
        {
            if (!_records.Remove(snapshotId))
            {
                return Task.FromResult(new StashSnapshotDeleteResult(false, null));
            }

            return Task.FromResult(new StashSnapshotDeleteResult(true, null));
        }

        public Task<StashSnapshotRetentionResult> ApplyRetentionAsync(
            InventoryProfileScope scope,
            DateTimeOffset retainFromUtc,
            bool dryRun,
            CancellationToken cancellationToken) =>
            Task.FromResult(new StashSnapshotRetentionResult(0, 0, dryRun));
    }

    private sealed class FakeItemFactCatalog(
        IReadOnlyList<AmmoStats> ammo,
        IReadOnlyList<KeyFacts> keys) : IItemFactCatalog
    {
        public Task<IReadOnlyList<AmmoStats>> GetAmmoAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ammo);

        public Task<IReadOnlyList<AmmoPackContents>> GetAmmoPacksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AmmoPackContents>>([]);

        public Task<IReadOnlyList<LoadoutItemFacts>> GetLoadoutFactsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LoadoutItemFacts>>([]);

        public Task<IReadOnlyList<KeyFacts>> GetKeyFactsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(keys);

        public void Invalidate()
        {
        }
    }

    private sealed class FakeItemRepository(IReadOnlyDictionary<string, string?> wikiUriByItemId) : IItemRepository
    {
        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemDefinition?>(wikiUriByItemId.TryGetValue(itemId, out var wikiUri)
                ? new ItemDefinition(
                    itemId,
                    itemId,
                    itemId,
                    string.Empty,
                    ItemCategory.Unknown,
                    new ItemDimensions(1, 1),
                    FleaEligible: true,
                    IconUri: null,
                    ImageUri: null,
                    WikiUri: wikiUri,
                    PropertiesType: null,
                    PropertiesJson: null,
                    CategoryIds: new HashSet<string>(),
                    Provenance: new DataProvenance("fixture", DateTimeOffset.UnixEpoch))
                : null);

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(null);
    }

    private sealed class FakeWikiLinkOpener : IWikiLinkOpener
    {
        public string? LastOpened { get; private set; }

        public bool TryOpen(string? wikiUrl)
        {
            LastOpened = wikiUrl;
            return true;
        }
    }

    private sealed class FakeRuntimeStateStore(ApplicationRuntimeSnapshot current) : IRuntimeStateStore
    {
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067

        public ApplicationRuntimeSnapshot Current { get; private set; } = current;

        public void Update(Func<ApplicationRuntimeSnapshot, ApplicationRuntimeSnapshot> update) =>
            Current = update(Current);
    }
}
