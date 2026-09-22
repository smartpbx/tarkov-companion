namespace TarkovCompanion.Application.Services.Intelligence;

/// <summary>Turns the upstream caliber token into something a player can read.</summary>
/// <remarks>
/// json.tarkov.dev writes calibers as "Caliber556x45NATO". Only the prefix is dropped: the digits
/// are left exactly as the source wrote them, because inserting the decimal point a player expects
/// would be this app guessing at a value it was never given. Shared so Ammo and Loadout say the
/// same thing; Loadout used to print the raw token in its rows and issues.
/// </remarks>
public static class CaliberText
{
    private const string Prefix = "Caliber";

    public static string Describe(string caliber) =>
        caliber.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && caliber.Length > Prefix.Length
            ? caliber[Prefix.Length..]
            : caliber;
}
