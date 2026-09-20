using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.GroupServer.Security;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

/// <summary>The relay's nonces and resume tickets: good once, good briefly, bounded (#289).</summary>
public sealed class RelayKeyPossessionTests
{
    [Fact]
    public void ANonceIsGoodOnceAndOnlyUntilItExpires()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        var challenges = new RelayPossessionChallenges(clock);

        var first = challenges.Issue();
        Assert.True(challenges.TryConsume(first.NonceBase64Url));
        Assert.False(challenges.TryConsume(first.NonceBase64Url));

        var byId = challenges.Issue();
        Assert.Equal(byId.NonceBase64Url, challenges.TryConsume(byId.ChallengeId));
        Assert.Null(challenges.TryConsume(byId.ChallengeId));
        Assert.False(challenges.TryConsume(byId.NonceBase64Url));

        var late = challenges.Issue();
        clock.Advance(ProtocolBounds.HandshakeChallengeLifetime);
        Assert.False(challenges.TryConsume(late.NonceBase64Url));
        Assert.False(challenges.TryConsume("never-issued"));
    }

    [Fact]
    public void OutstandingNoncesAreBoundedAndTheOldestGoesFirst()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        var challenges = new RelayPossessionChallenges(clock);
        var oldest = challenges.Issue();
        clock.Advance(TimeSpan.FromSeconds(1));
        for (var index = 0; index < RelayPossessionChallenges.MaximumOutstanding; index++)
        {
            challenges.Issue();
        }

        Assert.False(challenges.TryConsume(oldest.NonceBase64Url));
    }

    [Fact]
    public void TheDoorChallengeIsTheLabelledHashTheTabletPageComputes()
    {
        // The same vector scripts/test-relay-crypto.mjs pins for relay-crypto.js's deviceDoorChallenge.
        Assert.Equal(
            "-q2dGLxQOuZDew4Vnle_BxIkC34nnaISB0d_sTTFdzc",
            RelayPossessionChallenges.DeviceDoorChallenge("BwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwc"));
    }

    [Fact]
    public void ATicketIsAnsweredOnceAndADeviceHoldsOneAtATime()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        var tickets = new RelayResumeTickets(clock);
        var key = RelaySecurityTestFactory.DeviceKey("possession-ticket").KeyId;

        var earlier = tickets.Open(key);
        var ticket = tickets.Open(key);
        Assert.Null(tickets.Find(earlier.TicketId));
        Assert.Equal(ticket.TicketId, Assert.Single(tickets.Unanswered()).TicketId);

        Assert.True(tickets.Answer(ticket.TicketId, "ABCDE12345"));
        Assert.False(tickets.Answer(ticket.TicketId, "ZZZZZ99999"));
        Assert.Empty(tickets.Unanswered());
        Assert.Equal("ABCDE12345", tickets.Find(ticket.TicketId)!.PairingCode);

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(tickets.Find(ticket.TicketId));
    }
}
