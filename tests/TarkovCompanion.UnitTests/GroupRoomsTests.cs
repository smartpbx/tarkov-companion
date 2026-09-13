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
