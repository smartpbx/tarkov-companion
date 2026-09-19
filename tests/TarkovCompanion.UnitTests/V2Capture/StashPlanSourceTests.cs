using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.ViewModels.V2.StashScan;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Stash;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.V2Capture;

/// <summary>
/// #283: <see cref="StashOrganizationPlanner"/> had no caller. These run its caller with the
/// real engine and the real planner, so the groups are the ones the app produces.
/// </summary>
public sealed class StashPlanSourceTests
{
    private static readonly DateTimeOffset Now = LootScanFactFixtures.Now;

    [Fact]
    public async Task WhatNothingNeedsIsSoldWhereItFetchesMostAndSaysWhatItFetches()
    {
        var sorted = await SortAsync([Tile(0, 0, "gpu", 2, 1), Tile(1, 0, "keycard", 1, 1)]);

        var gpu = sorted.Plan.Items.Single(item => item.CanonicalItemId == "gpu");
        var keycard = sorted.Plan.Items.Single(item => item.CanonicalItemId == "keycard");
        Assert.Equal(StashPlanGroup.Sell, gpu.Group);
        Assert.Contains(StashManualOperation.AddToSellQueue, gpu.Operations);
        Assert.Contains(gpu.ReasonCodes, code => code.StartsWith("economics.flea-net.", StringComparison.Ordinal));
        Assert.NotNull(gpu.FleaFeeRoubles.Value);
        Assert.Equal(337_352 - gpu.FleaFeeRoubles.Value, gpu.NetValueRoubles.Value);
        // No flea listing, so the trader is the only way to sell it.
        Assert.Equal(StashPlanGroup.Sell, keycard.Group);
        Assert.Contains(keycard.ReasonCodes, code => code.StartsWith("economics.trader.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhatAQuestStillNeedsIsKeptAndTheReasonNamesTheQuest()
    {
        var sorted = await SortAsync(
            [Tile(0, 0, "salewa", 1, 2)],
            quests: [new("shortage", "shortage-objective", "salewa", 3, FoundInRaidRequired: false)],
            board: [Quest("shortage", "Shortage")]);

        var salewa = Assert.Single(sorted.Plan.Items);
        Assert.Equal(StashPlanGroup.Keep, salewa.Group);
        Assert.Contains("Shortage", StashSortWording.Why(salewa, sorted.ReasonsByItemKey[salewa.ItemKey]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APinnedItemIsKeptWhateverItSellsFor()
    {
        var sorted = await SortAsync(
            [Tile(0, 0, "gpu", 2, 1)],
            progress: progress => LootScanProfileRules.WithPin(progress, "gpu", true));

        Assert.Equal(StashPlanGroup.Keep, Assert.Single(sorted.Plan.Items).Group);
    }

    [Fact]
    public async Task AMonthOldFleaPriceSellsNothingAndTheRowSaysWhatIsMissing()
    {
        var sorted = await SortAsync([Tile(0, 0, "relic", 1, 1)]);

        var relic = Assert.Single(sorted.Plan.Items);
        Assert.Equal(StashPlanGroup.Review, relic.Group);
        // The listing count is a market figure too, and as old as the price.
        Assert.Equal(
            "Not known: what the flea returns after its fee and how readily another turns up.",
            StashSortWording.Why(relic, sorted.ReasonsByItemKey[relic.ItemKey]));
    }

    [Fact]
    public async Task AmmoAndKeysStayUnderReviewUntilTheirOwnServicesAnswer()
    {
        var sorted = await SortAsync(
            [Tile(0, 0, "gpu", 2, 1)],
            specialist: _ => StashSpecialistIntelligenceKind.Ammo);

        var ammo = Assert.Single(sorted.Plan.Items);
        Assert.Equal(StashPlanGroup.Review, ammo.Group);
        Assert.Equal("Ammo and keys aren't sorted yet.", StashSortWording.Why(ammo, null));
    }

    [Fact]
    public async Task ATileNobodyNamedIsNotInThePlanAndOneTheCatalogDoesNotKnowIsUnderReview()
    {
        var sorted = await SortAsync([Tile(0, 0, null, 1, 1), Tile(0, 1, "not-in-the-catalog", 1, 1)]);

        var unknown = Assert.Single(sorted.Plan.Items);
        Assert.Equal("not-in-the-catalog", unknown.CanonicalItemId);
        Assert.Equal(StashPlanGroup.Review, unknown.Group);
        Assert.Contains("stash.recommendation.unresolved", unknown.ReasonCodes);
    }

    [Fact]
    public async Task ASnapshotOpenedTheNextDayIsStillSortedOnItsFootprints()
    {
        // The tile was read a day before the plan is made; the prices are an hour old.
        var sorted = await SortAsync([Tile(0, 0, "gpu", 2, 1, readHoursAgo: 26)]);

        Assert.Equal(StashPlanGroup.Sell, Assert.Single(sorted.Plan.Items).Group);
    }

    private static async Task<StashSortPlan> SortAsync(
        StashReconstructedTile[] tiles,
        QuestItemRequirement[]? quests = null,
        QuestSummaryReadModel[]? board = null,
        Func<ProfileProgress, ProfileProgress>? progress = null,
        Func<string, StashSpecialistIntelligenceKind>? specialist = null)
    {
        var catalog = new LootScanFactFixtures.Catalog();
        var source = new StashPlanSource(new LootScanRecommendationSource(
            catalog,
            catalog,
            new LootScanNeedSource(
                new LootScanFactFixtures.Profiles(),
                new ProfileNeedAggregationService(quests ?? [], []),
                new LootScanFactFixtures.Quests(board ?? []),
                new LootScanFactFixtures.Requirements()),
            new LootScanRaidContextSource(new RaidStateService(), new LootScanFactFixtures.NoMaps(), new LootScanRaidPreference())));
        var profile = Profile(Context(Id(283), "generation-a", ProfileGameMode.Pvp), "unrelated");
        profile = new(profile.Context, profile.Name, progress?.Invoke(profile.Progress) ?? profile.Progress, profile.Lifecycle, Now.AddDays(-1));
        return await source.BuildAsync(
            new(
                [new("stash", 4, 10, tiles)],
                0,
                tiles.Count(tile => tile.IsKnown),
                tiles.Count(tile => !tile.IsKnown),
                new Dictionary<string, int>(StringComparer.Ordinal)),
            "snapshot-283",
            profile,
            specialist ?? (_ => StashSpecialistIntelligenceKind.None),
            Now,
            CancellationToken.None);
    }

    private static StashReconstructedTile Tile(int row, int column, string? itemId, int width, int height, int readHoursAgo = 0) => new(
        "stash",
        row,
        column,
        width,
        height,
        itemId,
        itemId,
        1,
        [],
        new EvidenceProvenance(
            EvidenceSourceClass.GameWrittenScreenshot,
            "fixture://stash",
            Now.AddHours(-readHoursAgo),
            new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.95),
            new ProducerIdentity("fixture", "1")));

    private static QuestSummaryReadModel Quest(string id, string name) => new(
        id,
        name,
        null,
        null,
        RecordedTaskState.Active,
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
}
