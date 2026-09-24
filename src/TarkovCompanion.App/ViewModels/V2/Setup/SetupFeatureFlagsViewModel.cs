using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.ReleaseExperience;
using TarkovCompanion.Core.Features;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>
/// [#314] Setup › Diagnostics: each feature flag, whether it is on, where that comes from, and Reset.
/// </summary>
/// <remarks>
/// The switch shows the player's choice, not what is running: a flag the feature reads once at
/// startup says "Restart to apply" until it has been, rather than flipping back to what is running.
/// </remarks>
public sealed class SetupFeatureFlagsViewModel : BindableViewModel
{
    private readonly FeatureFlagService _flags;

    public SetupFeatureFlagsViewModel(FeatureFlagService flags)
    {
        _flags = flags ?? throw new ArgumentNullException(nameof(flags));
        Rows = _flags.States.Select(state => new SetupFeatureFlagRowViewModel(_flags, state.Flag)).ToArray();
        Refresh();
        _flags.Changed += (_, _) => Refresh();
    }

    public string Heading => SetupText.FlagsHeading;

    public string RingLine => SetupText.FlagsRingLine(RingName(_flags.Ring));

    public IReadOnlyList<SetupFeatureFlagRowViewModel> Rows { get; }

    internal static string RingName(ReleaseRing ring) => ring switch
    {
        ReleaseRing.Dev => SetupText.FlagsRingDev,
        ReleaseRing.Stable => SetupText.FlagsRingStable,
        _ => SetupText.FlagsRingRough,
    };

    internal static string RingDefault(ReleaseRing ring) => ring switch
    {
        ReleaseRing.Dev => SetupText.FlagsRingDefaultDev,
        ReleaseRing.Stable => SetupText.FlagsRingDefaultStable,
        _ => SetupText.FlagsRingDefaultRough,
    };

    private void Refresh()
    {
        var states = _flags.States.ToDictionary(state => state.Flag.Key, StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            row.Apply(states[row.Key], _flags.Ring);
        }
    }
}

/// <summary>One flag's row: its switch, its source and its Reset.</summary>
public sealed class SetupFeatureFlagRowViewModel : BindableViewModel
{
    private readonly FeatureFlagDefinition _flag;
    private bool _isOn;
    private bool _isOverridden;
    private bool _waitsForRestart;
    private string _sourceLabel = string.Empty;

    internal SetupFeatureFlagRowViewModel(FeatureFlagService flags, FeatureFlagDefinition flag)
    {
        ArgumentNullException.ThrowIfNull(flags);
        _flag = flag ?? throw new ArgumentNullException(nameof(flag));
        ToggleCommand = new DelegateCommand(() => flags.Set(_flag, !_isOn));
        ResetCommand = new DelegateCommand(() => flags.Reset(_flag));
    }

    public string Key => _flag.Key;

    public string Title => SetupText.FlagTitle(_flag);

    public string Description => SetupText.FlagDescription(_flag);

    public string DetailLine => $"{_flag.Key} · #{_flag.OwnerIssue}";

    public string AutomationId => $"v2-setup-flag-{_flag.Key}";

    public string ResetAutomationId => $"v2-setup-flag-{_flag.Key}-reset";

    public bool IsOn
    {
        get => _isOn;
        private set => SetProperty(ref _isOn, value);
    }

    public bool IsOverridden
    {
        get => _isOverridden;
        private set => SetProperty(ref _isOverridden, value);
    }

    public bool WaitsForRestart
    {
        get => _waitsForRestart;
        private set => SetProperty(ref _waitsForRestart, value);
    }

    public string RestartNote => SetupText.FlagsRestartNote;

    /// <summary>"Rough default" or "Your choice (rough default: on)".</summary>
    public string SourceLabel
    {
        get => _sourceLabel;
        private set => SetProperty(ref _sourceLabel, value);
    }

    public ICommand ToggleCommand { get; }

    public ICommand ResetCommand { get; }

    internal void Apply(FeatureFlagState state, ReleaseRing ring)
    {
        IsOn = state.IsOn;
        IsOverridden = state.Source == FeatureFlagSource.Override;
        WaitsForRestart = state.WaitsForRestart;
        var ringName = SetupFeatureFlagsViewModel.RingName(ring);
        SourceLabel = IsOverridden
            ? SetupText.FlagsOverride(ringName, _flag.DefaultFor(ring) ? SetupText.FlagsDefaultOn : SetupText.FlagsDefaultOff)
            : SetupFeatureFlagsViewModel.RingDefault(ring);
    }
}
