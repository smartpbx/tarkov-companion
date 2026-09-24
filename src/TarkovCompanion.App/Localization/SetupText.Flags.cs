using TarkovCompanion.Core.Features;

namespace TarkovCompanion.App.Localization;

/// <summary>Feature flag titles and descriptions, by the flag's key (#314).</summary>
/// <remarks>
/// Core keeps its English on the definition, which is what the diagnostics and the flags file
/// name. A flag added there without a table entry still shows its own English here.
/// </remarks>
public static partial class SetupText
{
    public static string FlagTitle(FeatureFlagDefinition flag)
    {
        ArgumentNullException.ThrowIfNull(flag);
        var key = $"Setup.Flag.{flag.Key}.Title";
        return UiText.English.ContainsKey(key) ? UiText.Get(key) : flag.Title;
    }

    public static string FlagDescription(FeatureFlagDefinition flag)
    {
        ArgumentNullException.ThrowIfNull(flag);
        var key = $"Setup.Flag.{flag.Key}.Description";
        return UiText.English.ContainsKey(key) ? UiText.Get(key) : flag.Description;
    }
}
