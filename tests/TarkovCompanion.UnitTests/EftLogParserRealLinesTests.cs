using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Parses lines captured verbatim from a real Escape from Tarkov installation.
/// </summary>
/// <remarks>
/// Every earlier test here used log lines invented to match the parser, which is why the
/// parser could match nothing a real game writes and still look correct. These lines came off
/// a live install across 254 raid records, with account ids, addresses and session ids
/// redacted; the map names, lifecycle markers and layout are untouched.
/// </remarks>
public sealed class EftLogParserRealLinesTests
{
    private static readonly DateTimeOffset Observed = DateTimeOffset.UnixEpoch;

    private const string ProfileStatusShoreline =
        "2026-09-11 00:42:46.453|1.1.5.0.47242|Info|output|application|TRACE-NetworkGameCreate " +
        "profileStatus: 'Profileid: [REDACTED], Status: Busy, RaidMode: Online, Ip: [REDACTED], " +
        "Port: 17009, Location: Shoreline, Sid: [REDACTED], GameMode: deathmatch, shortId: [REDACTED]'";

    private const string ProfileStatusStreets =
        "2026-09-11 01:35:21.825|1.1.5.0.47242|Info|output|application|TRACE-NetworkGameCreate " +
        "profileStatus: 'Profileid: [REDACTED], Status: Busy, RaidMode: Online, Ip: [REDACTED], " +
        "Port: 17004, Location: TarkovStreets, Sid: [REDACTED], GameMode: deathmatch, shortId: [REDACTED]'";

    [Fact]
    public void ReadsTheMapAndTheStateFromOneProfileStatusLine()
    {
        var evidence = new EftLogParser().ParseLine(ProfileStatusShoreline, Observed);

        Assert.NotNull(evidence);
        Assert.Equal("shoreline", evidence.MapId);
        Assert.Equal(RaidLifecycleState.InRaid, evidence.SuggestedState);
    }

    [Theory]
    // The game's casing is not consistent between maps: two tokens are lowercase and the rest
    // are capitalized, so the lookup has to be case-insensitive rather than merely lowercased.
    [InlineData("bigmap", "customs")]
    [InlineData("TarkovStreets", "streets-of-tarkov")]
    [InlineData("Woods", "woods")]
    [InlineData("Interchange", "interchange")]
    [InlineData("Shoreline", "shoreline")]
    [InlineData("factory4_day", "factory")]
    [InlineData("Sandbox", "ground-zero")]
    // Ground Zero 21+ is its own location in the map catalog, not an alias of Ground Zero.
    [InlineData("Sandbox_high", "ground-zero-21")]
    [InlineData("Lighthouse", "lighthouse")]
    public void ResolvesEveryMapTokenSeenOnARealInstall(string token, string expected)
    {
        var line = ProfileStatusShoreline.Replace(
            "Location: Shoreline",
            $"Location: {token}",
            StringComparison.Ordinal);

        var evidence = new EftLogParser().ParseLine(line, Observed);

        Assert.NotNull(evidence);
        Assert.Equal(expected, evidence.MapId);
    }

    [Theory]
    [InlineData("2026-09-11 00:42:45.803|1.1.5.0.47242|Info|application|LocationLoaded:18 real:24.73 diff:6.72",
        RaidLifecycleState.LoadingRaid)]
    [InlineData("2026-09-11 00:43:49.714|1.1.5.0.47242|Info|application|GameStarted:75.58(75.58) real:88.64(88.64) diff:13.05",
        RaidLifecycleState.InRaid)]
    [InlineData("2026-08-23 22:58:18.463|1.1.0.1.46911|Info|output|[Narrate] Game Stopped",
        RaidLifecycleState.PostRaid)]
    public void RecognizesTheLifecycleMarkersTheGameActuallyWrites(string line, RaidLifecycleState expected)
    {
        var evidence = new EftLogParser().ParseLine(line, Observed);

        Assert.NotNull(evidence);
        Assert.Equal(expected, evidence.SuggestedState);
    }

    [Fact]
    public void DoesNotMistakeLocationLoadedForAMapName()
    {
        // "LocationLoaded:18" contains the word location followed by a colon, and must not be
        // read as a map called "18".
        var evidence = new EftLogParser().ParseLine(
            "2026-09-11 00:42:45.803|1.1.5.0.47242|Info|application|LocationLoaded:18 real:24.73 diff:6.72",
            Observed);

        Assert.NotNull(evidence);
        Assert.Null(evidence.MapId);
    }

    [Fact]
    public void TreatsTheProfileStatusLineAsStrongerEvidenceThanAPlainMapMention()
    {
        var withState = new EftLogParser().ParseLine(ProfileStatusStreets, Observed);
        var mapOnly = new EftLogParser().ParseLine("Location: TarkovStreets", Observed);

        Assert.NotNull(withState);
        Assert.NotNull(mapOnly);
        Assert.True(withState.Confidence.Value > mapOnly.Confidence.Value);
    }

    [Fact]
    public void IgnoresOrdinaryLinesThatNameNoMapAndNoState() =>
        Assert.Null(new EftLogParser().ParseLine(
            "2026-09-11 00:43:48.564|1.1.5.0.47242|Debug|application|Heap pre-allocation - disabled",
            Observed));
}
