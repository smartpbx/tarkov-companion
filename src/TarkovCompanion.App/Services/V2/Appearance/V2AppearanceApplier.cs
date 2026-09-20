using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Styling;
using TarkovCompanion.App.Themes.V2;
using TarkovCompanion.Core.Domain.Personalization;

namespace TarkovCompanion.App.Services.V2.Appearance;

/// <summary>
/// Paints the running application the way the stored preferences ask.
/// </summary>
/// <remarks>
/// The missing half of #266. The palettes, the type ramp and the density tokens all shipped and
/// were all unreachable: <c>V2Appearance.Resolve</c> had no caller and <c>App.axaml</c> pinned the
/// theme to Dark, so choosing light or high contrast was not a thing the product could do. This is
/// the caller.
///
/// <para>
/// Overrides go into <c>Application.Resources</c> itself rather than into the merged token
/// dictionary. A top-level entry shadows a merged one, every consumer already asks by
/// <c>DynamicResource</c>, and the authored dictionary stays the design system's own record of
/// what the sizes are — which is what <see cref="V2AppearanceResources"/> reads its baseline from,
/// once, before anything has been shadowed.
/// </para>
/// <para>
/// Reduced motion is a class on the window rather than a resource the transitions read. A
/// <c>DynamicResource</c> inside a <c>Transitions</c> setter has no resource host to resolve
/// against, so it would silently keep the authored duration; a class an ancestor carries is
/// matched by a second style that empties the transition list, which is a rule the styling engine
/// can actually apply.
/// </para>
/// </remarks>
public sealed class V2AppearanceApplier
{
    /// <summary>The class the window carries while transitions are suppressed.</summary>
    public const string ReducedMotionClass = "v2-motion-reduced";

    /// <summary>
    /// The class the window carries while the focus ring draws after a pointer click too.
    /// </summary>
    /// <remarks>
    /// The same reasoning as <see cref="ReducedMotionClass"/>: a `:focus-visible` selector has no
    /// class to toggle, so an ancestor class is what a style rule can actually key on. See the
    /// `Window.v2-focus-always` rules in V2PrimitiveStyles.axaml.
    /// </remarks>
    public const string FocusAlwaysVisibleClass = "v2-focus-always";

    private readonly Avalonia.Application _application;
    private readonly Func<PlatformColorValues?> _readSystemColors;
    private Dictionary<string, object?>? _baseline;

    public V2AppearanceApplier(Avalonia.Application application, Func<PlatformColorValues?>? readSystemColors = null)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _readSystemColors = readSystemColors ?? (() => application.PlatformSettings?.GetColorValues());
    }

    /// <summary>The variant last resolved, for a caller that wants to report it.</summary>
    public ThemeVariant? Applied { get; private set; }

    /// <summary>Repaints the application for <paramref name="preferences"/>.</summary>
    public void Apply(WorkspacePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var wanted = preferences.Normalized();

        var system = ReadSystemColors();
        var variant = V2Appearance.Resolve(
            ToAppearance(wanted.Theme),
            ToColorVision(wanted.ColorVision),
            system.PrefersDark,
            system.RequestsHighContrast);
        _application.RequestedThemeVariant = variant;
        Applied = variant;

        foreach (var (key, value) in V2AppearanceResources.Overrides(wanted, CaptureBaseline()))
        {
            _application.Resources[key] = value;
        }

        foreach (var window in Windows())
        {
            window.Classes.Set(ReducedMotionClass, wanted.ReduceMotion);
            window.Classes.Set(FocusAlwaysVisibleClass, wanted.FocusAlwaysVisible);
        }
    }

    /// <summary>Puts the current motion and focus classes on a window opened after the last apply.</summary>
    public void Attach(Window window, WorkspacePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(preferences);
        window.Classes.Set(ReducedMotionClass, preferences.ReduceMotion);
        window.Classes.Set(FocusAlwaysVisibleClass, preferences.FocusAlwaysVisible);
    }

    private (bool PrefersDark, bool RequestsHighContrast) ReadSystemColors()
    {
        // Headless render hosts and the earliest moments of startup have no platform settings.
        // Dark is the answer the application has always given when it could not ask.
        var colors = _readSystemColors();
        return colors is null
            ? (true, false)
            : (colors.ThemeVariant == PlatformThemeVariant.Dark,
                colors.ContrastPreference == ColorContrastPreference.High);
    }

    private IEnumerable<Window> Windows() =>
        _application.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows
            : [];

    private IReadOnlyDictionary<string, object?> CaptureBaseline()
    {
        if (_baseline is not null)
        {
            return _baseline;
        }

        _baseline = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in V2AppearanceResources.BaselineKeys)
        {
            _baseline[key] = _application.TryGetResource(key, _application.ActualThemeVariant, out var value)
                ? value
                : null;
        }

        return _baseline;
    }

    private static V2AppearancePreference ToAppearance(AppearanceTheme theme) => theme switch
    {
        AppearanceTheme.Light => V2AppearancePreference.Light,
        AppearanceTheme.Dark => V2AppearancePreference.Dark,
        AppearanceTheme.HighContrast => V2AppearancePreference.HighContrast,
        _ => V2AppearancePreference.System,
    };

    private static V2ColorVisionPreference ToColorVision(ColorVisionMode mode) => mode switch
    {
        ColorVisionMode.RedGreenSafe => V2ColorVisionPreference.RedGreenSafe,
        ColorVisionMode.BlueYellowSafe => V2ColorVisionPreference.BlueYellowSafe,
        ColorVisionMode.Monochrome => V2ColorVisionPreference.Monochrome,
        _ => V2ColorVisionPreference.Standard,
    };
}
