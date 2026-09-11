using System.Text.Json;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Exercises the group and dogtag parser against the shapes a live installation writes.
/// </summary>
/// <remarks>
/// The payloads below keep the game's real keys, nesting, casing and value types, with ids and
/// nicknames replaced. Inventing a tidier shape to match the parser is how a parser ends up
/// passing its tests and matching nothing the game writes, which has already happened once in
/// this repository.
///
/// Two of these tests are boundary tests rather than behaviour tests: the player's own
/// notification must never become a party member, and a squadmate's looted dogtags must never
/// be read out of their loadout. Both encode rules from docs/SAFETY.md, so a change that makes
/// them fail is a policy change, not a refactor.
/// </remarks>
public sealed class GroupNotificationParserTests
{
    private static readonly DateTimeOffset Observed = DateTimeOffset.UnixEpoch;

    /// <summary>
    /// Everything the game writes ahead of the payload, including the bracketed event id that
    /// a naive search for the first '[' would find instead of the JSON.
    /// </summary>
    private const string LinePrefix =
        "2026-09-11 22:21:00.000|1.1.5.0.47242|Info|output|backend|WebSocketSharp - " +
        "message received: NOTIFICATION [EVENTID] groupMatchRaidReady ";

    private static readonly string RaidReadyLine = Line("""
        [{"type":"groupMatchRaidReady","eventId":"ID_1","extendedProfile":{
        "_id":"ID_2","aid":9041989,
        "Info":{"Nickname":"PLAYER_A","Side":"Bear","Level":24,"MemberCategory":2,
        "GameVersion":"edge_of_darkness","SavageLockTime":1789089239,"SavageNickname":"PLAYER_A_SCAV",
        "Health":{"Hydration":{"Current":78.5,"Maximum":100},"BodyParts":{"Head":{"Health":{"Current":35,"Maximum":35}}}},
        "PrestigeLevel":0},
        "isLeader":false,"isReady":true,
        "PlayerVisualRepresentation":{"Info":{"Nickname":"PLAYER_A","Side":"Bear","Level":24,"MemberCategory":2,
        "GameVersion":"edge_of_darkness","SavageLockTime":1789089239,"SavageNickname":"PLAYER_A_SCAV",
        "PrestigeLevel":0},
        "Equipment":{"Id":"ID_3","Items":[{"_id":"ID_3","_tpl":"55d7217a4bdc2d86028b456d"},
        {"_id":"ID_4","_tpl":"59ff346386f77477562ff5e2","parentId":"ID_3","slotId":"FirstPrimaryWeapon",
        "upd":{"Repairable":{"MaxDurability":93.58,"Durability":92.95},"FireMode":{"FireMode":"single"}}}]}}}}]
        """);

    private static readonly string UserLeaveLine = Line("""
        [{"type":"groupMatchUserLeave","eventId":"ID_1","aid":9041989,"Nickname":"PLAYER_A"}]
        """);

    private static readonly string StartGameLine = Line("""
        [{"type":"groupMatchStartGame","eventId":"ID_1","groupId":"ID_2","estimate":150}]
        """);

    /// <summary>
    /// A group-typed payload carrying the marker the game only puts on the player's own
    /// notifications. Nothing in a real log looks like this; the test exists so that the one
    /// signal separating the player from their squad cannot be removed unnoticed.
    /// </summary>
    private static readonly string OwnNotificationLine = Line("""
        [{"type":"groupMatchRaidReady","profileid":"SELF_1","eventId":"ID_1","extendedProfile":{
        "_id":"ID_2","aid":9041989,"Info":{"Nickname":"PLAYER_A","Side":"Bear","Level":24}}}]
        """);

    private static readonly string EquipmentItemsWithDogtag = OneLine("""
        [{"_id":"ID_6","_tpl":"55d7217a4bdc2d86028b456d"},
        {"_id":"ID_5","_tpl":"6a354a30652075cf460944c6","parentId":"ID_6","slotId":"main",
        "upd":{"SpawnedInSession":true,"Dogtag":{
        "AccountId":"ID_7","ProfileId":"ID_8","Nickname":"VICTIM_NAME","Side":"Bear","Level":23,
        "Time":"2026-09-11T03:25:09.419+03:00","Status":"Killed by",
        "KillerAccountId":"ID_9","KillerProfileId":"ID_10","KillerName":"KILLER_NAME",
        "WeaponName":"59ff346386f77477562ff5e2 ShortName","CarriedByGroupMember":false}}}]
        """);

    /// <summary>A squadmate whose own loadout contains a dogtag they looted.</summary>
    private static readonly string RaidReadyLineWithSquadmateDogtag = Line("""
        [{"type":"groupMatchRaidReady","eventId":"ID_1","extendedProfile":{
        "_id":"ID_2","aid":9041989,
        "Info":{"Nickname":"PLAYER_A","Side":"Bear","Level":24},
        "isLeader":true,"isReady":false,
        "PlayerVisualRepresentation":{"Info":{"Nickname":"PLAYER_A"},
        "Equipment":{"Id":"ID_6","Items":[{"_id":"ID_6","_tpl":"55d7217a4bdc2d86028b456d"},
        {"_id":"ID_5","_tpl":"6a354a30652075cf460944c6","parentId":"ID_6","slotId":"main",
        "upd":{"Dogtag":{"Nickname":"VICTIM_NAME","Side":"Bear","Level":23,
        "Time":"2026-09-11T03:25:09.419+03:00","KillerName":"KILLER_NAME",
        "WeaponName":"59ff346386f77477562ff5e2 ShortName","CarriedByGroupMember":false}}}]}}}}]
        """);

    [Fact]
    public void ReadsASquadmateFromARaidReadyNotification()
    {
        var observation = GroupNotificationParser.ParseLine(RaidReadyLine, Observed);

        Assert.NotNull(observation);
        Assert.Equal(GroupObservationKind.MemberUpdated, observation.Kind);
        Assert.Equal("groupMatchRaidReady", observation.NotificationType);
        Assert.Equal(Observed, observation.ObservedUtc);

        var member = observation.Member;
        Assert.NotNull(member);
        Assert.Equal("ID_2", member.MemberId);
        Assert.Equal(9041989L, member.AccountId);
        Assert.Equal("PLAYER_A", member.Nickname);
        Assert.Equal("Bear", member.Side);
        Assert.Equal(24, member.Level);
        Assert.False(member.IsLeader);
        Assert.True(member.IsReady);
    }

    [Fact]
    public void ReadsTheScavCooldownAsAnInstantRatherThanANumber()
    {
        var observation = GroupNotificationParser.ParseLine(RaidReadyLine, Observed);

        Assert.NotNull(observation);
        var member = observation.Member;
        Assert.NotNull(member);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789089239), member.ScavLockedUntil);
    }

    [Fact]
    public void KeepsEquipmentFlatWithItsParentAndSlotPointers()
    {
        // The game states equipment as a flat array with pointers, so the parser must not
        // silently fold it into a tree or drop the entries that have no slot.
        var observation = GroupNotificationParser.ParseLine(RaidReadyLine, Observed);

        Assert.NotNull(observation);
        var member = observation.Member;
        Assert.NotNull(member);
        Assert.Collection(
            member.Equipment,
            container =>
            {
                Assert.Equal("ID_3", container.ItemId);
                Assert.Equal("55d7217a4bdc2d86028b456d", container.TemplateId);
                Assert.Null(container.ParentItemId);
                Assert.Null(container.SlotId);
            },
            weapon =>
            {
                Assert.Equal("ID_4", weapon.ItemId);
                Assert.Equal("59ff346386f77477562ff5e2", weapon.TemplateId);
                Assert.Equal("ID_3", weapon.ParentItemId);
                Assert.Equal("FirstPrimaryWeapon", weapon.SlotId);
            });
    }

    [Fact]
    public void ReadsTheDepartingMemberFromALeaveNotification()
    {
        var observation = GroupNotificationParser.ParseLine(UserLeaveLine, Observed);

        Assert.NotNull(observation);
        Assert.Equal(GroupObservationKind.MemberLeft, observation.Kind);

        var member = observation.Member;
        Assert.NotNull(member);
        Assert.Equal(9041989L, member.AccountId);
        Assert.Equal("PLAYER_A", member.Nickname);

        // The compact shape states no profile id, and nothing invents one.
        Assert.Null(member.MemberId);

        // Readiness was never stated, so it stays unstated rather than becoming "not ready".
        Assert.Null(member.IsReady);
        Assert.Null(member.IsLeader);
        Assert.Empty(member.Equipment);
    }

    [Fact]
    public void KeysTheSamePersonIdenticallyAcrossBothNotificationShapes()
    {
        // Ready notifications repeat hundreds of times per session, so a caller keeps one
        // entry per member. That only works if the compact leave shape, which carries no
        // profile id, still keys to the same person as the full shape.
        var ready = GroupNotificationParser.ParseLine(RaidReadyLine, Observed);
        var left = GroupNotificationParser.ParseLine(UserLeaveLine, Observed);

        Assert.NotNull(ready?.Member);
        Assert.NotNull(left?.Member);
        Assert.Equal(ready.Member.Key, left.Member.Key);
    }

    [Fact]
    public void ReadsTheQueueEstimateFromAStartGameNotification()
    {
        var observation = GroupNotificationParser.ParseLine(StartGameLine, Observed);

        Assert.NotNull(observation);
        Assert.Equal(GroupObservationKind.MatchStarting, observation.Kind);
        Assert.Equal("ID_2", observation.GroupId);
        Assert.Equal(TimeSpan.FromSeconds(150), observation.QueueEstimate);

        // This notification describes the party, not a person.
        Assert.Null(observation.Member);
    }

    [Fact]
    public void NeverReadsThePlayersOwnNotificationAsAGroupMember()
    {
        // A top-level "profileid" is the game's marker for the signed-in player. Group
        // notifications never carry it, so a payload that does is the player's own and must
        // not come back as a member of their own party.
        Assert.Null(GroupNotificationParser.ParseLine(OwnNotificationLine, Observed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // A line with the right words and no payload at all.
    [InlineData("2026-09-11 22:21:00.000|1.1.5.0.47242|Info|backend|groupMatchRaidReady")]
    // A payload truncated mid-write, which is what a tail of a file being appended to sees.
    [InlineData(LinePrefix + "[{\"type\":\"groupMatchRaidReady\",\"extendedProfile\":{\"_id\":")]
    // Valid JSON of the wrong shape: the root is an object rather than the usual array.
    [InlineData(LinePrefix + "[{}] trailing")]
    public void IgnoresLinesItCannotTrust(string? line) =>
        Assert.Null(GroupNotificationParser.ParseLine(line, Observed));

    [Fact]
    public void IgnoresAGroupNotificationTypeItDoesNotKnow()
    {
        var line = Line("""
            [{"type":"groupMatchSomethingNew","eventId":"ID_1","extendedProfile":{"_id":"ID_2","aid":9041989}}]
            """);

        Assert.Null(GroupNotificationParser.ParseLine(line, Observed));
    }

    [Fact]
    public void IgnoresANotificationThatIsNotAboutAGroupAtAll()
    {
        var line =
            "2026-09-11 22:21:00.000|1.1.5.0.47242|Info|backend|NOTIFICATION [EVENTID] userConfirmed " +
            "[{\"type\":\"userConfirmed\",\"profileid\":\"SELF_1\",\"location\":\"Shoreline\"}]";

        Assert.Null(GroupNotificationParser.ParseLine(line, Observed));
    }

    [Fact]
    public void ReadsALootedDogtagOutOfAnEquipmentItemsArray()
    {
        using var document = JsonDocument.Parse(EquipmentItemsWithDogtag);

        var dogtag = Assert.Single(GroupNotificationParser.ReadDogtags(document.RootElement));

        Assert.Equal("VICTIM_NAME", dogtag.VictimNickname);
        Assert.Equal("Bear", dogtag.VictimSide);
        Assert.Equal(23, dogtag.VictimLevel);
        Assert.Equal("KILLER_NAME", dogtag.KillerNickname);
        Assert.False(dogtag.CarriedByGroupMember);
        Assert.Equal("ID_5", dogtag.SourceItemId);
    }

    [Fact]
    public void KeepsTheMoscowOffsetOnADogtagRatherThanReadingItAsLocalTime()
    {
        using var document = JsonDocument.Parse(EquipmentItemsWithDogtag);

        var dogtag = Assert.Single(GroupNotificationParser.ReadDogtags(document.RootElement));

        Assert.NotNull(dogtag.KilledAt);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 11, 3, 25, 9, 419, TimeSpan.FromHours(3)),
            dogtag.KilledAt);

        // DateTimeOffset equality compares instants, so the offset itself is asserted
        // separately: it is what proves the timestamp was not read as a local wall clock.
        Assert.Equal(TimeSpan.FromHours(3), dogtag.KilledAt.Value.Offset);
    }

    [Fact]
    public void ExposesTheWeaponAsATemplateIdAndNeverAsAName()
    {
        // The game writes "<template id> ShortName", an unresolved localisation key. Showing
        // that string to a player would show them the key; the id is what a caller resolves.
        using var document = JsonDocument.Parse(EquipmentItemsWithDogtag);

        var dogtag = Assert.Single(GroupNotificationParser.ReadDogtags(document.RootElement));

        Assert.Equal("59ff346386f77477562ff5e2", dogtag.WeaponTemplateId);
        Assert.DoesNotContain("ShortName", dogtag.WeaponTemplateId, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsADogtagFromASingleItemAsWellAsFromAWholeArray()
    {
        using var document = JsonDocument.Parse(EquipmentItemsWithDogtag);
        var item = document.RootElement[1];

        var dogtag = Assert.Single(GroupNotificationParser.ReadDogtags(item));

        Assert.Equal("VICTIM_NAME", dogtag.VictimNickname);
    }

    [Fact]
    public void FindsNoDogtagsInAnOrdinaryLoadout()
    {
        using var document = JsonDocument.Parse("""
            [{"_id":"ID_3","_tpl":"55d7217a4bdc2d86028b456d"},
            {"_id":"ID_4","_tpl":"59ff346386f77477562ff5e2","parentId":"ID_3","slotId":"FirstPrimaryWeapon",
            "upd":{"Repairable":{"MaxDurability":93.58,"Durability":92.95}}}]
            """);

        Assert.Empty(GroupNotificationParser.ReadDogtags(document.RootElement));
    }

    [Fact]
    public void NeverHarvestsDogtagsOutOfASquadmatesLoadout()
    {
        // A squadmate's dogtags name players the reader never met, which docs/SAFETY.md puts
        // out of bounds. The dogtag item still appears in the loadout as an ordinary item,
        // because the game shows the reader what their squad is carrying; what it must not
        // carry is the names printed inside it.
        var observation = GroupNotificationParser.ParseLine(RaidReadyLineWithSquadmateDogtag, Observed);

        Assert.NotNull(observation);
        var member = observation.Member;
        Assert.NotNull(member);
        Assert.Contains(member.Equipment, item => item.ItemId == "ID_5");
        Assert.All(member.Equipment, item =>
        {
            Assert.DoesNotContain("VICTIM_NAME", item.TemplateId, StringComparison.Ordinal);
            Assert.DoesNotContain("VICTIM_NAME", item.SlotId ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("KILLER_NAME", item.TemplateId, StringComparison.Ordinal);
        });
    }

    /// <summary>Wraps a payload in the prefix the game writes ahead of it, on one line.</summary>
    private static string Line(string payloadJson) => LinePrefix + OneLine(payloadJson);

    /// <summary>
    /// Folds a payload written across several source lines back onto one.
    /// </summary>
    /// <remarks>
    /// The game writes each notification as a single line. The payloads above are broken up
    /// only so a reviewer can read them, so the line breaks are removed before the parser
    /// ever sees them.
    /// </remarks>
    private static string OneLine(string payloadJson) => payloadJson.ReplaceLineEndings(string.Empty);
}
