using System.Security.Cryptography;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Core.Domain.Stash;
using TarkovCompanion.Infrastructure.Persistence.Stash;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.StashScanFixtures;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.StashScan;

/// <summary>
/// The guided scan driven from painted pixels to the numbers other workspaces show: what a scan
/// says the player owns has to change what the hideout says they still need.
/// </summary>
public sealed class GuidedStashScanEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private const string Bolts = "syn-bolts";

    private static readonly InventoryProfileScope Scope = new(
        Guid.Parse("66666666-6666-6666-6666-666666666666"),
        "generation-a",
        "Regular");

    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "guided-stash-scan-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AScanClosedHalfwayAndFinishedLaterChangesWhatTheHideoutStillNeeds()
    {
        var layout = SyntheticStashLayout.Build(rows: 34);
        var boltsInStash = layout.Placements.Count(placement => placement.Item.ItemId == Bolts);
        Assert.True(boltsInStash >= 3, "the painted stash needs a few bolts for the requirement to move");

        var profiles = new MemoryProfileService(Profile(owned: new Dictionary<string, int>(StringComparer.Ordinal)));
        var requirements = new FixedRequirementCatalog(
            [new("lavatory", "Lavatory", [1, 2, 3])],
            [new("lavatory", 2, Bolts, boltsInStash)]);
        var needs = new ProfileNeedAggregationService([], requirements.Requirements);
        var hideout = new HideoutWorkspaceViewModel(requirements, profiles, new NamedItemRepository());
        var snapshots = new MemorySnapshotStore();
        var pendingStore = new JsonFileGuidedStashScanStore(Path.Combine(_directory, "stash-scan-in-progress.json"));

        var before = await HideoutBoltsAsync(hideout);
        var neededBefore = needs.GetItemNeed(await profiles.GetActiveAsync(CancellationToken.None), Bolts).Summary.HideoutCount;

        // First sitting: two of the three screens, then the application is closed.
        var firstSitting = Service(snapshots, profiles, pendingStore);
        await firstSitting.StartAsync(Scope, "data-1", CancellationToken.None);
        Assert.Equal(GuidedStashFrameOutcome.Added, await AddAsync(firstSitting, layout, firstRow: 0));
        Assert.Equal(GuidedStashFrameOutcome.Added, await AddAsync(firstSitting, layout, firstRow: 10));
        Assert.Equal(24, firstSitting.Current.RowsCovered);
        Assert.Null(snapshots.Saved);

        // Second sitting: a new process finds the scan where it was left, and finishes it.
        var secondSitting = Service(snapshots, profiles, pendingStore);
        await secondSitting.InitializeAsync(CancellationToken.None);
        Assert.True(secondSitting.Current.IsCollecting);
        Assert.True(secondSitting.Current.WasResumed);
        Assert.Equal(2, secondSitting.Current.Screenshots);
        Assert.Equal(2, secondSitting.Current.PlacedScreenshots);
        Assert.Equal(24, secondSitting.Current.RowsCovered);

        Assert.Equal(GuidedStashFrameOutcome.Added, await AddAsync(secondSitting, layout, firstRow: 20));
        Assert.Equal(34, secondSitting.Current.RowsCovered);
        var finished = await secondSitting.FinishAsync(CancellationToken.None);

        Assert.NotNull(finished);
        Assert.NotNull(snapshots.Saved);
        Assert.True(snapshots.Saved!.IsCurrent);
        Assert.False(secondSitting.Current.IsCollecting);
        Assert.Null(await pendingStore.LoadAsync(CancellationToken.None));
        Assert.Equal(boltsInStash, finished!.Reconstruction.OwnedCounts[Bolts]);

        var after = await HideoutBoltsAsync(hideout);
        var neededAfter = needs.GetItemNeed(await profiles.GetActiveAsync(CancellationToken.None), Bolts).Summary.HideoutCount;
        var line = $"[stash-e2e] downstream: Bolts owned 0 -> {boltsInStash} because a scan said so · Lavatory level 2 row \"{before.ProgressLabel}\" -> \"{after.ProgressLabel}\" · hideout still needs {neededBefore} -> {neededAfter}";
        output.WriteLine(line);
        Console.WriteLine(line);

        Assert.Equal($"0 / {boltsInStash}", before.ProgressLabel);
        Assert.False(before.IsSatisfied);
        Assert.Equal($"{boltsInStash} / {boltsInStash}", after.ProgressLabel);
        Assert.True(after.IsSatisfied);
        Assert.Equal(boltsInStash, neededBefore);
        Assert.Equal(0, neededAfter);
    }

    [Fact]
    public async Task AMisStepIsKeptExplainedAndRecoverable()
    {
        var layout = SyntheticStashLayout.Build(rows: 34);
        var snapshots = new MemorySnapshotStore();
        var profiles = new MemoryProfileService(Profile(owned: new Dictionary<string, int>(StringComparer.Ordinal)));
        var pendingStore = new JsonFileGuidedStashScanStore(Path.Combine(_directory, "mis-step.json"));
        var scan = Service(snapshots, profiles, pendingStore);

        Assert.Equal(GuidedStashFrameOutcome.NotCollecting, await AddAsync(scan, layout, firstRow: 0));

        var started = await scan.StartAsync(Scope, "data-1", CancellationToken.None);
        Assert.Contains("top of your stash", started.NextStep, StringComparison.Ordinal);
        Assert.Equal(GuidedStashFrameOutcome.Added, await AddAsync(scan, layout, firstRow: 0));

        // The same screenshot again.
        Assert.Equal(GuidedStashFrameOutcome.Duplicate, await AddAsync(scan, layout, firstRow: 0));
        Assert.Equal(1, scan.Current.Screenshots);

        // Scrolled a whole screen too far: nothing shared with the first.
        Assert.Equal(GuidedStashFrameOutcome.AddedUnplaced, await AddAsync(scan, layout, firstRow: 20));
        Assert.Equal(2, scan.Current.Screenshots);
        Assert.Equal(1, scan.Current.PlacedScreenshots);
        Assert.Contains("Scroll back up", scan.Current.NextStep, StringComparison.Ordinal);

        // Starting again while one is open must not replace it.
        await scan.StartAsync(Scope, "data-1", CancellationToken.None);
        Assert.Equal(2, scan.Current.Screenshots);

        // The bridging screenshot places itself and the one that was waiting.
        Assert.Equal(GuidedStashFrameOutcome.Added, await AddAsync(scan, layout, firstRow: 10));
        Assert.Equal(3, scan.Current.PlacedScreenshots);
        Assert.Equal(34, scan.Current.RowsCovered);

        // And the last one can be taken back.
        await scan.UndoLastAsync(CancellationToken.None);
        Assert.Equal(2, scan.Current.Screenshots);
        Assert.Equal(1, scan.Current.PlacedScreenshots);

        // Nothing but Discard throws it away.
        Assert.NotNull(await pendingStore.LoadAsync(CancellationToken.None));
        await scan.DiscardAsync(CancellationToken.None);
        Assert.False(scan.Current.IsCollecting);
        Assert.Null(await pendingStore.LoadAsync(CancellationToken.None));
        Assert.Null(snapshots.Saved);
    }

    [Fact]
    public async Task AScanWithUnknownTilesOnlyRaisesCountsAndAWholeOneMayLowerThem()
    {
        var profiles = new MemoryProfileService(Profile(owned: new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["item-a"] = 5,
            ["item-b"] = 1,
            ["item-unseen"] = 9,
        }));
        var applier = new StashOwnedCountsApplier(profiles);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal) { ["item-a"] = 2, ["item-b"] = 4 };

        var lowerBound = await applier.ApplyAsync(new([], 0, KnownTiles: 6, UnknownTiles: 3, seen), CancellationToken.None);
        var ownedAfterLowerBound = (await profiles.GetActiveAsync(CancellationToken.None)).OwnedItemCounts;
        Assert.Equal(5, ownedAfterLowerBound["item-a"]);
        Assert.Equal(4, ownedAfterLowerBound["item-b"]);
        Assert.Equal(9, ownedAfterLowerBound["item-unseen"]);
        Assert.Equal((1, 0), (lowerBound.Raised, lowerBound.Lowered));

        var whole = await applier.ApplyAsync(new([], 0, KnownTiles: 6, UnknownTiles: 0, seen), CancellationToken.None);
        var ownedAfterWhole = (await profiles.GetActiveAsync(CancellationToken.None)).OwnedItemCounts;
        Assert.Equal(2, ownedAfterWhole["item-a"]);
        Assert.Equal(9, ownedAfterWhole["item-unseen"]);
        Assert.Equal((0, 1), (whole.Raised, whole.Lowered));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static GuidedStashScanService Service(
        IStashSnapshotStore snapshots,
        IPlayerProfileService profiles,
        IGuidedStashScanPendingStore pendingStore)
    {
        var assembler = new StashScanAssembler();
        return new(
            assembler,
            new StashLayoutAligner(),
            new StashReconstructionProjector(),
            new StashScanWorkflow(assembler, snapshots, new StashSnapshotComparer()),
            new StashOwnedCountsApplier(profiles),
            pendingStore);
    }

    /// <summary>One painted screen through the real pixel reader and reconstructor, into the scan.</summary>
    private static async Task<GuidedStashFrameOutcome> AddAsync(GuidedStashScanService scan, SyntheticStashLayout layout, int firstRow)
    {
        var options = new SyntheticStashFrameOptions();
        var catalog = SyntheticStashLayout.Catalog;
        var builder = new GridPixelReconstructionBuilder(
            new StashScanMeasurement.FixedIconEvidenceCache(catalog
                .Select(item => StashScanMeasurement.Evidence(item.ItemId, StashScanMeasurement.InGameFingerprint(item, options)))
                .ToArray()),
            new StashScanMeasurement.FixedItemRepository(catalog.ToDictionary(item => item.ItemId, StashScanMeasurement.Definition, StringComparer.Ordinal)),
            new StashScanMeasurement.UnavailableOcrEngine());
        var image = SyntheticStashPainter.RenderFrame(layout, firstRow, options);
        var request = await builder.BuildAsync(image, InventoryGridSurface.Stash, StashScanMeasurement.ObservedUtc, cancellationToken: CancellationToken.None);
        var reconstruction = new InventoryGridReconstructor().Reconstruct(request, CancellationToken.None);
        return await scan.AddScreenshotAsync(
            $"artifact-row-{firstRow:D2}",
            CaptureCorrelationId.New(),
            new CaptureContextMetadata(null, null, null, null, null, null, "desktop"),
            Convert.ToHexStringLower(SHA256.HashData(image.Pixels.Span)),
            StashScanMeasurement.ObservedUtc,
            0,
            reconstruction,
            CancellationToken.None);
    }

    private static async Task<HideoutRequirementRowViewModel> HideoutBoltsAsync(HideoutWorkspaceViewModel hideout)
    {
        await hideout.RefreshAsync();
        await hideout.SelectAsync(Assert.Single(hideout.Stations), CancellationToken.None);
        return Assert.Single(hideout.Items, row => row.ItemName == "Bolts");
    }

    private static PlayerProfile Profile(IReadOnlyDictionary<string, int> owned) => new(
        Guid.NewGuid(),
        "Tester",
        GameMode.Regular,
        Level: 10,
        Faction.Usec,
        Edition: null,
        TraderLevels: new Dictionary<string, int>(StringComparer.Ordinal),
        CompletedTaskIds: new HashSet<string>(StringComparer.Ordinal),
        ObjectiveProgress: new Dictionary<string, int>(StringComparer.Ordinal),
        HideoutStationLevels: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["lavatory"] = 1 },
        WishlistItemIds: new HashSet<string>(StringComparer.Ordinal),
        OwnedItemCounts: owned,
        EventItemStates: new Dictionary<string, EventItemState>(StringComparer.Ordinal),
        ItemOverrides: new Dictionary<string, string>(StringComparer.Ordinal),
        UpdatedUtc: DateTimeOffset.UtcNow);

    private sealed class MemoryProfileService(PlayerProfile profile) : IPlayerProfileService
    {
        private PlayerProfile _profile = profile;

        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(_profile);

        public Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken)
        {
            _profile = profile;
            return Task.CompletedTask;
        }

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) => Task.FromResult(_profile);
    }

    private sealed class FixedRequirementCatalog(
        IReadOnlyList<HideoutStationSummary> stations,
        IReadOnlyList<HideoutItemRequirement> requirements) : IRequirementCatalog
    {
        public IReadOnlyList<HideoutItemRequirement> Requirements => requirements;

        public Task<IReadOnlyList<HideoutItemRequirement>> GetHideoutRequirementsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(requirements);

        public Task<IReadOnlyList<QuestItemRequirement>> GetQuestRequirementsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuestItemRequirement>>([]);

        public Task<IReadOnlyList<HideoutStationSummary>> GetStationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(stations);

        public void Invalidate()
        {
        }
    }

    private sealed class NamedItemRepository : IItemRepository
    {
        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemDefinition?>(SyntheticStashLayout.Catalog.FirstOrDefault(item => item.ItemId == itemId) is { } item
                ? StashScanMeasurement.Definition(item)
                : null);

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(null);
    }

    private sealed class MemorySnapshotStore : IStashSnapshotStore
    {
        public StashSnapshotRecord? Saved { get; private set; }

        public Task SaveAsync(StashSnapshotRecord snapshot, CancellationToken cancellationToken)
        {
            Saved = snapshot;
            return Task.CompletedTask;
        }

        public Task<StashSnapshotRecord?> ReadCurrentAsync(InventoryProfileScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(Saved);

        public Task<StashSnapshotRecord?> ReadAsync(InventoryProfileScope scope, Guid snapshotId, CancellationToken cancellationToken) =>
            Task.FromResult(Saved);

        public Task<IReadOnlyList<StashSnapshotSummary>> ListAsync(InventoryProfileScope scope, int maximumCount, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StashSnapshotSummary>>([]);

        public Task<StashSnapshotDeleteResult> DeleteAsync(InventoryProfileScope scope, Guid snapshotId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StashSnapshotRetentionResult> ApplyRetentionAsync(InventoryProfileScope scope, DateTimeOffset retainFromUtc, bool dryRun, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
