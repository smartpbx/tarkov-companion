using System.Text.Json;
using TarkovCompanion.GroupServer;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.Group;

/// <summary>
/// The relay's hold: what ends it, what does not, and what it costs.
/// </summary>
public sealed class GroupRoomChangesTests(ITestOutputHelper output)
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
    /// What one held exchange costs the relay when it finally answers.
    /// </summary>
    /// <remarks>
    /// A hold keeps a socket and a continuation, and ends by writing one room. The room is the
    /// part worth a number, because the whole argument for pushing rather than ticking is that
    /// the payload is tiny — so it is measured against the largest room this relay will hold
    /// rather than against a typical one.
    /// </remarks>
    [Theory]
    [InlineData(5, false, 8, 24 * 1024)]
    [InlineData(GroupRooms.MaximumMembersPerRoom, true, 60, 128 * 1024)]
    public void AHeldExchangeEndsBySendingOneSmallPage(int members, bool atEveryCeiling, int marked, int ceiling)
    {
        var rooms = new GroupRooms(TimeProvider.System);
        for (var member = 0; member < members; member++)
        {
            rooms.Publish(
                Room,
                $"Member-{member:D2}",
                atEveryCeiling ? Crowded($"Member-{member:D2}") : Ordinary($"Member-{member:D2}"));
        }

        var marks = new GroupMarks(TimeProvider.System);
        for (var mark = 0; mark < marked; mark++)
        {
            marks.AddWaypoint(Room, "Member-00", "streets-of-tarkov", mark, 0, mark, $"Mark {mark}");
        }

        var (waypoints, pings) = marks.Read(Room);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            rooms.Read(Room, "Member-00") with { Waypoints = waypoints, Pings = pings, Revision = 42 },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;

        // What a hold costs when it finally answers, which is the question an operator asks
        // about holding anything at all. Recorded as a bound rather than an exact size so a new
        // optional field does not fail it, and so the order of magnitude is written down.
        output.WriteLine($"{members} members{(atEveryCeiling ? " at every ceiling" : "")}: {bytes} bytes.");
        Assert.InRange(bytes, 1, ceiling);
    }

    /// <summary>A squad as one actually publishes: a position, a trail, an exit list.</summary>
    private static GroupMemberState Ordinary(string name) => new(
        name,
        "streets-of-tarkov",
        "InRaid",
        "pmc",
        123.4,
        567.8,
        90.1,
        2.5,
        [],
        ["Debut", "Checking", "Shootout Picnic"])
    {
        Y = 12.3,
        QuestIds = ["5936d90786f7742b1420ba5b", "5936d90786f7742b1420ba5c"],
        Trail = [.. Enumerable.Range(0, 10).Select(step => new GroupTrailPoint(step, step, step) { Y = step })],
        Extracts = ["Dorms V-Ex", "ZB-1011", "Old Gas"],
        RaidClockSeconds = 1234,
        RaidClockAgeSeconds = 12,
    };

    /// <summary>One member publishing as much as the relay will accept from them.</summary>
    private static GroupMemberState Crowded(string name) => new(
        name,
        "streets-of-tarkov",
        "InRaid",
        "pmc",
        123.4,
        567.8,
        90.1,
        2.5,
        [.. Enumerable.Range(0, 24).Select(item => $"An item with quite a long name {item}")],
        [.. Enumerable.Range(0, 24).Select(quest => $"A quest with quite a long name {quest}")])
    {
        Y = 12.3,
        QuestIds = [.. Enumerable.Range(0, 40).Select(quest => $"5936d90786f7742b1420ba{quest:D2}")],
        Trail = [.. Enumerable.Range(0, 12).Select(step => new GroupTrailPoint(step, step, step) { Y = step })],
        Extracts = [.. Enumerable.Range(0, 16).Select(exit => $"An extract with a long name {exit}")],
        Transits = [.. Enumerable.Range(0, 16).Select(transit => $"A transit with a long name {transit}")],
        RaidClockSeconds = 1234,
        RaidClockAgeSeconds = 12,
    };

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
