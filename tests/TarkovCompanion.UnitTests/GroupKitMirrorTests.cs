using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The one thing a player's own game will not tell them, taken from the people whose game did.
/// </summary>
public sealed class GroupKitMirrorTests
{
    [Fact]
    public void Describes_a_partner_in_the_order_a_player_reads_gear()
    {
        var squad = Squad(Member(
            "Nikita",
            ("Backpack", "bp"),
            ("FirstPrimaryWeapon", "rifle"),
            ("ArmorVest", "armour")));

        var observed = GroupKitMirror.Describe(squad, Names);

        var kit = Assert.Single(observed);
        Assert.Equal("Nikita", kit.Name);
        Assert.Equal(["rifle name", "armour name", "bp name"], kit.Loadout);
    }

    [Fact]
    public void Leaves_out_slots_a_player_does_not_read()
    {
        var squad = Squad(Member(
            "Nikita",
            ("FirstPrimaryWeapon", "rifle"),
            ("Eyewear", "glasses"),
            ("Scabbard", "knife")));

        var kit = Assert.Single(GroupKitMirror.Describe(squad, Names));

        Assert.Equal(["rifle name"], kit.Loadout);
    }

    [Fact]
    public void Leaves_out_an_id_the_catalog_has_never_heard_of()
    {
        // Better silent than a raw template id presented to somebody as their gun.
        var squad = Squad(Member("Nikita", ("FirstPrimaryWeapon", "unknown"), ("Headwear", "helmet")));

        var kit = Assert.Single(GroupKitMirror.Describe(squad, Names));

        Assert.Equal(["helmet name"], kit.Loadout);
    }

    /// <summary>
    /// A member whose gear resolved to nothing is still worth publishing, if anything else was.
    /// </summary>
    /// <remarks>
    /// This used to assert the opposite, and the premise has genuinely changed rather than the
    /// rule being wrong. The entry carried a kit and nothing else, so an empty kit was an empty
    /// entry; it now also carries the level, side and scav timer that the member's own game
    /// will not tell them, and dropping the entry would drop those with it.
    /// </remarks>
    [Fact]
    public void A_member_whose_gear_resolved_to_nothing_still_carries_what_else_is_known()
    {
        var squad = Squad(Member("Nikita", ("FirstPrimaryWeapon", "unknown")));

        var kit = Assert.Single(GroupKitMirror.Describe(squad, Names));

        Assert.Empty(kit.Loadout);
        Assert.NotNull(kit.Level);
    }

    [Fact]
    public void Says_nothing_about_a_member_with_no_nickname()
    {
        var squad = Squad(new GroupMember(
            "pid", 1, null, "Bear", 40, false, true, null,
            [new("item", "rifle", null, "FirstPrimaryWeapon")]));

        Assert.Empty(GroupKitMirror.Describe(squad, Names));
    }

    [Fact]
    public void Stops_at_the_bound_the_server_will_accept()
    {
        var squad = Squad(Enumerable
            .Range(0, 12)
            .Select(index => Member($"Player {index}", ("FirstPrimaryWeapon", "rifle")))
            .ToArray());

        Assert.Equal(8, GroupKitMirror.Describe(squad, Names).Count);
    }

    [Fact]
    public void Finds_a_player_in_what_somebody_else_published()
    {
        IReadOnlyList<IReadOnlyList<ObservedKit>> published =
        [
            [new("Someone else", ["a gun"])],
            [new("Clayton", ["an M4", "a Slick"])],
        ];

        Assert.Equal(["an M4", "a Slick"], GroupKitMirror.Find(published, "Clayton"));
    }

    [Fact]
    public void Matches_a_name_regardless_of_case_or_stray_spaces()
    {
        IReadOnlyList<IReadOnlyList<ObservedKit>> published = [[new("Clayton", ["an M4"])]];

        Assert.Equal(["an M4"], GroupKitMirror.Find(published, "  clayton "));
    }

    [Fact]
    public void Finds_nothing_for_a_player_nobody_described()
    {
        IReadOnlyList<IReadOnlyList<ObservedKit>> published = [[new("Someone else", ["a gun"])]];

        Assert.Empty(GroupKitMirror.Find(published, "Clayton"));
        Assert.Empty(GroupKitMirror.Find(published, null));
        Assert.Empty(GroupKitMirror.Find(published, "   "));
    }

    [Fact]
    public void Takes_the_first_description_rather_than_inventing_a_disagreement()
    {
        IReadOnlyList<IReadOnlyList<ObservedKit>> published =
        [
            [new("Clayton", ["an M4"])],
            [new("Clayton", ["an AK"])],
        ];

        Assert.Equal(["an M4"], GroupKitMirror.Find(published, "Clayton"));
    }

    [Fact]
    public void Asks_for_every_gear_id_the_caller_has_not_named()
    {
        var squad = Squad(
            Member("One", ("FirstPrimaryWeapon", "rifle"), ("Headwear", "helmet")),
            Member("Two", ("FirstPrimaryWeapon", "rifle"), ("Eyewear", "glasses")));

        var unresolved = GroupKitMirror.UnresolvedIds(squad, id => id == "helmet");

        Assert.Equal(["rifle"], unresolved);
    }

    private static string? Names(string templateId) =>
        templateId == "unknown" ? null : templateId + " name";

    private static SquadSnapshot Squad(params GroupMember[] members) =>
        new(members, null, null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_member_nothing_is_known_about_is_left_out_entirely()
    {
        // The rule that survived the change: an entry carrying nothing is an entry that says
        // nothing, and publishing one would put a name in everybody's room for no reason.
        var squad = Squad(new GroupMember(
            "pid:Anonymous",
            1,
            "Anonymous",
            Side: null,
            Level: null,
            IsLeader: false,
            IsReady: true,
            ScavLockedUntil: null,
            Equipment: []));

        Assert.Empty(GroupKitMirror.Describe(squad, Names));
    }

    [Fact]
    public void The_scav_timer_and_the_side_ride_along_with_the_kit()
    {
        // All three come off the same notification the kit does, and every one of them
        // describes somebody other than the person reading it — which is the whole reason the
        // group can hand them back.
        var squad = Squad(Member("Nikita", ("Headwear", "helmet")));

        var kit = Assert.Single(GroupKitMirror.Describe(squad, Names));

        Assert.Equal(40, kit.Level);
        Assert.Equal("Bear", kit.Side);
    }

    [Fact]
    public void One_squadmate_missing_a_field_does_not_erase_what_another_saw()
    {
        // Two squadmates may have seen this player at different moments. Taking the first
        // entry whole would discard half of what the group actually knows.
        var partial = new ObservedKit("Nikita", []) { Level = 40 };
        var other = new ObservedKit("Nikita", ["helmet name"])
        {
            ScavLockedUntil = DateTimeOffset.Parse("2026-09-14T04:00:00Z"),
        };

        var found = GroupKitMirror.FindAll([[partial], [other]], "Nikita");

        Assert.Equal(40, found!.Level);
        Assert.NotNull(found.ScavLockedUntil);
        Assert.Equal(["helmet name"], found.Loadout);
    }

    [Fact]
    public void Nobody_having_seen_this_player_is_nothing_rather_than_an_empty_answer()
    {
        Assert.Null(GroupKitMirror.FindAll([[new ObservedKit("Geo", [])]], "Nikita"));
    }

    private static GroupMember Member(string nickname, params (string Slot, string Template)[] gear) =>
        new(
            "pid:" + nickname,
            nickname.GetHashCode(StringComparison.Ordinal),
            nickname,
            "Bear",
            40,
            false,
            true,
            null,
            gear.Select(item => new GroupEquipmentItem(
                item.Template + ":" + nickname,
                item.Template,
                null,
                item.Slot)).ToArray());
}
