using System.Collections.ObjectModel;
using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Devices;

namespace TarkovCompanion.App.ViewModels.V2.Team;

/// <summary>
/// [#920] The relay owner's panel in Team › Devices: the rooms on the relay, and the few things the
/// owner can do to one — clear its drawings or marks, remove a member, reset it.
/// </summary>
/// <remarks>
/// Shown only when the relay itself says this desktop is its owner (<see cref="IsRelayOwner"/>);
/// a squadmate's desktop asks, is told no, and never sees it. Every action is destructive to other
/// people's screens, so each one asks once, inside the room it acts on, before it is sent.
/// </remarks>
public sealed class RelayAdminPanelViewModel : BindableViewModel
{
    private readonly RelayAdminClient _client;
    private readonly TimeProvider _clock;
    private bool _isRelayOwner;
    private string? _status;
    private bool _loaded;

    public RelayAdminPanelViewModel(RelayAdminClient client, TimeProvider? clock = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _clock = clock ?? TimeProvider.System;
        RefreshCommand = new AsyncDelegateCommand(() => RefreshAsync(CancellationToken.None));
    }

    public bool IsRelayOwner
    {
        get => _isRelayOwner;
        private set => SetProperty(ref _isRelayOwner, value);
    }

    public ObservableCollection<RelayAdminRoomRow> Rooms { get; } = [];

    public bool HasNoRooms => _loaded && Rooms.Count == 0;

    /// <summary>What the last action or read came to, in a few words.</summary>
    public string? Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => !string.IsNullOrEmpty(_status);

    public ICommand RefreshCommand { get; }

    /// <summary>Asks the relay whether this desktop is its owner, and if so, for its rooms.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsRelayOwner = await _client.IsRelayOwnerAsync(cancellationToken).ConfigureAwait(true);
        if (!IsRelayOwner)
        {
            return;
        }

        var rooms = await _client.ReadRoomsAsync(cancellationToken).ConfigureAwait(true);
        if (rooms is null)
        {
            Status = RelayAdminText.Unreachable;
            return;
        }

        Show(rooms);
    }

    /// <summary>The render tool's seam: the panel as a relay owner with these rooms sees it.</summary>
    internal void PresentForPreview(IReadOnlyList<RelayAdminRoomView> rooms, string? status = null, string? pendingInFirstRoom = null)
    {
        IsRelayOwner = true;
        Show(rooms);
        Status = status;
        if (pendingInFirstRoom is not null && Rooms.Count > 0)
        {
            Rooms[0].Ask(pendingInFirstRoom, () => Task.FromResult(new RelayAdminCallResult(RelayAdminCallOutcome.Done)));
        }
    }

    private void Show(IReadOnlyList<RelayAdminRoomView> rooms)
    {
        var now = _clock.GetUtcNow();
        Rooms.Clear();
        foreach (var room in rooms)
        {
            Rooms.Add(new RelayAdminRoomRow(this, room, now));
        }

        _loaded = true;
        OnPropertyChanged(nameof(HasNoRooms));
    }

    /// <summary>One question open at a time: asking in one room closes it in every other.</summary>
    internal void Asking(RelayAdminRoomRow asking)
    {
        foreach (var room in Rooms)
        {
            if (!ReferenceEquals(room, asking))
            {
                room.CancelPending();
            }
        }
    }

    /// <summary>Runs a confirmed action, says what it did, and reads the rooms again.</summary>
    internal async Task RunAsync(Func<Task<RelayAdminCallResult>> action)
    {
        var result = await action().ConfigureAwait(true);
        Status = result.Outcome switch
        {
            RelayAdminCallOutcome.Done => RelayAdminText.Done(result.Cleared),
            RelayAdminCallOutcome.NotOwner => RelayAdminText.NotOwner,
            RelayAdminCallOutcome.Unsupported => RelayAdminText.Unsupported,
            RelayAdminCallOutcome.Unreachable => RelayAdminText.Unreachable,
            _ => RelayAdminText.Refused(result.Status ?? 0),
        };
        var status = Status;
        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        Status = status;
    }

    internal RelayAdminClient Client => _client;
}

/// <summary>[#920] One room on the relay, with its own confirm step.</summary>
public sealed class RelayAdminRoomRow : BindableViewModel
{
    private readonly RelayAdminPanelViewModel _panel;
    private Func<Task<RelayAdminCallResult>>? _pending;
    private string? _pendingPrompt;

    internal RelayAdminRoomRow(RelayAdminPanelViewModel panel, RelayAdminRoomView room, DateTimeOffset now)
    {
        _panel = panel;
        Room = room.Room;
        Title = string.IsNullOrWhiteSpace(room.Label) ? RelayAdminText.RoomNamed(room.Room[..Math.Min(8, room.Room.Length)]) : room.Label!;
        var members = room.Members ?? [];
        var drawings = members.Sum(member => member.Drawings);
        var activity = room.LastActivityUtc is { } last
            ? RelayAdminText.LastActivity(TeamText.Ago(UnitText.Duration(now - last)))
            : RelayAdminText.NoActivity;
        Summary = string.Join(
            " · ",
            RelayAdminText.Members(members.Count),
            RelayAdminText.Marks(room.Waypoints + room.Pings),
            RelayAdminText.Drawings(drawings),
            activity);
        Members = [.. members.Select(member => new RelayAdminMemberRow(this, member, now))];
        Removed = room.Removed is { Count: > 0 } removed ? RelayAdminText.RemovedNames(string.Join(", ", removed)) : null;
        var client = panel.Client;
        ClearDrawingsCommand = new DelegateCommand(() =>
            Ask(RelayAdminText.ConfirmClearDrawings(Title), () => client.ClearDrawingsAsync(Room, null, CancellationToken.None)));
        ClearMarksCommand = new DelegateCommand(() =>
            Ask(RelayAdminText.ConfirmClearMarks(Title), () => client.ClearMarksAsync(Room, CancellationToken.None)));
        ResetCommand = new DelegateCommand(() =>
            Ask(RelayAdminText.ConfirmReset(Title), () => client.ResetRoomAsync(Room, CancellationToken.None)));
        ConfirmCommand = new AsyncDelegateCommand(ConfirmAsync);
        CancelCommand = new DelegateCommand(CancelPending);
    }

    public string Room { get; }

    public string Title { get; }

    public string Summary { get; }

    public IReadOnlyList<RelayAdminMemberRow> Members { get; }

    public bool HasNoMembers => Members.Count == 0;

    public string? Removed { get; }

    public bool HasRemoved => Removed is not null;

    public string? PendingPrompt
    {
        get => _pendingPrompt;
        private set
        {
            if (SetProperty(ref _pendingPrompt, value))
            {
                OnPropertyChanged(nameof(IsConfirming));
            }
        }
    }

    public bool IsConfirming => _pendingPrompt is not null;

    public ICommand ClearDrawingsCommand { get; }

    public ICommand ClearMarksCommand { get; }

    public ICommand ResetCommand { get; }

    public ICommand ConfirmCommand { get; }

    public ICommand CancelCommand { get; }

    internal RelayAdminClient Client => _panel.Client;

    /// <summary>Holds an action until it is confirmed; nothing is sent before that.</summary>
    internal void Ask(string prompt, Func<Task<RelayAdminCallResult>> action)
    {
        _panel.Asking(this);
        _pending = action;
        PendingPrompt = prompt;
    }

    internal void CancelPending()
    {
        _pending = null;
        PendingPrompt = null;
    }

    private async Task ConfirmAsync()
    {
        if (_pending is not { } action)
        {
            return;
        }

        CancelPending();
        await _panel.RunAsync(action).ConfigureAwait(true);
    }
}

/// <summary>[#920] One member of a room, as the owner can act on them.</summary>
public sealed class RelayAdminMemberRow
{
    internal RelayAdminMemberRow(RelayAdminRoomRow room, RelayAdminMemberView member, DateTimeOffset now)
    {
        Name = member.Name;
        Detail = string.Join(
            " · ",
            RelayAdminText.Drawings(member.Drawings),
            TeamText.Ago(UnitText.Duration(now - member.LastSeenUtc)));
        HasDrawings = member.Drawings > 0;
        var client = room.Client;
        ClearDrawingsCommand = new DelegateCommand(() =>
            room.Ask(RelayAdminText.ConfirmClearMemberDrawings(Name), () => client.ClearDrawingsAsync(room.Room, Name, CancellationToken.None)));
        RemoveCommand = new DelegateCommand(() =>
            room.Ask(RelayAdminText.ConfirmRemove(Name), () => client.RemoveMemberAsync(room.Room, Name, CancellationToken.None)));
    }

    public string Name { get; }

    public string Detail { get; }

    public bool HasDrawings { get; }

    public ICommand ClearDrawingsCommand { get; }

    public ICommand RemoveCommand { get; }
}
