namespace TarkovCompanion.Application.Services.Intelligence;

/// <summary>
/// Whether a weapon and a round are the same caliber, asked of the catalog's caliber strings.
/// </summary>
/// <remarks>
/// The loadout check compared the two strings as they came, ignoring case only, and called any
/// difference "this weapon cannot use this ammunition". Counted on the 2026-09-14 catalog, 171
/// weapons and 200 rounds, 32 weapon calibers:
///
/// Formatting is not what differs. The catalog writes one token per caliber
/// (<c>Caliber545x39</c>) and 30 of the 32 weapon calibers match a round's token exactly. The
/// folding below costs nothing and means a source that writes "5.45x39 mm" will not start
/// flagging every rifle, but on today's data it changes no answer.
///
/// What differs is one alias. The PP-9 Klin is <c>Caliber9x18PMM</c> and every 9x18 round is
/// <c>Caliber9x18PM</c>, including the round named "9x18mm PMM PstM gzh": the catalog files the
/// PMM loading under PM itself, and the Klin is the one item that uses the other token. So the
/// Klin was told it could fire nothing at all. PMM is the hotter loading of the same cartridge
/// and the game chambers both, which is what the alias says.
///
/// The other unmatched weapon caliber, <c>Caliber725</c>, is the RShG-2 launcher: it has no
/// rounds to pick, so it can never be compared and needs nothing here.
/// </remarks>
public static class CaliberKey
{
    /// <summary>Calibers the catalog names twice, keyed and valued as <see cref="Fold"/> writes them.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["9X18PMM"] = "9X18PM",
    };

    /// <summary>Whether two caliber strings name one caliber.</summary>
    public static bool Matches(string weaponCaliber, string ammunitionCaliber) =>
        string.Equals(Of(weaponCaliber), Of(ammunitionCaliber), StringComparison.Ordinal);

    /// <summary>The form two strings for one caliber share: folded, then through the aliases.</summary>
    public static string Of(string caliber)
    {
        ArgumentNullException.ThrowIfNull(caliber);
        var folded = Fold(caliber);
        return Aliases.GetValueOrDefault(folded, folded);
    }

    /// <summary>
    /// Letters and digits only, upper-cased, without the catalog's "Caliber" prefix or a trailing
    /// "mm": "Caliber9x18PM", "9x18 pm" and "9X18-PM" are one string.
    /// </summary>
    private static string Fold(string caliber)
    {
        Span<char> buffer = caliber.Length <= 64 ? stackalloc char[caliber.Length] : new char[caliber.Length];
        var length = 0;
        foreach (var character in caliber)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[length++] = char.ToUpperInvariant(character);
            }
        }

        var folded = new string(buffer[..length]);
        if (folded.StartsWith("CALIBER", StringComparison.Ordinal))
        {
            folded = folded["CALIBER".Length..];
        }

        return folded;
    }
}
