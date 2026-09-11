using System.Globalization;

namespace TarkovCompanion.Core.Domain.Raids;

/// <summary>
/// What a group notification said had happened.
/// </summary>
/// <remarks>
/// Six group notification types exist and they collapse into three outcomes a caller treats
/// differently. Switching on this rather than on the raw type string means a type the game
/// adds later either lands in one of these buckets or is ignored, instead of reaching the UI
/// as an unhandled string.
/// </remarks>
public enum GroupObservationKind
{
    /// <summary>A member's readiness, level or loadout was restated.</summary>
    MemberUpdated,

    /// <summary>A member is no longer in the party.</summary>
    MemberLeft,

    /// <summary>The party entered matchmaking together.</summary>
    MatchStarting,
}

/// <summary>
/// One entry from a party member's loadout, kept in the shape the game states it.
/// </summary>
/// <remarks>
/// The game writes equipment as a flat array whose entries point at their container through
/// <see cref="ParentItemId"/> and <see cref="SlotId"/>, not as a tree. Keeping it flat means
/// this layer never invents a hierarchy the log did not state; a caller that wants a tree can
/// build one and own the guesses that go with it.
///
/// Only identity is carried. The durability, fire-mode and stack details the game also writes
/// into each item's "upd" block are dropped, because showing a squadmate's loadout does not
/// need them and the narrowest read of another person's data is the right one.
/// </remarks>
/// <param name="ItemId">The item's own id, unique within the loadout.</param>
/// <param name="TemplateId">
/// The item template id ("_tpl"). It is an id, not a name: a caller resolves it against synced
/// item data to display anything.
/// </param>
/// <param name="ParentItemId">The id of the container this sits in, absent for the root.</param>
/// <param name="SlotId">The named slot within that container, for example FirstPrimaryWeapon.</param>
public sealed record GroupEquipmentItem(
    string ItemId,
    string TemplateId,
    string? ParentItemId,
    string? SlotId);

/// <summary>
/// A member of the player's own party, as one notification described them.
/// </summary>
/// <remarks>
/// This is the party the game already shows the player in the raid, which is why it may be
/// read at all. It is never the player's own record: the game marks its own notifications
/// with a top-level profile id and group notifications never carry one, so the two are told
/// apart by that asymmetry and nothing here is ever displayed as the player themselves.
///
/// Everything except the identifiers is optional because notifications differ in how much
/// they state. A field the notification did not carry stays null rather than being filled
/// with a plausible default; a squad list showing "not ready" for someone whose notification
/// never mentioned readiness would be an invention.
///
/// Health, game version, prestige, member category and scav nickname are all present in the
/// source and deliberately absent here. Nothing in the product needs them.
/// </remarks>
/// <param name="MemberId">The member's profile id, absent from the compact leave notifications.</param>
/// <param name="AccountId">The member's numeric account id, carried by every observed shape.</param>
/// <param name="Nickname">The member's PMC nickname.</param>
/// <param name="Side">Bear or Usec, verbatim from the game.</param>
/// <param name="Level">The member's PMC level.</param>
/// <param name="IsLeader">Whether the member leads the party; null when unstated.</param>
/// <param name="IsReady">Whether the member is ready; null when unstated.</param>
/// <param name="ScavLockedUntil">When the member's scav becomes available again; null when free or unstated.</param>
/// <param name="Equipment">The member's loadout, flat, possibly empty.</param>
public sealed record GroupMember(
    string? MemberId,
    long? AccountId,
    string? Nickname,
    string? Side,
    int? Level,
    bool? IsLeader,
    bool? IsReady,
    DateTimeOffset? ScavLockedUntil,
    IReadOnlyList<GroupEquipmentItem> Equipment)
{
    /// <summary>
    /// The stable identity to deduplicate and index members by.
    /// </summary>
    /// <remarks>
    /// Ready and not-ready notifications fire on every toggle by any member, so the same
    /// person arrives hundreds of times per session and a caller keeps one entry per member
    /// rather than a list of events. Value equality cannot do that job: the records differ
    /// whenever a loadout changes, and <see cref="Equipment"/> compares by reference anyway.
    ///
    /// The two notification shapes do not carry the same identifiers. The full shape has both
    /// a profile id and an account id; the compact leave shape has only the account id. The
    /// account id is therefore preferred so that a member's leave event keys to the same
    /// person as their earlier ready events. The prefix keeps the two id spaces from ever
    /// colliding.
    ///
    /// A member with neither identifier cannot be keyed, and is never produced: the parser
    /// discards such a notification instead of emitting an anonymous member.
    /// </remarks>
    public string Key => AccountId is { } accountId
        ? "aid:" + accountId.ToString(CultureInfo.InvariantCulture)
        : "pid:" + MemberId;
}

/// <summary>
/// A dogtag looted from a body, as the item itself records it.
/// </summary>
/// <remarks>
/// This describes a player the reader has not necessarily met, so it may only be read from an
/// inventory the player owns: the dogtag they are carrying prints these names on its face in
/// game, which is what makes it theirs to see. Reading the same structure out of somebody
/// else's inventory would describe a player the reader never encountered, and is out of
/// bounds. Nothing here is ever transmitted or written into an exported file.
///
/// The victim's and killer's account and profile ids are all present in the source and
/// deliberately not carried here. Nicknames are what the item shows; the numeric ids are what
/// would let records be joined across raids into a picture of somebody, which is the line
/// this project does not cross.
/// </remarks>
/// <param name="VictimNickname">The dead player's nickname, as printed on the tag.</param>
/// <param name="VictimSide">Bear or Usec, verbatim from the game.</param>
/// <param name="VictimLevel">The dead player's level at the time.</param>
/// <param name="KillerNickname">Who killed them, which is usually but not always the reader.</param>
/// <param name="KilledAt">
/// When the kill happened, as an instant. The game writes this with a +03:00 offset rather
/// than in the reader's time zone, so it is kept as a <see cref="DateTimeOffset"/> and
/// converted for display rather than being read as a local wall clock.
/// </param>
/// <param name="WeaponTemplateId">
/// The template id of the weapon used, or null when the game stated something that was not a
/// template id. The game writes this field as an unresolved localisation key, not a name, so
/// a caller resolves the id against synced item data; there is no weapon name to display here.
/// </param>
/// <param name="CarriedByGroupMember">The game's own flag, passed through unchanged.</param>
/// <param name="SourceItemId">
/// The inventory item id the tag came from, when stated. It is the natural key for keeping one
/// entry per tag while the same inventory is restated over and over.
/// </param>
public sealed record DogtagObservation(
    string VictimNickname,
    string? VictimSide,
    int? VictimLevel,
    string? KillerNickname,
    DateTimeOffset? KilledAt,
    string? WeaponTemplateId,
    bool CarriedByGroupMember,
    string? SourceItemId = null);

/// <summary>
/// One group notification, read into the smallest form the product needs.
/// </summary>
/// <remarks>
/// <see cref="Member"/> is present for <see cref="GroupObservationKind.MemberUpdated"/> and
/// <see cref="GroupObservationKind.MemberLeft"/> and absent for
/// <see cref="GroupObservationKind.MatchStarting"/>, which describes the party rather than a
/// person. A notification that carried no usable identity is not reported at all, so a member
/// observation always names somebody.
/// </remarks>
/// <param name="Kind">What happened.</param>
/// <param name="NotificationType">The game's own type string, kept for diagnostics.</param>
/// <param name="ObservedUtc">When the line was read, not when the game wrote it.</param>
/// <param name="Member">Who it was about, when it was about a person.</param>
/// <param name="GroupId">The party id, stated only when the match starts.</param>
/// <param name="QueueEstimate">The queue time the game predicted, stated only when the match starts.</param>
public sealed record GroupObservation(
    GroupObservationKind Kind,
    string NotificationType,
    DateTimeOffset ObservedUtc,
    GroupMember? Member,
    string? GroupId = null,
    TimeSpan? QueueEstimate = null);
