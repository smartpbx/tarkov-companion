using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.StashScan;
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
        Assert.Single(viewModel.ScanTargets, target => target.Intent == ScanIntent.Keys).SelectCommand.Execute(null);
        viewModel.StartSelectedScanCommand.Execute(null);

        Assert.Equal(ScanIntent.Keys, viewModel.ScanTarget);
        Assert.Equal(ScanIntent.Keys, requested);

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

    private static StashSnapshotRecord Record()
    {
        var provenance = V2ContractTestData.ScreenshotProvenance();
        var region = new StashCaptureRegion(
            "region-1",
            "artifact-1",
            0,
            "stash",
            V2ContractTestData.Complete<GridCellAddress?>("origin", new GridCellAddress(0, 0)),
            V2ContractTestData.Grid(
                V2ContractTestData.Cell(0, 0, Item("ammo-9x19", "9x19mm PST gzh", 2)),
                V2ContractTestData.Cell(0, 1, Item("key-101", "ULTRA medical storage key", 1)),
                V2ContractTestData.Cell(0, 2, Item("item-gas-analyzer", "Gas analyzer", 1))));
        var stash = new StashRecognition(
            "stash-snapshot-1",
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
            SnapshotId,
            Scope(),
            "data-snapshot-1",
            V2ContractTestData.ObservedUtc.AddMinutes(1),
            isCurrent: true,
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
        private StashSnapshotRecord? _record;

        public void Seed(StashSnapshotRecord record) => _record = record;

        public Task SaveAsync(StashSnapshotRecord snapshot, CancellationToken cancellationToken)
        {
            _record = snapshot;
            return Task.CompletedTask;
        }

        public Task<StashSnapshotRecord?> ReadCurrentAsync(InventoryProfileScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(_record is { IsCurrent: true } record && record.ProfileScope == scope ? record : null);

        public Task<StashSnapshotRecord?> ReadAsync(InventoryProfileScope scope, Guid snapshotId, CancellationToken cancellationToken) =>
            Task.FromResult(_record is { } record && record.SnapshotId == snapshotId && record.ProfileScope == scope ? record : null);

        public Task<IReadOnlyList<StashSnapshotSummary>> ListAsync(InventoryProfileScope scope, int maximumCount, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StashSnapshotSummary>>(_record is { } record && record.ProfileScope == scope
                ? [new StashSnapshotSummary(
                    record.SnapshotId,
                    record.Recognition.Result.Value!.SnapshotId,
                    record.DataSnapshotId,
                    record.RecordedUtc,
                    record.IsCurrent,
                    record.Recognition.Result.Status,
                    record.Recognition.Result.Provenance.Coverage ?? new EvidenceCoverage(description: "fixture"))]
                : []);

        public Task<StashSnapshotDeleteResult> DeleteAsync(InventoryProfileScope scope, Guid snapshotId, CancellationToken cancellationToken)
        {
            if (_record?.SnapshotId != snapshotId)
            {
                return Task.FromResult(new StashSnapshotDeleteResult(false, null));
            }

            _record = null;
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
