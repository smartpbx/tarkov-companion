using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Collapsing the group notification stream into a party the player can read.
/// </summary>
public sealed class SquadStateServiceTests
{
    private static readonly DateTimeOffset Observed = new(2026, 9, 11, 23, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A readiness toggle republishes the whole member, hundreds of times a session.
    /// </summary>
    [Fact]
    public void KeepsOneRowPerMemberHoweverOftenTheyAreRestated()
    {
        var service = new SquadStateService();

        for (var toggle = 0; toggle < 50; toggle++)
        {
            service.Apply(Updated(Member(accountId: 9001, nickname: "Kestrel") with
            {
                IsReady = toggle % 2 == 0,
            }));
        }

        var snapshot = service.Current;
        Assert.Single(snapshot.Members);
        Assert.Equal("Kestrel", snapshot.Members[0].Nickname);
        Assert.False(snapshot.Members[0].IsReady);
    }

    /// <summary>
    /// A later, thinner notification must not erase what a fuller one already established.
    /// </summary>
    [Fact]
    public void KeepsFactsAnEarlierNotificationStated()
    {
        var service = new SquadStateService();
        service.Apply(Updated(Member(accountId: 9001, nickname: "Kestrel") with
        {
            Side = "Bear",
            Level = 42,
            IsLeader = true,
        }));

        service.Apply(Updated(new GroupMember(null, 9001, null, null, null, null, true, null, [])));

        var member = Assert.Single(service.Current.Members);
        Assert.Equal("Kestrel", member.Nickname);
        Assert.Equal("Bear", member.Side);
        Assert.Equal(42, member.Level);
        Assert.True(member.IsLeader);
        Assert.True(member.IsReady);
    }

    [Fact]
    public void DropsAMemberWhoLeaves()
    {
        var service = new SquadStateService();
        service.Apply(Updated(Member(accountId: 9001, nickname: "Kestrel")));
        service.Apply(Updated(Member(accountId: 9002, nickname: "Auger")));

        service.Apply(new GroupObservation(
            GroupObservationKind.MemberLeft,
            "groupMatchUserLeave",
            Observed,
            Member(accountId: 9001, nickname: null)));

        var member = Assert.Single(service.Current.Members);
        Assert.Equal("Auger", member.Nickname);
    }

    /// <summary>
    /// The leader is what a player looks for first, so the leader is listed first.
    /// </summary>
    [Fact]
    public void ListsTheLeaderFirst()
    {
        var service = new SquadStateService();
        service.Apply(Updated(Member(accountId: 9002, nickname: "Auger")));
        service.Apply(Updated(Member(accountId: 9001, nickname: "Kestrel") with { IsLeader = true }));

        Assert.Equal("Kestrel", service.Current.Members[0].Nickname);
    }

    [Fact]
    public void RecordsWhenThePartyQueuedTogether()
    {
        var service = new SquadStateService();

        var snapshot = service.Apply(new GroupObservation(
            GroupObservationKind.MatchStarting,
            "groupMatchRaidReady",
            Observed,
            Member: null,
            GroupId: "party-1",
            QueueEstimate: TimeSpan.FromSeconds(90)));

        Assert.Equal(Observed, snapshot.MatchStartedUtc);
        Assert.Equal(TimeSpan.FromSeconds(90), snapshot.QueueEstimate);
    }

    [Fact]
    public void ForgetsThePartyWhenObservationRestarts()
    {
        var service = new SquadStateService();
        service.Apply(Updated(Member(accountId: 9001, nickname: "Kestrel")));

        var snapshot = service.Clear();

        Assert.Empty(snapshot.Members);
        Assert.Equal(SquadSnapshot.Empty, snapshot);
    }

    private static GroupObservation Updated(GroupMember member) => new(
        GroupObservationKind.MemberUpdated,
        "groupMatchUserReady",
        Observed,
        member);

    private static GroupMember Member(long accountId, string? nickname) =>
        new(null, accountId, nickname, null, null, null, null, null, []);
}
