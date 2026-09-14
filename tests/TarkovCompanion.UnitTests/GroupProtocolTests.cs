using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The number that lets a client tell version skew from a broken feature.
/// </summary>
/// <remarks>
/// #139's Now 27. Everything on this wire is additive, so a mismatch is almost never fatal —
/// which is why it needs saying out loud. A client quietly missing a field it was never sent
/// looks exactly like a feature that does not work, and nothing inside the application can tell
/// those apart. The day the group key replaced a room name and a server secret, a client that
/// had updated could not talk to a server that had not, and nothing said so.
/// </remarks>
public sealed class GroupProtocolTests
{
    [Fact]
    public void EveryRoomReplyCarriesTheNumber()
    {
        var state = new GroupRoomState("room", [], DateTimeOffset.UnixEpoch);

        Assert.Equal(GroupProtocol.Version, state.Protocol);
    }

    [Fact]
    public void AMemberIsAgedByTheServerRatherThanByItself()
    {
        // A publisher has no idea how long ago its own last message arrived, and a companion
        // that crashed keeps its last position age for ever — so the panel read "12s ago" for
        // the three minutes until the room forgot it, and the marker looked live until it
        // vanished.
        var clock = new Clock();
        var rooms = new GroupRooms(clock);
        rooms.Publish("room", "Geo", Member("Geo"));

        clock.Advance(TimeSpan.FromSeconds(90));
        var seen = Assert.Single(rooms.Read("room", "Clay").Members);

        Assert.Equal(90, seen.SinceSeconds);
    }

    [Fact]
    public void SomebodyWhoJustPublishedIsNotStale()
    {
        var rooms = new GroupRooms(TimeProvider.System);
        rooms.Publish("room", "Geo", Member("Geo"));

        var seen = Assert.Single(rooms.Read("room", "Clay").Members);

        Assert.NotNull(seen.SinceSeconds);
        Assert.True(seen.SinceSeconds < 5);
    }

    [Fact]
    public void TheAgeIsSeparateFromTheScreenshotsAge()
    {
        // Two different questions: how old the picture a position came from is, and how long
        // since we heard from the person at all.
        var clock = new Clock();
        var rooms = new GroupRooms(clock);
        rooms.Publish("room", "Geo", Member("Geo") with { PositionAgeSeconds = 12 });

        clock.Advance(TimeSpan.FromSeconds(90));
        var seen = Assert.Single(rooms.Read("room", "Clay").Members);

        Assert.Equal(12, seen.PositionAgeSeconds);
        Assert.Equal(90, seen.SinceSeconds);
    }

    private static GroupMemberState Member(string name) =>
        new(name, "bigmap", "InRaid", "pmc", 1, 2, 90, 0, [], []);

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 14, 18, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
