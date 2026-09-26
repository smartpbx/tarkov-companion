using System.Text.Json;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// Reads the group notifications the game logs about the player's own party, and the looted
/// dogtags it writes into inventory blobs.
/// </summary>
/// <remarks>
/// The game writes these into its backend log, the same file the raid-boundary notifications
/// come from, so this adds no newly opened file. It is opened for these features and nothing
/// else; the push-notifications log stays closed.
///
/// Only the player's own party and the player's own looted dogtags are in scope. The party is
/// what the game already shows the player in the raid, and a dogtag prints its names on the
/// item the player is carrying. Nothing read here is transmitted, bundled, or written into an
/// exported file, and nothing here is ever attributed to the player: group notifications are
/// about other people by construction, which is why <see cref="ParseLine"/> refuses any
/// payload that carries the player's own marker.
///
/// Like <see cref="EftLogParser"/>, this tolerates shapes it does not recognize and returns
/// null rather than throwing, so a log format change cannot interrupt observation.
/// </remarks>
public static class GroupNotificationParser
{
    /// <summary>
    /// The prefix every observed group notification type shares, used to reject other lines
    /// before any JSON work happens.
    /// </summary>
    private const string GroupTypeMarker = "groupMatch";

    /// <summary>The largest queue estimate that is worth believing, in seconds.</summary>
    private const double MaxQueueSeconds = 86_400;

    /// <summary>The largest value <see cref="DateTimeOffset.FromUnixTimeSeconds"/> accepts.</summary>
    private const long MaxUnixSeconds = 253_402_300_799;

    /// <summary>
    /// Reads one log line as a change to the player's party, or returns null.
    /// </summary>
    /// <remarks>
    /// This is called on every line the log watcher sees, so it rejects the overwhelming
    /// majority on a single ordinal substring scan before touching the JSON parser.
    ///
    /// Callers must debounce the result. Ready and not-ready notifications fire on every
    /// readiness toggle by any member, hundreds of times per session, a ready one carrying that
    /// member's entire fifty-item inventory. The right consumption is a dictionary keyed by
    /// <see cref="GroupMember.Key"/> holding the latest state per member, refreshed on a
    /// timer, not a stream of events pushed at the UI as they arrive.
    /// </remarks>
    /// <param name="line">A raw log line, which may be anything at all.</param>
    /// <param name="observedUtc">When the line was read; normalized to UTC on the result.</param>
    /// <returns>What the notification said, or null if the line was not a usable group notification.</returns>
    public static GroupObservation? ParseLine(string? line, DateTimeOffset observedUtc)
    {
        if (string.IsNullOrEmpty(line) || !line.Contains(GroupTypeMarker, StringComparison.Ordinal))
        {
            return null;
        }

        // The payload is preceded by "NOTIFICATION <eventId> <type> " and that event id is
        // itself bracketed, so searching for a bare '[' finds the id and never the JSON.
        var start = line.IndexOf("[{", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line[start..]);
        }
        catch (JsonException)
        {
            // A truncated or reshaped notification must not interrupt observation.
            return null;
        }

        using (document)
        {
            return ReadPayload(document.RootElement, observedUtc.ToUniversalTime());
        }
    }

    private static GroupObservation? ReadPayload(JsonElement root, DateTimeOffset observedUtc)
    {
        // Every observed payload is an array of exactly one object, but the length is read
        // rather than assumed so a future batched notification degrades to its first entry.
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            return null;
        }

        var payload = root[0];
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // A top-level "profileid" is the game's marker for a notification about the signed-in
        // player. Group notifications never carry one; their identity is nested under
        // extendedProfile as "_id" and "aid". That asymmetry is the only thing separating the
        // player from their squadmates in these files, so a payload carrying the player's
        // marker is refused outright rather than being read as a member of their own party.
        if (payload.TryGetProperty("profileid", out _))
        {
            return null;
        }

        var type = ReadText(payload, "type");
        if (type is null)
        {
            return null;
        }

        return type switch
        {
            "groupMatchRaidReady" or "groupMatchRaidNotReady" or "groupMatchInviteAccept" =>
                ReadMemberUpdate(payload, type, observedUtc),
            "groupMatchUserLeave" or "groupMatchWasRemoved" =>
                ReadMemberDeparture(payload, type, observedUtc),
            "groupMatchStartGame" => ReadMatchStart(payload, type, observedUtc),
            _ => null,
        };
    }

    private static GroupObservation? ReadMemberUpdate(JsonElement payload, string type, DateTimeOffset observedUtc)
    {
        // Three shapes, measured on 1.1.5.1.47510 (#403, 2026-09-22..25): RaidReady nests the
        // member under extendedProfile (187 of 187); InviteAccept writes the same fields at the
        // top level (7 of 7); RaidNotReady carries only the account id (161 of 161). Reading
        // only the first shape left every un-readied squadmate showing as ready.
        var profile = TryGetObject(payload, "extendedProfile", out var nested) ? nested : payload;

        var memberId = ReadText(profile, "_id");
        var accountId = ReadInt64(profile, "aid");
        if (memberId is null && accountId is null)
        {
            // Without an identifier the record cannot be keyed to a person, and an unkeyed
            // member would accumulate as a duplicate on every readiness toggle.
            return null;
        }

        // The game writes the same Info object twice, byte for byte: once at
        // extendedProfile.Info and again at extendedProfile.PlayerVisualRepresentation.Info.
        // The shallower one is read and the nested duplicate is ignored, so that a future
        // divergence between the two shows up as missing data rather than as two sources
        // quietly disagreeing.
        _ = TryGetObject(profile, "Info", out var info);

        var member = new GroupMember(
            memberId,
            accountId,
            ReadText(info, "Nickname"),
            ReadText(info, "Side"),
            ReadInt32(info, "Level"),
            ReadBool(profile, "isLeader"),
            ReadinessOf(type, ReadBool(profile, "isReady")),
            ReadUnixSeconds(info, "SavageLockTime"),
            ReadEquipment(profile));

        return new GroupObservation(GroupObservationKind.MemberUpdated, type, observedUtc, member);
    }

    /// <summary>
    /// The notification's type is itself a statement of readiness, and outranks the field: a
    /// not-ready notification carries no "isReady" at all, and a member it names is not ready.
    /// </summary>
    private static bool? ReadinessOf(string type, bool? stated) => type switch
    {
        "groupMatchRaidNotReady" => false,
        "groupMatchRaidReady" => stated ?? true,
        _ => stated,
    };

    private static GroupObservation? ReadMemberDeparture(JsonElement payload, string type, DateTimeOffset observedUtc)
    {
        // The leave payload is compact: an account id and a nickname at the top level, with no
        // extendedProfile and no profile id. The account id is therefore the only thing that
        // ties a departure back to the ready notifications for the same person.
        //
        // groupMatchWasRemoved was never observed on a live installation, so it is read on the
        // same tolerant path: it is reported when it names somebody and ignored when it does
        // not, rather than being given an invented meaning.
        var memberId = ReadText(payload, "_id");
        var accountId = ReadInt64(payload, "aid");
        if (memberId is null && accountId is null)
        {
            return null;
        }

        // Nothing but identity is stated here. Readiness and leadership are left null instead
        // of defaulted, because a departed member whose UI row reads "not ready" would be
        // showing a fact the game never wrote.
        var member = new GroupMember(
            memberId,
            accountId,
            ReadText(payload, "Nickname"),
            Side: null,
            Level: null,
            IsLeader: null,
            IsReady: null,
            ScavLockedUntil: null,
            Equipment: []);

        return new GroupObservation(GroupObservationKind.MemberLeft, type, observedUtc, member);
    }

    private static GroupObservation ReadMatchStart(JsonElement payload, string type, DateTimeOffset observedUtc)
    {
        // "estimate" is the queue time the game predicts, in seconds. It describes the party
        // rather than any person, so this notification carries no member at all.
        TimeSpan? queueEstimate = null;
        if (ReadDouble(payload, "estimate") is { } seconds && seconds is >= 0 and <= MaxQueueSeconds)
        {
            queueEstimate = TimeSpan.FromSeconds(seconds);
        }

        return new GroupObservation(
            GroupObservationKind.MatchStarting,
            type,
            observedUtc,
            Member: null,
            GroupId: ReadText(payload, "groupId"),
            QueueEstimate: queueEstimate);
    }

    /// <summary>
    /// Reads a member's loadout as the flat list the game states.
    /// </summary>
    /// <remarks>
    /// Equipment lives only under PlayerVisualRepresentation, unlike Info which is duplicated.
    ///
    /// Dogtags inside this inventory are not read. They belong to the squadmate who looted
    /// them and name players the reader never met, which is outside what the game shows the
    /// reader in the raid.
    /// </remarks>
    private static IReadOnlyList<GroupEquipmentItem> ReadEquipment(JsonElement profile)
    {
        if (!TryGetObject(profile, "PlayerVisualRepresentation", out var representation) ||
            !TryGetObject(representation, "Equipment", out var equipment) ||
            !equipment.TryGetProperty("Items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var loadout = new List<GroupEquipmentItem>(items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
        {
            var itemId = ReadText(item, "_id");
            var templateId = ReadText(item, "_tpl");
            if (itemId is null || templateId is null)
            {
                continue;
            }

            loadout.Add(new GroupEquipmentItem(
                itemId,
                templateId,
                ReadText(item, "parentId"),
                ReadText(item, "slotId")));
        }

        return loadout;
    }

    /// <summary>Reads a Unix-seconds field as an instant, treating 0 as "no lock".</summary>
    private static DateTimeOffset? ReadUnixSeconds(JsonElement element, string property)
    {
        var seconds = ReadInt64(element, property);
        return seconds is > 0 and <= MaxUnixSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value)
            : null;
    }

    private static bool TryGetObject(JsonElement element, string property, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        // An undefined element reads as absent from every accessor below, so an absent branch
        // costs the caller no extra null handling.
        value = default;
        return false;
    }

    private static string? ReadText(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadInt64(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : null;

    private static int? ReadInt32(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    private static double? ReadDouble(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number)
            ? number
            : null;

    private static bool? ReadBool(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        return value.ValueKind == JsonValueKind.False ? false : null;
    }
}
