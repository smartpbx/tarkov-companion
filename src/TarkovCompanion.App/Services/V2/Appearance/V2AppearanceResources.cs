using TarkovCompanion.Core.Domain.Personalization;

namespace TarkovCompanion.App.Services.V2.Appearance;

/// <summary>
/// What one appearance record means, as resource key to value.
/// </summary>
/// <remarks>
/// Separate from the applier, and pure, so the arithmetic can be measured without an Avalonia
/// application: the thing worth testing is that 200% actually doubles the type ramp and that
/// Comfortable actually widens the default gap, not that a dictionary assignment happened.
///
/// <para>
/// The baseline is read from the loaded token dictionary rather than restated here. Restating it
/// would give the design system two sources for the same number, and the one in code would be the
/// one nobody updates. The applier captures the baseline once, before it has overridden anything.
/// </para>
/// </remarks>
public static class V2AppearanceResources
{
    /// <summary>The type ramp the text scale multiplies, size and line height together.</summary>
    /// <remarks>
    /// Line heights scale with their sizes. Scaling the size alone is the classic way to make a
    /// 200% setting produce overlapping text: Avalonia honours an explicit LineHeight even when it
    /// is smaller than the glyphs.
    /// </remarks>
    public static IReadOnlyList<string> ScaledTypeKeys { get; } =
    [
        "V2.Type.Heading1.Size",
        "V2.Type.Heading1.LineHeight",
        "V2.Type.Heading2.Size",
        "V2.Type.Heading2.LineHeight",
        "V2.Type.Heading3.Size",
        "V2.Type.Heading3.LineHeight",
        "V2.Type.Body.Size",
        "V2.Type.Body.LineHeight",
        "V2.Type.Label.Size",
        "V2.Type.Label.LineHeight",
        // The V1 instrument ramp too. The V2 shell still hosts V1 pages and V1-classed text
        // inside its own workspaces, and a text-size setting that moved half the words on a page
        // reads as a bug rather than as a setting.
        "Instrument.Type.Display.Size",
        "Instrument.Type.Title.Size",
        "Instrument.Type.Metric.Size",
        "Instrument.Type.Lede.Size",
        "Instrument.Type.SectionTitle.Size",
        "Instrument.Type.Body.Size",
        "Instrument.Type.Label.Size",
        "Instrument.Type.Caption.Size",
    ];

    /// <summary>
    /// The density the styles actually ask for, whatever the player chose.
    /// </summary>
    /// <remarks>
    /// Every V2 style asks for <c>V2.Density.Standard.*</c>; the Compact and Comfortable tokens
    /// exist for the two opt-in classes. Rather than re-tagging hundreds of controls, the chosen
    /// density is copied over the Standard token, so "standard" means "whatever the player set".
    /// </remarks>
    public static IReadOnlyList<(string Target, string Source)> DensityKeys { get; } =
    [
        ("V2.Density.Standard.Gap", ".Gap"),
        ("V2.Density.Standard.Inset", ".Inset"),
    ];

    /// <summary>The keys the applier has to have read before it overrides anything.</summary>
    public static IReadOnlyList<string> BaselineKeys { get; } =
    [
        .. ScaledTypeKeys,
        "V2.Density.Compact.Gap",
        "V2.Density.Compact.Inset",
        "V2.Density.Standard.Gap",
        "V2.Density.Standard.Inset",
        "V2.Density.Comfortable.Gap",
        "V2.Density.Comfortable.Inset",
        "V2.Motion.Full.Duration",
        "V2.Motion.Reduced.Duration",
    ];

    /// <summary>The resource values these preferences ask for, given the design system's own.</summary>
    /// <param name="preferences">What the player chose.</param>
    /// <param name="baseline">The token dictionary as authored, before any override.</param>
    public static IReadOnlyDictionary<string, object> Overrides(
        WorkspacePreferences preferences,
        IReadOnlyDictionary<string, object?> baseline)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(baseline);

        var overrides = new Dictionary<string, object>(StringComparer.Ordinal);
        var scale = preferences.Normalized().TextScale;
        foreach (var key in ScaledTypeKeys)
        {
            if (baseline.TryGetValue(key, out var value) && value is double authored)
            {
                // Whole device-independent pixels: a 17.5 px body size and a 26.25 px line height
                // land on different device pixels at the same scale and make the ramp look
                // uneven at exactly the setting somebody turned on to read it more easily.
                overrides[key] = Math.Round(authored * scale, MidpointRounding.AwayFromZero);
            }
        }

        var density = preferences.Density switch
        {
            InterfaceDensity.Compact => "V2.Density.Compact",
            InterfaceDensity.Comfortable => "V2.Density.Comfortable",
            _ => "V2.Density.Standard",
        };
        foreach (var (target, suffix) in DensityKeys)
        {
            if (baseline.TryGetValue(density + suffix, out var value) && value is not null)
            {
                overrides[target] = value;
            }
        }

        var motion = preferences.ReduceMotion ? "V2.Motion.Reduced.Duration" : "V2.Motion.Full.Duration";
        if (baseline.TryGetValue(motion, out var duration) && duration is not null)
        {
            overrides["V2.Motion.Effective.Duration"] = duration;
        }

        return overrides;
    }
}
