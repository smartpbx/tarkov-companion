using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.UnitTests.Planning;

public sealed class HideoutUpgradePlannerTests
{
    private static readonly HideoutStationSummary[] Stations =
    [
        new("generator", "Generator", [1, 2, 3]),
        new("vents", "Vents", [1, 2]),
        new("farm", "Bitcoin Farm", [1, 2]),
        new("stash", "Stash", [1]),
    ];

    private static readonly HideoutItemRequirement[] Requirements =
    [
        new("generator", 1, "bolts", 2),
        new("generator", 2, "bolts", 3),
        new("generator", 2, "cable", 1),
        new("vents", 1, "bolts", 4),
        new("farm", 1, "gpu", 5),
        new("farm", 2, "gpu", 10),
        new("stash", 1, "roubles", 100),
    ];

    private static readonly HideoutPrerequisites Prerequisites = new(
        [
            new("farm", 1, "generator", 2),
            new("farm", 1, "vents", 1),
            new("vents", 1, "generator", 1),
            new("farm", 2, "generator", 3),
        ],
        [new("farm", 1, "Mechanic loyalty 2")])
    {
        ConstructionTimes =
        [
            new("generator", 1, TimeSpan.FromHours(2)),
            new("generator", 2, TimeSpan.FromHours(4)),
            new("vents", 1, TimeSpan.FromMinutes(30)),
            new("farm", 1, TimeSpan.FromDays(1)),
        ],
    };

    private static readonly Dictionary<string, int> Nothing = new(StringComparer.Ordinal);

    [Fact]
    public void ThePathBuildsPrerequisitesAndLowerLevelsFirst()
    {
        var path = HideoutUpgradePlanner.CriticalPath(Stations, Nothing, Requirements, Prerequisites, Nothing, "farm", 1);

        Assert.Equal(
            ["generator 1", "generator 2", "vents 1", "farm 1"],
            path.Select(step => $"{step.StationId} {step.Level}"));
        Assert.Equal(["Mechanic loyalty 2"], path[^1].AlsoNeeds);
        Assert.Equal(TimeSpan.FromHours(2), path[0].ConstructionTime);
        Assert.Equal(TimeSpan.FromHours(30.5), TimeSpan.FromTicks(path.Sum(step => step.ConstructionTime!.Value.Ticks)));
    }

    [Fact]
    public void ThePathLeavesOutWhatIsBuiltAndNeverRepeatsALevel()
    {
        var built = new Dictionary<string, int> { ["generator"] = 2, ["vents"] = 1 };

        var path = HideoutUpgradePlanner.CriticalPath(Stations, built, Requirements, Prerequisites, Nothing, "farm", 2);

        Assert.Equal(
            ["farm 1", "generator 3", "farm 2"],
            path.Select(step => $"{step.StationId} {step.Level}"));
    }

    [Fact]
    public void ABuiltTargetOrALevelTheCatalogDoesNotListHasNoPath()
    {
        var built = new Dictionary<string, int> { ["stash"] = 1 };

        Assert.Empty(HideoutUpgradePlanner.CriticalPath(Stations, built, Requirements, Prerequisites, Nothing, "stash", 1));
        Assert.Empty(HideoutUpgradePlanner.CriticalPath(Stations, built, Requirements, Prerequisites, Nothing, "stash", 4));
        Assert.Empty(HideoutUpgradePlanner.CriticalPath(Stations, built, Requirements, Prerequisites, Nothing, "nowhere", 1));
    }

    [Fact]
    public void APrerequisiteCycleEndsInsteadOfLooping()
    {
        var cycle = new HideoutPrerequisites(
            [new("generator", 1, "vents", 1), new("vents", 1, "generator", 1)],
            []);

        var path = HideoutUpgradePlanner.CriticalPath(Stations, Nothing, Requirements, cycle, Nothing, "generator", 1);

        Assert.Equal(["vents 1", "generator 1"], path.Select(step => $"{step.StationId} {step.Level}"));
    }

    [Fact]
    public void NextUpgradesComeInTheOrderTheyBecomePossible()
    {
        // Round one: generator 1 and stash 1 need no other station. Round two: generator 2 and
        // vents 1, the one with fewer items to find first. The farm only opens in round three,
        // after both, behind the two levels that ask for no items at all.
        var next = HideoutUpgradePlanner.NextUpgrades(Stations, Nothing, Requirements, Prerequisites, Nothing, 7);

        Assert.Equal(
            ["generator 1", "stash 1", "vents 1", "generator 2", "generator 3", "vents 2", "farm 1"],
            next.Select(step => $"{step.StationId} {step.Level}"));
    }

    [Fact]
    public void NextUpgradesStopWhenNothingElseCanBeBuilt()
    {
        var blocked = new HideoutPrerequisites([new("stash", 1, "farm", 2)], []);
        HideoutStationSummary[] only = [new("stash", "Stash", [1])];

        Assert.Empty(HideoutUpgradePlanner.NextUpgrades(only, Nothing, Requirements, blocked, Nothing, 3));
    }

    [Fact]
    public void TheShoppingListCountsAnItemOnceAcrossStepsAndKeepsUnknownApartFromNone()
    {
        var owned = new Dictionary<string, int>(StringComparer.Ordinal) { ["bolts"] = 4, ["cable"] = 1 };
        var path = HideoutUpgradePlanner.CriticalPath(Stations, Nothing, Requirements, Prerequisites, owned, "farm", 1);

        var list = HideoutUpgradePlanner.ShoppingList(path, owned);

        // bolts: 2 + 3 + 4 = 9 against 4 held. cable is covered and leaves the list. gpu was never counted.
        Assert.Equal(["bolts", "gpu"], list.Select(line => line.ItemId));
        Assert.Equal((9, 4, 5, 3), (list[0].Need, list[0].Have, list[0].Remaining, list[0].Steps));
        Assert.Null(list[1].Have);
        Assert.Equal(5, list[1].Remaining);
    }

    [Fact]
    public void EveryNextLevelIgnoresWhetherTheLevelCanBeStarted()
    {
        var built = new Dictionary<string, int> { ["stash"] = 1 };

        var steps = HideoutUpgradePlanner.EveryNextLevel(Stations, built, Requirements, Prerequisites, Nothing);

        Assert.Equal(["farm 1", "generator 1", "vents 1"], steps.Select(step => $"{step.StationId} {step.Level}"));
    }
}
