using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>One profile in the list Setup › Game &amp; Profile offers.</summary>
public sealed class SetupProfileRowViewModel : BindableViewModel
{
    private bool _isEditing;
    private SetupProfileModeOption _editMode;
    private string _editWipe;

    internal SetupProfileRowViewModel(
        ProfileRecord record,
        bool isActive,
        IReadOnlyList<SetupProfileModeOption> modes,
        ParameterCommand<SetupProfileRowViewModel> switchTo,
        ParameterCommand<SetupProfileRowViewModel> archive,
        ParameterCommand<SetupProfileRowViewModel> restore,
        ParameterCommand<SetupProfileRowViewModel> saveEdit)
    {
        Id = record.Context.Identity.ProfileId;
        Name = record.Name;
        IsActive = isActive;
        IsArchived = record.Lifecycle == ProfileLifecycle.Archived;
        Mode = SetupProfilesViewModel.ModeLabel(record.Context.Mode);
        Wipe = record.Context.WipeSeason.Value;
        Summary = $"{Mode} · {Wipe} · {record.Context.Locale.Language}";
        StatusLabel = IsActive
            ? V2ShellText.Get("V2.Setup.Profiles.ActiveBadge")
            : IsArchived ? V2ShellText.Get("V2.Setup.Profiles.ArchivedBadge") : string.Empty;
        SwitchCommand = switchTo;
        ArchiveCommand = archive;
        RestoreCommand = restore;

        // #292 task 3: mode and wipe label are set at creation and were unreachable after that.
        Modes = modes;
        _editMode = modes.FirstOrDefault(option => option.Mode == record.Context.Mode) ?? modes[0];
        _editWipe = Wipe;
        BeginEditCommand = new DelegateCommand(() => IsEditing = true);
        CancelEditCommand = new DelegateCommand(() =>
        {
            IsEditing = false;
            EditMode = _editMode;
            EditWipe = Wipe;
        });
        SaveEditCommand = saveEdit;
    }

    public IReadOnlyList<SetupProfileModeOption> Modes { get; }

    public bool IsEditing
    {
        get => _isEditing;
        private set => SetProperty(ref _isEditing, value);
    }

    public SetupProfileModeOption EditMode
    {
        get => _editMode;
        set => SetProperty(ref _editMode, value ?? Modes[0]);
    }

    public string EditWipe
    {
        get => _editWipe;
        set => SetProperty(ref _editWipe, value ?? string.Empty);
    }

    public ICommand BeginEditCommand { get; }

    public ICommand CancelEditCommand { get; }

    public ICommand SaveEditCommand { get; }

    public string EditLabel => V2ShellText.Get("V2.Setup.Profiles.Edit");

    public string SaveEditLabel => V2ShellText.Get("V2.Setup.Profiles.SaveEdit");

    public string CancelEditLabel => V2ShellText.Get("V2.Setup.Profiles.CancelEdit");

    public string EditModeFieldLabel => V2ShellText.Get("V2.Setup.Profiles.ModeLabel");

    public string EditWipeFieldLabel => V2ShellText.Get("V2.Setup.Profiles.WipeLabel");

    public Guid Id { get; }

    public string Name { get; }

    public string Mode { get; }

    public string Wipe { get; }

    public string Summary { get; }

    public bool IsActive { get; }

    public bool IsArchived { get; }

    public string StatusLabel { get; }

    public bool HasStatus => StatusLabel.Length > 0;

    public bool CanSwitch => !IsActive && !IsArchived;

    /// <summary>The active profile cannot be archived: it would leave the app with nothing selected.</summary>
    public bool CanArchive => !IsActive && !IsArchived;

    public bool CanRestore => IsArchived;

    public string AutomationName => $"{Name}, {Summary}";

    public string SwitchLabel => V2ShellText.Get("V2.Setup.Profiles.Switch");

    public string ArchiveLabel => V2ShellText.Get("V2.Setup.Profiles.Archive");

    public string RestoreLabel => V2ShellText.Get("V2.Setup.Profiles.Restore");

    public ICommand SwitchCommand { get; }

    public ICommand ArchiveCommand { get; }

    public ICommand RestoreCommand { get; }
}

/// <summary>A game mode a new profile can be created in.</summary>
public sealed record SetupProfileModeOption(ProfileGameMode Mode, string Label);

/// <summary>
/// Setup › Game &amp; Profile: the list of profiles, switching between them, and creating a new one for
/// a game mode and wipe (#269).
/// </summary>
/// <remarks>
/// Everything here goes through <see cref="ProfileManagementService"/>, so what this page does is
/// what the app does: the switch changes which progress every page reads and, through the runtime
/// context, which game mode and language the catalog is fetched for. This page never mutates the
/// list itself; it redraws from the context the service publishes.
/// </remarks>
public sealed class SetupProfilesViewModel : BindableViewModel, IDisposable
{
    private readonly ProfileManagementService _service;
    private readonly Action<Action> _post;
    private readonly Action<ProfileRuntimeContextChanged> _onChanged;
    private readonly ParameterCommand<SetupProfileRowViewModel> _switchRow;
    private readonly ParameterCommand<SetupProfileRowViewModel> _archiveRow;
    private readonly ParameterCommand<SetupProfileRowViewModel> _restoreRow;
    private readonly ParameterCommand<SetupProfileRowViewModel> _saveEditRow;
    private string _activeTitle = string.Empty;
    private string _activeDetail = string.Empty;
    private string _scopeLine = string.Empty;
    private string _notice = string.Empty;
    private string _newName = string.Empty;
    private string _newWipe = string.Empty;
    private SetupProfileModeOption _selectedMode;
    private string _message = string.Empty;
    private bool _messageIsError;
    private bool _showArchived;

    public SetupProfilesViewModel(
        ProfileManagementService service,
        Action<Action>? post = null,
        SetupProfileTransferViewModel? transfer = null)
    {
        Transfer = transfer;
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _post = post ?? (action => action());
        Modes =
        [
            new(ProfileGameMode.Pvp, ModeLabel(ProfileGameMode.Pvp)),
            new(ProfileGameMode.Pve, ModeLabel(ProfileGameMode.Pve)),
            new(ProfileGameMode.Seasonal, ModeLabel(ProfileGameMode.Seasonal)),
        ];
        _selectedMode = Modes[0];
        _switchRow = new(row => Run(row, service.SwitchAsync, "V2.Setup.Profiles.Switched"));
        _archiveRow = new(row => Run(row, service.ArchiveAsync, "V2.Setup.Profiles.ArchivedDone"));
        _restoreRow = new(row => Run(row, service.RestoreAsync, "V2.Setup.Profiles.Restored"));
        _saveEditRow = new(SaveEdit);
        CreateCommand = new AsyncDelegateCommand(CreateAsync);
        _onChanged = change => _post(() => Apply(change.Snapshot));
        _service.Changed += _onChanged;
        Apply(_service.Current);
    }

    public ObservableCollection<SetupProfileRowViewModel> Profiles { get; } = [];

    /// <summary>Export and import of the active profile's progress; null where no file access is composed.</summary>
    public SetupProfileTransferViewModel? Transfer { get; }

    public bool HasTransfer => Transfer is not null;

    public IReadOnlyList<SetupProfileModeOption> Modes { get; }

    public ICommand CreateCommand { get; }

    public string Heading => V2ShellText.Get("V2.Setup.Profiles.Heading");
    public string Intro => V2ShellText.Get("V2.Setup.Profiles.Intro");
    public string NewHeading => V2ShellText.Get("V2.Setup.Profiles.NewHeading");
    public string NameLabel => V2ShellText.Get("V2.Setup.Profiles.NameLabel");
    public string NamePlaceholder => V2ShellText.Get("V2.Setup.Profiles.NamePlaceholder");
    public string WipeLabel => V2ShellText.Get("V2.Setup.Profiles.WipeLabel");
    public string WipePlaceholder => V2ShellText.Get("V2.Setup.Profiles.WipePlaceholder");
    public string ModeFieldLabel => V2ShellText.Get("V2.Setup.Profiles.ModeLabel");
    public string CreateLabel => V2ShellText.Get("V2.Setup.Profiles.Create");
    public string ShowArchivedLabel => V2ShellText.Get("V2.Setup.Profiles.ShowArchived");

    /// <summary>The active profile's name, or a sentence saying there is none.</summary>
    public string ActiveTitle
    {
        get => _activeTitle;
        private set => SetProperty(ref _activeTitle, value);
    }

    public string ActiveDetail
    {
        get => _activeDetail;
        private set => SetProperty(ref _activeDetail, value);
    }

    /// <summary>What the catalog is being fetched for, so a switch visibly changes something.</summary>
    /// <summary>
    /// "wipe · EN" for the top bar, beside the profile's name and game mode: the other two things
    /// that decide which progress and which catalog every page is showing. Empty with no profile.
    /// </summary>
    public string HeaderSuffix
    {
        get => _headerSuffix;
        private set => SetProperty(ref _headerSuffix, value);
    }

    private string _headerSuffix = string.Empty;

    public string ScopeLine
    {
        get => _scopeLine;
        private set => SetProperty(ref _scopeLine, value);
    }

    /// <summary>Shown when the context is not usable: no profile, or a profile with no game mode.</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value))
            {
                OnPropertyChanged(nameof(HasNotice));
            }
        }
    }

    public bool HasNotice => Notice.Length > 0;

    public string NewName
    {
        get => _newName;
        set => SetProperty(ref _newName, value ?? string.Empty);
    }

    public string NewWipe
    {
        get => _newWipe;
        set => SetProperty(ref _newWipe, value ?? string.Empty);
    }

    public SetupProfileModeOption SelectedMode
    {
        get => _selectedMode;
        set => SetProperty(ref _selectedMode, value ?? Modes[0]);
    }

    public bool ShowArchived
    {
        get => _showArchived;
        set
        {
            if (SetProperty(ref _showArchived, value))
            {
                Apply(_service.Current);
            }
        }
    }

    public bool HasArchived { get; private set; }

    /// <summary>The outcome of the last thing the player asked for, in one line.</summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => Message.Length > 0;

    public bool MessageIsError
    {
        get => _messageIsError;
        private set => SetProperty(ref _messageIsError, value);
    }

    public static string ModeLabel(ProfileGameMode mode) => GameModeLabel.Of(mode);

    /// <summary>Loads the workspace and draws it, for a caller that wants the list before any event has.</summary>
    /// <remarks>
    /// Nothing is said on failure. This is a passive read, and early in a launch the profile table may
    /// not be migrated yet; an error the player did not cause and cannot act on is worse than an empty
    /// list that fills in when the context is published. Errors from something they pressed are shown.
    /// </remarks>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            Apply(await _service.LoadAsync(cancellationToken).ConfigureAwait(true));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }
    }

    public void Dispose() => _service.Changed -= _onChanged;

    private async Task CreateAsync()
    {
        var name = NewName.Trim();
        try
        {
            var snapshot = await _service
                .CreateAsync(name, SelectedMode.Mode, NewWipe, CancellationToken.None)
                .ConfigureAwait(true);
            Apply(snapshot);
            NewName = string.Empty;
            NewWipe = string.Empty;
            Say(V2ShellText.Format("V2.Setup.Profiles.Created", CultureInfo.CurrentCulture, name), isError: false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fail(exception);
        }
    }

    private async void Run(
        SetupProfileRowViewModel? row,
        Func<Guid, CancellationToken, Task<ProfileRuntimeContextSnapshot>> action,
        string doneKey)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            Apply(await action(row.Id, CancellationToken.None).ConfigureAwait(true));
            Say(V2ShellText.Format(doneKey, CultureInfo.CurrentCulture, row.Name), isError: false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fail(exception);
        }
    }

    private async void SaveEdit(SetupProfileRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            Apply(await _service
                .UpdateAsync(row.Id, row.EditMode.Mode, row.EditWipe, CancellationToken.None)
                .ConfigureAwait(true));
            Say(V2ShellText.Format("V2.Setup.Profiles.Edited", CultureInfo.CurrentCulture, row.Name), isError: false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fail(exception);
        }
    }

    private void Fail(Exception exception) =>
        // The domain's own refusals (archive the active profile, a blank name) are already written for
        // the player; anything else is shown as-is rather than swallowed.
        Say(exception.Message, isError: true);

    private void Say(string text, bool isError)
    {
        MessageIsError = isError;
        Message = text;
    }

    private void Apply(ProfileRuntimeContextSnapshot snapshot)
    {
        Profiles.Clear();
        var archived = 0;
        foreach (var record in snapshot.Workspace.Profiles)
        {
            var isActive = snapshot.Workspace.ActiveProfileId == record.Context.Identity.ProfileId;
            if (record.Lifecycle == ProfileLifecycle.Archived)
            {
                archived++;
                if (!ShowArchived)
                {
                    continue;
                }
            }

            Profiles.Add(new(record, isActive, Modes, _switchRow, _archiveRow, _restoreRow, _saveEditRow));
        }

        HasArchived = archived > 0;
        OnPropertyChanged(nameof(HasArchived));
        var active = snapshot.ActiveProfile;
        ActiveTitle = active?.Name ?? V2ShellText.Get("V2.Setup.Profiles.None");
        ActiveDetail = active is null
            ? string.Empty
            : $"{ModeLabel(active.Context.Mode)} · {active.Context.WipeSeason.Value}";
        ScopeLine = snapshot.CatalogScope is { } scope
            ? V2ShellText.Format(
                "V2.Setup.Profiles.DataFor",
                CultureInfo.CurrentCulture,
                ModeLabel(active!.Context.Mode),
                scope.Language)
            : string.Empty;
        HeaderSuffix = active is null
            ? string.Empty
            : string.Join(
                " · ",
                new[] { active.Context.WipeSeason.Value, active.Context.Locale.Language.ToUpperInvariant() }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
        Notice = snapshot.State switch
        {
            ProfileRuntimeContextState.UnknownGameMode => V2ShellText.Get("V2.Setup.Profiles.UnknownMode"),
            ProfileRuntimeContextState.NoActiveProfile => V2ShellText.Get("V2.Setup.Profiles.NoProfile"),
            _ => string.Empty,
        };
    }
}
