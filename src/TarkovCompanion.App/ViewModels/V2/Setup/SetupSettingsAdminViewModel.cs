using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.Settings;
using TarkovCompanion.App.Services.V2.Notifications;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Network;
using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Application.Services.Personalization;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Recommendations;
using TarkovCompanion.Application.Services.ReleaseExperience;
using TarkovCompanion.Application.Services.Setup;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Domain.Personalization;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Core.Network;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>What a pending reset or import would do, before it is confirmed.</summary>
public enum SetupSettingsPendingKind
{
    None = 0,
    ResetSection,
    ResetAll,
    Import,
}

/// <summary>
/// [#902] The live services behind every <see cref="SettingsRegistry"/> group beyond the first three.
/// Each is optional: a group whose source is missing (a test, a tool) is left as it is.
/// </summary>
/// <remarks>
/// Writes go through the services, never the files under them, because each service is what raises
/// the event its pages listen to: a reset written to network.json directly would leave Data &amp;
/// Privacy showing the old switches until a restart.
/// </remarks>
public sealed class SetupSettingsSources
{
    /// <summary>Reads and sets the window's interface scale (MainWindowViewModel.InterfaceScale).</summary>
    public (Func<double> Get, Action<double> Set)? InterfaceScale { get; init; }

    public NetworkPolicyService? Network { get; init; }

    public FeatureFlagService? FeatureFlags { get; init; }

    public RecommendationPolicyService? Horizons { get; init; }

    public IGroupSettingsStore? SquadSharing { get; init; }

    public IWorkspaceLayoutStore? Layout { get; init; }

    public IMapVariantPreferenceStore? MapDefaults { get; init; }
}

/// <summary>
/// Setup's "Reset this section", "Reset everything", export and import (#292 task 2).
/// </summary>
/// <remarks>
/// <para>
/// Three real stores, none of them a second preference file: <see cref="WorkspacePreferenceService"/>
/// (already the one place Accessibility writes to), the <see cref="NotificationBridge"/> a shell
/// composes for Setup > Notifications, and <see cref="IScreenshotRetentionStore"/> that Setup >
/// Privacy already reads. Every reset, export and import goes through this same trio, so an import
/// can never write somewhere the rest of the page does not also read from.
/// </para>
/// <para>
/// Nothing here applies before a preview: every action that changes something first computes a
/// <see cref="SetupSettingsDiff"/> against the live values and holds it as <see cref="PendingDiff"/>
/// until <see cref="ConfirmCommand"/> is pressed. An action with nothing to change (already at
/// defaults, an import identical to what is in force) never opens a preview at all — it says so in
/// <see cref="StatusMessage"/> instead, because a confirm dialog with nothing in it is a worse
/// answer than not showing one.
/// </para>
/// </remarks>
public sealed class SetupSettingsAdminViewModel : BindableViewModel
{
    private readonly WorkspacePreferenceService _preferences;
    private readonly IScreenshotRetentionStore _retention;
    private readonly NotificationBridge? _notifications;
    private readonly SetupSettingsSources _sources;
    private V2SetupSection _currentSection = V2SetupSection.Overview;
    private SetupSettingsPendingKind _pendingKind;
    private SetupSettingsSnapshot? _pendingTarget;
    private IReadOnlyList<SetupSettingsDiffRow> _pendingDiff = [];
    private string _pendingLabel = string.Empty;
    private string _exchangePath = string.Empty;
    private string _statusMessage = string.Empty;

    public SetupSettingsAdminViewModel(
        WorkspacePreferenceService preferences,
        IScreenshotRetentionStore retention,
        NotificationBridge? notifications = null,
        SetupSettingsSources? sources = null)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        _notifications = notifications;
        _sources = sources ?? new SetupSettingsSources();

        ResetSectionCommand = new AsyncDelegateCommand(PrepareResetSectionAsync);
        ResetAllCommand = new AsyncDelegateCommand(PrepareResetAllAsync);
        ExportCommand = new AsyncDelegateCommand(ExportAsync);
        PreviewImportCommand = new AsyncDelegateCommand(PreviewImportAsync);
        ConfirmCommand = new AsyncDelegateCommand(ConfirmAsync);
        CancelCommand = new DelegateCommand(ClearPending);
    }

    /// <summary>Which section a bare "Reset this section" press acts on. Pushed by
    /// <see cref="V2SetupWorkspaceViewModel"/> whenever the selection changes.</summary>
    public void SetCurrentSection(V2SetupSection section)
    {
        if (_currentSection == section)
        {
            return;
        }

        _currentSection = section;
        ClearPending();
        StatusMessage = string.Empty;
        OnPropertyChanged(nameof(CanResetSection));
        OnPropertyChanged(nameof(ShowsBackup));
        OnPropertyChanged(nameof(IsVisible));
    }

    /// <summary>The open section is the home of a registered setting, so "Reset this section" is offered at its foot.</summary>
    public bool CanResetSection => IsResettable(_currentSection);

    /// <summary>[#902] Backup &amp; reset (export, import, Reset everything) lives once, under About.</summary>
    public bool ShowsBackup => _currentSection == V2SetupSection.About;

    /// <summary>Whether the open section shows any of this card.</summary>
    public bool IsVisible => CanResetSection || ShowsBackup;

    public string ExchangePath
    {
        get => _exchangePath;
        set => SetProperty(ref _exchangePath, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(ShowsStatus));
            }
        }
    }

    /// <summary>A status line to show, and no open preview above it: an empty line would leave a gap at the card's foot.</summary>
    public bool ShowsStatus => !HasPendingChange && StatusMessage.Length > 0;

    public bool HasPendingChange => _pendingKind != SetupSettingsPendingKind.None;

    public string PendingLabel
    {
        get => _pendingLabel;
        private set => SetProperty(ref _pendingLabel, value);
    }

    public IReadOnlyList<SetupSettingsDiffRow> PendingDiff
    {
        get => _pendingDiff;
        private set => SetProperty(ref _pendingDiff, value);
    }

    public ICommand ResetSectionCommand { get; }

    public ICommand ResetAllCommand { get; }

    public ICommand ExportCommand { get; }

    public ICommand PreviewImportCommand { get; }

    public ICommand ConfirmCommand { get; }

    public ICommand CancelCommand { get; }

    private async Task PrepareResetSectionAsync()
    {
        var section = _currentSection;
        if (!IsResettable(section))
        {
            StatusMessage = SetupText.AdminNothingResettable;
            ClearPending();
            return;
        }

        var current = await CaptureCurrentAsync().ConfigureAwait(true);
        var target = current;
        foreach (var domain in SettingsRegistry.DomainsIn(section))
        {
            target = WithDefault(target, domain);
        }

        target = target with
        {
            Layout = new SortedDictionary<string, string>(
                target.Layout.Where(entry => !SettingsRegistry.IsLayoutKeyIn(entry.Key, section)).ToDictionary(),
                StringComparer.Ordinal),
        };

        SetOrClearPending(SetupSettingsPendingKind.ResetSection, target, current, SetupText.AdminResetSectionQuestion, SetupText.AdminSectionAlreadyDefault);
    }

    private async Task PrepareResetAllAsync()
    {
        var current = await CaptureCurrentAsync().ConfigureAwait(true);
        SetOrClearPending(
            SetupSettingsPendingKind.ResetAll,
            SetupSettingsSnapshot.Default,
            current,
            SetupText.AdminResetAllQuestion,
            SetupText.AdminAllAlreadyDefault);
    }

    private async Task ExportAsync()
    {
        if (string.IsNullOrWhiteSpace(ExchangePath))
        {
            StatusMessage = SetupText.AdminNeedsPath;
            return;
        }

        try
        {
            var current = await CaptureCurrentAsync().ConfigureAwait(true);
            await File.WriteAllTextAsync(ExchangePath, SetupSettingsExport.ToJson(current)).ConfigureAwait(true);
            StatusMessage = SetupText.AdminExported(ExchangePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = SetupText.AdminNotExported(exception.Message);
        }
    }

    private async Task PreviewImportAsync()
    {
        if (string.IsNullOrWhiteSpace(ExchangePath))
        {
            StatusMessage = SetupText.AdminNeedsPath;
            return;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(ExchangePath).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = SetupText.AdminNotImported(exception.Message);
            ClearPending();
            return;
        }

        var current = await CaptureCurrentAsync().ConfigureAwait(true);
        var result = SetupSettingsExport.Validate(text, current);
        if (!result.IsValid)
        {
            StatusMessage = SetupText.AdminNotImported(result.Error);
            ClearPending();
            return;
        }

        SetOrClearPending(
            SetupSettingsPendingKind.Import,
            result.Snapshot!,
            current,
            SetupText.AdminImportQuestion(ExchangePath),
            SetupText.AdminImportNothing);
    }

    private async Task ConfirmAsync()
    {
        if (_pendingTarget is not { } target)
        {
            return;
        }

        var applied = _pendingKind;
        await ApplyAsync(target).ConfigureAwait(true);

        StatusMessage = applied switch
        {
            SetupSettingsPendingKind.ResetSection => SetupText.AdminSectionReset,
            SetupSettingsPendingKind.ResetAll => SetupText.AdminAllReset,
            SetupSettingsPendingKind.Import => SetupText.AdminImported,
            _ => StatusMessage,
        };
        ClearPending();
    }

    /// <summary>Everything registered, as it is in force now.</summary>
    internal async Task<SetupSettingsSnapshot> CaptureCurrentAsync()
    {
        var retention = await _retention.GetAsync(CancellationToken.None).ConfigureAwait(true);
        var squad = _sources.SquadSharing is { } group
            ? await group.GetAsync(CancellationToken.None).ConfigureAwait(true)
            : null;
        var maps = _sources.MapDefaults is { } mapStore
            ? await mapStore.GetAllAsync(CancellationToken.None).ConfigureAwait(true)
            : SetupSettingsSnapshot.Default.MapDefaults;
        return new SetupSettingsSnapshot(_preferences.Current, _notifications?.Settings ?? NotificationSettings.Default, retention)
        {
            InterfaceScale = _sources.InterfaceScale is { } scale ? scale.Get() : 1,
            Network = _sources.Network?.Controls ?? NetworkControls.Default,
            FeatureFlags = _sources.FeatureFlags is { } flags
                ? new SortedDictionary<string, bool>(
                    flags.States.Where(state => state.Source == FeatureFlagSource.Override)
                        .ToDictionary(state => state.Flag.Key, state => state.IsOn),
                    StringComparer.Ordinal)
                : SetupSettingsSnapshot.Default.FeatureFlags,
            Horizons = _sources.Horizons?.CurrentSettings ?? RecommendationHorizonSettings.Default,
            SquadSharing = squad is null
                ? SquadSharingChoices.Default
                : new SquadSharingChoices(squad.IsEnabled, squad.SharesLoadout, squad.SharesQuests),
            Layout = _sources.Layout?.Entries ?? SetupSettingsSnapshot.Default.Layout,
            MapDefaults = maps,
        }.Normalized();
    }

    /// <summary>Writes every group that differs, each through the service its pages listen to.</summary>
    private async Task ApplyAsync(SetupSettingsSnapshot target)
    {
        var current = await CaptureCurrentAsync().ConfigureAwait(true);
        var token = CancellationToken.None;
        if (target.Appearance != current.Appearance)
        {
            await _preferences.UpdateAsync(target.Appearance, token).ConfigureAwait(true);
        }

        if (_notifications is not null && target.Notifications != current.Notifications)
        {
            // One write of the whole record (#888). The per-switch calls this replaced never set
            // quiet hours, so an import or reset listed quiet-hours changes and applied none.
            await _notifications.ReplaceAsync(target.Notifications, token).ConfigureAwait(true);
        }

        if (target.ScreenshotRetention != current.ScreenshotRetention)
        {
            await _retention.SaveAsync(target.ScreenshotRetention, token).ConfigureAwait(true);
        }

        if (_sources.InterfaceScale is { } scale && !target.InterfaceScale.Equals(current.InterfaceScale))
        {
            scale.Set(target.InterfaceScale);
        }

        if (target.Network != current.Network)
        {
            _sources.Network?.Set(target.Network);
        }

        if (_sources.FeatureFlags is { } flags)
        {
            foreach (var state in flags.States)
            {
                var wanted = target.FeatureFlags.TryGetValue(state.Flag.Key, out var chosen) ? chosen : (bool?)null;
                var now = current.FeatureFlags.TryGetValue(state.Flag.Key, out var held) ? held : (bool?)null;
                if (wanted == now)
                {
                    continue;
                }

                if (wanted is { } isOn)
                {
                    flags.Set(state.Flag, isOn);
                }
                else
                {
                    flags.Reset(state.Flag);
                }
            }
        }

        if (_sources.Horizons is { } horizons && target.Horizons != current.Horizons)
        {
            await horizons.UpdateAsync(target.Horizons, token).ConfigureAwait(true);
        }

        if (_sources.SquadSharing is { } group && target.SquadSharing != current.SquadSharing)
        {
            // The three switches only. The relay address, name and key stay exactly as they are.
            var stored = await group.GetAsync(token).ConfigureAwait(true);
            await group.SaveAsync(
                    stored with
                    {
                        IsEnabled = target.SquadSharing.IsEnabled,
                        SharesLoadout = target.SquadSharing.SharesLoadout,
                        SharesQuests = target.SquadSharing.SharesQuests,
                    },
                    token)
                .ConfigureAwait(true);
        }

        if (_sources.Layout is { } layout && SetupSettingsDiff.Compare(current with { Layout = target.Layout }, current).Count > 0)
        {
            layout.Replace(target.Layout);
        }

        if (_sources.MapDefaults is { } maps && SetupSettingsDiff.Compare(current with { MapDefaults = target.MapDefaults }, current).Count > 0)
        {
            await maps.ReplaceAllAsync(target.MapDefaults, token).ConfigureAwait(true);
        }
    }

    /// <summary><paramref name="snapshot"/> with one registered group put back to its default.</summary>
    internal static SetupSettingsSnapshot WithDefault(SetupSettingsSnapshot snapshot, SettingsDomain domain)
    {
        var defaults = SetupSettingsSnapshot.Default;
        return domain switch
        {
            SettingsDomain.Appearance => snapshot with { Appearance = defaults.Appearance },
            SettingsDomain.InterfaceScale => snapshot with { InterfaceScale = defaults.InterfaceScale },
            SettingsDomain.Notifications => snapshot with { Notifications = defaults.Notifications },
            SettingsDomain.ScreenshotTidying => snapshot with { ScreenshotRetention = defaults.ScreenshotRetention },
            SettingsDomain.Network => snapshot with { Network = defaults.Network },
            SettingsDomain.FeatureFlags => snapshot with { FeatureFlags = defaults.FeatureFlags },
            SettingsDomain.Horizons => snapshot with { Horizons = defaults.Horizons },
            SettingsDomain.SquadSharing => snapshot with { SquadSharing = defaults.SquadSharing },
            SettingsDomain.Layout => snapshot with { Layout = defaults.Layout },
            SettingsDomain.MapDefaults => snapshot with { MapDefaults = defaults.MapDefaults },
            _ => throw new ArgumentOutOfRangeException(nameof(domain), domain, "Every registered group needs a default here."),
        };
    }

    /// <summary>A group with no live source here (a test, a tool) is shown and applied as it is now, never as a change.</summary>
    private SetupSettingsSnapshot OnlyWhatCanBeApplied(SetupSettingsSnapshot target, SetupSettingsSnapshot current) => target with
    {
        Notifications = _notifications is null ? current.Notifications : target.Notifications,
        InterfaceScale = _sources.InterfaceScale is null ? current.InterfaceScale : target.InterfaceScale,
        Network = _sources.Network is null ? current.Network : target.Network,
        FeatureFlags = _sources.FeatureFlags is { } flags
            ? new SortedDictionary<string, bool>(
                target.FeatureFlags.Where(entry => flags.States.Any(state => state.Flag.Key == entry.Key)).ToDictionary(),
                StringComparer.Ordinal)
            : current.FeatureFlags,
        Horizons = _sources.Horizons is null ? current.Horizons : target.Horizons,
        SquadSharing = _sources.SquadSharing is null ? current.SquadSharing : target.SquadSharing,
        Layout = _sources.Layout is null ? current.Layout : target.Layout,
        MapDefaults = _sources.MapDefaults is null ? current.MapDefaults : target.MapDefaults,
    };

    private void SetOrClearPending(
        SetupSettingsPendingKind kind,
        SetupSettingsSnapshot target,
        SetupSettingsSnapshot current,
        string label,
        string nothingToChangeMessage)
    {
        target = OnlyWhatCanBeApplied(target.Normalized(), current);
        var diff = SetupSettingsDiff.Compare(current, target);
        if (diff.Count == 0)
        {
            StatusMessage = nothingToChangeMessage;
            ClearPending();
            return;
        }

        _pendingKind = kind;
        _pendingTarget = target;
        PendingDiff = [.. diff.Select(SetupSettingsDiffRow.From)];
        PendingLabel = label;
        StatusMessage = string.Empty;
        OnPropertyChanged(nameof(HasPendingChange));
        OnPropertyChanged(nameof(ShowsStatus));
    }

    private void ClearPending()
    {
        _pendingKind = SetupSettingsPendingKind.None;
        _pendingTarget = null;
        PendingDiff = [];
        PendingLabel = string.Empty;
        OnPropertyChanged(nameof(HasPendingChange));
        OnPropertyChanged(nameof(ShowsStatus));
    }

    private static bool IsResettable(V2SetupSection section) => SettingsRegistry.HasSettings(section);
}
