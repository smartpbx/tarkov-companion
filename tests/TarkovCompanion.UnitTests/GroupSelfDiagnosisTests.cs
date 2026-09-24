using System.Globalization;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What a companion says about its own sharing when it has nothing to share.
/// </summary>
/// <remarks>
/// Reported twice, by two different players on fresh installs: they appeared in everybody's
/// group list and never appeared on anybody's map. The client publishes even with no position
/// — name, map and raid state go up with null coordinates — so being in the list proves
/// nothing about having a marker, and the person it is happening to was told nothing at all.
///
/// The only way to find the cause was for somebody else to notice the absence and ask. The
/// client with the problem is the one that can see it.
/// </remarks>
public sealed class GroupSelfDiagnosisTests
{
    /// <summary>The case that was reported: in the room, on nobody's map.</summary>
    [Fact]
    public void ACompanionWithNoScreenshotFolderSaysSoRatherThanJustSharing()
    {
        var detail = Said("Geo", 2, Snapshot(watchingScreenshots: false));

        Assert.Contains("Sharing as Geo · 2 others", detail, StringComparison.Ordinal);
        Assert.Contains("screenshot folder has not been found", detail, StringComparison.Ordinal);
        Assert.Contains("Settings", detail, StringComparison.Ordinal);
    }

    /// <summary>Early in a raid, "take a screenshot" is the honest answer.</summary>
    [Fact]
    public void ACompanionEarlyInARaidIsJustToldToTakeOne()
    {
        var detail = Said(
            "Geo",
            1,
            Snapshot(watchingScreenshots: true, state: RaidLifecycleState.InRaid, startedMinutesAgo: 0));

        Assert.Contains("take a screenshot in the raid", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Steam", detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Well into a raid with nothing arriving, the likeliest cause is named.
    /// </summary>
    /// <remarks>
    /// Escape from Tarkov's screenshot key defaults to F12 and so does Steam's. A player who
    /// added the game to Steam as a non-Steam shortcut has the overlay active, and it takes
    /// F12 before the game sees it — Steam writes a screenshot into its own userdata folder,
    /// the game writes nothing, and this companion watches a correct folder that nothing ever
    /// arrives in.
    ///
    /// That is indistinguishable from "has not taken one yet" unless it is said, and it cost a
    /// player an evening. "Take a screenshot" is unhelpful advice to somebody who just did.
    /// </remarks>
    [Fact]
    public void ACompanionWellIntoARaidWithNothingArrivingNamesTheSteamOverlay()
    {
        var detail = Said(
            "Geo",
            1,
            Snapshot(watchingScreenshots: true, state: RaidLifecycleState.InRaid, startedMinutesAgo: 6));

        Assert.Contains("no screenshot has arrived this raid", detail, StringComparison.Ordinal);
        Assert.Contains("Steam", detail, StringComparison.Ordinal);
        Assert.Contains("rebind", detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Outside a raid there is no position to have, so nothing is said about it.
    /// </summary>
    /// <remarks>
    /// Nagging in the normal state would make working software look broken, which is the
    /// failure this whole change exists to avoid in the other direction.
    /// </remarks>
    [Fact]
    public void ACompanionOutsideARaidIsNotNagged()
    {
        var detail = Said(
            "Geo",
            1,
            Snapshot(watchingScreenshots: true, state: RaidLifecycleState.Menu));

        Assert.Equal("Sharing as Geo · 1 other", detail);
    }

    /// <summary>A companion with a position says only what it is doing.</summary>
    [Fact]
    public void ACompanionWithAPositionSaysNothingExtra()
    {
        var detail = Said(
            "Geo",
            0,
            Snapshot(
                watchingScreenshots: true,
                state: RaidLifecycleState.InRaid,
                hasPosition: true,
                startedMinutesAgo: 20));

        Assert.Equal("Sharing as Geo · nobody else here", detail);
    }

    [Fact]
    public void AnUnsupportedPlatformSaysThatInsteadOfSuggestingSettings()
    {
        var detail = Said("Geo", 1, Snapshot(supported: false));

        Assert.Contains("not supported here", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings", detail, StringComparison.Ordinal);
    }

    /// <summary>[#314] The service's code, in the English the player reads.</summary>
    private static string Said(string name, int others, ApplicationRuntimeSnapshot snapshot)
    {
        using var scope = UiText.Scope(UiText.Create("en", _ => { }));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            return PhraseText.Say(GroupSessionService.DescribeSharing(name, others, snapshot));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static ApplicationRuntimeSnapshot Snapshot(
        bool watchingScreenshots = true,
        bool supported = true,
        RaidLifecycleState state = RaidLifecycleState.InRaid,
        bool hasPosition = false,
        int startedMinutesAgo = 0)
    {
        var store = new RuntimeStateStore(new(
            false,
            Offline: true,
            GameMode.Regular,
            "en",
            TimeSpan.FromHours(9),
            TimeSpan.FromMinutes(5)));
        store.Update(current => current with
        {
            Observation = new(
                supported,
                IsWatchingLogs: supported,
                watchingScreenshots && supported,
                supported ? "logs" : null,
                watchingScreenshots && supported ? "shots" : null,
                Confidence.Certain,
                "test"),
            Raid = current.Raid with
            {
                State = state,
                StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-startedMinutesAgo),
                LastKnownPosition = hasPosition
                    ? new(
                        DateTimeOffset.UnixEpoch,
                        new WorldPosition(1, 2, 3),
                        new QuaternionOrientation(0, 0, 0, 1),
                        90,
                        null,
                        null,
                        "shot.png")
                    : null,
            },
        });
        return store.Current;
    }
}
