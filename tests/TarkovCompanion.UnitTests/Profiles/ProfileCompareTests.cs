using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Profiles;

/// <summary>[#269] Setup › Game &amp; Profile's compare: what each side counts.</summary>
public sealed class ProfileCompareTests
{
    private static readonly HashSet<string> Keys = ["key-dorm-314", "key-factory"];
    private static readonly HashSet<string> Ammo = ["m855a1", "ps"];
    private static readonly AmmoPackContents[] Packs =
    [
        new("pack-m855a1", "m855a1", 30, new DataProvenance("test", ProfileV2Fixtures.Now)),
        new("pack-junk", "not-ammo", 99, new DataProvenance("test", ProfileV2Fixtures.Now)),
    ];

    [Fact]
    public void EachSideCountsItsOwnProgressQuestsItemsAndRaids()
    {
        var record = ProfileV2Fixtures.Profile(1, "g1", ProfileGameMode.Pve, "gpu", wipe: "Wipe 3");
        var id = record.Context.Identity.ProfileId;
        var player = Player(id) with
        {
            Level = 37,
            CompletedTaskIds = new HashSet<string> { "debut", "checking" },
            HideoutStationLevels = new Dictionary<string, int> { ["lavatory"] = 2, ["medstation"] = 3, ["gym"] = 0 },
            OwnedItemCounts = new Dictionary<string, int>
            {
                ["key-dorm-314"] = 2,
                ["key-factory"] = 0,
                ["m855a1"] = 60,
                ["ps"] = 15,
                ["pack-m855a1"] = 2,
                ["pack-junk"] = 5,
                ["gpu"] = 1,
            },
        };
        var quests = new QuestProgressSnapshot(
            new QuestProfileScope(id, GameMode.Pve, "g1"),
            1,
            new Dictionary<string, RecordedTaskProgress>
            {
                // Done in both records: counted once.
                ["debut"] = new("debut", RecordedTaskState.Completed, "Log", 1, ProfileV2Fixtures.Now),
                ["shortage"] = new("shortage", RecordedTaskState.Completed, "Log", 1, ProfileV2Fixtures.Now),
                ["gunsmith"] = new("gunsmith", RecordedTaskState.Active, "Log", 1, ProfileV2Fixtures.Now),
            },
            new Dictionary<string, RecordedObjectiveProgress>(),
            [],
            []);
        RaidHistoryEntry[] raids =
        [
            new(Guid.NewGuid(), id, "customs", "Pve", null, null, "Survived", null),
            new(Guid.NewGuid(), id, "woods", "Pve", null, null, "Run through", null),
            new(Guid.NewGuid(), id, "factory", "Pve", null, null, "Killed", null),
            new(Guid.NewGuid(), id, "reserve", "Pve", null, null, null, null),
            new(Guid.NewGuid(), Guid.NewGuid(), "customs", "Regular", null, null, "Survived", null),
        ];

        var side = ProfileCompareService.Summarize(record, player, quests, raids, Keys, Ammo, Packs);

        Assert.Equal(ProfileGameMode.Pve, side.Mode);
        Assert.Equal("Wipe 3", side.Wipe);
        Assert.Equal(37, side.Level);
        Assert.Equal(3, side.QuestsCompleted);
        Assert.Equal(5, side.HideoutLevels);
        Assert.Equal(1, side.KeysOwned);
        Assert.Equal(60 + 15 + (2 * 30), side.AmmoRounds);
        Assert.Equal(4, side.Raids);
        Assert.Equal(2, side.Survived);
        Assert.Equal(1, side.Died);
    }

    [Fact]
    public async Task TheCompareStartsWithTheActiveProfileBesideAnotherAndSaysWhenModesDiffer()
    {
        var pvp = ProfileV2Fixtures.Profile(1, "g1", ProfileGameMode.Pvp, "gpu");
        var pve = ProfileV2Fixtures.Profile(2, "g2", ProfileGameMode.Pve, "gpu");
        var workspace = new ProfileWorkspaceSnapshot(3, pve.Context.Identity.ProfileId, [pvp, pve]);
        var compared = new List<(Guid, Guid)>();
        var model = new SetupProfileCompareViewModel((left, right, _) =>
        {
            compared.Add((left.Context.Identity.ProfileId, right.Context.Identity.ProfileId));
            return Task.FromResult((Side(left, 20), Side(right, 31)));
        });

        await model.SetProfilesAsync(workspace);

        Assert.Equal((pve.Context.Identity.ProfileId, pvp.Context.Identity.ProfileId), compared.Last());
        var level = model.Rows.Single(row => row.Label == "Level");
        Assert.Equal(("20", "31", true), (level.Left, level.Right, level.Differs));
        Assert.Equal("Different game modes: their progress is kept apart.", model.Status);
    }

    private static ProfileCompareSide Side(ProfileRecord record, int level) => new(
        record.Context.Identity.ProfileId, record.Name, record.Context.Mode, record.Context.WipeSeason.Value,
        level, 0, 0, 0, 0, 0, 0, 0);

    private static PlayerProfile Player(Guid id) => new(
        id, "p", GameMode.Pve, 1, Faction.Unknown, null,
        new Dictionary<string, int>(), new HashSet<string>(), new Dictionary<string, int>(), new Dictionary<string, int>(),
        new HashSet<string>(), new Dictionary<string, int>(), new Dictionary<string, EventItemState>(),
        new Dictionary<string, string>(), ProfileV2Fixtures.Now, "g1");
}
