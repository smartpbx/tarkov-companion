using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>[#403] The brief's squad line from the game's own party list, and its spawning kicker.</summary>
public sealed class PreRaidBriefPartyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 20, 14, 7, TimeSpan.Zero);

    private static Situation At(SituationPhase phase) =>
        new(7, Now, new SituationFact<SituationPhase>(phase, new Confidence(0.9), SituationSource.GameLog, Now, "Because."))
        {
            Map = new SituationFact<string>("customs", new Confidence(0.9), SituationSource.GameLog, Now, "The log named the map."),
        };

    private static readonly SituationParty Party = new(
        [new("PLAYER_B", true, true), new("PLAYER_C", false, false), new("PLAYER_D", null, false)],
        1,
        Now,
        "The game's group notifications.");

    [Fact]
    public void WithoutCompanionsTheSquadLineSaysWhoIsReadyFromTheGamesPartyList()
    {
        var brief = PreRaidBriefBuilder.Build(new PreRaidBriefInputs(At(SituationPhase.Matching) with { Party = Party }));

        Assert.Equal(new[] { "PLAYER_B · ready", "PLAYER_C · not ready", "PLAYER_D" }, brief.Squad);
        Assert.Equal("from the game's party list", brief.SquadFrom);
    }

    [Fact]
    public void CompanionsStillComeFirstAndAreLabelledAsSuch()
    {
        var situation = At(SituationPhase.Matching) with
        {
            Party = Party,
            Squad = [new SituationSquadMember("Geo", SquadMemberState.OutOfRaid, null, null, null, null, null, "relay")],
        };

        var brief = PreRaidBriefBuilder.Build(new PreRaidBriefInputs(situation));

        Assert.Equal(new[] { "Geo · not in raid" }, brief.Squad);
        Assert.Equal("from their companions", brief.SquadFrom);
    }

    [Fact]
    public void OnceTheGameIsSpawningTheKickerSaysSo()
    {
        var loading = At(SituationPhase.Loading) with
        {
            Stages = [new RaidPhaseMarker(RaidPhaseMarkerKind.MatchingStarted, Now), new RaidPhaseMarker(RaidPhaseMarkerKind.Spawning, Now)],
        };

        Assert.Equal("BRIEF · SPAWNING", PreRaidBriefBuilder.Build(new PreRaidBriefInputs(loading)).Kicker);
        Assert.Equal("BRIEF · WHILE IT LOADS", PreRaidBriefBuilder.Build(new PreRaidBriefInputs(At(SituationPhase.Loading))).Kicker);
    }
}
