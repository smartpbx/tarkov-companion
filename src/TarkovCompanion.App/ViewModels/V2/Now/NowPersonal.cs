using TarkovCompanion.Application.Services.Personal;

namespace TarkovCompanion.App.ViewModels.V2.Now;

/// <summary>
/// [#712 2-4] What the player's own history adds to the Now panel: their measured walking pace and
/// the exits they have used on this map and side.
/// </summary>
/// <param name="Pace">Null while too few trails are recorded: the fixed careful pace is used.</param>
/// <param name="ExitUses">Times each exit was used here, by name; a weight on the pick, never a filter.</param>
public sealed record NowPersonal(WalkPace? Pace, IReadOnlyDictionary<string, int> ExitUses)
{
    public static readonly NowPersonal None = new(null, new Dictionary<string, int>());

    public bool IsYourPace => Pace is not null;
}

/// <summary>The exit YOU names, and whether the player's own use of it is why.</summary>
/// <param name="Uses">Times the player used this exit on this map and side.</param>
/// <param name="UsesDecided">A nearer exit would have been named without the player's own use.</param>
public sealed record NowExitPick(NowExit Exit, int Uses, bool UsesDecided);
