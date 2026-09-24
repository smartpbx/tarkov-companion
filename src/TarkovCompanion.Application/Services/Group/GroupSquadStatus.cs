namespace TarkovCompanion.Application.Services.Group;

/// <summary>
/// [#289] What the player told the squad from the Team workspace: an extract, a note, ready or not.
/// </summary>
/// <param name="Ready">True ready, false not ready, null when they have not said.</param>
/// <param name="Extract">The extract they plan to leave by, by the name the map catalog uses.</param>
/// <param name="ExtractMapId">The map that extract is on; it is sent only while that map is theirs.</param>
/// <param name="Note">A short line for the squad.</param>
public sealed record SquadStatus(bool? Ready, string? Extract, string? ExtractMapId, string? Note)
{
    public static SquadStatus None { get; } = new(null, null, null, null);

    /// <summary>The relay's bound on a note, which the text box enforces too.</summary>
    public const int NoteLimit = 120;

    /// <summary>The relay's bound on an extract name, the same as an offered extract's.</summary>
    public const int ExtractLimit = 64;

    /// <summary>Trimmed, with an empty string as nothing and anything longer than the relay takes cut.</summary>
    public SquadStatus Normalized() => new(
        Ready,
        Clip(Extract, ExtractLimit),
        Clip(Extract, ExtractLimit) is null ? null : Clip(ExtractMapId, 64),
        Clip(Note, NoteLimit));

    /// <summary>
    /// The extract to publish while the player's raid is on <paramref name="currentMapId"/>.
    /// </summary>
    /// <remarks>
    /// A plan for Customs said while the player is on Woods would read as a plan for Woods, so a
    /// chosen extract travels only with its own map, or while the player is on no map at all
    /// (planning in the menu, which is when a squad agrees where to leave).
    /// </remarks>
    public string? ExtractFor(string? currentMapId) =>
        Extract is null ||
        currentMapId is not { Length: > 0 } ||
        ExtractMapId is null ||
        string.Equals(ExtractMapId, currentMapId, StringComparison.OrdinalIgnoreCase)
            ? Extract
            : null;

    private static string? Clip(string? value, int limit)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > limit ? trimmed[..limit] : trimmed;
    }
}

/// <summary>
/// [#289] Holds <see cref="SquadStatus"/> and tells the group session the moment it changes.
/// </summary>
/// <remarks>
/// Kept for the session only. "Ready" is a question about the next few minutes, and one that
/// survived a restart would tell a squad somebody was ready who had not said so tonight.
/// </remarks>
public sealed class GroupSquadStatus
{
    private SquadStatus _current = SquadStatus.None;

    public SquadStatus Current => Volatile.Read(ref _current);

    /// <summary>Raised after <see cref="Current"/> changes, so the group hears now rather than on a tick.</summary>
    public event Action? Changed;

    public void Set(SquadStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var normalized = status.Normalized();
        if (Interlocked.Exchange(ref _current, normalized) == normalized)
        {
            return;
        }

        Changed?.Invoke();
    }
}
