using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Core.Network;

namespace TarkovCompanion.App.ViewModels.V2.Team;

/// <summary>
/// [#902] The three sharing switches save when flipped; the address, name and key wait for Save
/// and survive leaving the page; and a switch that Local only overrides says so beside itself.
/// </summary>
/// <remarks>
/// Before this, only Save wrote anything, and the shell reloads this form every time a Team tab
/// opens. So a switch flipped on Group and followed by a visit to Devices was quietly put back,
/// and so was anything typed into the fields. Setup meanwhile had its own "Sync quests with
/// squad" switch over the same stored value, which saved at once and went stale beside this one.
/// Now there is one set of switches, here, and every other view re-reads the store's Changed.
///
/// A switch writes the three switches over whatever the store holds for the fields, so flipping
/// one never commits a half-typed relay address. The fields keep an edit across a reload by being
/// compared with what was last read: a field that still says what the store said takes the new
/// stored value, and one the player has changed keeps the player's text.
/// </remarks>
public sealed partial class TeamWorkspaceViewModel
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private INetworkPolicy? _networkPolicy;
    private Action<Action> _dispatch = static action => action();
    private GroupSharingSettings? _loaded;
    private bool _applyingStored;
    private int _ownSaves;
    private NetworkVerdict _sharingVerdict = NetworkVerdict.Allowed;

    /// <summary>Raised by "Change" beside a blocked switch; the shell opens Setup › Data &amp; Privacy.</summary>
    public event EventHandler? OpenDataPrivacyRequested;

    /// <summary>The last switch save, so a caller (Leave, a test) can wait for it.</summary>
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

    public ICommand OpenNetworkSettingsCommand { get; private set; } = null!;

    /// <summary>Local only, or the Squad sharing permission, stops anything leaving.</summary>
    public bool IsSharingBlocked => _sharingVerdict != NetworkVerdict.Allowed;

    public string SharingBlockedLabel => _sharingVerdict switch
    {
        NetworkVerdict.LocalOnly => TeamText.BlockedByLocalOnly,
        NetworkVerdict.SwitchedOff => TeamText.BlockedBySharingOff,
        _ => string.Empty,
    };

    /// <summary>The address, name or key differ from what is stored.</summary>
    public bool HasUnsavedFields =>
        _loaded is { } loaded &&
        (!SameText(ServerUri, loaded.ServerUri) || !SameText(DisplayName, loaded.DisplayName) || !SameText(Key, loaded.Key));

    private void AttachSharingState(INetworkPolicy? networkPolicy, Action<Action>? dispatch)
    {
        _dispatch = dispatch ?? PostToUi;
        _networkPolicy = networkPolicy;
        OpenNetworkSettingsCommand = new DelegateCommand(() => OpenDataPrivacyRequested?.Invoke(this, EventArgs.Empty));
        _groupSettings.Changed += GroupSettingsChanged;
        if (_networkPolicy is not null)
        {
            _networkPolicy.Changed += (_, _) => _dispatch(RefreshSharingVerdict);
        }

        RefreshSharingVerdict();
    }

    private static void PostToUi(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    private void RefreshSharingVerdict()
    {
        var verdict = _networkPolicy?.Check(NetworkService.SquadSharing) ?? NetworkVerdict.Allowed;
        if (verdict == _sharingVerdict)
        {
            return;
        }

        _sharingVerdict = verdict;
        OnPropertyChanged(nameof(IsSharingBlocked));
        OnPropertyChanged(nameof(SharingBlockedLabel));
    }

    /// <summary>Somebody else saved (Setup, the V1 page, a reset): show it. Our own saves are skipped.</summary>
    private void GroupSettingsChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _ownSaves) > 0)
        {
            return;
        }

        _dispatch(() => _ = LoadAsync());
    }

    private void ApplyStored(GroupSharingSettings stored)
    {
        var previous = _loaded;
        _applyingStored = true;
        try
        {
            IsEnabled = stored.IsEnabled;
            SharesLoadout = stored.SharesLoadout;
            SharesQuests = stored.SharesQuests;
            // A field the player has not touched since the last read follows the store; an edited one stays.
            if (previous is null || SameText(ServerUri, previous.ServerUri))
            {
                ServerUri = stored.ServerUri ?? string.Empty;
            }

            if (previous is null || SameText(DisplayName, previous.DisplayName))
            {
                DisplayName = stored.DisplayName ?? string.Empty;
            }

            if (previous is null || SameText(Key, previous.Key))
            {
                Key = stored.Key ?? string.Empty;
            }

            _loaded = stored;
        }
        finally
        {
            _applyingStored = false;
        }

        OnPropertyChanged(nameof(HasUnsavedFields));
    }

    private void SwitchFlipped()
    {
        if (_applyingStored)
        {
            return;
        }

        var prior = PendingSave;
        PendingSave = SaveSwitchesAsync(prior);
    }

    private void FieldEdited()
    {
        OnPropertyChanged(nameof(HasUnsavedFields));
        if (!_applyingStored && HasUnsavedFields)
        {
            Status = TeamText.UnsavedFields;
        }
    }

    private async Task SaveSwitchesAsync(Task prior)
    {
        await prior.ConfigureAwait(true);
        GroupSharingSettings? saved = null;
        var ok = await SaveOwnAsync(stored => saved = stored with
        {
            IsEnabled = IsEnabled,
            SharesLoadout = SharesLoadout,
            SharesQuests = SharesQuests,
        }).ConfigureAwait(true);
        if (ok && saved is not null)
        {
            Status = HasUnsavedFields ? TeamText.UnsavedFields : SavedStatus(saved);
        }
    }

    /// <summary>Reads, builds and writes under one gate, so two quick flips cannot interleave.</summary>
    private async Task<bool> SaveOwnAsync(Func<GroupSharingSettings, GroupSharingSettings> build)
    {
        await _saveGate.WaitAsync().ConfigureAwait(true);
        Interlocked.Increment(ref _ownSaves);
        try
        {
            var stored = await _groupSettings.GetAsync(CancellationToken.None).ConfigureAwait(true);
            var next = build(stored);
            await _groupSettings.SaveAsync(next, CancellationToken.None).ConfigureAwait(true);
            // The fields now compare against what is stored, so a saved field is no longer "edited".
            _loaded = _loaded is null
                ? next
                : _loaded with { ServerUri = next.ServerUri, DisplayName = next.DisplayName, Key = next.Key };
            OnPropertyChanged(nameof(HasUnsavedFields));
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CrashLog.Write("workspace-fault/team", $"save: {exception}");
            Status = TeamText.SaveFailed(exception.Message);
            return false;
        }
        finally
        {
            Interlocked.Decrement(ref _ownSaves);
            _saveGate.Release();
        }
    }

    private static string SavedStatus(GroupSharingSettings settings) =>
        !settings.IsEnabled
            ? TeamText.SavedSharingOff
            : settings.Gap is { } missing
                ? TeamText.SavedStillNeeds(PhraseText.Say(missing))
                : TeamText.SavedSharingStarts;

    private static bool SameText(string field, string? stored) =>
        string.Equals(field.Trim(), stored?.Trim() ?? string.Empty, StringComparison.Ordinal);
}
