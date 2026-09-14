using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The first tests GroupRooms has had.
/// </summary>
public sealed class GroupRoomsTests
{
    /// <summary>
    /// A stranger from LFG is not published to the group room.
    /// </summary>
    /// <remarks>
    /// The game describes every member of the in-game party, so a five-man filled from
    /// matchmaking carries a random's nickname and loadout, and nothing tested whether that
    /// person was in the room. It went to the relay and came back to anyone holding the key.
    /// SAFETY.md rule 1 says other players' log data is never transmitted; the exception it
    /// records covers the people who are in the room, and this is what makes that true.
    /// </remarks>
    [Fact]
    public void AnObservationAboutSomebodyOutsideTheRoomIsNotStored()
    {
        var rooms = new GroupRooms(TimeProvider.System);
        rooms.Publish("room", "Geo", Member("Geo"));
        rooms.Publish("room", "Clay", Member("Clay", observed:
        [
            new("Geo", ["Slick", "Altyn"]),
            new("SomeRandom", ["PACA", "SSh-68"]),
        ]));

        var seen = rooms.Read("room", "Max").Members.SelectMany(member => member.Observed).ToArray();

        Assert.Single(seen);
        Assert.Equal("Geo", seen[0].Name);
    }

    /// <summary>
    /// Describing somebody who has not joined yet costs one tick, deliberately.
    /// </summary>
    /// <remarks>
    /// Pruning happens on the way in as well as on the way out, so an observation about a
    /// person who is not yet in the room is not merely hidden, it is never stored. That loses
    /// a kit for one publish interval — five seconds — and the next publish carries it once
    /// they have joined.
    ///
    /// Worth the five seconds: it means a stranger's nickname and loadout are not sitting in
    /// the relay's memory waiting for a read that filters them, and a future change that
    /// persisted room state could not write them to disk. Failing closed is the safe direction
    /// here, because a kit that arrives late costs a moment and one that should never have
    /// been stored cannot be recalled.
    /// </remarks>
    [Fact]
    public void DescribingSomebodyWhoHasNotJoinedYetCostsOnePublish()
    {
        var rooms = new GroupRooms(TimeProvider.System);
        rooms.Publish("room", "Clay", Member("Clay", observed: [new("Geo", ["Slick"])]));
        rooms.Publish("room", "Geo", Member("Geo"));

        // Geo was not in the room when Clay described them, so it was not kept.
        Assert.Empty(rooms.Read("room", "Max").Members.SelectMany(member => member.Observed));

        // Clay publishes again five seconds later, as the loop does.
        rooms.Publish("room", "Clay", Member("Clay", observed: [new("Geo", ["Slick"])]));

        Assert.Single(rooms.Read("room", "Max").Members.SelectMany(member => member.Observed));
    }

    /// <summary>The whole point of the mirror still works: A describes B, B reads it back.</summary>
    [Fact]
    public void AnObservationAboutSomebodyInTheRoomSurvives()
    {
        var rooms = new GroupRooms(TimeProvider.System);
        rooms.Publish("room", "Geo", Member("Geo"));
        rooms.Publish("room", "Clay", Member("Clay", observed: [new("Geo", ["Slick", "Altyn"])]));

        var seen = rooms.Read("room", "Geo").Members.SelectMany(member => member.Observed).ToArray();

        Assert.Single(seen);
        Assert.Equal(["Slick", "Altyn"], seen[0].Loadout);
    }

    /// <summary>
    /// An in-game nickname and a typed display name differing only by case are one person.
    /// </summary>
    /// <remarks>
    /// The same rule the client already uses to hand a player their own kit. There is no better
    /// key: the logs carry the nickname, the relay carries what somebody typed, and the local
    /// player's own account id is absent from the logs entirely.
    /// </remarks>
    [Fact]
    public void TheNameMatchIgnoresCase()
    {
        var rooms = new GroupRooms(TimeProvider.System);
        rooms.Publish("room", "geo", Member("geo"));
        rooms.Publish("room", "Clay", Member("Clay", observed: [new("GEO", ["Slick"])]));

        Assert.Single(rooms.Read("room", "geo").Members.SelectMany(member => member.Observed));
    }

    /// <summary>Somebody who has left stops being described, without waiting for their entry to expire.</summary>
    [Fact]
    public void AnObservationIsDroppedOnceThatPersonLeavesTheRoom()
    {
        var rooms = new GroupRooms(TimeProvider.System);
        rooms.Publish("room", "Geo", Member("Geo"));
        rooms.Publish("room", "Clay", Member("Clay", observed: [new("Geo", ["Slick"])]));
        Assert.Single(rooms.Read("room", "Max").Members.SelectMany(member => member.Observed));

        rooms.Remove("room", "Geo");

        Assert.Empty(rooms.Read("room", "Max").Members.SelectMany(member => member.Observed));
    }

    /// <summary>The asker is left out, so they do not get a second marker on their own position.</summary>
    [Fact]
    public void ReadingARoomLeavesOutTheAsker()
    {
        var rooms = new GroupRooms(TimeProvider.System);
        rooms.Publish("room", "Clay", Member("Clay"));
        rooms.Publish("room", "Geo", Member("Geo"));

        var members = rooms.Read("room", "Clay").Members;

        Assert.Single(members);
        Assert.Equal("Geo", members[0].Name);
    }

    private static GroupMemberState Member(string name, IReadOnlyList<GroupObservedMember>? observed = null) => new(
        name,
        "bigmap",
        "InRaid",
        "pmc",
        1,
        2,
        90,
        0,
        [],
        [])
    {
        Observed = observed ?? [],
    };
}

public sealed class GroupRoomBoundsTests
{
    /// <summary>
    /// A guesser walking the key space cannot leave an entry behind per attempt.
    /// </summary>
    /// <remarks>
    /// Rooms were GetOrAdd-only and never removed, so every key anybody ever sent created a
    /// room that lived for the life of the process. The cap is checked before the room is
    /// created, which is the part that matters: checking after would still have allocated.
    /// </remarks>
    [Fact]
    public void RoomsAreCappedBeforeANewOneIsCreated()
    {
        var rooms = new GroupRooms(TimeProvider.System);
        for (var attempt = 0; attempt < GroupRooms.MaximumRooms + 50; attempt++)
        {
            rooms.Publish($"room-{attempt}", "Clay", Member("Clay"));
        }

        Assert.Equal(GroupRooms.MaximumRooms, rooms.RoomCount);
    }

    /// <summary>A full room keeps working for the people already in it.</summary>
    [Fact]
    public void AMemberAlreadyInAFullRoomCanStillPublish()
    {
        var rooms = new GroupRooms(TimeProvider.System);
        for (var seat = 0; seat < GroupRooms.MaximumMembersPerRoom; seat++)
        {
            rooms.Publish("room", $"member-{seat}", Member($"member-{seat}"));
        }

        rooms.Publish("room", "late", Member("late"));
        Assert.Equal(GroupRooms.MaximumMembersPerRoom, rooms.MemberCount);

        // The people who were there keep updating, which is the point of capping new arrivals
        // rather than capping publishes.
        rooms.Publish("room", "member-0", Member("member-0", observed: []));
        Assert.Equal(GroupRooms.MaximumMembersPerRoom, rooms.MemberCount);
        Assert.Contains(rooms.Read("room", "nobody").Members, member => member.Name == "member-0");
    }

    /// <summary>
    /// A room nobody reads still forgets, and an empty room is removed.
    /// </summary>
    /// <remarks>
    /// Expiry used to happen only inside Read, so an abandoned room kept its members for ever
    /// and the room itself was never removed at all.
    /// </remarks>
    [Fact]
    public void SweepingDropsStaleMembersAndThenTheEmptyRoom()
    {
        var clock = new Clock();
        var rooms = new GroupRooms(clock);
        rooms.Publish("room", "Clay", Member("Clay"));
        Assert.Equal(1, rooms.RoomCount);

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(1, rooms.Sweep());

        Assert.Equal(0, rooms.RoomCount);
        Assert.Equal(0, rooms.MemberCount);
    }

    /// <summary>A live room survives a sweep.</summary>
    [Fact]
    public void SweepingKeepsARoomSomebodyIsStillIn()
    {
        var clock = new Clock();
        var rooms = new GroupRooms(clock);
        rooms.Publish("room", "Clay", Member("Clay"));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, rooms.Sweep());
        Assert.Equal(1, rooms.MemberCount);
    }

    private static GroupMemberState Member(string name, IReadOnlyList<GroupObservedMember>? observed = null) => new(
        name, "bigmap", "InRaid", "pmc", 1, 2, 90, 0, [], [])
    {
        Observed = observed ?? [],
    };

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 13, 21, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

public sealed class GroupMemberStateValidationTests
{
    /// <summary>
    /// A malformed payload is a bad request, not a server fault.
    /// </summary>
    /// <remarks>
    /// `"observed": null` reached a Count on a null list and threw, which the framework turned
    /// into a 500 — and an unhandled exception per attempt for anybody who cared to send them.
    /// The collections are non-nullable in the record and a JSON null lands in them anyway.
    /// </remarks>
    [Fact]
    public void ANullCollectionIsRejectedRatherThanThrowing()
    {
        var state = new GroupMemberState("Clay", "bigmap", "InRaid", "pmc", 1, 2, 90, 0, null!, null!)
        {
            Observed = null!,
            Trail = null!,
            QuestIds = null!,
        };

        Assert.Null(state.Validate());
    }

    [Theory]
    [InlineData("", "display name")]
    [InlineData("   ", "display name")]
    public void ANamelessMemberIsRefused(string name, string expected) =>
        Assert.Contains(expected, Valid() with { Name = name } is var s && s.Validate() is { } m ? m : "", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void TooManyObservationsAreRefused() =>
        Assert.Contains(
            "at most eight",
            (Valid() with
            {
                Observed = Enumerable.Range(0, 9).Select(i => new GroupObservedMember($"p{i}", [])).ToArray(),
            }).Validate() ?? string.Empty,
            StringComparison.Ordinal);

    [Fact]
    public void TooLongATrailIsRefused() =>
        Assert.Contains(
            "twelve points",
            (Valid() with
            {
                Trail = Enumerable.Range(0, 13).Select(i => new GroupTrailPoint(i, i, i)).ToArray(),
            }).Validate() ?? string.Empty,
            StringComparison.Ordinal);

    /// <summary>
    /// Forty ids, which is more quests than anybody has open at once.
    /// </summary>
    /// <remarks>
    /// Bounded on its own terms rather than with the names beside it. A name is read and five
    /// fill a panel; an id is counted, and ranking tonight's maps by where the group overlaps
    /// wants the whole active list.
    /// </remarks>
    [Fact]
    public void TooManyQuestIdsAreRefused() =>
        Assert.Contains(
            "at most forty",
            (Valid() with
            {
                QuestIds = [.. Enumerable.Range(0, 41).Select(index => $"task-{index}")],
            }).Validate() ?? string.Empty,
            StringComparison.Ordinal);

    [Fact]
    public void AnOverlongQuestIdIsRefused() =>
        Assert.Contains(
            "64 characters",
            (Valid() with { QuestIds = [new string('t', 65)] }).Validate() ?? string.Empty,
            StringComparison.Ordinal);

    [Fact]
    public void AnOrdinaryMemberIsAccepted() => Assert.Null(Valid().Validate());

    private static GroupMemberState Valid() =>
        new("Clay", "bigmap", "InRaid", "pmc", 1, 2, 90, 0, [], []);
}
