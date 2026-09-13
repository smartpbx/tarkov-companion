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
        var detail = GroupSessionService.DescribeSharing("Geo", 2, Snapshot(watchingScreenshots: false));

        Assert.Contains("Sharing as Geo · 2 others", detail, StringComparison.Ordinal);
        Assert.Contains("screenshot folder has not been found", detail, StringComparison.Ordinal);
        Assert.Contains("Settings", detail, StringComparison.Ordinal);
    }

    /// <summary>Watching the folder but nothing photographed yet is a different sentence.</summary>
    [Fact]
    public void ACompanionInARaidWithNoScreenshotYetSaysWhatToDo()
    {
        var detail = GroupSessionService.DescribeSharing(
            "Geo",
            1,
            Snapshot(watchingScreenshots: true, state: RaidLifecycleState.InRaid));

        Assert.Contains("take a screenshot in the raid", detail, StringComparison.Ordinal);
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
        var detail = GroupSessionService.DescribeSharing(
            "Geo",
            1,
            Snapshot(watchingScreenshots: true, state: RaidLifecycleState.Menu));

        Assert.Equal("Sharing as Geo · 1 other", detail);
    }

    /// <summary>A companion with a position says only what it is doing.</summary>
    [Fact]
    public void ACompanionWithAPositionSaysNothingExtra()
    {
        var detail = GroupSessionService.DescribeSharing(
            "Geo",
            0,
            Snapshot(watchingScreenshots: true, state: RaidLifecycleState.InRaid, hasPosition: true));

        Assert.Equal("Sharing as Geo · nobody else here", detail);
    }

    [Fact]
    public void AnUnsupportedPlatformSaysThatInsteadOfSuggestingSettings()
    {
        var detail = GroupSessionService.DescribeSharing("Geo", 1, Snapshot(supported: false));

        Assert.Contains("not supported here", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings", detail, StringComparison.Ordinal);
    }

    private static ApplicationRuntimeSnapshot Snapshot(
        bool watchingScreenshots = true,
        bool supported = true,
        RaidLifecycleState state = RaidLifecycleState.InRaid,
        bool hasPosition = false)
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
