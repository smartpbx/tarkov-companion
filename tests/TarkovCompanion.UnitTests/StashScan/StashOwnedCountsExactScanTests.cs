using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Profile;

namespace TarkovCompanion.UnitTests.StashScan;

/// <summary>An exact scan answers for the needed items it did not see; anything less leaves "?".</summary>
public sealed class StashOwnedCountsExactScanTests
{
    [Fact]
    public async Task AnExactScanRecordsNoneHeldForNeededItemsNobodyHasCounted()
    {
        var profiles = new Profiles(new Dictionary<string, int>(StringComparer.Ordinal) { ["cable"] = 3 });
        var applier = new StashOwnedCountsApplier(profiles, new Needs(["bolts", "cable", "salewa"], ["gpu", "bolts"]));

        var change = await applier.ApplyAsync(Scan(unknownTiles: 0, unplaced: 0, ("bolts", 4)), CancellationToken.None);

        // bolts was seen. cable has a count somebody recorded, which an unseen item does not
        // overwrite: it may be in a case. salewa and gpu had none and now read 0, not "?".
        Assert.Equal(4, profiles.Saved!.OwnedItemCounts["bolts"]);
        Assert.Equal(3, profiles.Saved.OwnedItemCounts["cable"]);
        Assert.Equal(0, profiles.Saved.OwnedItemCounts["salewa"]);
        Assert.Equal(0, profiles.Saved.OwnedItemCounts["gpu"]);
        Assert.Equal(2, change.RecordedNone);
        Assert.Contains("2 needed and not seen", change.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public async Task AScanThatIsNotExactLeavesTheUnknownUnknown(int unknownTiles, int unplaced)
    {
        var profiles = new Profiles(new Dictionary<string, int>(StringComparer.Ordinal));
        var applier = new StashOwnedCountsApplier(profiles, new Needs(["bolts", "salewa"], []));

        var change = await applier.ApplyAsync(Scan(unknownTiles, unplaced, ("bolts", 4)), CancellationToken.None);

        Assert.False(profiles.Saved!.OwnedItemCounts.ContainsKey("salewa"));
        Assert.Equal(0, change.RecordedNone);
    }

    [Fact]
    public async Task AnExactScanThatOnlyFindsNothingNewStillSavesTheZeros()
    {
        var profiles = new Profiles(new Dictionary<string, int>(StringComparer.Ordinal) { ["bolts"] = 4 });
        var applier = new StashOwnedCountsApplier(profiles, new Needs(["salewa"], []));

        await applier.ApplyAsync(Scan(0, 0, ("bolts", 4)), CancellationToken.None);

        Assert.Equal(0, profiles.Saved!.OwnedItemCounts["salewa"]);
    }

    private static StashReconstruction Scan(int unknownTiles, int unplaced, params (string Id, int Count)[] seen) =>
        new([], unplaced, seen.Length, unknownTiles, seen.ToDictionary(item => item.Id, item => item.Count, StringComparer.Ordinal));

    private sealed class Needs(string[] quest, string[] hideout) : IRequirementCatalog
    {
        public Task<IReadOnlyList<HideoutItemRequirement>> GetHideoutRequirementsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HideoutItemRequirement>>([.. hideout.Select(id => new HideoutItemRequirement("station", 1, id, 1))]);

        public Task<IReadOnlyList<QuestItemRequirement>> GetQuestRequirementsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuestItemRequirement>>([.. quest.Select(id => new QuestItemRequirement("task", "objective", id, 1, false))]);

        public Task<IReadOnlyList<HideoutStationSummary>> GetStationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HideoutStationSummary>>([]);

        public void Invalidate()
        {
        }
    }

    private sealed class Profiles(Dictionary<string, int> owned) : IPlayerProfileService
    {
        public PlayerProfile? Saved { get; private set; }

        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(TestProfile.Create() with { OwnedItemCounts = owned });

        public Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken)
        {
            Saved = profile;
            return Task.CompletedTask;
        }

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
