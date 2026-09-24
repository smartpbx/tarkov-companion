using System.Windows.Input;
using TarkovCompanion.Application.Services.Group;

namespace TarkovCompanion.App.ViewModels.V2.Team;

/// <summary>[#289] One squadmate's extract, note and ready state, as their companion shared them.</summary>
public sealed record TeamSquadStatusRowViewModel(string Name, bool? Ready, string? Extract, string? Note)
{
    public string ReadyLabel => Ready switch
    {
        true => "Ready",
        false => "Not ready",
        null => string.Empty,
    };

    public bool IsReady => Ready == true;

    public bool IsNotReady => Ready == false;

    public bool HasReady => Ready is not null;

    public string ExtractLabel => Extract is { Length: > 0 } extract ? $"Extract · {extract}" : string.Empty;

    public bool HasExtract => ExtractLabel.Length > 0;

    public bool HasNote => Note is { Length: > 0 };
}

/// <summary>
/// [#289] The Team workspace's "Ready check": what this player shares with the squad (an extract
/// from the map on screen, a short note, ready or not) and what each squadmate shared.
/// </summary>
/// <remarks>
/// Only what the player picks here is sent, and only through their own companion; a squadmate's
/// row is only what their companion chose to publish. Nothing is inferred: a member whose build
/// predates this says nothing, which is shown as nothing rather than as "not ready".
/// </remarks>
public sealed partial class TeamWorkspaceViewModel
{
    /// <summary>The extract picker's first entry, which clears the choice.</summary>
    public const string NoExtract = "No extract";

    private GroupSquadStatus? _squadStatus;
    private string _noteDraft = string.Empty;
    private IReadOnlyList<string> _extractOptions = [NoExtract];
    private string? _extractMapId;
    private ICommand? _readyCommand;
    private ICommand? _notReadyCommand;
    private ICommand? _shareNoteCommand;

    /// <summary>Whether this workspace can share a status at all (composed with a status holder).</summary>
    public bool CanShareStatus { get; private set; }

    private SquadStatus MyStatus => _squadStatus?.Current ?? SquadStatus.None;

    public bool IsReady => MyStatus.Ready == true;

    public bool IsNotReady => MyStatus.Ready == false;

    /// <summary>Sets ready; pressing it again takes the answer back to "not said".</summary>
    public ICommand ReadyCommand => _readyCommand ??= new DelegateCommand(() => SetReady(IsReady ? null : true));

    public ICommand NotReadyCommand => _notReadyCommand ??= new DelegateCommand(() => SetReady(IsNotReady ? null : false));

    /// <summary>The extracts of the map on screen, after <see cref="NoExtract"/>.</summary>
    public IReadOnlyList<string> ExtractOptions => _extractOptions;

    /// <summary>The extract shared, or <see cref="NoExtract"/>.</summary>
    /// <remarks>
    /// A null from the picker is ignored: the combo box writes one back when its list is rebuilt
    /// without the chosen item (the top bar moved to another map), and that is not the player
    /// clearing their plan. Only picking <see cref="NoExtract"/> clears it.
    /// </remarks>
    public string? SelectedExtract
    {
        get => MyStatus.Extract is { } extract && _extractOptions.Contains(extract, StringComparer.Ordinal) ? extract : NoExtract;
        set
        {
            if (value is null || _squadStatus is not { } status)
            {
                return;
            }

            var extract = value == NoExtract ? null : value;
            status.Set(MyStatus with { Extract = extract, ExtractMapId = extract is null ? null : _extractMapId });
        }
    }

    /// <summary>What is typed in the note box, sent by <see cref="ShareNoteCommand"/>.</summary>
    public string NoteDraft
    {
        get => _noteDraft;
        set
        {
            if (SetProperty(ref _noteDraft, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanShareNote));
            }
        }
    }

    public int NoteLimit { get; } = SquadStatus.NoteLimit;

    /// <summary>True when the box says something other than what the squad already has.</summary>
    public bool CanShareNote => _squadStatus is not null &&
        !string.Equals(NoteDraft.Trim(), MyStatus.Note ?? string.Empty, StringComparison.Ordinal);

    /// <summary>Sends the note; an empty box takes the note back.</summary>
    public ICommand ShareNoteCommand => _shareNoteCommand ??= new DelegateCommand(ShareNote);

    /// <summary>"Your note · meet at dorms", or empty when none is shared.</summary>
    public string SharedNoteLabel => MyStatus.Note is { } note ? $"Shared · {note}" : string.Empty;

    public bool HasSharedNote => SharedNoteLabel.Length > 0;

    /// <summary>Every squadmate who said anything, in the relay's order.</summary>
    public IReadOnlyList<TeamSquadStatusRowViewModel> SquadStatusRows { get; private set; } = [];

    public bool HasSquadStatusRows => SquadStatusRows.Count > 0;

    /// <summary>"2 of 3 ready", counting this player; empty with nobody else in the group.</summary>
    public string ReadySummary { get; private set; } = string.Empty;

    private void AttachSquadStatus(GroupSquadStatus? status)
    {
        _squadStatus = status;
        CanShareStatus = status is not null;
        if (status is null)
        {
            return;
        }

        _noteDraft = status.Current.Note ?? string.Empty;

        // Set only from this workspace, on the UI thread; heard here so every binding follows
        // whichever control changed it.
        status.Changed += () =>
        {
            if (NoteDraft.Length == 0 && MyStatus.Note is { } note)
            {
                NoteDraft = note;
            }

            RaiseMyStatus();
            RefreshSquadStatus(_group);
        };
    }

    private void SetReady(bool? ready)
    {
        if (_squadStatus is not { } status)
        {
            return;
        }

        status.Set(MyStatus with { Ready = ready });
    }

    private void ShareNote()
    {
        if (_squadStatus is not { } status)
        {
            return;
        }

        status.Set(MyStatus with { Note = NoteDraft });
        NoteDraft = MyStatus.Note ?? string.Empty;
        OnPropertyChanged(nameof(CanShareNote));
    }

    private void RaiseMyStatus()
    {
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsNotReady));
        OnPropertyChanged(nameof(SelectedExtract));
        OnPropertyChanged(nameof(CanShareNote));
        OnPropertyChanged(nameof(SharedNoteLabel));
        OnPropertyChanged(nameof(HasSharedNote));
    }

    /// <summary>Rebuilds the squad's rows and the extract list; runs on every <see cref="Apply"/>.</summary>
    private void RefreshSquadStatus(GroupSnapshot group)
    {
        var rows = BuildSquadStatusRows(group.Members);
        if (!rows.SequenceEqual(SquadStatusRows))
        {
            SquadStatusRows = rows;
            OnPropertyChanged(nameof(SquadStatusRows));
            OnPropertyChanged(nameof(HasSquadStatusRows));
        }

        var summary = DescribeReadiness(MyStatus.Ready, group.Members);
        if (summary != ReadySummary)
        {
            ReadySummary = summary;
            OnPropertyChanged(nameof(ReadySummary));
        }

        // The extracts of the map the Raid workspace shows (the top bar's map picker), by the same
        // names its extract list and map labels use.
        var mapId = _raidCockpit?.Renderer?.Scene.LocationId;
        IReadOnlyList<string> options =
        [
            NoExtract,
            .. (_raidCockpit?.MapExtracts ?? [])
                .Select(row => row.Name)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.CurrentCulture),
        ];
        if (mapId != _extractMapId || !options.SequenceEqual(_extractOptions))
        {
            _extractMapId = mapId;
            _extractOptions = options;
            OnPropertyChanged(nameof(ExtractOptions));
            OnPropertyChanged(nameof(SelectedExtract));
        }
    }

    internal static IReadOnlyList<TeamSquadStatusRowViewModel> BuildSquadStatusRows(IReadOnlyList<GroupMemberView> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return
        [
            .. members
                .Where(member => member.Ready is not null || member.PlannedExtract is not null || member.Note is not null)
                .Select(member => new TeamSquadStatusRowViewModel(member.Name, member.Ready, member.PlannedExtract, member.Note)),
        ];
    }

    /// <summary>"2 of 3 ready": this player and every squadmate, a silent one counted as not ready.</summary>
    internal static string DescribeReadiness(bool? mine, IReadOnlyList<GroupMemberView> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0)
        {
            return string.Empty;
        }

        var ready = (mine == true ? 1 : 0) + members.Count(member => member.Ready == true);
        return $"{ready} of {members.Count + 1} ready";
    }

    /// <summary>"Ready · Extract · ZB-1011 · meet at dorms" for the member lists; empty when unsaid.</summary>
    internal static string DescribeStatus(GroupMemberView member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var row = new TeamSquadStatusRowViewModel(member.Name, member.Ready, member.PlannedExtract, member.Note);
        return JoinDetail(row.ReadyLabel, row.ExtractLabel, row.Note ?? string.Empty);
    }
}
