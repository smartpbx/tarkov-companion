using System.Text.Json;
using System.Text.RegularExpressions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// Parses ordinary text written by EFT into conservative raid evidence. The parser intentionally
/// ignores unrecognized lines so log format additions do not interrupt observation.
/// </summary>
public sealed partial class EftLogParser
{
    /// <summary>Location tokens learned from synced map data, when available.</summary>
    private volatile IReadOnlyDictionary<string, string>? _syncedAliases;

    /// <summary>The signed-in profile id, which is the PMC one.</summary>
    private volatile string? _pmcProfileId;

    /// <summary>
    /// Replaces the built-in token table with the pairing json.tarkov.dev publishes.
    /// </summary>
    /// <remarks>
    /// Upstream states a map's log token and its normalized name together, so the mapping is
    /// fact rather than guesswork, and new maps arrive with a sync. The fallback table below
    /// only has to carry the application until the first refresh completes.
    /// </remarks>
    public void UpdateAliases(IReadOnlyDictionary<string, string> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        _syncedAliases = aliases.Count == 0
            ? null
            : new Dictionary<string, string>(aliases, StringComparer.OrdinalIgnoreCase);
    }

    private static readonly IReadOnlyDictionary<string, string> FallbackAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bigmap"] = "customs",
            ["customs"] = "customs",
            ["factory4_day"] = "factory",
            ["factory4_night"] = "night-factory",
            ["factory"] = "factory",
            ["interchange"] = "interchange",
            ["laboratory"] = "the-lab",
            ["lighthouse"] = "lighthouse",
            ["rezervbase"] = "reserve",
            ["reserve"] = "reserve",
            ["sandbox"] = "ground-zero",
            ["sandbox_high"] = "ground-zero-21",
            ["shoreline"] = "shoreline",
            ["tarkovstreets"] = "streets-of-tarkov",
            ["terminal"] = "terminal",
            ["woods"] = "woods",
        };

    /// <summary>
    /// Remembers which profile is the player's own.
    /// </summary>
    /// <remarks>
    /// The game logs notifications about the player and about their teammates in the same
    /// files. Only a notification carrying this profile id describes the player, so nothing
    /// is attributed to them without matching it. Teammate notifications are never a source
    /// of raid state.
    /// </remarks>
    public void RememberSelf(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        _pmcProfileId = profileId;
    }

    /// <summary>
    /// Names which side a raid was played on, from the profile that ran it.
    /// </summary>
    /// <remarks>
    /// The account runs raids under two profiles and the logs contain no word for the
    /// difference: "scav", "pmcSide" and "IsScav" appear nowhere as role markers. Only the
    /// profile that is signed in gets a profile-selection line, so that one is the PMC, and a
    /// raid notification carrying any other profile was run as a scav. Reported as inferred
    /// rather than stated, because it rests on that one asymmetry.
    /// </remarks>
    private string? DescribeSide(string profileId) => _pmcProfileId is null
        ? null
        : string.Equals(profileId, _pmcProfileId, StringComparison.Ordinal) ? "PMC" : "scav";

    public RaidEvidence? ParseLine(string? line, DateTimeOffset observedUtc)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        LearnSelfIdentity(line);
        if (TryParseOwnRaidNotification(line, observedUtc) is { } notification)
        {
            return notification;
        }

        var mapId = TryExtractMapId(line);
        var state = SuggestedState(line);
        if (mapId is null && state is null)
        {
            return null;
        }

        var confidence = state switch
        {
            RaidLifecycleState.InRaid => new Confidence(0.95),
            RaidLifecycleState.LoadingRaid or RaidLifecycleState.PostRaid => new Confidence(0.90),
            RaidLifecycleState.Menu => new Confidence(0.85),
            _ when mapId is not null => new Confidence(0.75),
            _ => new Confidence(0.60),
        };

        return new RaidEvidence(
            RaidEvidenceKind.LogLine,
            observedUtc.ToUniversalTime(),
            mapId,
            state,
            confidence,
            Summarize(state, mapId));
    }

    /// <summary>
    /// Recognizes the raid lifecycle from the markers the game actually writes.
    /// </summary>
    /// <remarks>
    /// These were verified against 254 raid records from a real installation. The prose
    /// phrases this previously matched on ("raid started", "player spawned", "matching
    /// completed" and seven others) appear nowhere in a real log; the game writes compact
    /// camel-case markers instead. Only "game stopped" ever fired, and only on an older
    /// build, so raid state was effectively never detected.
    ///
    /// The richest line is TRACE-NetworkGameCreate, which carries the map and the busy/free
    /// status together and lands about a second before GameStarted, so it is the earliest
    /// point at which the companion can follow the player onto the right map.
    /// </remarks>
    private static RaidLifecycleState? SuggestedState(string line)
    {
        // "[Narrate] Game Stopped" was only observed on an older build, so it is kept as a
        // signal but cannot be relied on alone to detect the end of a raid.
        if (ContainsAny(line, "game stopped", "status: free"))
        {
            return RaidLifecycleState.PostRaid;
        }

        if (ContainsAny(line, "status: busy", "gamestarted", "gamespawn"))
        {
            return RaidLifecycleState.InRaid;
        }

        if (ContainsAny(line, "locationloaded", "trace-networkgamecreate"))
        {
            return RaidLifecycleState.LoadingRaid;
        }

        return null;
    }

    private string? TryExtractMapId(string line)
    {
        var match = LocationPattern().Match(line);
        return match.Success ? ResolveMapId(match.Groups["map"].Value) : null;
    }

    private string? ResolveMapId(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var candidate = token.Trim().Replace(' ', '_');
        if (_syncedAliases is { } synced && synced.TryGetValue(candidate, out var syncedMapId))
        {
            return syncedMapId;
        }

        return FallbackAliases.TryGetValue(candidate, out var mapId) ? mapId : null;
    }

    /// <summary>Picks the player's own profile id out of the profile-selection line.</summary>
    private void LearnSelfIdentity(string line)
    {
        if (_pmcProfileId is not null || !line.Contains("SelectedProfile", StringComparison.Ordinal))
        {
            return;
        }

        var match = SelfProfilePattern().Match(line);
        if (match.Success)
        {
            _pmcProfileId = match.Groups["profile"].Value;
        }
    }

    /// <summary>
    /// Reads a raid boundary from the notifications the game logs about the player.
    /// </summary>
    /// <remarks>
    /// These are far better evidence than the surrounding prose. userConfirmed opens a raid
    /// and userMatchOver closes it, both naming the map and both carrying the profile id, so
    /// the pair gives an exact start, end and duration for the player specifically. A close
    /// whose status is Transfer is a move to another map rather than the end of a raid;
    /// treating the two alike would invent a raid that never happened.
    ///
    /// The same files carry notifications describing teammates. Those are never read as the
    /// player's state: without a matching profile id nothing is attributed at all.
    /// </remarks>
    private RaidEvidence? TryParseOwnRaidNotification(string line, DateTimeOffset observedUtc)
    {
        if (!line.Contains("userConfirmed", StringComparison.Ordinal) &&
            !line.Contains("userMatchOver", StringComparison.Ordinal))
        {
            return null;
        }

        // The payload is preceded by "NOTIFICATION <eventId> <type> ", and that event id is
        // itself bracketed. Taking the first '[' therefore grabbed the id and the parse
        // failed every time, which is why raid end never fired: unlike raid start, it has no
        // prose line to fall back on.
        var start = line.IndexOf("[{", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line[start..]);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var payload = document.RootElement[0];
            var type = ReadText(payload, "type");

            // A top-level "profileid" is what marks a notification as the player's own
            // session. Notifications about teammates carry "aid" and "_id" nested under
            // "extendedProfile" and never this key, so its presence is the check rather than
            // its value. Matching the value against the signed-in profile instead was wrong
            // and silently discarded every scav raid: a scav run is a different profile, and
            // it never appears in a profile-selection line.
            var profileId = ReadText(payload, "profileid");
            if (profileId is null)
            {
                return null;
            }

            var mapId = ResolveMapId(ReadText(payload, "location"));
            var status = ReadText(payload, "status");
            var transferred = string.Equals(status, "Transfer", StringComparison.OrdinalIgnoreCase);

            // A transfer proves the raid was a scav run, and is the only thing in these logs
            // that proves side outright.
            //
            // Measured over 66 raids: all 16 transfers were scav, and all 39 PMC raids ended
            // Free. The converse does not hold, because 11 of the 27 scav raids also ended
            // Free, so Free proves nothing and side there still comes from which profile ran
            // the raid. One-way, but it is the one direction that is certain, and it is a
            // check on the profile inference rather than a replacement for it: the two
            // disagreeing means one of them is wrong, and the summary says so rather than
            // quietly picking a winner.
            var inferred = DescribeSide(profileId);
            var contradicted = transferred && string.Equals(inferred, "PMC", StringComparison.Ordinal);
            // Two forms deliberately: the recorded value stays null when the side is not yet
            // knowable, while the sentence still reads naturally.
            var side = transferred ? "scav" : inferred;
            var sideWord = side ?? "raid";
            var sideBasis = side is null
                ? null
                : transferred
                    ? "The game ended this raid with status Transfer, which has only ever appeared on scav runs."
                    : "Inferred from which profile ran the raid. The game records no side directly.";
            return type switch
            {
                "userConfirmed" => new(
                    RaidEvidenceKind.LogLine,
                    observedUtc.ToUniversalTime(),
                    mapId,
                    RaidLifecycleState.InRaid,
                    new Confidence(0.98),
                    mapId is null
                        ? $"The game confirmed a {sideWord} raid."
                        : $"The game confirmed a {sideWord} raid on {mapId}.")
                {
                    Side = side,
                    SideBasis = sideBasis,
                    // The game's own confirmation is the one thing that unambiguously begins a
                    // raid, which matters when the end of the previous one was never seen.
                    StartsNewRaid = true,
                    EventId = ReadText(payload, "eventId"),
                },
                // A transfer ends the raid like any other userMatchOver.
                //
                // This used to hold the raid open, on the reading that Transfer meant transit
                // to another map with the raid continuing. Against a live installation that
                // was wrong in a way that mattered: 16 of 66 raid ends carry this status, and
                // one of them was watched ending a Streets raid with nothing following it for
                // the rest of the session. Holding the raid open lost the end of roughly one
                // raid in four, which is most of what raid tracking is for.
                //
                // What Transfer means in the game is still not established. It lands only on
                // scav runs and on a bit over half of those, which is the shape of a
                // particular kind of scav exit rather than of scav runs in general; that is a
                // guess and is not written down as more. The status is named in the summary
                // rather than hidden, so a player who reads it has the same fact this
                // comment does.
                "userMatchOver" when transferred => new(
                    RaidEvidenceKind.LogLine,
                    observedUtc.ToUniversalTime(),
                    mapId,
                    RaidLifecycleState.PostRaid,
                    new Confidence(0.95),
                    contradicted
                        ? "The game reported the raid as over with status Transfer, which means a scav run, "
                            + "but the profile that ran it is the one used for PMC raids. The two disagree."
                        : $"The game reported the {sideWord} raid as over, with status Transfer.")
                {
                    Side = side,
                    SideBasis = sideBasis,
                },
                "userMatchOver" => new(
                    RaidEvidenceKind.LogLine,
                    observedUtc.ToUniversalTime(),
                    mapId,
                    RaidLifecycleState.PostRaid,
                    new Confidence(0.98),
                    $"The game reported the {sideWord} raid as over.")
                {
                    Side = side,
                    SideBasis = sideBasis,
                },
                _ => null,
            };
        }
        catch (JsonException)
        {
            // A truncated or reshaped notification must not interrupt observation.
            return null;
        }
    }

    private static string? ReadText(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ContainsAny(string line, params string[] markers) =>
        markers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string Summarize(RaidLifecycleState? state, string? mapId) => (state, mapId) switch
    {
        (not null, not null) => $"Log indicates {state} on {mapId}.",
        (not null, null) => $"Log indicates {state}.",
        (null, not null) => $"Log names map {mapId}.",
        _ => "Unrecognized log evidence.",
    };

    [GeneratedRegex(
        // The closing quote before the colon matters: the game writes the map both as
        // "Location: Shoreline" in prose and as "location":"Shoreline" inside JSON.
        @"(?:location|map)(?:id)?['""]?\s*[:=]\s*['""]?(?<map>[a-z0-9_-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocationPattern();

    [GeneratedRegex(
        @"SelectedProfile\s+ProfileId:\s*(?<profile>[A-Za-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SelfProfilePattern();
}
