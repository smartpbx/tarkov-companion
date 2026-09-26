using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Situations;

/// <summary>Reads the four lines that split "getting into a raid" into matching, loading and started.</summary>
/// <remarks>
/// Only the game's own <c>application</c> log markers, matched on their fixed prefix. Measured on the
/// 2026-09-23..25 sessions: every raid wrote <c>Matching with group id:</c> when Ready was pressed
/// (solo too, with the account id as the group), then the scene preset, then
/// <c>MatchingCompleted:</c>, <c>LocationLoaded:</c> and <c>GameStarted:</c>. An offline or transit raid
/// writes <c>MatchingCompleted:0 real:0</c>, which <see cref="Raids.LoadTimeParser"/> rightly refuses
/// as a queue time but which still says matching is over, so this does not share that parser's filter.
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
            : null;
        return kind is { } found ? new RaidPhaseMarker(found, observedUtc.ToUniversalTime()) : null;
    }
}
