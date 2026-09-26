using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.Diagnostics;
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

    /// <summary>[#935] Reads and writes the interface language's culture (null: follow Windows).</summary>
    public (Func<string?> Get, Action<string?> Set)? InterfaceLanguage { get; init; }

    /// <summary>[#935] Reads and sets the shell's Capture shortcut switch. Settable: the shell that
    /// owns the switch is composed after this, and attaches it (SetupSettingsAdminViewModel.AttachCaptureShortcut).</summary>
    public (Func<bool> Get, Action<bool> Set)? CaptureShortcut { get; set; }
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
    private PendingRequest? _pendingRequest;
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

    /// <summary>[#935] The shell's Capture shortcut switch, which Reset everything, Export and Import cover.</summary>
    public void AttachCaptureShortcut(Func<bool> get, Action<bool> set)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        _sources.CaptureShortcut = (get, set);
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
        SetOrClearPending(
            new PendingRequest(SetupSettingsPendingKind.ResetSection, section, null),
            SectionReset(current, section),
            current,
            SetupText.AdminResetSectionQuestion,
            SetupText.AdminSectionAlreadyDefault);
    }

    /// <summary><paramref name="current"/> with every setting whose home is <paramref name="section"/> at its default.</summary>
    internal static SetupSettingsSnapshot SectionReset(SetupSettingsSnapshot current, V2SetupSection section)
    {
        var target = current;
        foreach (var domain in SettingsRegistry.DomainsIn(section))
        {
            target = WithDefault(target, domain);
        }

        return target with
        {
            Layout = new SortedDictionary<string, string>(
                target.Layout.Where(entry => !SettingsRegistry.IsLayoutKeyIn(entry.Key, section)).ToDictionary(),
                StringComparer.Ordinal),
        };
    }

    private async Task PrepareResetAllAsync()
    {
        var current = await CaptureCurrentAsync().ConfigureAwait(true);
        SetOrClearPending(
            new PendingRequest(SetupSettingsPendingKind.ResetAll, _currentSection, null),
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
        catch (Exception exception) when (IsFileProblem(exception))
        {
            CrashLog.Write("warning/Settings", $"Settings export to a file failed: {exception.GetType().Name}: {exception.Message}");
            StatusMessage = SetupText.AdminNotExported(SetupText.AdminFileProblem(exception, writing: true));
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
        catch (Exception exception) when (IsFileProblem(exception))
        {
            CrashLog.Write("warning/Settings", $"Settings import could not read the file: {exception.GetType().Name}: {exception.Message}");
            StatusMessage = SetupText.AdminNotImported(SetupText.AdminFileProblem(exception, writing: false));
            ClearPending();
            return;
        }

        var current = await CaptureCurrentAsync().ConfigureAwait(true);
        var result = SetupSettingsExport.Validate(text, current);
        if (!result.IsValid)
        {
            CrashLog.Write("warning/Settings", $"Settings import refused the file ({result.Error}): {result.Detail}");
            StatusMessage = SetupText.AdminNotImported(SetupText.AdminImportError(result.Error));
            ClearPending();
            return;
        }

        SetOrClearPending(
            new PendingRequest(SetupSettingsPendingKind.Import, _currentSection, text),
            result.Snapshot!,
            current,
            SetupText.AdminImportQuestion(ExchangePath),
            SetupText.AdminImportNothing);
    }

    /// <remarks>
    /// [#935] The target is built again here, from what is in force now, and not taken from the
    /// preview. The preview's target was a whole-app snapshot: a squad-sharing switch turned off, a
    /// scale changed, or a page filter chosen while the preview stood open was written back to its
    /// old value on Confirm, although no row of the confirmed preview named it.
    /// </remarks>
    private async Task ConfirmAsync()
    {
        if (_pendingRequest is not { } request)
        {
            return;
        }

        try
        {
            SetupSettingsSnapshot current;
            try
            {
                current = await CaptureCurrentAsync().ConfigureAwait(true);
            }
            catch (Exception exception) when (IsStoreProblem(exception))
            {
                CrashLog.Write("warning/Settings", $"Settings could not be read before applying: {exception.GetType().Name}: {exception.Message}");
                StatusMessage = SetupText.AdminNotApplied;
                return;
            }

            SetupSettingsSnapshot target;
            switch (request.Kind)
            {
                case SetupSettingsPendingKind.ResetSection:
                    target = SectionReset(current, request.Section);
                    break;
                case SetupSettingsPendingKind.ResetAll:
                    target = SetupSettingsSnapshot.Default;
                    break;
                default:
                    var result = SetupSettingsExport.Validate(request.ImportText ?? string.Empty, current);
                    if (!result.IsValid)
                    {
                        StatusMessage = SetupText.AdminNotImported(SetupText.AdminImportError(result.Error));
                        return;
                    }

                    target = result.Snapshot!;
                    break;
            }

            target = OnlyWhatCanBeApplied(target.Normalized(), current);
            var failed = await ApplyAsync(target, current).ConfigureAwait(true);
            var done = request.Kind switch
            {
                SetupSettingsPendingKind.ResetSection => SetupText.AdminSectionReset,
                SetupSettingsPendingKind.ResetAll => SetupText.AdminAllReset,
                _ => SetupText.AdminImported,
            };
            var languageChanged = !failed.Contains(SettingsDomain.InterfaceLanguage) &&
                !string.Equals(target.InterfaceLanguage, current.InterfaceLanguage, StringComparison.OrdinalIgnoreCase);
            StatusMessage = failed.Count > 0
                ? SetupText.AdminPartlyApplied(failed.Select(SetupText.SettingsDomainName))
                : languageChanged ? SetupText.AdminWithRestart(done) : done;
        }
        finally
        {
            ClearPending();
        }
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
                : new SquadSharingChoices(squad.IsEnabled, squad.SharesLoadout, squad.SharesQuests, squad.SharesReadyCheck),
            Layout = _sources.Layout?.Entries ?? SetupSettingsSnapshot.Default.Layout,
            MapDefaults = maps,
            InterfaceLanguage = _sources.InterfaceLanguage is { } language ? language.Get() : null,
            CaptureShortcut = _sources.CaptureShortcut is not { } shortcut || shortcut.Get(),
        }.Normalized();
    }

    /// <summary>Writes every group that differs, each through the service its pages listen to.</summary>
    /// <returns>[#935] The groups whose write failed. One locked file no longer stops the groups after it,
    /// and a failure is said on the status line rather than lost to the dispatcher.</returns>
    private async Task<IReadOnlyList<SettingsDomain>> ApplyAsync(SetupSettingsSnapshot target, SetupSettingsSnapshot current)
    {
        var token = CancellationToken.None;
        var failed = new List<SettingsDomain>();

        async Task Write(SettingsDomain domain, bool differs, Func<Task> write)
        {
            if (!differs)
            {
                return;
            }

            try
            {
                await write().ConfigureAwait(true);
            }
            catch (Exception exception) when (IsStoreProblem(exception))
            {
                CrashLog.Write("warning/Settings", $"Settings group {domain} was not saved: {exception.GetType().Name}: {exception.Message}");
                failed.Add(domain);
            }
        }

        Task Sync(Action write)
        {
            write();
            return Task.CompletedTask;
        }

        await Write(SettingsDomain.Appearance, target.Appearance != current.Appearance, () => _preferences.UpdateAsync(target.Appearance, token)).ConfigureAwait(true);

        // One write of the whole record (#888). The per-switch calls this replaced never set
        // quiet hours, so an import or reset listed quiet-hours changes and applied none.
        await Write(
                SettingsDomain.Notifications,
                _notifications is not null && target.Notifications != current.Notifications,
                () => _notifications!.ReplaceAsync(target.Notifications, token))
            .ConfigureAwait(true);

        await Write(
                SettingsDomain.ScreenshotTidying,
                target.ScreenshotRetention != current.ScreenshotRetention,
                () => _retention.SaveAsync(target.ScreenshotRetention, token))
            .ConfigureAwait(true);

        await Write(
                SettingsDomain.InterfaceScale,
                _sources.InterfaceScale is not null && !target.InterfaceScale.Equals(current.InterfaceScale),
                () => Sync(() => _sources.InterfaceScale!.Value.Set(target.InterfaceScale)))
            .ConfigureAwait(true);

        await Write(SettingsDomain.Network, _sources.Network is not null && target.Network != current.Network, () => Sync(() => _sources.Network!.Set(target.Network))).ConfigureAwait(true);

        if (_sources.FeatureFlags is { } flags)
        {
            await Write(SettingsDomain.FeatureFlags, true, () => Sync(() =>
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
            })).ConfigureAwait(true);
        }

        await Write(
                SettingsDomain.Horizons,
                _sources.Horizons is not null && target.Horizons != current.Horizons,
                () => _sources.Horizons!.UpdateAsync(target.Horizons, token))
            .ConfigureAwait(true);

        if (_sources.SquadSharing is { } group)
        {
            await Write(SettingsDomain.SquadSharing, target.SquadSharing != current.SquadSharing, async () =>
            {
                // The four switches only. The relay address, name and key stay exactly as they are.
                var stored = await group.GetAsync(token).ConfigureAwait(true);
                await group.SaveAsync(
                        stored with
                        {
                            IsEnabled = target.SquadSharing.IsEnabled,
                            SharesLoadout = target.SquadSharing.SharesLoadout,
                            SharesQuests = target.SquadSharing.SharesQuests,
                            SharesReadyCheck = target.SquadSharing.SharesReadyCheck, // [#961]
                        },
                        token)
                    .ConfigureAwait(true);
            }).ConfigureAwait(true);
        }

        if (_sources.Layout is { } layout)
        {
            await Write(
                    SettingsDomain.Layout,
                    SetupSettingsDiff.Compare(current with { Layout = target.Layout }, current).Count > 0,
                    () => Sync(() => layout.Replace(target.Layout)))
                .ConfigureAwait(true);
        }

        if (_sources.MapDefaults is { } maps)
        {
            await Write(
                    SettingsDomain.MapDefaults,
                    SetupSettingsDiff.Compare(current with { MapDefaults = target.MapDefaults }, current).Count > 0,
                    () => maps.ReplaceAllAsync(target.MapDefaults, token))
                .ConfigureAwait(true);
        }

        if (_sources.InterfaceLanguage is { } language)
        {
            await Write(
                    SettingsDomain.InterfaceLanguage,
                    !string.Equals(target.InterfaceLanguage, current.InterfaceLanguage, StringComparison.OrdinalIgnoreCase),
                    () => Sync(() => language.Set(target.InterfaceLanguage)))
                .ConfigureAwait(true);
        }

        if (_sources.CaptureShortcut is { } shortcut)
        {
            await Write(
                    SettingsDomain.CaptureShortcut,
                    target.CaptureShortcut != current.CaptureShortcut,
                    () => Sync(() => shortcut.Set(target.CaptureShortcut)))
                .ConfigureAwait(true);
        }

        return failed;
    }

    private static bool IsStoreProblem(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException;

    private static bool IsFileProblem(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

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
            SettingsDomain.InterfaceLanguage => snapshot with { InterfaceLanguage = defaults.InterfaceLanguage },
            SettingsDomain.CaptureShortcut => snapshot with { CaptureShortcut = defaults.CaptureShortcut },
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
        InterfaceLanguage = _sources.InterfaceLanguage is null ? current.InterfaceLanguage : target.InterfaceLanguage,
        CaptureShortcut = _sources.CaptureShortcut is null ? current.CaptureShortcut : target.CaptureShortcut,
    };

    private void SetOrClearPending(
        PendingRequest request,
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

        _pendingKind = request.Kind;
        _pendingRequest = request;
        PendingDiff = [.. diff.Select(SetupSettingsDiffRow.From)];
        PendingLabel = label;
        StatusMessage = string.Empty;
        OnPropertyChanged(nameof(HasPendingChange));
        OnPropertyChanged(nameof(ShowsStatus));
    }

    private void ClearPending()
    {
        _pendingKind = SetupSettingsPendingKind.None;
        _pendingRequest = null;
        PendingDiff = [];
        PendingLabel = string.Empty;
        OnPropertyChanged(nameof(HasPendingChange));
        OnPropertyChanged(nameof(ShowsStatus));
    }

    private static bool IsResettable(V2SetupSection section) => SettingsRegistry.HasSettings(section);

    /// <summary>What the preview asked for, so Confirm can build it again from what is in force then.</summary>
    private sealed record PendingRequest(SetupSettingsPendingKind Kind, V2SetupSection Section, string? ImportText);
}
