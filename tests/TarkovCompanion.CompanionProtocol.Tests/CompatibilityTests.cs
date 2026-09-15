using static TarkovCompanion.CompanionProtocol.Tests.ProtocolTestData;

namespace TarkovCompanion.CompanionProtocol.Tests;

public sealed class CompatibilityTests
{
    private static readonly DateTimeOffset Sunset = new(2027, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(2, 0, 2, 3, 2, 0, 2, 1, "Compatible", 1, null)]
    [InlineData(2, 0, 2, 0, 2, 0, 2, 0, "Compatible", 0, null)]
    [InlineData(2, 2, 2, 3, 2, 0, 2, 1, "UpgradeDesktop", null, "UpdateDesktop")]
    [InlineData(2, 0, 2, 0, 2, 1, 2, 2, "UpgradeClient", null, "UpdateTablet")]
    [InlineData(1, 0, 1, 9, 2, 0, 2, 0, "NoSharedMajor", null, "UpdateTablet")]
    [InlineData(3, 0, 3, 1, 2, 0, 2, 0, "NoSharedMajor", null, "UpdateDesktop")]
    public void NegotiationSelectsTheHighestSharedMinorOrAnExplicitRecovery(
        int clientMinMajor,
        int clientMinMinor,
        int clientMaxMajor,
        int clientMaxMinor,
        int desktopMinMajor,
        int desktopMinMinor,
        int desktopMaxMajor,
        int desktopMaxMinor,
        string disposition,
        int? negotiatedMinor,
        string? recovery)
    {
        var hello = ProtocolCompatibility.Negotiate(
            new ClientHello(
                new ProtocolVersionRange(new CompanionProtocolVersion(clientMinMajor, clientMinMinor), new CompanionProtocolVersion(clientMaxMajor, clientMaxMinor)),
                "tablet-a",
                []),
            new ProtocolVersionRange(new CompanionProtocolVersion(desktopMinMajor, desktopMinMinor), new CompanionProtocolVersion(desktopMaxMajor, desktopMaxMinor)),
            Now);

        Assert.Equal(Enum.Parse<CompatibilityDisposition>(disposition), hello.Disposition);
        Assert.Equal(negotiatedMinor, hello.NegotiatedVersion?.Minor);
        Assert.Equal(recovery is null ? (CompatibilityRecoveryAction?)null : Enum.Parse<CompatibilityRecoveryAction>(recovery), hello.RecoveryAction);
    }

    [Fact]
    public void ADeprecatedMinorIsAnnouncedBeforeSunsetAndUnsupportedAfterIt()
    {
        var client = new ClientHello(
            new ProtocolVersionRange(new CompanionProtocolVersion(2, 0), new CompanionProtocolVersion(2, 0)),
            "old-tablet",
            []);
        var desktop = new ProtocolVersionRange(new CompanionProtocolVersion(2, 0), new CompanionProtocolVersion(2, 1));
        var notice = new ProtocolDeprecationNotice(
            new CompanionProtocolVersion(2, 0),
            Sunset,
            new CompanionProtocolVersion(2, 1),
            CompatibilityRecoveryAction.UpdateTablet,
            "Protocol 2.0 is replaced by 2.1.");

        var before = ProtocolCompatibility.Negotiate(client, desktop, Sunset.AddDays(-1), notice);
        var after = ProtocolCompatibility.Negotiate(client, desktop, Sunset, notice);
        var upgraded = ProtocolCompatibility.Negotiate(
            new ClientHello(new ProtocolVersionRange(new CompanionProtocolVersion(2, 0), new CompanionProtocolVersion(2, 1)), "new-tablet", []),
            desktop,
            Sunset,
            notice);
        var desktopPastSunset = ProtocolCompatibility.Negotiate(
            client,
            new ProtocolVersionRange(new CompanionProtocolVersion(2, 0), new CompanionProtocolVersion(2, 0)),
            Sunset,
            notice);

        Assert.Equal(CompatibilityDisposition.Compatible, before.Disposition);
        Assert.Equal(notice, before.Deprecation);
        Assert.Equal(CompatibilityDisposition.UpgradeClient, after.Disposition);
        Assert.Equal(CompatibilityRecoveryAction.UpdateTablet, after.RecoveryAction);
        Assert.Equal(new CompanionProtocolVersion(2, 1), upgraded.NegotiatedVersion);
        Assert.Null(upgraded.Deprecation);
        Assert.Equal(CompatibilityDisposition.UpgradeDesktop, desktopPastSunset.Disposition);
    }

    [Fact]
    public void HelloAndDeprecationShapesCannotContradictThemselves()
    {
        var desktop = ProtocolVersionRange.Current;

        Assert.Throws<ArgumentException>(() => new ServerHello(CompatibilityDisposition.Compatible, null, desktop, null, null));
        Assert.Throws<ArgumentException>(() => new ServerHello(CompatibilityDisposition.Compatible, CompanionProtocolVersion.Current, desktop, null, CompatibilityRecoveryAction.UpdateTablet));
        Assert.Throws<ArgumentException>(() => new ServerHello(CompatibilityDisposition.Compatible, new CompanionProtocolVersion(2, 5), desktop, null, null));
        Assert.Throws<ArgumentException>(() => new ServerHello(CompatibilityDisposition.UpgradeClient, CompanionProtocolVersion.Current, desktop, null, CompatibilityRecoveryAction.UpdateTablet));
        Assert.Throws<ArgumentException>(() => new ProtocolDeprecationNotice(
            new CompanionProtocolVersion(2, 1), Sunset, new CompanionProtocolVersion(2, 1), CompatibilityRecoveryAction.UpdateTablet, "same"));
        Assert.Throws<ArgumentException>(() => new ProtocolDeprecationNotice(
            new CompanionProtocolVersion(2, 1), Sunset, new CompanionProtocolVersion(3, 0), CompatibilityRecoveryAction.UpdateTablet, "new major"));
        Assert.Throws<ArgumentException>(() => new ProtocolVersionRange(new CompanionProtocolVersion(2, 1), new CompanionProtocolVersion(2, 0)));
        Assert.True(CompanionProtocolVersion.Current.CanRead(new CompanionProtocolVersion(2, 0)));
        Assert.False(CompanionProtocolVersion.Current.CanRead(new CompanionProtocolVersion(2, 1)));
        Assert.False(CompanionProtocolVersion.Current.CanRead(new CompanionProtocolVersion(3, 0)));
    }
}
