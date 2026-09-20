using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.Personalization;
using TarkovCompanion.Core.Domain.Personalization;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>One choice in a row of them, drawn as a toggle that says which is current.</summary>
public sealed class V2AppearanceChoiceViewModel : BindableViewModel
{
    private bool _isCurrent;

    private readonly Func<string> _label;

    public V2AppearanceChoiceViewModel(string id, string labelKey, Action choose)
        : this(id, () => V2ShellText.Get(labelKey), choose)
    {
    }

    public V2AppearanceChoiceViewModel(string id, Func<string> label, Action choose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(choose);
        Id = id;
        _label = label;
        ChooseCommand = new DelegateCommand(choose);
    }

    public string Id { get; }

    public string Label => _label();

    public string AutomationId => $"v2-setup-appearance-{Id}";

    /// <summary>
    /// Whether this is the choice in force.
    /// </summary>
    /// <remarks>
    /// The selected class draws a border and a filled background, and the label carries the tick
    /// as well: "no state relies on colour alone" is the one acceptance criterion in #266 that a
    /// selected-looking button fails silently on a monochrome palette.
    /// </remarks>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set
        {
            if (SetProperty(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(DisplayLabel));
                OnPropertyChanged(nameof(AutomationState));
            }
        }
    }

    public string DisplayLabel => IsCurrent ? $"✓ {Label}" : Label;

    public string AutomationState => IsCurrent
        ? V2ShellText.Get("V2.Setup.Appearance.StateOn")
        : V2ShellText.Get("V2.Setup.Appearance.StateOff");

    public ICommand ChooseCommand { get; }
}

/// <summary>
/// The Appearance section: theme, colour vision, text scale, density and motion.
/// </summary>
/// <remarks>
/// Every control here writes to the one <see cref="WorkspacePreferences"/> record and nothing
/// else. The section used to hold the window-scale stepper alone, which is a different setting
/// with a different meaning — it zooms the whole shell, chrome included, and it is kept because
/// somebody sitting further from a 1080p monitor wants exactly that. Text scale changes the type
/// ramp only, which is what a screen reader user and an accessibility audit both mean by scaling.
/// </remarks>
public sealed class V2AppearanceSettingsViewModel : BindableViewModel
{
    private readonly WorkspacePreferenceService _preferences;
    private readonly Func<WorkspacePreferences, Task> _apply;

    public V2AppearanceSettingsViewModel(WorkspacePreferenceService preferences)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _apply = wanted => _preferences.UpdateAsync(wanted, CancellationToken.None);

        Themes =
        [
            Choice("theme-system", "V2.Setup.Appearance.Theme.System", () => SetTheme(AppearanceTheme.System)),
            Choice("theme-dark", "V2.Setup.Appearance.Theme.Dark", () => SetTheme(AppearanceTheme.Dark)),
            Choice("theme-light", "V2.Setup.Appearance.Theme.Light", () => SetTheme(AppearanceTheme.Light)),
            Choice("theme-contrast", "V2.Setup.Appearance.Theme.HighContrast", () => SetTheme(AppearanceTheme.HighContrast)),
        ];
        ColorVisions =
        [
            Choice("vision-standard", "V2.Setup.Appearance.Vision.Standard", () => SetColorVision(ColorVisionMode.Standard)),
            Choice("vision-redgreen", "V2.Setup.Appearance.Vision.RedGreen", () => SetColorVision(ColorVisionMode.RedGreenSafe)),
            Choice("vision-blueyellow", "V2.Setup.Appearance.Vision.BlueYellow", () => SetColorVision(ColorVisionMode.BlueYellowSafe)),
            Choice("vision-mono", "V2.Setup.Appearance.Vision.Monochrome", () => SetColorVision(ColorVisionMode.Monochrome)),
        ];
        Densities =
        [
            Choice("density-standard", "V2.Setup.Appearance.Density.Standard", () => SetDensity(InterfaceDensity.Standard)),
            Choice("density-compact", "V2.Setup.Appearance.Density.Compact", () => SetDensity(InterfaceDensity.Compact)),
            Choice("density-comfortable", "V2.Setup.Appearance.Density.Comfortable", () => SetDensity(InterfaceDensity.Comfortable)),
        ];
        TextScales = [.. WorkspacePreferences.TextScales.Select(percent =>
            new V2AppearanceChoiceViewModel(
                $"text-{percent}",
                () => V2ShellText.Format("V2.Setup.Appearance.TextScaleOption", CultureInfo.CurrentCulture, percent),
                () => SetTextScale(percent)))];

        ReduceMotionCommand = new DelegateCommand(() => SetReduceMotion(!Current.ReduceMotion));
        FocusAlwaysVisibleCommand = new DelegateCommand(() => SetFocusAlwaysVisible(!Current.FocusAlwaysVisible));
        ResetCommand = new DelegateCommand(() => _ = _apply(WorkspacePreferences.Default));
        _preferences.Changed += OnPreferencesChanged;
        Refresh();
    }

    public WorkspacePreferences Current => _preferences.Current;

    public IReadOnlyList<V2AppearanceChoiceViewModel> Themes { get; }

    public IReadOnlyList<V2AppearanceChoiceViewModel> ColorVisions { get; }

    public IReadOnlyList<V2AppearanceChoiceViewModel> Densities { get; }

    public IReadOnlyList<V2AppearanceChoiceViewModel> TextScales { get; }

    public ICommand ReduceMotionCommand { get; }

    public ICommand FocusAlwaysVisibleCommand { get; }

    public ICommand ResetCommand { get; }

    public string ThemeLabel => V2ShellText.Get("V2.Setup.Appearance.ThemeLabel");
    public string ThemeHint => V2ShellText.Get("V2.Setup.Appearance.ThemeHint");
    public string VisionLabel => V2ShellText.Get("V2.Setup.Appearance.VisionLabel");
    public string VisionHint => V2ShellText.Get("V2.Setup.Appearance.VisionHint");
    public string TextScaleLabel => V2ShellText.Get("V2.Setup.Appearance.TextScaleLabel");
    public string TextScaleHint => V2ShellText.Get("V2.Setup.Appearance.TextScaleHint");
    public string DensityLabel => V2ShellText.Get("V2.Setup.Appearance.DensityLabel");
    public string MotionLabel => V2ShellText.Get("V2.Setup.Appearance.MotionLabel");
    public string MotionHint => V2ShellText.Get("V2.Setup.Appearance.MotionHint");
    public string ResetAllLabel => V2ShellText.Get("V2.Setup.Appearance.ResetAllLabel");

    /// <summary>The motion toggle's own label, which says what pressing it will do.</summary>
    public string MotionToggleLabel => Current.ReduceMotion
        ? V2ShellText.Get("V2.Setup.Appearance.Motion.Reduced")
        : V2ShellText.Get("V2.Setup.Appearance.Motion.Full");

    public string FocusLabel => V2ShellText.Get("V2.Setup.Appearance.FocusLabel");
    public string FocusHint => V2ShellText.Get("V2.Setup.Appearance.FocusHint");

    /// <summary>The focus toggle's own label, the same shape as <see cref="MotionToggleLabel"/>.</summary>
    public string FocusToggleLabel => Current.FocusAlwaysVisible
        ? V2ShellText.Get("V2.Setup.Appearance.Focus.Always")
        : V2ShellText.Get("V2.Setup.Appearance.Focus.KeyboardOnly");

    /// <summary>What is in force now, in one line, so the section can be read without counting ticks.</summary>
    public string Summary => V2ShellText.Format(
        "V2.Setup.Appearance.Summary",
        CultureInfo.CurrentCulture,
        ThemeName(Current.Theme),
        Current.TextScalePercent,
        DensityName(Current.Density));

    public void Dispose() => _preferences.Changed -= OnPreferencesChanged;

    private V2AppearanceChoiceViewModel Choice(string id, string labelKey, Action choose) =>
        new(id, labelKey, choose);

    private void SetTheme(AppearanceTheme theme) => _ = _apply(Current with { Theme = theme });

    private void SetColorVision(ColorVisionMode mode) => _ = _apply(Current with { ColorVision = mode });

    private void SetDensity(InterfaceDensity density) => _ = _apply(Current with { Density = density });

    private void SetTextScale(int percent) => _ = _apply(Current with { TextScalePercent = percent });

    private void SetReduceMotion(bool reduce) => _ = _apply(Current with { ReduceMotion = reduce });

    private void SetFocusAlwaysVisible(bool always) => _ = _apply(Current with { FocusAlwaysVisible = always });

    private void OnPreferencesChanged(object? sender, WorkspacePreferences preferences) => Refresh();

    private void Refresh()
    {
        var current = Current;
        Mark(Themes, ThemeId(current.Theme));
        Mark(ColorVisions, VisionId(current.ColorVision));
        Mark(Densities, DensityId(current.Density));
        Mark(TextScales, $"text-{current.TextScalePercent}");
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(MotionToggleLabel));
        OnPropertyChanged(nameof(FocusToggleLabel));
        OnPropertyChanged(nameof(Summary));
    }

    private static void Mark(IReadOnlyList<V2AppearanceChoiceViewModel> choices, string currentId)
    {
        foreach (var choice in choices)
        {
            choice.IsCurrent = string.Equals(choice.Id, currentId, StringComparison.Ordinal);
        }
    }

    private static string ThemeId(AppearanceTheme theme) => theme switch
    {
        AppearanceTheme.System => "theme-system",
        AppearanceTheme.Light => "theme-light",
        AppearanceTheme.HighContrast => "theme-contrast",
        _ => "theme-dark",
    };

    private static string VisionId(ColorVisionMode mode) => mode switch
    {
        ColorVisionMode.RedGreenSafe => "vision-redgreen",
        ColorVisionMode.BlueYellowSafe => "vision-blueyellow",
        ColorVisionMode.Monochrome => "vision-mono",
        _ => "vision-standard",
    };

    private static string DensityId(InterfaceDensity density) => density switch
    {
        InterfaceDensity.Compact => "density-compact",
        InterfaceDensity.Comfortable => "density-comfortable",
        _ => "density-standard",
    };

    private static string ThemeName(AppearanceTheme theme) => V2ShellText.Get(theme switch
    {
        AppearanceTheme.System => "V2.Setup.Appearance.Theme.System",
        AppearanceTheme.Light => "V2.Setup.Appearance.Theme.Light",
        AppearanceTheme.HighContrast => "V2.Setup.Appearance.Theme.HighContrast",
        _ => "V2.Setup.Appearance.Theme.Dark",
    });

    private static string DensityName(InterfaceDensity density) => V2ShellText.Get(density switch
    {
        InterfaceDensity.Compact => "V2.Setup.Appearance.Density.Compact",
        InterfaceDensity.Comfortable => "V2.Setup.Appearance.Density.Comfortable",
        _ => "V2.Setup.Appearance.Density.Standard",
    });
}
