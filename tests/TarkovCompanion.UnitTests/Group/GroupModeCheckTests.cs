using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Group;

/// <summary>[#269] A group whose members play different game modes.</summary>
public sealed class GroupModeCheckTests
{
    [Theory]
    [InlineData("pvp", "pve", true)]
    [InlineData("pvp", "seasonal", true)]
    [InlineData("pvp", "PVP", false)]
    [InlineData("pvp", "Regular", false)]
    [InlineData("pvp", null, false)]
    [InlineData(null, "pve", false)]
    [InlineData("pvp", "made-up", false)]
    public void OnlyTwoKnownModesThatDifferCount(string? own, string? theirs, bool differs) =>
        Assert.Equal(differs, GroupModeCheck.Differs(own, theirs));

    [Fact]
    public void AMemberOnAnotherModeLosesTheirQuestsAndNothingElse()
    {
        var members = new[] { Member("Sam", "pve"), Member("Ann", "pvp"), Member("Old", null) };

        var separated = GroupModeCheck.Separate("pvp", members);

        Assert.Empty(separated[0].QuestIds);
        Assert.Empty(separated[0].Quests);
        Assert.Empty(separated[0].Objectives);
        Assert.Equal("customs", separated[0].MapId);
        Assert.Equal(["debut"], separated[1].QuestIds);
        // A companion that sends no mode is not assumed to differ.
        Assert.Equal(["debut"], separated[2].QuestIds);
    }

    [Fact]
    public void TheWarningIsOneLine()
    {
        Assert.Null(GroupModeCheck.Warning("pvp", [Member("Ann", "pvp")]));
        Assert.Equal(
            "Sam is on PvE; this profile is PvP. Quests are not shared across modes.",
            GroupModeCheck.Warning("pvp", [Member("Sam", "pve"), Member("Ann", "pvp")]));
        Assert.Equal(
            "Sam and Bo are on another mode; this profile is PvP. Quests are not shared across modes.",
            GroupModeCheck.Warning("pvp", [Member("Sam", "pve"), Member("Bo", "seasonal")]));
    }

    [Fact]
    public void AWarningIsSaidOnceUntilSomebodyNewArrives()
    {
        var warnings = new GroupModeWarnings();
        var group = new[] { Member("Sam", "pve"), Member("Ann", "pvp") };

        Assert.NotNull(warnings.Current("pvp", group));
        warnings.Dismiss("pvp", group);
        Assert.Null(warnings.Current("pvp", group));

        var joined = group.Append(Member("Bo", "pve")).ToArray();
        Assert.Equal("Bo is on PvE; this profile is PvP. Quests are not shared across modes.", warnings.Current("pvp", joined));

        // A switch to a PvE profile turns Ann into the one on another mode.
        Assert.Equal("Ann is on PvP; this profile is PvE. Quests are not shared across modes.", warnings.Current("pve", group));
    }

    private static GroupMemberView Member(string name, string? mode) =>
        new(name, "customs", RaidLifecycleState.InRaid, null, null, null, null, [], ["Debut"])
        {
            QuestIds = ["debut"],
            Objectives = [new GroupObjectiveView("debut", "shoot", 1)],
            GameMode = mode,
        };
}
