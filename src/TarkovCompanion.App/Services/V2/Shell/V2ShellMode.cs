namespace TarkovCompanion.App.Services.V2.Shell;

/// <summary>
/// Which shell this process starts with. Chosen once, at launch, and never swapped.
/// </summary>
/// <remarks>
/// <see cref="VariantB"/> is now the default launch shell (package 1 of the V2 rough pass);
/// neither preview may replace the other while the process is alive, since hot-swapping would
/// mean two runtime graphs over one database and one screenshot watcher, which is a class of bug
/// this application has already paid for once. Restarting with a different option is the only
/// switch: <c>--ui-shell legacy</c> is the explicit fallback to V1.
///
/// Zero is deliberately undefined, the same rule the V2 contract enums follow, so a value that was
/// never set cannot quietly read as the legacy shell.
/// </remarks>
public enum V2ShellMode
{
    Legacy = 1,
    VariantA,
    VariantB,
}

/// <summary>The launch option's vocabulary, and the one parser for it.</summary>
public static class V2ShellModes
{
    public const string Option = "--ui-shell";
    public const string LegacyToken = "legacy";
    public const string VariantAToken = "v2-a";
    public const string VariantBToken = "v2-b";

    public static IReadOnlyList<string> Tokens { get; } = [LegacyToken, VariantAToken, VariantBToken];

    /// <summary>
    /// The shell a token names, or <see cref="V2ShellMode.Legacy"/> when no token was given.
    /// </summary>
    /// <remarks>
    /// An unknown token is fatal rather than reported and ignored, which is the opposite of how an
    /// unknown option is treated. An unknown option is a flag from a newer build; an unknown shell
    /// is somebody asking for a particular interface, and quietly handing them a different one
    /// would make every screenshot and every comparison taken from that launch a lie about which
    /// variant it shows.
    /// </remarks>
    public static V2ShellMode Parse(string? token)
    {
        if (token is null)
        {
            return V2ShellMode.Legacy;
        }

        return token.Trim().ToLowerInvariant() switch
        {
            LegacyToken => V2ShellMode.Legacy,
            VariantAToken => V2ShellMode.VariantA,
            VariantBToken => V2ShellMode.VariantB,
            _ => throw new ArgumentException(
                $"{Option} must be one of {string.Join(", ", Tokens)}; '{token}' is not a shell this build has.",
                nameof(token)),
        };
    }

    public static string ToToken(this V2ShellMode mode) => mode switch
    {
        V2ShellMode.Legacy => LegacyToken,
        V2ShellMode.VariantA => VariantAToken,
        V2ShellMode.VariantB => VariantBToken,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a defined shell."),
    };

    /// <summary>Whether this is one of the provisional V2 presentations rather than the V1 shell.</summary>
    public static bool IsPreview(this V2ShellMode mode) => mode is V2ShellMode.VariantA or V2ShellMode.VariantB;
}
