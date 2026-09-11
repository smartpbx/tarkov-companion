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

    public RaidEvidence? ParseLine(string? line, DateTimeOffset observedUtc)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
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
        if (!match.Success)
        {
            return null;
        }

        var candidate = match.Groups["map"].Value.Trim().Replace(' ', '_');
        if (_syncedAliases is { } synced && synced.TryGetValue(candidate, out var syncedMapId))
        {
            return syncedMapId;
        }

        return FallbackAliases.TryGetValue(candidate, out var mapId) ? mapId : null;
    }

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
        @"(?:location|map)(?:id)?\s*[:=]\s*['""]?(?<map>[a-z0-9_-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocationPattern();
}
