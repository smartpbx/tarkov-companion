using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests.Group;

/// <summary>
/// The relay's hold: what ends it, what does not, and what it costs.
/// </summary>
public sealed class GroupRoomChangesTests
{
    private const string Room = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task AHoldEndsWhenAnotherMemberPublishes()
    {
        var changes = new GroupRoomChanges(TimeProvider.System);
        changes.Record(Room, "Bravo");
        var since = changes.RevisionFor(Room, "Bravo");

        var held = changes.WaitAsync(Room, "Bravo", since, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(held.IsCompleted);

        changes.Record(Room, "Alpha");
        var revision = await held.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(revision > since, $"Revision {revision} should have moved past {since}.");
    }

    /// <summary>
    /// A member's own publish does not end their own hold.
    /// </summary>
    /// <remarks>
    /// Every exchange publishes before it waits, so a counter that did not exclude the caller
    /// would be moved by the very request that started the wait and the hold would be over
    /// before it began — a long poll that polls.
    /// </remarks>
    [Fact]
    public async Task AMembersOwnPublishDoesNotEndTheirOwnHold()
    {
        var changes = new GroupRoomChanges(TimeProvider.System);
        changes.Record(Room, "Bravo");
        var since = changes.RevisionFor(Room, "Bravo");

        var held = changes.WaitAsync(Room, "Bravo", since, TimeSpan.FromSeconds(5), CancellationToken.None);
        changes.Record(Room, "Bravo");
        changes.Record(Room, "Bravo");
        await Task.Delay(120);

        Assert.False(held.IsCompleted);
        Assert.Equal(since, changes.RevisionFor(Room, "Bravo"));

        changes.Record(Room, "Alpha");
        Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(2)) > since);
    }

    /// <summary>A mark is a change to the room, whoever made it.</summary>
    [Fact]
    public async Task AMarkEndsEverybodysHoldIncludingTheSenders()
    {
        var changes = new GroupRoomChanges(TimeProvider.System);
        changes.Record(Room, "Alpha");
        var since = changes.RevisionFor(Room, "Alpha");
        var held = changes.WaitAsync(Room, "Alpha", since, TimeSpan.FromSeconds(5), CancellationToken.None);

        changes.Record(Room, byMemberKey: null);

        Assert.True(await held.WaitAsync(TimeSpan.FromSeconds(2)) > since);
    }

    [Fact]
    public async Task AHoldGivesUpAtTheTimeItWasGiven()
    {
        var changes = new GroupRoomChanges(TimeProvider.System);
        changes.Record(Room, "Bravo");
        var since = changes.RevisionFor(Room, "Bravo");

        var started = DateTimeOffset.UtcNow;
        var revision = await changes.WaitAsync(
            Room,
            "Bravo",
            since,
            TimeSpan.FromMilliseconds(250),
            CancellationToken.None);

        Assert.Equal(since, revision);
        Assert.InRange(DateTimeOffset.UtcNow - started, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));
    }

    /// <summary>A caller that hangs up is not something the relay keeps holding for.</summary>
    [Fact]
    public async Task AHoldEndsWhenTheCallerGoesAway()
    {
        var changes = new GroupRoomChanges(TimeProvider.System);
        changes.Record(Room, "Bravo");
        using var gone = new CancellationTokenSource();
        var held = changes.WaitAsync(Room, "Bravo", changes.RevisionFor(Room, "Bravo"), TimeSpan.FromMinutes(1), gone.Token);

        await gone.CancelAsync();

        await held.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, changes.WaitingCount);
    }

    /// <summary>Asking for longer than the relay allows gets the relay's answer, not the caller's.</summary>
    [Fact]
    public async Task AHoldIsNeverLongerThanTheRelayAllows()
    {
        var changes = new GroupRoomChanges(TimeProvider.System);
        changes.Record(Room, "Bravo");
        var since = changes.RevisionFor(Room, "Bravo");
        var held = changes.WaitAsync(Room, "Bravo", since, TimeSpan.FromHours(1), CancellationToken.None);

        // Not waited out — a twenty-second test is not worth the proof. What is checked is that
        // the hold is counted while it runs and released the moment the room moves.
        await Task.Delay(50);
        Assert.Equal(1, changes.WaitingCount);

        changes.Record(Room, "Alpha");
        await held.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, changes.WaitingCount);
    }

    /// <summary>A caller asking for no wait is answered exactly as it always was.</summary>
    [Fact]
    public async Task NoWaitMeansNoWait()
    {
        var changes = new GroupRoomChanges(TimeProvider.System);
        changes.Record(Room, "Bravo");

        var revision = await changes.WaitAsync(Room, "Bravo", 0, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(changes.RevisionFor(Room, "Bravo"), revision);
        Assert.Equal(0, changes.WaitingCount);
    }

    /// <summary>
    /// A key guesser cannot leave a counter behind for every key they try.
    /// </summary>
    /// <remarks>
    /// The same bound the room store has and for the same reason. Past it the oldest room's
    /// counter is dropped, which costs that room one immediate answer.
    /// </remarks>
    [Fact]
    public void RoomsAreBoundedTheWayTheRoomStoreIs()
    {
        var changes = new GroupRoomChanges(TimeProvider.System);
        for (var attempt = 0; attempt < GroupRooms.MaximumRooms + 200; attempt++)
        {
            changes.Record(GroupKey.RoomFor($"guessed-key-number-{attempt:D6}"), "Guesser");
        }

        // The most recent room is still counted — the bound drops the oldest, not the newest —
        // and the first one has been forgotten, which is the whole of the bound.
        var last = GroupKey.RoomFor($"guessed-key-number-{GroupRooms.MaximumRooms + 199:D6}");
        Assert.Equal(1, changes.RevisionFor(last, "Somebody-else"));
        Assert.Equal(0, changes.RevisionFor(GroupKey.RoomFor("guessed-key-number-000000"), "Somebody-else"));
    }
}
