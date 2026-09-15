using Avalonia.Styling;

namespace TarkovCompanion.App.Themes.V2;

public enum V2AppearancePreference
{
    System,
    Light,
    Dark,
    HighContrast,
}

public enum V2ColorVisionPreference
{
    Standard,
    RedGreenSafe,
    BlueYellowSafe,
    Monochrome,
}

/// <summary>
/// The theme variants that V2ThemeVariants.axaml is keyed by, and the one rule that picks one.
/// </summary>
/// <remarks>
/// Avalonia's type converter accepts only Default, Light, and Dark as theme-dictionary keys. The
/// first version of this palette keyed a dictionary with the bare string "HighContrast", which
/// compiled and would have thrown the first time anything loaded it. Custom variants therefore
/// live here and the dictionaries refer to them through x:Static. Each one inherits Light or Dark,
/// so a colour-vision dictionary only has to override the status roles it changes.
///
/// Persisting the preference belongs to #270 and applying the variant to a host belongs to #267;
/// this class only decides which variant a preference means.
/// </remarks>
public static class V2Appearance
{
    public static ThemeVariant HighContrast { get; } = new("V2HighContrast", ThemeVariant.Dark);

    public static ThemeVariant DarkRedGreenSafe { get; } = new("V2DarkRedGreenSafe", ThemeVariant.Dark);

    public static ThemeVariant LightRedGreenSafe { get; } = new("V2LightRedGreenSafe", ThemeVariant.Light);

    public static ThemeVariant DarkBlueYellowSafe { get; } = new("V2DarkBlueYellowSafe", ThemeVariant.Dark);

    public static ThemeVariant LightBlueYellowSafe { get; } = new("V2LightBlueYellowSafe", ThemeVariant.Light);

    public static ThemeVariant DarkMonochrome { get; } = new("V2DarkMonochrome", ThemeVariant.Dark);

    public static ThemeVariant LightMonochrome { get; } = new("V2LightMonochrome", ThemeVariant.Light);

    /// <summary>
    /// Resolves a stored preference against what the operating system currently reports.
    /// </summary>
    /// <remarks>
    /// An operating-system contrast theme wins over any in-app appearance choice, because a person
    /// who turned it on did so for every application. The high-contrast palette is checked in CI
    /// against all three simulated colour-vision deficiencies, so it has no colour-vision variants.
    /// </remarks>
    public static ThemeVariant Resolve(
        V2AppearancePreference appearance,
        V2ColorVisionPreference colorVision,
        bool systemPrefersDark,
        bool systemRequestsHighContrast)
    {
        if (appearance == V2AppearancePreference.HighContrast || systemRequestsHighContrast)
        {
            return HighContrast;
        }

        var dark = appearance switch
        {
            V2AppearancePreference.Dark => true,
            V2AppearancePreference.Light => false,
            _ => systemPrefersDark,
        };

        return colorVision switch
        {
            V2ColorVisionPreference.RedGreenSafe => dark ? DarkRedGreenSafe : LightRedGreenSafe,
            V2ColorVisionPreference.BlueYellowSafe => dark ? DarkBlueYellowSafe : LightBlueYellowSafe,
            V2ColorVisionPreference.Monochrome => dark ? DarkMonochrome : LightMonochrome,
            _ => dark ? ThemeVariant.Dark : ThemeVariant.Light,
        };
    }
}
