namespace TarkovCompanion.Core.Domain.Raids;

/// <summary>Which step of getting into a raid a log line marks.</summary>
/// <remarks>
/// The raid state itself has only "loading" for everything between the menu and the raid, and it
/// enters "in raid" on the <c>profileStatus Busy</c> line, which the game writes about a minute
/// before the player can move. A player watching the queue and a player watching a loading bar
/// want different things on screen, so these four markers are kept apart (ADR 0022).
/// </remarks>
public enum RaidPhaseMarkerKind
{
    /// <summary><c>Matching with group id:</c>, written when the player presses Ready.</summary>
    MatchingStarted,

    /// <summary><c>MatchingCompleted:</c>, the server found the raid; loading begins.</summary>
    MatchingCompleted,

    /// <summary><c>LocationLoaded:</c>, the map is loaded; the game waits for the other players.</summary>
    LocationLoaded,

    /// <summary><c>GameStarted:</c>, the player is in the raid and can move.</summary>
    GameStarted,
}

/// <summary>One of those markers, and when the log wrote it.</summary>
public sealed record RaidPhaseMarker(RaidPhaseMarkerKind Kind, DateTimeOffset ObservedUtc);
