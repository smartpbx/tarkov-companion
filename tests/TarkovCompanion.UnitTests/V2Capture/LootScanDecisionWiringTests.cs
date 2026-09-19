using System.Globalization;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.UnitTests.Profiles;
using TarkovCompanion.UnitTests.Runtime;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.V2Capture;

/// <summary>
/// #282: a scanned frame is decided, not only valued, and every input that decides it is read
/// from where the app keeps it. Each test runs the real handoff, source, engine and planner;
/// only the catalog, the profile store and the quest board are given.
/// </summary>
public sealed class LootScanDecisionWiringTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task APinnedItemIsReadFromTheProfileAndTakenAlthoughItsPriceSaysLeave()
    {
        var unpinned = await EvaluateAsync(new Scan { Loot = [Named(0, 0, "bolts", "Bolts", 1, 1)], Carried = Backpack(2, 2) });
        var pinned = await EvaluateAsync(new Scan
        {
            Loot = [Named(0, 0, "bolts", "Bolts", 1, 1)],
            Carried = Backpack(2, 2),
            Progress = progress => LootScanProfileRules.WithPin(progress, "bolts", true),
        });

        Assert.Equal(LootScanVerdict.Leave, Assert.Single(unpinned.Decisions).Verdict);
        var decision = Assert.Single(pinned.Decisions);
        Assert.Equal(LootScanVerdict.Take, decision.Verdict);
        Assert.Contains(decision.Recommendation!.Decision.Value!.Reasons, reason => reason.Code == "profile.pinned");
        Assert.NotNull(decision.Placement);
    }

    [Fact]
    public async Task TheFleaNetIsThePriceLessTheFeeAndDecidesTakeOrLeave()
    {
        var result = await EvaluateAsync(new Scan
        {
            Loot = [Named(0, 0, "gpu", "Graphics card", 2, 1), Named(1, 0, "bolts", "Bolts", 1, 1)],
            Carried = Backpack(4, 4),
        });

        var gpu = result.Decisions.Single(item => item.Item.Value!.CanonicalId.Value == "gpu");
        var bolts = result.Decisions.Single(item => item.Item.Value!.CanonicalId.Value == "bolts");
        var inputs = gpu.Economics!.Inputs;
        var fee = FleaMarketFee.Calculate(250_000, 337_352, 1, Rates);
        Assert.Equal(fee, inputs.FleaFeeRoubles.Value);
        Assert.Equal(337_352 - fee, inputs.FleaNetRoubles.Value);
        Assert.Equal(EvidenceSourceClass.DerivedCalculation, inputs.FleaNetRoubles.Provenance.SourceClass);
        Assert.Equal(LootScanVerdict.Take, gpu.Verdict);
        Assert.Equal(LootScanVerdict.Leave, bolts.Verdict);
        Assert.Equal("economics.complete", gpu.Economics.Status.Code);
    }

    [Fact]
    public async Task ACarriedItemNobodyCouldNameStillHoldsItsSquaresAndIsNeverDropped()
    {
        // A 1x3 backpack: an unnamed 1x1 at the left, two free squares. The card fits beside it.
        var fits = await EvaluateAsync(new Scan
        {
            Loot = [Named(0, 0, "gpu", "Graphics card", 2, 1)],
            Carried = Backpack(1, 3, Unnamed(0, 0)),
        });
        // A 1x2 backpack with the same unnamed item: only dropping it would make room.
        var blocked = await EvaluateAsync(new Scan
        {
            Loot = [Named(0, 0, "gpu", "Graphics card", 2, 1)],
            Carried = Backpack(1, 2, Unnamed(0, 0)),
        });

        var take = Assert.Single(fits.Decisions);
        Assert.Equal(LootScanVerdict.Take, take.Verdict);
        Assert.Equal(new GridCellAddress(0, 1), take.Placement!.Anchor);
        var none = Assert.Single(blocked.Decisions);
        Assert.NotEqual(LootScanVerdict.Swap, none.Verdict);
        Assert.NotEqual(LootScanVerdict.Take, none.Verdict);
        Assert.Empty(none.Drops);
    }

    [Fact]
    public async Task WithTheBackpackUnreadTheAdviceStillReachesThePlayerAndSaysWhatItLacks()
    {
        var result = await EvaluateAsync(new Scan { Loot = [Named(0, 0, "gpu", "Graphics card", 2, 1)] });
        var decision = Assert.Single(result.Decisions);
        var card = Assert.Single(new LootScanViewModel(result, culture: CultureInfo.InvariantCulture).Decisions);

        // No fit is claimed: the contract keeps this a review.
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("carried.capacity-incomplete", decision.Reasons[0].Code);
        Assert.True(card.IsAdvisedTake);
        Assert.Equal("TAKE?", card.VerdictLabel);
        Assert.Equal("Worth its squares", card.HeadlineReason);
        Assert.Contains("your backpack wasn't read", card.WhyLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACarriedItemThePlayerPinnedIsNeverOfferedForDropping()
    {
        GridCellObservation[] full = [Named(0, 0, "bolts", "Bolts", 1, 1), Named(0, 1, "bolts", "Bolts", 1, 1)];
        var swapped = await EvaluateAsync(new Scan
        {
            Loot = [Named(0, 0, "gpu", "Graphics card", 2, 1)],
            Carried = Backpack(1, 2, full),
        });
        var kept = await EvaluateAsync(new Scan
        {
            Loot = [Named(0, 0, "gpu", "Graphics card", 2, 1)],
            Carried = Backpack(1, 2, full),
            Progress = progress => LootScanProfileRules.WithPin(progress, "bolts", true),
        });

        var swap = Assert.Single(swapped.Decisions);
        Assert.Equal(LootScanVerdict.Swap, swap.Verdict);
        Assert.Equal(2, swap.Drops.Count);
        var leave = Assert.Single(kept.Decisions);
        Assert.Equal(LootScanVerdict.Leave, leave.Verdict);
        Assert.Equal("capacity.no-supported-fit", leave.Reasons[0].Code);
    }

    [Fact]
    public async Task AQuestThePlayerIsOnIsNamedAndTheMissingStashScanIsSaidRatherThanHidden()
    {
        var result = await EvaluateAsync(new Scan
        {
            Loot = [Named(0, 0, "salewa", "Salewa", 1, 2)],
            Carried = Backpack(4, 4),
            QuestRequirements = [new("shortage", "shortage-objective", "salewa", 3, FoundInRaidRequired: false)],
            Board = [Quest("shortage", "Shortage", RecordedTaskState.Active)],
        });
        var decision = Assert.Single(result.Decisions);
        var card = Assert.Single(new LootScanViewModel(result, culture: CultureInfo.InvariantCulture).Decisions);

        var advice = decision.Recommendation!.Decision.Value!;
        Assert.Equal(TarkovCompanion.Core.Abstractions.V2.RecommendationAction.Take, advice.Action);
        Assert.Contains(advice.Reasons, reason => reason.Category == RecommendationReasonCategory.CurrentQuest);
        // Holdings could not be subtracted, so the engine's answer is partial and stays a review.
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("TAKE?", card.VerdictLabel);
        Assert.Equal("Current quest", card.HeadlineReason);
        Assert.Contains("Shortage", card.WhyLabel, StringComparison.Ordinal);
        Assert.Contains("no stash scan yet", card.WhyLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AQuestFarDownTheChainIsNotAReasonToTake()
    {
        var result = await EvaluateAsync(new Scan
        {
            Loot = [Named(0, 0, "bolts", "Bolts", 1, 1)],
            Carried = Backpack(2, 2),
            QuestRequirements = [new("q7", "q7-objective", "bolts", 1, FoundInRaidRequired: false)],
            Board =
            [
                Quest("q1", "One", RecordedTaskState.NotStarted),
                Quest("q2", "Two", RecordedTaskState.NotStarted, "q1"),
                Quest("q3", "Three", RecordedTaskState.NotStarted, "q2"),
                Quest("q4", "Four", RecordedTaskState.NotStarted, "q3"),
                Quest("q5", "Five", RecordedTaskState.NotStarted, "q4"),
                Quest("q6", "Six", RecordedTaskState.NotStarted, "q5"),
                Quest("q7", "Seven", RecordedTaskState.NotStarted, "q6"),
            ],
        });

        // Seven steps off is past the engine's five-step horizon, so price decides and says leave.
        Assert.Equal(LootScanVerdict.Leave, Assert.Single(result.Decisions).Verdict);
    }

    [Fact]
    public async Task OutsideARaidThePhaseIsUnreadAndTheWorkspaceAsksForIt()
    {
        var result = await EvaluateAsync(new Scan
        {
            Loot = [Named(0, 0, "gpu", "Graphics card", 2, 1)],
            Carried = Backpack(4, 4),
            Phase = null,
        });
        var card = Assert.Single(new LootScanViewModel(result, culture: CultureInfo.InvariantCulture).Decisions);

        Assert.Equal(LootScanVerdict.Review, Assert.Single(result.Decisions).Verdict);
        Assert.False(card.IsAdvisedTake);
        Assert.Contains("the raid phase (pick it above)", card.WhyLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARiskierRaidAsksMoreOfAnItemBeforeItIsWorthTaking()
    {
        var normal = await EvaluateAsync(new Scan { Loot = [Named(0, 0, "salewa", "Salewa", 1, 2)], Carried = Backpack(4, 4) });
        var critical = await EvaluateAsync(new Scan
        {
            Loot = [Named(0, 0, "salewa", "Salewa", 1, 2)],
            Carried = Backpack(4, 4),
            Risk = RecommendationRaidRisk.Critical,
        });

        Assert.Equal(LootScanVerdict.Take, Assert.Single(normal.Decisions).Verdict);
        Assert.Equal(LootScanVerdict.Leave, Assert.Single(critical.Decisions).Verdict);
    }

    [Fact]
    public async Task AnItemRuleThisBuildCannotReadIsNotTreatedAsNoRule()
    {
        var result = await EvaluateAsync(new Scan
        {
            Loot = [Named(0, 0, "gpu", "Graphics card", 2, 1)],
            Carried = Backpack(4, 4),
            Progress = progress => LootScanProfileRules.WithRule(progress, "gpu", "MeltForScrap"),
        });

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Contains(
            decision.Recommendation!.Decision.Value!.Reasons,
            reason => reason.Code == "profile.override-untrusted");
    }

    [Fact]
    public async Task AnAlwaysLeaveRuleOutranksAHighPrice()
    {
        var result = await EvaluateAsync(new Scan
        {
            Loot = [Named(0, 0, "gpu", "Graphics card", 2, 1)],
            Carried = Backpack(4, 4),
            Progress = progress => LootScanProfileRules.WithRule(progress, "gpu", LootScanProfileRules.LeaveRule),
        });

        Assert.Equal(LootScanVerdict.Leave, Assert.Single(result.Decisions).Verdict);
    }

    [Fact]
    public async Task WithNoProfileGivenNothingIsClaimedAboutPinsOrRules()
    {
        var source = new LootScanRecommendationSource(new Catalog());
        var loot = new InventoryGridReconstructor().Reconstruct(
            new(InventoryGridSurface.VisibleLoot, Lattice(1, 1), [Named(0, 0, "bolts", "Bolts", 1, 1)]),
            CancellationToken.None);

        var candidate = Assert.Single(await source.BuildAsync(
            loot,
            new CaptureSessionId(Guid.NewGuid()),
            "artifact",
            1,
            new string('a', 64),
            new(Id(1), "generation-a", "Pvp"),
            "snapshot-a",
            Now,
            CancellationToken.None));

        // The defect this replaces: all three were "false", complete, at full confidence.
        Assert.Null(candidate.Profile.Pinned.Value);
        Assert.Equal(ResultCompleteness.Unknown, candidate.Profile.Pinned.Status.Completeness);
        Assert.Equal(ResultCompleteness.Unknown, candidate.Profile.ProtectedItem.Status.Completeness);
        Assert.Equal(ResultCompleteness.Unknown, candidate.Profile.ExplicitAction.Status.Completeness);
    }

    [Fact]
    public async Task ChangingAChoiceDecidesTheSameFrameAgainWithoutASecondScreenshot()
    {
        var harness = await Harness.CreateAsync(new Scan { Loot = [Named(0, 0, "bolts", "Bolts", 1, 1)], Carried = Backpack(2, 2) });
        var results = new List<LootScanResult>();
        harness.Handoff.LootScanEvaluated += (_, result) => results.Add(result);
        var first = await harness.EvaluateAsync();
        var controls = new LootScanWorkspaceControls(harness.Runtime, harness.Preference, harness.Handoff);

        await controls.SetPinnedAsync("bolts", true);

        Assert.Equal(LootScanVerdict.Leave, Assert.Single(first.Decisions).Verdict);
        Assert.True(controls.IsPinned("bolts"));
        var again = Assert.Single(results);
        Assert.Equal(first.ArtifactId, again.ArtifactId);
        Assert.Equal(LootScanVerdict.Take, Assert.Single(again.Decisions).Verdict);
    }

    private static readonly FleaMarketRates Rates = new(0.05, 0.05, Now.AddHours(-1));

    private static async Task<LootScanResult> EvaluateAsync(Scan scan)
    {
        var harness = await Harness.CreateAsync(scan);
        return await harness.EvaluateAsync();
    }

    private sealed record Scan
    {
        public required GridCellObservation[] Loot { get; init; }

        public GridReconstructionRequest? Carried { get; init; }

        public Func<ProfileProgress, ProfileProgress>? Progress { get; init; }

        public QuestItemRequirement[] QuestRequirements { get; init; } = [];

        public QuestSummaryReadModel[] Board { get; init; } = [];

        public RecommendationRaidPhase? Phase { get; init; } = RecommendationRaidPhase.Early;

        public RecommendationRaidRisk Risk { get; init; } = RecommendationRaidRisk.Low;
    }

    private sealed class Harness(
        Scan scan,
        ProfileRuntimeContextService runtime,
        LootScanCaptureHandoff handoff,
        LootScanRaidPreference preference)
    {
        public ProfileRuntimeContextService Runtime { get; } = runtime;

        public LootScanCaptureHandoff Handoff { get; } = handoff;

        public LootScanRaidPreference Preference { get; } = preference;

        public static async Task<Harness> CreateAsync(Scan scan)
        {
            var clock = new ManualTimeProvider(Now);
            var profiles = new ProfileContextService(new MemoryProfileStore(), new ProfileClock(Now));
            var profile = Profile(Context(Id(282), "generation-a", ProfileGameMode.Pvp), "unrelated");
            await profiles.CreateAsync(
                new(profile.Context, profile.Name, scan.Progress?.Invoke(profile.Progress) ?? profile.Progress, true),
                CancellationToken.None);
            var runtime = new ProfileRuntimeContextService(profiles);
            await runtime.InitializeAsync(CancellationToken.None);
            var preference = new LootScanRaidPreference { Phase = scan.Phase, Risk = scan.Risk };
            var catalog = new Catalog();
            var source = new LootScanRecommendationSource(
                catalog,
                catalog,
                new LootScanNeedSource(
                    new Profiles(),
                    new ProfileNeedAggregationService(scan.QuestRequirements, []),
                    new Quests(scan.Board),
                    new Requirements()),
                new LootScanRaidContextSource(new RaidStateService(), new NoMaps(), preference));
            var handoff = new LootScanCaptureHandoff(
                runtime,
                new InventoryGridReconstructor(),
                new LootScanDecisionService(clock),
                clock,
                recommendations: source);
            return new(scan, runtime, handoff, preference);
        }

        public Task<LootScanResult> EvaluateAsync()
        {
            var correlation = CaptureCorrelationId.New();
            return Handoff.EvaluateAsync(
                new LootScanFrame(
                    correlation.ToString(),
                    new CaptureSessionId(Guid.NewGuid()),
                    correlation,
                    new CaptureContextMetadata("raid", null, "customs", null, null, null, "desktop"),
                    "artifact",
                    1,
                    new string('a', 64),
                    "desktop",
                    new GridReconstructionRequest(InventoryGridSurface.VisibleLoot, Lattice(2, 4), scan.Loot))
                {
                    CarriedGrid = scan.Carried,
                },
                Runtime.Current.ActiveProfile!,
                CancellationToken.None);
        }
    }

    private static GridReconstructionRequest Backpack(int rows, int columns, params GridCellObservation[] carried) =>
        new(InventoryGridSurface.CarriedInventory, Lattice(rows, columns), carried);

    private static DetectedGridLattice Lattice(int rows, int columns) => new(
        rows,
        columns,
        cellWidthPixels: 63,
        cellHeightPixels: 63,
        new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current),
        Scored(0.97),
        new EvidenceRegion(1260, 180, columns * 63, rows * 63, EvidenceCoordinateSpace.SourcePixels));

    private static QuestSummaryReadModel Quest(string id, string name, RecordedTaskState state, params string[] requires) => new(
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
        requires.Select(required => new QuestPrerequisiteReadModel(required, ["complete"], RecordedTaskState.NotStarted)).ToArray(),
        []);

    /// <summary>A cell as the recognizer reports one it named, with found-in-raid unread.</summary>
    private static GridCellObservation Named(int row, int column, string itemId, string name, int width, int height)
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
            $"cell-{row}-{column}",
            new GridCellAddress(row, column),
            Known("item", item, provenance, new EvidenceRegion(1260 + (column * 63), 180 + (row * 63), width * 63, height * 63, EvidenceCoordinateSpace.SourcePixels)));
    }

    /// <summary>A carried cell whose footprint was measured and whose icon matched nothing.</summary>
    private static GridCellObservation Unnamed(int row, int column) => new(
        $"carried-{row}-{column}",
        new GridCellAddress(row, column),
        new EvidencedValue<RecognizedItem>(
            "item",
            null,
            new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current),
            Scored(0.4),
            new EvidenceRegion(1260 + (column * 63), 180 + (row * 63), 63, 63, EvidenceCoordinateSpace.SourcePixels)));

    private static EvidenceProvenance Scored(double score) => new(
        EvidenceSourceClass.GameWrittenScreenshot,
        "fixture://cell",
        Now,
        new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, score),
        new ProducerIdentity("fixture", "1"));

    private static EvidencedValue<T> Known<T>(string fieldId, T value, EvidenceProvenance provenance, EvidenceRegion? bounds = null) =>
        new(fieldId, value, new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current), provenance, bounds);

    private sealed class Catalog : IItemRepository, IItemMarketFactSource
    {
        private static readonly Dictionary<string, (string Name, int Width, int Height, bool Flea, long? Average, long? Trader, long Base, int Offers)> Items = new()
        {
            ["gpu"] = ("Graphics card", 2, 1, true, 337_352, 120_000, 250_000, 40),
            ["bolts"] = ("Bolts", 1, 1, true, 9_000, 3_000, 7_000, 60),
            ["salewa"] = ("Salewa", 1, 2, true, 60_000, 12_000, 40_000, 25),
        };

        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.TryGetValue(itemId, out var item)
                ? new ItemDefinition(
                    itemId,
                    item.Name,
                    item.Name,
                    string.Empty,
                    ItemCategory.Barter,
                    new ItemDimensions(item.Width, item.Height),
                    item.Flea,
                    null,
                    null,
                    null,
                    null,
                    null,
                    new HashSet<string>(),
                    new DataProvenance("fixture", Now.AddHours(-1)))
                : null);

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.TryGetValue(itemId, out var item)
                ? new ItemPriceSnapshot(
                    item.Average,
                    item.Trader is { } trader ? [new TraderOffer("therapist", "Therapist", trader, new DataProvenance("fixture", Now.AddHours(-1)))] : [],
                    item.Average,
                    null,
                    null,
                    new DataProvenance("fixture", Now.AddHours(-1)))
                : null);

        Task<ItemMarketFacts?> IItemMarketFactSource.GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.TryGetValue(itemId, out var item)
                ? new ItemMarketFacts(itemId, item.Base, item.Offers, false, Now.AddHours(-1))
                : null);

        public Task<FleaMarketRates?> GetFleaRatesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<FleaMarketRates?>(Rates);
    }

    private sealed class Profiles : IPlayerProfileService
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
                new Dictionary<string, EventItemState>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal),
                DateTimeOffset.UnixEpoch));

        public Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Quests(QuestSummaryReadModel[] tasks) : IQuestReadService
    {
        public Task<QuestBoardReadModel> GetQuestBoardAsync(QuestProfileScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(new QuestBoardReadModel(scope, 1, null, tasks, []));

        public Task<QuestItemNeedsReadModel> GetItemNeedsAsync(QuestProfileScope scope, string itemId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<QuestMapObjectivesReadModel> GetActiveMapObjectivesAsync(
            QuestProfileScope scope,
            IReadOnlyCollection<string> mapIds,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Requirements : IRequirementCatalog
    {
        public Task<IReadOnlyList<HideoutItemRequirement>> GetHideoutRequirementsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HideoutItemRequirement>>([]);

        public Task<IReadOnlyList<QuestItemRequirement>> GetQuestRequirementsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuestItemRequirement>>([]);

        public Task<IReadOnlyList<HideoutStationSummary>> GetStationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HideoutStationSummary>>([]);

        public void Invalidate()
        {
        }
    }

    private sealed class NoMaps : IMapDataService
    {
        public Task<MapDefinition?> GetAsync(string mapId, CancellationToken cancellationToken) =>
            Task.FromResult<MapDefinition?>(null);
    }
}
