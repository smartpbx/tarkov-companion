using System.Collections.ObjectModel;
using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Situations;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.App.ViewModels.V2.Now;

/// <summary>
/// [#712 0-4, 0-5] The Raid page's Now panel: NOW, YOU, SQUAD, NEXT and LAST SCAN from the situation.
/// </summary>
/// <remarks>
/// It reads <see cref="SituationService"/> and nothing underneath it (ADR 0022), with two
/// exceptions the situation does not carry yet: the map's exits (for the nearest offered one) and
/// the Loot page's own verdict (decision 7 of #712: the verdict stays here, one tap opens Loot).
/// The situation changes rarely and the clock every second, so the state is re-projected on a
/// one-second tick; squad rows are updated in place so a row that pulses keeps pulsing.
/// </remarks>
public sealed partial class NowPanelViewModel : BindableViewModel, IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _clock;
    private readonly Action<Action> _post;
    private readonly ITimer? _timer;
    private readonly SituationService? _source;
    private Situation _situation = Situation.Initial;
    private IReadOnlyList<NowExit> _exits = [];
    private NowLootVerdict? _verdict;
    private NowPanelState _state = new();
    private PreRaidBriefViewModel? _brief;
    private TimeSpan _leaveMargin = NowPanelState.LeaveMargin;
    private bool _disposed;

    public NowPanelViewModel(SituationService? situation, TimeProvider? clock = null, Action<Action>? post = null, bool tick = true)
    {
        _clock = clock ?? TimeProvider.System;
        _post = post ?? (action => action());
        MoreCommand = new DelegateCommand(() => MoreRequested?.Invoke(this, NowMoreTopic.All));
        WrongCommand = new DelegateCommand(() => MoreRequested?.Invoke(this, NowMoreTopic.Corrections));
        WrongSideCommand = new DelegateCommand(() => MoreRequested?.Invoke(this, NowMoreTopic.CorrectSide));
        WrongExitsCommand = new DelegateCommand(() => MoreRequested?.Invoke(this, NowMoreTopic.CorrectExits));
        OpenLootCommand = new DelegateCommand(() => OpenLootRequested?.Invoke(this, EventArgs.Empty));
        _source = situation;
        if (situation is not null)
        {
            _situation = situation.Current;
            situation.Changed += SituationChanged;
        }

        Refresh();
        if (tick)
        {
            _timer = _clock.CreateTimer(_ => _post(Refresh), null, Tick, Tick);
        }
    }

    /// <summary>More or "wrong?" was pressed: the host opens the Raid plan cards at this topic.</summary>
    public event EventHandler<NowMoreTopic>? MoreRequested;

    /// <summary>LAST SCAN was pressed: the host opens the full Loot page.</summary>
    public event EventHandler? OpenLootRequested;

    /// <summary>
    /// A squadmate's row changed (a new area or state, or a ping): the seam for the map-edge pulse
    /// on that member's side (#712 T3 "direction-aware attention"). The row itself pulses already.
    /// </summary>
    public event EventHandler<NowSquadRowViewModel>? SquadPulsed;

    /// <summary>
    /// [#712 0-9] The pre-raid brief: while it is shown (matching or loading, its own flag on) the
    /// panel is the brief, and the blocks take over at GameStarted.
    /// </summary>
    public PreRaidBriefViewModel? Brief
    {
        get => _brief;
        set
        {
            if (ReferenceEquals(_brief, value))
            {
                return;
            }

            if (_brief is not null)
            {
                _brief.PropertyChanged -= BriefChanged;
            }

            _brief = value;
            if (_brief is not null)
            {
                _brief.PropertyChanged += BriefChanged;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowsBrief));
            OnPropertyChanged(nameof(ShowsBlocks));
        }
    }

    public bool ShowsBrief => _brief?.IsShown == true;

    public bool ShowsBlocks => !ShowsBrief;

    /// <summary>Sends a ping at a squadmate's last shared spot; null where there is no squad session.</summary>
    public Func<string, CancellationToken, Task<bool>>? PingMember { get; set; }

    /// <summary>[#712 0-5] Places a squad waypoint at a squadmate's last shared spot (hold or right-click a row).</summary>
    public Func<string, CancellationToken, Task<bool>>? WaypointMember { get; set; }

    /// <summary>[#712 0-6] The spare minutes the late-raid line adds to the walk (the Leave margin setting).</summary>
    public TimeSpan LeaveMargin
    {
        get => _leaveMargin;
        set
        {
            if (_leaveMargin != value)
            {
                _leaveMargin = value;
                Refresh();
            }
        }
    }

    /// <summary>The squadmate's colour on the map, "#RRGGBB", so a row matches its dot.</summary>
    public Func<string, string?>? MemberColour { get; set; }

    public NowPanelState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                RaiseFold(); // [#712 0-7]
            }
        }
    }

    public ObservableCollection<NowSquadRowViewModel> SquadRows { get; } = [];

    public Situation Situation => _situation;

    public ICommand MoreCommand { get; }

    public ICommand WrongCommand { get; }

    /// <summary>[#712 0-6] YOU's side chip: Corrections, at the side row.</summary>
    public ICommand WrongSideCommand { get; }

    /// <summary>[#712 0-6] YOU's exits chip: Corrections, at the offered exits.</summary>
    public ICommand WrongExitsCommand { get; }

    public ICommand OpenLootCommand { get; }

    public string MoreLabel => NowText.More;

    /// <summary>Hands the panel a situation directly (tests, and the render preview's own states).</summary>
    public void Show(Situation situation)
    {
        _situation = situation ?? throw new ArgumentNullException(nameof(situation));
        Refresh();
    }

    /// <summary>The map's exits, nearest first as the map lists them; the panel picks the one to name.</summary>
    public void SetExits(IReadOnlyList<NowExit> exits)
    {
        _exits = exits ?? [];
        Refresh();
    }

    /// <summary>The Loot page's result, taken when it arrives: LAST SCAN shows its counts and top rows.</summary>
    public void ShowLoot(LootScanViewModel? result)
    {
        _verdict = result is null ? null : Verdict(result, _clock.GetUtcNow());
        Refresh();
    }

    public void ShowLoot(NowLootVerdict? verdict)
    {
        _verdict = verdict;
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var state = NowPanelState.Project(_situation, _clock.GetUtcNow(), _exits, _verdict, _leaveMargin);
        UpdateSquad(state.Squad);
        State = state;
    }

    /// <summary>
    /// [#712 0-5] A squadmate's ping arrived: their row flashes. The map edge toward it is the
    /// cockpit's half (SquadPingAttention); only the ping's own sender, never a guess, lights a row.
    /// </summary>
    public bool FlashMember(string name)
    {
        if (SquadRows.FirstOrDefault(row => string.Equals(row.Name, name, StringComparison.Ordinal)) is not { } row)
        {
            return false;
        }

        row.Pulse();
        return true;
    }

    internal static NowLootVerdict Verdict(LootScanViewModel result, DateTimeOffset receivedUtc)
    {
        ArgumentNullException.ThrowIfNull(result);
        var rows = result.Decisions
            .Where(decision => !decision.IsPending)
            .OrderBy(decision => decision.Verdict switch
            {
                LootScanVerdict.Take => 0,
                LootScanVerdict.Swap => 1,
                LootScanVerdict.Review => 2,
                _ => 3,
            })
            .Take(NowPanelState.MaximumVerdictRows)
            .Select(decision => new NowLootRow(decision.Verdict, decision.Name, decision.HeadlineReason, decision.ShortValuePerSquareLabel))
            .ToArray();
        return new(result.TakeCount, result.SwapCount, result.LeaveCount, result.ReviewCount, rows, receivedUtc);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        if (_source is not null)
        {
            _source.Changed -= SituationChanged;
        }

        foreach (var row in SquadRows)
        {
            row.Dispose();
        }
    }

    private void BriefChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(PreRaidBriefViewModel.IsShown))
        {
            OnPropertyChanged(nameof(ShowsBrief));
            OnPropertyChanged(nameof(ShowsBlocks));
        }
    }

    private void SituationChanged(object? sender, SituationChangedEventArgs e) => _post(() =>
    {
        _situation = e.Current;
        Refresh();
    });

    /// <summary>Rows keyed by name and updated in place; a changed area or state pulses the row.</summary>
    private void UpdateSquad(IReadOnlyList<NowSquadRow> rows)
    {
        for (var index = SquadRows.Count - 1; index >= 0; index--)
        {
            if (!rows.Any(row => string.Equals(row.Name, SquadRows[index].Name, StringComparison.Ordinal)))
            {
                SquadRows[index].Dispose();
                SquadRows.RemoveAt(index);
            }
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var existing = SquadRows.FirstOrDefault(item => string.Equals(item.Name, row.Name, StringComparison.Ordinal));
            if (existing is null)
            {
                existing = new NowSquadRowViewModel(row.Name, MemberColour?.Invoke(row.Name), _clock, _post, Ping, Waypoint);
                SquadRows.Insert(Math.Min(index, SquadRows.Count), existing);
                existing.Update(row, pulse: false);
                continue;
            }

            if (existing.Update(row, pulse: true))
            {
                SquadPulsed?.Invoke(this, existing);
            }

            var at = SquadRows.IndexOf(existing);
            if (at != index && index < SquadRows.Count)
            {
                SquadRows.Move(at, index);
            }
        }
    }

    private async Task<bool> Ping(NowSquadRowViewModel row)
    {
        if (PingMember is not { } ping)
        {
            return false;
        }

        var sent = await ping(row.Name, CancellationToken.None).ConfigureAwait(true);
        if (sent)
        {
            SquadPulsed?.Invoke(this, row);
        }

        return sent;
    }

    private async Task<bool> Waypoint(NowSquadRowViewModel row)
    {
        if (WaypointMember is not { } place)
        {
            return false;
        }

        return await place(row.Name, CancellationToken.None).ConfigureAwait(true);
    }
}

/// <summary>Where More opens the Raid plan cards: each topic is a group of the old sixteen cards.</summary>
public enum NowMoreTopic
{
    All,
    Extracts,
    Objectives,
    Marks,
    Spawns,
    Traffic,
    Corrections,
    Squad,
    Loot,

    /// <summary>[#712 0-6] Corrections, brought to the side row ("wrong?" on YOU's side).</summary>
    CorrectSide,

    /// <summary>[#712 0-6] Corrections, brought to the offered exits ("wrong?" on YOU's exit).</summary>
    CorrectExits,
}

/// <summary>One SQUAD row: what that squadmate's own companion shared, with a Ping and a pulse.</summary>
public sealed class NowSquadRowViewModel : BindableViewModel, IDisposable
{
    /// <summary>How long a changed row's edge stays lit (#712 T3: two seconds).</summary>
    public static readonly TimeSpan PulseLength = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _clock;
    private readonly Action<Action> _post;
    private readonly Func<NowSquadRowViewModel, Task<bool>> _ping;
    private readonly Func<NowSquadRowViewModel, Task<bool>>? _waypoint;
    private NowSquadRow? _row;
    private bool _isPulsing;
    private ITimer? _pulseEnd;

    internal NowSquadRowViewModel(
        string name,
        string? colour,
        TimeProvider clock,
        Action<Action> post,
        Func<NowSquadRowViewModel, Task<bool>> ping,
        Func<NowSquadRowViewModel, Task<bool>>? waypoint = null)
    {
        _waypoint = waypoint;
        Name = name;
        Colour = colour ?? "#E0B45C";
        _clock = clock;
        _post = post;
        _ping = ping;
        PingCommand = new AsyncDelegateCommand(PingAsync);
        WaypointCommand = new AsyncDelegateCommand(WaypointAsync);
    }

    public string Name { get; }

    public string Colour { get; }

    public string Where => _row?.Where ?? string.Empty;

    public string Age => _row?.Age ?? string.Empty;

    public bool CanPing => _row?.CanPing == true;

    public bool IsAway => _row?.IsAway == true;

    /// <summary>A squadmate out of this raid reads quieter than one in it.</summary>
    public double RowOpacity => IsAway ? 0.7 : 1;

    public string PingTip => NowText.PingTip(Name);

    public ICommand PingCommand { get; }

    /// <summary>[#712 0-5] Hold or right-click: a squad waypoint at this squadmate's last shared spot.</summary>
    public ICommand WaypointCommand { get; }

    /// <summary>Lit for <see cref="PulseLength"/> after the row changed or was pinged.</summary>
    public bool IsPulsing
    {
        get => _isPulsing;
        private set => SetProperty(ref _isPulsing, value);
    }

    /// <summary>Returns whether the row changed in a way worth a pulse.</summary>
    internal bool Update(NowSquadRow row, bool pulse)
    {
        var previous = _row;
        _row = row;
        OnPropertyChanged(nameof(Where));
        OnPropertyChanged(nameof(Age));
        OnPropertyChanged(nameof(CanPing));
        OnPropertyChanged(nameof(IsAway));
        OnPropertyChanged(nameof(RowOpacity));
        var changed = pulse && previous is not null && !string.Equals(previous.Signature, row.Signature, StringComparison.Ordinal);
        if (changed)
        {
            Pulse();
        }

        return changed;
    }

    internal void Pulse()
    {
        IsPulsing = true;
        _pulseEnd?.Dispose();
        _pulseEnd = _clock.CreateTimer(_ => _post(EndPulse), null, PulseLength, Timeout.InfiniteTimeSpan);
    }

    public void Dispose() => _pulseEnd?.Dispose();

    private void EndPulse() => IsPulsing = false;

    private async Task PingAsync()
    {
        if (!CanPing)
        {
            return;
        }

        if (await _ping(this).ConfigureAwait(true))
        {
            Pulse();
        }
    }

    private async Task WaypointAsync()
    {
        if (!CanPing || _waypoint is null)
        {
            return;
        }

        if (await _waypoint(this).ConfigureAwait(true))
        {
            Pulse();
        }
    }
}
