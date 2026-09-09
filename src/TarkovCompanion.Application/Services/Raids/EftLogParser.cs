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
    private static readonly IReadOnlyDictionary<string, string> MapAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bigmap"] = "customs",
            ["customs"] = "customs",
            ["factory4_day"] = "factory",
            ["factory4_night"] = "factory",
            ["factory"] = "factory",
            ["interchange"] = "interchange",
            ["laboratory"] = "labs",
            ["labs"] = "labs",
            ["lighthouse"] = "lighthouse",
            ["rezervbase"] = "reserve",
            ["reserve"] = "reserve",
            ["sandbox"] = "ground-zero",
            ["sandbox_high"] = "ground-zero",
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

    private static RaidLifecycleState? SuggestedState(string line)
    {
        if (ContainsAny(line, "raid ended", "game stopped", "profile status: free", "session end"))
        {
            return RaidLifecycleState.PostRaid;
        }

        if (ContainsAny(line, "game started", "raid started", "profile status: busy", "player spawned"))
        {
            return RaidLifecycleState.InRaid;
        }

        if (ContainsAny(line, "raid_loading", "loading raid", "matching completed", "loading location"))
        {
            return RaidLifecycleState.LoadingRaid;
        }

        if (ContainsAny(line, "entered menu", "main menu", "profile selected"))
        {
            return RaidLifecycleState.Menu;
        }

        return null;
    }

    private static string? TryExtractMapId(string line)
    {
        var match = LocationPattern().Match(line);
        if (!match.Success)
        {
            return null;
        }

        var candidate = match.Groups["map"].Value.Trim().Replace(' ', '_');
        return MapAliases.TryGetValue(candidate, out var mapId) ? mapId : null;
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
