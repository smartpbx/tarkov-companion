using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.StashScan;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Stash;
using TarkovCompanion.UnitTests.V2Contracts;
using TarkovCompanion.UnitTests.V2Shell;

namespace TarkovCompanion.UnitTests.StashScan;

public sealed class StashScanWorkspaceViewModelTests
{
    private static readonly Guid ProfileId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid SnapshotId = Guid.Parse("30000000-0000-0000-0000-000000000002");

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
