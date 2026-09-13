using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What the group panel beside the map says about each person in it.
/// </summary>
/// <remarks>
/// A marker can only say where somebody is. The things actually asked out loud mid-raid are
/// whether they are even on this map, how old that position is, and what they are carrying,
/// and each of those is a sentence rather than a dot.
/// </remarks>
public sealed class MapGroupPanelTests
{
    private static readonly IReadOnlyList<MapLocation> Locations =
    [
        new("ground-zero", null, "Ground Zero", null, null, []),
        new("customs", null, "Customs", null, null, []),
    ];

    [Fact]
    public void NamesTheMapRatherThanItsSlug()
    {
        var row = MapViewModel.Describe(
            Member("Nate", "ground-zero", RaidLifecycleState.InRaid, side: "PMC"),
            isHere: true,
            Locations);

        Assert.Equal("Nate", row.Name);
        Assert.Equal("Ground Zero · In raid · PMC", row.Where);
        Assert.False(row.IsElsewhere);
    }

    [Fact]
    public void MarksSomebodyOnAnotherMapAsElsewhere()
    {
        var row = MapViewModel.Describe(
            Member("Nate", "customs", RaidLifecycleState.InRaid),
            isHere: false,
            Locations);

        Assert.True(row.IsElsewhere);
        Assert.Equal("Customs · In raid", row.Where);
    }

    /// <summary>A map the catalog has never heard of is still named, with what it has.</summary>
    [Fact]
    public void FallsBackToTheSlugForAMapItDoesNotKnow()
    {
        var row = MapViewModel.Describe(
            Member("Nate", "labyrinth", RaidLifecycleState.InRaid),
            isHere: false,
            Locations);

        Assert.Equal("labyrinth · In raid", row.Where);
    }

    [Fact]
    public void SaysHowOldThePositionIs()
    {
        var fresh = MapViewModel.Describe(
            Member("Nate", "ground-zero", RaidLifecycleState.InRaid, age: TimeSpan.FromSeconds(18)),
            isHere: true,
            Locations);
        Assert.Equal("112, -44 · 18s ago", fresh.Position);
        Assert.False(fresh.IsStale);

        var old = MapViewModel.Describe(
            Member("Nate", "ground-zero", RaidLifecycleState.InRaid, age: TimeSpan.FromMinutes(8)),
            isHere: true,
            Locations);
        Assert.Equal("112, -44 · 8m ago", old.Position);
        Assert.True(old.IsStale);
    }

    /// <summary>
    /// Somebody who has shared nothing is stale, not fresh.
    /// </summary>
    /// <remarks>
    /// An unknown age drawn as though it were current is the one reading that would send
    /// somebody to a place nobody has been for ten minutes.
    /// </remarks>
    [Fact]
    public void TreatsAnUnknownPositionAsStale()
    {
        var row = MapViewModel.Describe(
            Member("Nate", null, RaidLifecycleState.Menu, hasPosition: false),
            isHere: false,
            Locations);

        Assert.Equal("Menu", row.Where);
        Assert.Equal("No position shared", row.Position);
        Assert.True(row.IsStale);
        Assert.True(row.IsElsewhere);
        Assert.False(row.HasExtra);
    }

    [Fact]
    public void CarriesLoadoutAndQuestsWhereTheyAreShared()
    {
        var row = MapViewModel.Describe(
            Member("Nate", "ground-zero", RaidLifecycleState.InRaid) with
            {
                Loadout = ["6B43", "M4A1"],
                Quests = ["Debut"],
            },
            isHere: true,
            Locations);

        Assert.True(row.HasExtra);
        Assert.Equal("6B43 · M4A1 · Debut", row.Extra);
    }

    private static GroupMemberView Member(
        string name,
        string? mapId,
        RaidLifecycleState state,
        string? side = null,
        TimeSpan? age = null,
        bool hasPosition = true) =>
        new(
            name,
            mapId,
            state,
            side,
            hasPosition ? new WorldPosition(112.4, 3, -44.2) : (WorldPosition?)null,
            null,
            age,
            [],
            []);
}
