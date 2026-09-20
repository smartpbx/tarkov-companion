using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Notifications;
using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Application.Services.Personalization;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Setup;
using TarkovCompanion.Core.Domain.Personalization;

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
    private V2SetupSection _currentSection = V2SetupSection.Overview;
    private SetupSettingsPendingKind _pendingKind;
    private SetupSettingsSnapshot? _pendingTarget;
    private IReadOnlyList<SetupSettingsDiffEntry> _pendingDiff = [];
    private string _pendingLabel = string.Empty;
    private string _exchangePath = string.Empty;
    private string _statusMessage = string.Empty;

    public SetupSettingsAdminViewModel(
        WorkspacePreferenceService preferences,
        IScreenshotRetentionStore retention,
        NotificationBridge? notifications = null)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        _notifications = notifications;

        ResetSectionCommand = new AsyncDelegateCommand(PrepareResetSectionAsync);
        ResetAllCommand = new AsyncDelegateCommand(PrepareResetAllAsync);
        ExportCommand = new AsyncDelegateCommand(ExportAsync);
        PreviewImportCommand = new AsyncDelegateCommand(PreviewImportAsync);
        ConfirmCommand = new AsyncDelegateCommand(ConfirmAsync);
        CancelCommand = new DelegateCommand(ClearPending);
    }

    /// <summary>Which section a bare "Reset this section" press acts on. Pushed by
    /// <see cref="V2SetupWorkspaceViewModel"/> whenever the selection changes.</summary>
    public void SetCurrentSection(V2SetupSection section) => _currentSection = section;

    public string ExchangePath
    {
        get => _exchangePath;
        set => SetProperty(ref _exchangePath, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool HasPendingChange => _pendingKind != SetupSettingsPendingKind.None;

    public string PendingLabel
    {
        get => _pendingLabel;
        private set => SetProperty(ref _pendingLabel, value);
    }

    public IReadOnlyList<SetupSettingsDiffEntry> PendingDiff
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
            StatusMessage = "Nothing on this section can be reset.";
            ClearPending();
            return;
        }

        var current = await CaptureCurrentAsync().ConfigureAwait(true);
        var target = section switch
        {
            V2SetupSection.Accessibility => current with { Appearance = WorkspacePreferences.Default },
            V2SetupSection.Notifications => current with { Notifications = NotificationSettings.Default },
            V2SetupSection.Privacy => current with { ScreenshotRetention = ScreenshotRetentionSettings.Default },
            _ => current,
        };

        SetOrClearPending(SetupSettingsPendingKind.ResetSection, target, current, "Reset this section?", "This section is already at its defaults.");
    }

    private async Task PrepareResetAllAsync()
    {
        var current = await CaptureCurrentAsync().ConfigureAwait(true);
        SetOrClearPending(
            SetupSettingsPendingKind.ResetAll,
            SetupSettingsSnapshot.Default,
            current,
            "Reset everything?",
            "Everything is already at its defaults.");
    }

    private async Task ExportAsync()
    {
        if (string.IsNullOrWhiteSpace(ExchangePath))
        {
            StatusMessage = "Enter a file path first.";
            return;
        }

        try
        {
            var current = await CaptureCurrentAsync().ConfigureAwait(true);
            await File.WriteAllTextAsync(ExchangePath, SetupSettingsExport.ToJson(current)).ConfigureAwait(true);
            StatusMessage = $"Exported to {ExchangePath}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Not exported · {exception.Message}";
        }
    }

    private async Task PreviewImportAsync()
    {
        if (string.IsNullOrWhiteSpace(ExchangePath))
        {
            StatusMessage = "Enter a file path first.";
            return;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(ExchangePath).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Not imported · {exception.Message}";
            ClearPending();
            return;
        }

        var result = SetupSettingsExport.Validate(text);
        if (!result.IsValid)
        {
            StatusMessage = $"Not imported · {result.Error}";
            ClearPending();
            return;
        }

        var current = await CaptureCurrentAsync().ConfigureAwait(true);
        SetOrClearPending(
            SetupSettingsPendingKind.Import,
            result.Snapshot!,
            current,
            $"Import from {ExchangePath}?",
            "Already matches what is in force. Nothing to import.");
    }

    private async Task ConfirmAsync()
    {
        if (_pendingTarget is not { } target)
        {
            return;
        }

        var applied = _pendingKind;
        await _preferences.UpdateAsync(target.Appearance, CancellationToken.None).ConfigureAwait(true);
        if (_notifications is not null)
        {
            foreach (var kind in NotificationSamples.All)
            {
                await _notifications.SetEnabledAsync(kind, target.Notifications.IsEnabled(kind), CancellationToken.None).ConfigureAwait(true);
            }

            await _notifications.SetPopupAsync(target.Notifications.ShowsDesktopPopup, CancellationToken.None).ConfigureAwait(true);
        }

        await _retention.SaveAsync(target.ScreenshotRetention, CancellationToken.None).ConfigureAwait(true);

        StatusMessage = applied switch
        {
            SetupSettingsPendingKind.ResetSection => "This section's settings were reset.",
            SetupSettingsPendingKind.ResetAll => "Everything was reset to its defaults.",
            SetupSettingsPendingKind.Import => "Imported.",
            _ => StatusMessage,
        };
        ClearPending();
    }

    private async Task<SetupSettingsSnapshot> CaptureCurrentAsync()
    {
        var retention = await _retention.GetAsync(CancellationToken.None).ConfigureAwait(true);
        return new SetupSettingsSnapshot(_preferences.Current, _notifications?.Settings ?? NotificationSettings.Default, retention);
    }

    private void SetOrClearPending(
        SetupSettingsPendingKind kind,
        SetupSettingsSnapshot target,
        SetupSettingsSnapshot current,
        string label,
        string nothingToChangeMessage)
    {
        var diff = SetupSettingsDiff.Compare(current, target);
        if (diff.Count == 0)
        {
            StatusMessage = nothingToChangeMessage;
            ClearPending();
            return;
        }

        _pendingKind = kind;
        _pendingTarget = target;
        PendingDiff = diff;
        PendingLabel = label;
        StatusMessage = string.Empty;
        OnPropertyChanged(nameof(HasPendingChange));
    }

    private void ClearPending()
    {
        _pendingKind = SetupSettingsPendingKind.None;
        _pendingTarget = null;
        PendingDiff = [];
        PendingLabel = string.Empty;
        OnPropertyChanged(nameof(HasPendingChange));
    }

    private static bool IsResettable(V2SetupSection section) => section is
        V2SetupSection.Accessibility or V2SetupSection.Notifications or V2SetupSection.Privacy;
}
