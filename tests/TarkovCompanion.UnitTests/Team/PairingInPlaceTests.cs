using System.Net;
using TarkovCompanion.App.ViewModels.V2.Tablet;

namespace TarkovCompanion.UnitTests.Team;

/// <summary>
/// What the pairing panel says, and why it says it.
/// </summary>
/// <remarks>
/// [V2 rough package 48] Clayton hit three things at once: pairing arrived as a bare popout window,
/// the admin-key box never said what an admin key was, and "Start pairing" failed with "The relay
/// would not accept a new pairing invitation. Try again shortly." — which was true of every cause
/// and useful for one. These pin the messages, because a message that names the wrong cause costs
/// an evening.
/// </remarks>
public sealed class PairingInPlaceTests
{
    [Fact]
    public void TheAdminKeyHelpSaysWhatItIsAndWhoSetsIt()
    {
        // The answer to "what is the admin key" is a variable name on a server, so the line says
        // the variable name and who sets it, and what claiming buys.
        Assert.Contains("TARKOV_RELAY_ADMIN_KEY", CompanionPairingViewModel.AdminKeyHelp, StringComparison.Ordinal);
        Assert.Contains("operator", CompanionPairingViewModel.AdminKeyHelp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("owner", CompanionPairingViewModel.AdminKeyHelp, StringComparison.OrdinalIgnoreCase);
        Assert.True(CompanionPairingViewModel.AdminKeyHelp.Length < 260, "one line, not a lecture");
    }

    [Fact]
    public void ARelayThatCannotBeClaimedNamesWhatTheOperatorMustSet()
    {
        // The state that produced the complaint: /admin/relay/claim answers 501 before it reads the
        // admin key, so no desktop can ever own this relay. Retyping and retrying cannot fix it.
        Assert.Contains(
            "TARKOV_RELAY_OWNER_RECOVERY_SECRET",
            CompanionPairingViewModel.NotConfiguredForClaimingMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Try again", CompanionPairingViewModel.NotConfiguredForClaimingMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // A clock difference: retrying changes nothing until a clock moves, so it says which clocks.
    [InlineData("offer-future-dated", "clock", false)]
    [InlineData("offer-expired", "clock", false)]
    // A build mismatch: the relay could not read what this desktop generated.
    [InlineData("pairing-code-malformed", "build", false)]
    // A duplicate or a full table: a *new* invitation can help, so it says to make one.
    [InlineData("attempt-duplicate", "new one", false)]
    [InlineData("invitation-limit", "expire", false)]
    public void EachRefusalReasonIsSaidInItsOwnWords(string code, string expected, bool suggestsBlindRetry)
    {
        var said = CompanionPairingViewModel.DescribeOfferRefusal(
            new CompanionPairingViewModel.RelayRefusal(HttpStatusCode.BadRequest, code));

        Assert.Contains(expected, said, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(suggestsBlindRetry, said.Contains("Try again shortly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnlyRateLimitingSuggestsWaiting()
    {
        // The one cause where waiting is the answer is the one that says to wait.
        var limited = CompanionPairingViewModel.DescribeOfferRefusal(
            new CompanionPairingViewModel.RelayRefusal(HttpStatusCode.TooManyRequests, string.Empty));

        Assert.Contains("Try again in a minute", limited, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelayTooOldToReadTheInvitationIsNamedAsSuch()
    {
        // The relay answers a body it could not parse with an English sentence rather than a code;
        // that shape is what an older relay looks like to a newer desktop.
        var said = CompanionPairingViewModel.DescribeOfferRefusal(
            new CompanionPairingViewModel.RelayRefusal(HttpStatusCode.BadRequest, "A pairing offer is required."));

        Assert.Contains("older build", said, StringComparison.Ordinal);
        Assert.Contains("Update the relay", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCodeIsRepeatedRatherThanSwallowed()
    {
        // Whatever the relay says reaches the screen, so the next new cause is diagnosable from
        // the desktop instead of only from the relay's journal.
        var said = CompanionPairingViewModel.DescribeOfferRefusal(
            new CompanionPairingViewModel.RelayRefusal(HttpStatusCode.BadRequest, "something-new"));

        Assert.Contains("something-new", said, StringComparison.Ordinal);
    }
}
