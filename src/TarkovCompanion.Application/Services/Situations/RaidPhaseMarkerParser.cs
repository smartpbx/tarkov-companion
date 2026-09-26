using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Situations;

/// <summary>Reads the lines that split "getting into a raid" into matching, loading, spawning and started.</summary>
/// <remarks>
/// Only the game's own <c>application</c> log markers, matched on their fixed prefix. Measured on the
/// 2026-09-23..25 sessions: every raid wrote <c>Matching with group id:</c> when Ready was pressed
/// (solo too, with the account id as the group), then the scene preset, then
/// <c>MatchingCompleted:</c>, <c>LocationLoaded:</c> and <c>GameStarted:</c>. An offline or transit raid
/// writes <c>MatchingCompleted:0 real:0</c>, which <see cref="Raids.LoadTimeParser"/> rightly refuses
/// as a queue time but which still says matching is over, so this does not share that parser's filter.
/// The #403 re-measure (1.1.5.1.47510, 55 raids) added the three queue steps and the two spawn lines;
/// they do not always arrive in order (a queue step after MatchingCompleted, LocationLoaded before it),
/// which is why <see cref="SituationFolder"/> ranks stages rather than trusting the last line.
/// </remarks>
public static class RaidPhaseMarkerParser
{
    public static RaidPhaseMarker? ParseLine(string? line, DateTimeOffset observedUtc)
    {
        if (string.IsNullOrEmpty(line) || !line.Contains("|application|", StringComparison.Ordinal))
        {
            return null;
        }

        RaidPhaseMarkerKind? kind =
            line.Contains("|Matching with group id:", StringComparison.Ordinal) ? RaidPhaseMarkerKind.MatchingStarted
            : line.Contains("|MatchingCompleted:", StringComparison.Ordinal) ? RaidPhaseMarkerKind.MatchingCompleted
            : line.Contains("|LocationLoaded:", StringComparison.Ordinal) ? RaidPhaseMarkerKind.LocationLoaded
            : line.Contains("|GameStarted:", StringComparison.Ordinal) ? RaidPhaseMarkerKind.GameStarted
            : line.Contains("|TRACE-NetworkGameMatching ", StringComparison.Ordinal) ? RaidPhaseMarkerKind.MatchingStep
            : line.Contains("|GameSpawn:", StringComparison.Ordinal) ? RaidPhaseMarkerKind.Spawning
            : line.Contains("|GameSpawned:", StringComparison.Ordinal) ? RaidPhaseMarkerKind.Spawned
            : null;
        return kind is { } found ? new RaidPhaseMarker(found, observedUtc.ToUniversalTime()) : null;
    }
}
