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
    /// Everything the game writes ahead of the notification type, including the bracketed
    /// event id that a naive search for the first '[' would find instead of the JSON.
    /// </summary>
    private const string LineHeader =
        "2026-09-11 22:21:00.000|1.1.5.0.47242|Info|output|backend|WebSocketSharp - " +
        "message received: NOTIFICATION [EVENTID] ";

    /// <summary>The header of a ready notification, for the cases that supply no valid payload.</summary>
    private const string LinePrefix = LineHeader + "groupMatchRaidReady ";

    private static readonly string RaidReadyLine = Line("groupMatchRaidReady", """
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

    private static readonly string UserLeaveLine = Line("groupMatchUserLeave", """
        [{"type":"groupMatchUserLeave","eventId":"ID_1","aid":9041989,"Nickname":"PLAYER_A"}]
        """);

    private static readonly string StartGameLine = Line("groupMatchStartGame", """
        [{"type":"groupMatchStartGame","eventId":"ID_1","groupId":"ID_2","estimate":150}]
        """);

    /// <summary>
    /// A group-typed payload carrying the marker the game only puts on the player's own
    /// notifications. Nothing in a real log looks like this; the test exists so that the one
    /// signal separating the player from their squad cannot be removed unnoticed.
    /// </summary>
    private static readonly string OwnNotificationLine = Line("groupMatchRaidReady", """
        [{"type":"groupMatchRaidReady","profileid":"SELF_1","eventId":"ID_1","extendedProfile":{
        "_id":"ID_2","aid":9041989,"Info":{"Nickname":"PLAYER_A","Side":"Bear","Level":24}}}]
        """);

    /// <summary>A squadmate whose own loadout contains a dogtag they looted.</summary>
    private static readonly string RaidReadyLineWithSquadmateDogtag = Line("groupMatchRaidReady", """
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

        Assert.NotNull(ready);
        Assert.NotNull(left);

        var readyMember = ready.Member;
        var departedMember = left.Member;
        Assert.NotNull(readyMember);
        Assert.NotNull(departedMember);
        Assert.Equal(readyMember.Key, departedMember.Key);
        Assert.Equal("aid:9041989", readyMember.Key);
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
    // Well-formed JSON of the right shape that states no type at all.
    [InlineData(LinePrefix + "[{}]")]
    public void IgnoresLinesItCannotTrust(string? line) =>
        Assert.Null(GroupNotificationParser.ParseLine(line, Observed));

    [Fact]
    public void IgnoresAGroupNotificationTypeItDoesNotKnow()
    {
        var line = Line("groupMatchSomethingNew", """
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
    public void NeverHarvestsDogtagsOutOfASquadmatesLoadout()
    {
        // A squadmate's dogtags name players the reader never met, which docs/SAFETY.md puts
        // out of bounds. The dogtag item still appears in the loadout as an ordinary item,
        // because the game shows the reader what their squad is carrying; what it must not
        // carry is the names printed inside it.
        //
        // There is no code left that can read those names, and this is what keeps it that
        // way: the reader that could was removed once it was established that the player's
        // own inventory is never written to these logs, so every dogtag they contain belongs
        // to somebody else.
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

    /// <summary>
    /// [#403] 161 of 161 not-ready notifications on 1.1.5.1.47510 carried only an account id; the
    /// parser looked for extendedProfile, read nothing, and a squadmate who un-readied stayed ready.
    /// </summary>
    [Fact]
    public void ReadsANotReadyNotificationThatNamesOnlyTheAccount()
    {
        var line = Line("groupMatchRaidNotReady", """
            [{"type":"groupMatchRaidNotReady","eventId":"ID_1","aid":9041989}]
            """);

        var observation = GroupNotificationParser.ParseLine(line, Observed);

        Assert.NotNull(observation);
        Assert.Equal(GroupObservationKind.MemberUpdated, observation.Kind);
        var member = observation.Member!;
        Assert.False(member.IsReady);
        Assert.Null(member.Nickname);
        // The same key as the member's earlier ready line, so the two merge into one row.
        Assert.Equal(GroupNotificationParser.ParseLine(RaidReadyLine, Observed)!.Member!.Key, member.Key);
    }

    /// <summary>[#403] 7 of 7 invite-accepted notifications wrote the member at the top level.</summary>
    [Fact]
    public void ReadsTheJoiningMemberFromAnInviteAcceptedNotification()
    {
        var line = Line("groupMatchInviteAccept", """
            [{"type":"groupMatchInviteAccept","eventId":"ID_1","_id":"ID_2","aid":9041990,
            "Info":{"Nickname":"PLAYER_C","Side":"Usec","Level":20},
            "PlayerVisualRepresentation":{"Info":{"Nickname":"PLAYER_C"}},"isLeader":false,"isReady":false}]
            """);

        var observation = GroupNotificationParser.ParseLine(line, Observed);

        Assert.NotNull(observation);
        Assert.Equal(GroupObservationKind.MemberUpdated, observation.Kind);
        var member = observation.Member!;
        Assert.Equal("PLAYER_C", member.Nickname);
        Assert.Equal("Usec", member.Side);
        Assert.Equal(20, member.Level);
        Assert.Equal(9041990, member.AccountId);
        Assert.False(member.IsReady);
        Assert.False(member.IsLeader);
    }

    /// <summary>A not-ready line is not-ready whatever else it says; the type is the statement.</summary>
    [Fact]
    public void ANotReadyNotificationIsNeverReadAsReady()
    {
        var line = Line("groupMatchRaidNotReady", """
            [{"type":"groupMatchRaidNotReady","eventId":"ID_1","extendedProfile":{"aid":9041989,"isReady":true}}]
            """);

        Assert.False(GroupNotificationParser.ParseLine(line, Observed)!.Member!.IsReady);
    }

    /// <summary>Wraps a payload in the header the game writes ahead of it, on one line.</summary>
    private static string Line(string notificationType, string payloadJson) =>
        LineHeader + notificationType + " " + OneLine(payloadJson);

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
