using System.Globalization;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.UnitTests.Profiles;
using TarkovCompanion.UnitTests.Runtime;
using TarkovCompanion.UnitTests.V2Shell;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.V2Capture;

/// <summary>
/// The scan loop from the shell's Arm button to the Loot decision page, with the real shell,
/// bridge, context source, capture coordinator, handoff, recommendation source, engine and
/// planner. Only the recogniser's output, the catalog and the profile's stores are given.
/// </summary>
public sealed class ArmedLootLoopTests
{
    private static readonly DateTimeOffset Now = LootScanFactFixtures.Now;

    [Fact]
    public async Task AnArmedLootScreenshotFromTheWatcherReachesTheLootPageAndSaysWhy()
    {
        await using var loop = await Loop.CreateAsync();
        loop.Arm(ScanIntent.Loot);

        // Exactly what RaidObservationService does with a settled screenshot.
        var receipt = await loop.SubmitAsync(CaptureIntakeContext.For(loop.Coordinator, loop.Context.Describe()));
        var page = await loop.LootPageAsync();

        Assert.Equal(CaptureQueueDisposition.Accepted, receipt.Disposition);
        var salewa = page.Decisions.Single(card => card.Name == "Salewa");
        Assert.Equal("TAKE", salewa.VerdictLabel);
        Assert.Equal("Current quest · 3 for Shortage", salewa.HeadlineReason);
        Assert.Contains("Keep 3 more for Shortage (current)", salewa.WhyLabel, StringComparison.Ordinal);

        var bolts = page.Decisions.Single(card => card.Name == "Bolts");
        Assert.Equal("LEAVE", bolts.VerdictLabel);

        var card = page.Decisions.Single(item => item.Name == "Graphics card");
        Assert.Equal("TAKE", card.VerdictLabel);

        // Marked Allergic on the Events page: never weighed on price, and the row says so.
        var analyzer = page.Decisions.Single(item => item.Name == "Gas analyzer");
        Assert.Equal("REVIEW", analyzer.VerdictLabel);
        Assert.Equal("Allergic · do not eat", analyzer.HeadlineReason);
        Assert.Contains("allergic", analyzer.WhyLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheContextTheWatcherUsedToSubmitIsRefusedByAnArmedSession()
    {
        await using var loop = await Loop.CreateAsync();
        loop.Arm(ScanIntent.Loot);

        // What the watcher built before: no workspace, and "desktop" where the shell armed as
        // "this-desktop". Intake compares the two and refuses, which is why an armed Loot intent
        // never reached the Loot page from the game's own screenshot.
        var refused = await loop.SubmitAsync(new CaptureContextMetadata(null, null, null, null, null, null, "desktop"));

        Assert.Equal(CaptureQueueDisposition.Rejected, refused.Disposition);
        Assert.Equal("capture_context_changed_since_arm", refused.Code);
    }

    [Fact]
    public async Task WithNothingArmedTheWatcherDescribesWhereThePlayerIs()
    {
        await using var loop = await Loop.CreateAsync();

        var context = CaptureIntakeContext.For(loop.Coordinator, loop.Context.Describe());

        Assert.Equal(loop.Shell.Router.Current.Location.Route.Value, context.ActiveWorkspace);
        Assert.Equal(V2NavigationContext.ThisDesktop, context.InitiatingDevice);
        Assert.NotNull(context.ProfileContext);
    }

    private sealed class Loop : IAsyncDisposable
    {
        private readonly string _config;
        private readonly V2ShellCaptureBridge _bridge;

        private Loop(
            string config,
            V2ShellViewModel shell,
            CaptureSessionCoordinator coordinator,
            ShellCaptureContextSource context,
            V2ShellCaptureBridge bridge)
        {
            _config = config;
            Shell = shell;
            Coordinator = coordinator;
            Context = context;
            _bridge = bridge;
        }

        public V2ShellViewModel Shell { get; }

        public CaptureSessionCoordinator Coordinator { get; }

        public ShellCaptureContextSource Context { get; }

        public static async Task<Loop> CreateAsync()
        {
            var clock = new ManualTimeProvider(Now);
            var config = V2ShellTestData.TemporaryDirectory();
            var store = new RuntimeStateStore(new RuntimeOptions(true, true, GameMode.Regular, "en", TimeSpan.FromHours(9), TimeSpan.FromMinutes(5)));
            var shell = new V2ShellViewModel(V2ShellMode.VariantB, config, store);
            var profiles = new ProfileContextService(new MemoryProfileStore(), new ProfileClock(Now));
            var profile = Profile(Context(Id(282), "generation-a", ProfileGameMode.Pvp), "unrelated");
            await profiles.CreateAsync(new(profile.Context, profile.Name, profile.Progress, true), CancellationToken.None);
            var runtime = new ProfileRuntimeContextService(profiles);
            await runtime.InitializeAsync(CancellationToken.None);
            var active = runtime.Current.ActiveProfile!;
            var scope = new InventoryProfileScope(
                active.Context.Identity.ProfileId,
                active.Context.Identity.Generation,
                active.Context.Mode.ToString());

            var catalog = new LootScanFactFixtures.Catalog();
            var player = new EventfulProfiles();
            var source = new LootScanRecommendationSource(
                catalog,
                catalog,
                new LootScanNeedSource(
                    player,
                    new ProfileNeedAggregationService([new("shortage", "shortage-objective", "salewa", 3, FoundInRaidRequired: false)], []),
                    new LootScanFactFixtures.Quests([Quest("shortage", "Shortage", RecordedTaskState.Active)]),
                    new LootScanFactFixtures.Requirements()),
                new LootScanRaidContextSource(
                    new RaidStateService(),
                    new LootScanFactFixtures.NoMaps(),
                    new LootScanRaidPreference { Phase = RecommendationRaidPhase.Early, Risk = RecommendationRaidRisk.Low }),
                new LootScanEventStateSource(player, new Feast()));
            var handoff = new LootScanCaptureHandoff(
                runtime,
                new InventoryGridReconstructor(),
                new LootScanDecisionService(clock),
                clock,
                recommendations: source,
                observedInventory: new EmptyStash(scope, active.Context.DataSnapshot.SnapshotId));
            var origin = new WorkspaceOrigin(
                new WorkspaceId(Guid.Parse("10000000-0000-0000-0000-000000000920")),
                new CompanionDeviceId(Guid.Parse("10000000-0000-0000-0000-000000000921")),
                WorkspaceOriginKind.DesktopApplication,
                "desktop");
            // The coordinator keeps the wall clock because the bridge arms with it.
            var coordinator = new CaptureSessionCoordinator(
                new InlineCaptureWorkScheduler(),
                new ReadsALootScreen(),
                handoff,
                origin);
            var context = new ShellCaptureContextSource(store, runtime);
            var bridge = new V2ShellCaptureBridge(shell, coordinator, handoff, new IntelCaptureHandoff(), origin, contextSource: context);
            return new(config, shell, coordinator, context, bridge);
        }

        public void Arm(ScanIntent intent)
        {
            Shell.CaptureIntents.Single(offered => offered.Intent == intent).SelectCommand.Execute(null);
            Shell.ArmCaptureCommand.Execute(null);
        }

        public ValueTask<CaptureQueueReceipt> SubmitAsync(CaptureContextMetadata context)
        {
            var now = TimeProvider.System.GetUtcNow();
            return Coordinator.EnqueueAsync(
                new(
                    CaptureDeliveryKind.WatchedFile,
                    new MemoryCaptureSource(
                        new CapturedImage(new byte[16], 2, 2, 8, PixelFormat.Bgra8888, now, "fixture"),
                        CaptureSourceKind.GameWrittenScreenshot),
                    context,
                    now,
                    CaptureCorrelationId.New()),
                CancellationToken.None);
        }

        public async Task<LootScanViewModel> LootPageAsync()
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (Shell.LootScanResult is null && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            return Shell.LootScanResult ?? throw new TimeoutException("No loot scan reached the shell.");
        }

        public async ValueTask DisposeAsync()
        {
            _bridge.Dispose();
            await Coordinator.DisposeAsync();
            await Shell.DisposeAsync();
            try
            {
                Directory.Delete(_config, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The recognition seam: a loot screen read as four named items and a backpack.</summary>
    private sealed class ReadsALootScreen : ICaptureSessionPipeline
    {
        public Task<CaptureAnalysis> AnalyzeAsync(CaptureAnalysisRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new CaptureAnalysis(
                new string('a', 64),
                RecognizedContext.Loot,
                false,
                true,
                null,
                new Confidence(0.95),
                new GridReconstructionRequest(
                    InventoryGridSurface.VisibleLoot,
                    Lattice(2, 4, 1260),
                    [
                        Named(0, 0, "salewa", "Salewa", 1, 2, 1260),
                        Named(0, 1, "bolts", "Bolts", 1, 1, 1260),
                        Named(0, 2, "gpu", "Graphics card", 2, 1, 1260),
                        Named(1, 1, "item-gas-analyzer", "Gas analyzer", 1, 1, 1260),
                    ]),
                CarriedGrid: new GridReconstructionRequest(
                    InventoryGridSurface.CarriedInventory,
                    Lattice(2, 3, 600),
                    [Named(0, 0, "bolts", "Bolts", 1, 1, 600), Named(0, 1, "bolts", "Bolts", 1, 1, 600)])));
    }

    private sealed class EmptyStash(InventoryProfileScope scope, string dataSnapshotId) : IObservedInventoryEvidenceReader
    {
        public Task<ObservedInventoryEvidenceSnapshot?> ReadCurrentAsync(InventoryProfileScope requested, CancellationToken cancellationToken) =>
            Task.FromResult<ObservedInventoryEvidenceSnapshot?>(new(
                Guid.Parse("20000000-0000-4000-8000-000000000920"),
                scope,
                dataSnapshotId,
                new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "inventory.complete"),
                new EvidenceCoverage(fraction: 1),
                Scored(1),
                [],
                unresolvedCells: 0));
    }

    private sealed class Feast : IEventCatalog
    {
        public Task<IReadOnlyList<EventDefinition>> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EventDefinition>>(
            [
                new("feast", "Feast", null, null, true, new HashSet<string> { "item-gas-analyzer" }, "{}", new DataProvenance("fixture", Now)),
            ]);
    }

    /// <summary>A profile whose Events page holds one Allergic result.</summary>
    private sealed class EventfulProfiles : IPlayerProfileService
    {
        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PlayerProfile(
                Id(282),
                "Local profile",
                GameMode.Regular,
                20,
                Faction.Unknown,
                null,
                new Dictionary<string, int>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<string, EventItemState>(StringComparer.Ordinal) { ["feast:item-gas-analyzer"] = EventItemState.Allergic },
                new Dictionary<string, string>(StringComparer.Ordinal),
                Now.AddDays(-1)));

        public Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static QuestSummaryReadModel Quest(string id, string name, RecordedTaskState state) => new(
        id,
        name,
        null,
        null,
        state,
        "Manual",
        null,
        new QuestEligibility(QuestEligibilityState.Available, []),
        RecordedObjectivesSatisfaction.Indeterminate,
        false,
        null,
        false,
        [],
        [],
        []);

    private static DetectedGridLattice Lattice(int rows, int columns, int left) => new(
        rows,
        columns,
        cellWidthPixels: 63,
        cellHeightPixels: 63,
        new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current),
        Scored(0.97),
        new EvidenceRegion(left, 180, columns * 63, rows * 63, EvidenceCoordinateSpace.SourcePixels));

    private static GridCellObservation Named(int row, int column, string itemId, string name, int width, int height, int left)
    {
        var provenance = Scored(0.97);
        var item = new RecognizedItem(
            Known("id", itemId, provenance),
            Known("name", name, provenance),
            Known<int?>("quantity", 1, provenance),
            Known<int?>("width", width, provenance),
            Known<int?>("height", height, provenance),
            Known<bool?>("rotated", false, provenance),
            new EvidencedValue<bool?>("fir", null, new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current), provenance),
            Known("condition", ItemConditionReading.NotApplicable, provenance));
        return new(
            $"cell-{left}-{row}-{column}",
            new GridCellAddress(row, column),
            Known("item", item, provenance, new EvidenceRegion(left + (column * 63), 180 + (row * 63), width * 63, height * 63, EvidenceCoordinateSpace.SourcePixels)));
    }

    private static EvidenceProvenance Scored(double score) => new(
        EvidenceSourceClass.GameWrittenScreenshot,
        "fixture://cell",
        Now,
        new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, score),
        new ProducerIdentity("fixture", "1"));

    private static EvidencedValue<T> Known<T>(string fieldId, T value, EvidenceProvenance provenance, EvidenceRegion? bounds = null) =>
        new(fieldId, value, new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current), provenance, bounds);
}
