using System.Globalization;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.UnitTests.Profiles;
using TarkovCompanion.UnitTests.Runtime;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.V2Capture;

/// <summary>
/// Package 37: what a scanned frame becomes once it reaches the handoff, the decision service
/// and the workspace, with the recognizer's output given rather than re-derived.
/// </summary>
public sealed class LootScanValuationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ANamedItemIsValuedFromTheCatalogAndNotTurnedAwayForFoundInRaidAlone()
    {
        var result = await EvaluateAsync(Named(0, 0, "gpu", "Graphics card", 2, 1));

        var decision = Assert.Single(result.Decisions);
        // Valued, and honestly not decided: nothing the companion holds gives a flea net, the
        // player's needs or a raid phase. What it must not be is "evidence incomplete".
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.NotEqual("item.evidence-incomplete", decision.Reasons[0].Code);
        Assert.Equal(337_352, decision.Economics!.Inputs.FleaGrossRoubles.Value);
        Assert.Equal(120_000, decision.Economics.Inputs.TraderRoubles.Value);
        Assert.Null(decision.Economics.Inputs.FleaNetRoubles.Value);
        Assert.Equal(ResultCompleteness.Unknown, decision.Economics.Inputs.FleaNetRoubles.Status.Completeness);
        Assert.Null(decision.Economics.BestNetValueRoubles);
    }

    [Fact]
    public async Task AnItemTheFleaDoesNotSellDeclaresThatChannelAbsentRatherThanUnknown()
    {
        var result = await EvaluateAsync(Named(0, 0, "keycard", "Lab keycard", 1, 1));

        var inputs = Assert.Single(result.Decisions).Economics!.Inputs;
        Assert.Equal(ResultCompleteness.Unavailable, inputs.FleaNetRoubles.Status.Completeness);
        Assert.Equal(90_000, inputs.TraderRoubles.Value);
    }

    [Fact]
    public async Task ACellTheRecognizerRefusedStaysARefusalWithItsLookalikesNamed()
    {
        var result = await EvaluateAsync(Refused(0, 0, "Dorm room 220 key", "Dorm room 308 key"));
        var card = Assert.Single(new LootScanViewModel(result, culture: CultureInfo.InvariantCulture).Decisions);

        Assert.True(card.IsReview);
        Assert.Equal("Dorm room 220 key or Dorm room 308 key", card.HeadlineReason);
        Assert.Equal(string.Empty, card.ShortValueLabel);
        Assert.Contains("too alike", card.WhyLabel, StringComparison.Ordinal);
    }

    // #572: the paired tablet lists the same rows, in the same order and words, as the Loot page,
    // and the result survives the surface's JSON.
    [Fact]
    public async Task TheTabletGetsTheLootPagesOwnRowsInItsOwnOrderAndWords()
    {
        var result = await EvaluateAsync(
            Refused(0, 0),
            Named(0, 1, "bolts", "Bolts", 1, 1),
            Named(0, 2, "gpu", "Graphics card", 2, 1));
        var workspace = new LootScanViewModel(result, culture: CultureInfo.InvariantCulture);

        var loot = TarkovCompanion.App.Services.V2.TabletLootResultBuilder.From(workspace);

        Assert.Equal(workspace.Decisions.Select(card => card.Name), loot.Rows.Select(row => row.Name));
        Assert.Equal(workspace.Decisions.Select(card => card.VerdictLabel), loot.Rows.Select(row => row.VerdictLabel));
        Assert.Equal(workspace.Decisions.Select(card => card.ShortValueLabel), loot.Rows.Select(row => row.Value));
        Assert.Equal(workspace.Decisions.Select(card => card.HeadlineReason), loot.Rows.Select(row => row.Reason));
        Assert.Equal(0, loot.HiddenRows);
        Assert.Contains(workspace.TakeSummary, loot.Summary, StringComparison.Ordinal);
        Assert.Equal(result.EvaluatedUtc, loot.EvaluatedUtc);

        var surface = new TarkovCompanion.Application.Services.Devices.TabletMapSurface(
            1, "customs", "Customs", "default", "v1", null, null, [], [], [], [],
            new TarkovCompanion.Application.Services.Devices.TabletMapView(null, 0, 0, 1, null, null),
            null, null, result.EvaluatedUtc, Loot: loot);
        var read = TarkovCompanion.Application.Services.Devices.TabletMapSurfaceJson.Deserialize(
            TarkovCompanion.Application.Services.Devices.TabletMapSurfaceJson.Serialize(surface));
        Assert.Equal(loot.Rows, read!.Loot!.Rows);
    }

    [Fact]
    public async Task TheWorkspaceListsWhatWasValuedDearestSquareFirstAndWhatWasNotReadLast()
    {
        var result = await EvaluateAsync(
            Refused(0, 0),
            Named(0, 1, "bolts", "Bolts", 1, 1),
            Named(0, 2, "gpu", "Graphics card", 2, 1));
        var workspace = new LootScanViewModel(result, culture: CultureInfo.InvariantCulture);

        Assert.Equal(["Graphics card", "Bolts"], workspace.Decisions.Take(2).Select(card => card.Name).ToArray());
        var gpu = workspace.Decisions[0];
        Assert.Equal("Valued, not decided", gpu.HeadlineReason);
        Assert.Equal("₽337k", gpu.ShortValueLabel);
        Assert.Equal("₽169k / sq", gpu.ShortValuePerSquareLabel);
        Assert.Equal("24-hour flea average, before the fee", gpu.PriceBasisLabel);
        Assert.True(gpu.HasCatalogValueOnly);
        Assert.Equal("Not recognised", workspace.Decisions[2].HeadlineReason);
    }

    private static async Task<LootScanResult> EvaluateAsync(params GridCellObservation[] cells)
    {
        var clock = new ManualTimeProvider(Now);
        var profiles = new ProfileContextService(new MemoryProfileStore(), new ProfileClock(Now));
        var profile = Profile(Context(Id(437), "generation-a", ProfileGameMode.Pvp), "item-a");
        await profiles.CreateAsync(Request(profile), CancellationToken.None);
        using var runtime = new ProfileRuntimeContextService(profiles);
        await runtime.InitializeAsync(CancellationToken.None);
        var handoff = new LootScanCaptureHandoff(
            runtime,
            new InventoryGridReconstructor(),
            new LootScanDecisionService(clock),
            clock,
            recommendations: new LootScanRecommendationSource(new Catalog()));
        var provenance = Scored(0.97);
        var lattice = new DetectedGridLattice(
            rows: 2,
            columns: 4,
            cellWidthPixels: 63,
            cellHeightPixels: 63,
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current),
            provenance,
            new EvidenceRegion(1260, 180, 252, 126, EvidenceCoordinateSpace.SourcePixels));
        var correlation = CaptureCorrelationId.New();
        return await handoff.EvaluateAsync(
            new LootScanFrame(
                correlation.ToString(),
                new CaptureSessionId(Guid.NewGuid()),
                correlation,
                new CaptureContextMetadata("raid", null, "customs", null, null, null, "desktop"),
                "artifact",
                1,
                new string('a', 64),
                "desktop",
                new GridReconstructionRequest(InventoryGridSurface.VisibleLoot, lattice, cells)),
            runtime.Current.ActiveProfile!,
            CancellationToken.None);
    }

    /// <summary>A cell as the recognizer reports one it named: scored, counted, found-in-raid unread.</summary>
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
        return new($"cell-{row}-{column}", new GridCellAddress(row, column), Known("item", item, provenance, Bounds(row, column, width, height)));
    }

    private static GridCellObservation Refused(int row, int column, params string[] lookalikes)
    {
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.GameWrittenScreenshot,
            "fixture://cell",
            Now,
            EvidenceConfidence.Unscored,
            new ProducerIdentity("fixture", "1"));
        var candidates = lookalikes
            .Select((name, index) => new EvidenceCandidate<RecognizedItem>(
                $"lookalike-{index}",
                name,
                ((EvidencedValue<RecognizedItem>)Named(row, column, $"lookalike-{index}", name, 1, 1).Item).Value!,
                provenance))
            .ToArray();
        return new(
            $"cell-{row}-{column}",
            new GridCellAddress(row, column),
            new EvidencedValue<RecognizedItem>(
                "item",
                null,
                new ResultStatus(candidates.Length == 0 ? ResultCompleteness.Unknown : ResultCompleteness.Partial, FreshnessState.Current),
                provenance,
                Bounds(row, column, 1, 1),
                candidates));
    }

    private static EvidenceRegion Bounds(int row, int column, int width, int height) =>
        new(1260 + (column * 63), 180 + (row * 63), width * 63, height * 63, EvidenceCoordinateSpace.SourcePixels);

    private static EvidenceProvenance Scored(double score) => new(
        EvidenceSourceClass.GameWrittenScreenshot,
        "fixture://cell",
        Now,
        new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, score),
        new ProducerIdentity("fixture", "1"));

    private static EvidencedValue<T> Known<T>(string fieldId, T value, EvidenceProvenance provenance, EvidenceRegion? bounds = null) =>
        new(fieldId, value, new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current), provenance, bounds);

    private sealed class Catalog : IItemRepository
    {
        private static readonly Dictionary<string, (string Name, int Width, int Height, bool Flea, long? Average, long? Trader)> Items = new()
        {
            ["gpu"] = ("Graphics card", 2, 1, true, 337_352, 120_000),
            ["bolts"] = ("Bolts", 1, 1, true, 25_345, 9_000),
            ["keycard"] = ("Lab keycard", 1, 1, false, null, 90_000),
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
    }
}
