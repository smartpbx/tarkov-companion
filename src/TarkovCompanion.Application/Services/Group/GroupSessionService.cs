using System.Diagnostics;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Feedback;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Network;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>
/// Publishes this player to their group and reports back who else is there.
/// </summary>
/// <remarks>
/// This is the one part of the companion that sends anything anywhere, and it is off until
/// somebody turns it on. What it sends is assembled in one method below, so the promise made
/// in the interface can be checked against the code rather than taken on trust.
///
/// It publishes when what it publishes changes, and on a slow tick besides. The tick alone
/// meant a position that arrived just after one waited most of five seconds to go up and most
/// of another to be collected, against a payload of a few hundred bytes. A change goes now; the
/// tick stays for presence, staleness and everything that is true whether or not anything moved.
///
/// The rate is bounded at both ends. No more than one exchange every
/// <see cref="MinimumExchangeGap"/>, so a raid producing state changes several times a second
/// produces a handful of exchanges; and the relay may hold an exchange open until the room
/// changes, so an idle group still costs one request per tick and not one per change.
///
/// A failure never interrupts anything. The group view is an extra, and losing it must not
/// cost the player their map.
/// </remarks>
public sealed class GroupSessionService : IAsyncDisposable
{
    /// <summary>How often this player is published and the group re-read.</summary>
    /// <remarks>
    /// Five seconds. Fast enough that a squadmate's marker feels current, slow enough that a
    /// group of six is a trivial amount of traffic for a small self-hosted service.
    /// </remarks>
    private static readonly TimeSpan PublishInterval = GroupPublishing.Interval;

    /// <summary>
    /// The shortest gap between two exchanges, whatever is happening locally.
    /// </summary>
    /// <remarks>
    /// Three hundred milliseconds, so a change goes out at once and anything that follows it
    /// inside the window is folded into a single exchange after it rather than one each. The
    /// first change is deliberately not delayed: waiting out a window before sending would buy
    /// tidier traffic with the exact latency this is here to remove.
    /// </remarks>
    private static readonly TimeSpan MinimumExchangeGap = TimeSpan.FromMilliseconds(300);

    /// <summary>How long the relay is asked to hold an exchange open waiting for the room to move.</summary>
    /// <remarks>
    /// The publish interval, not the relay's twenty-second ceiling. Holding longer would mean
    /// fewer requests and a member list, a position age and a staleness check that were all up
    /// to twenty seconds old — the tick is doing a job as well as costing one.
    /// </remarks>
    private static readonly TimeSpan HoldFor = PublishInterval;

    /// <summary>How long one exchange with the relay may take before it is abandoned.</summary>
    /// <remarks>
    /// The shared HttpClient is registered with Timeout.InfiniteTimeSpan, so a relay that
    /// accepts a connection and never answers parked this loop forever: the panel went on
    /// saying "Sharing as X · 3 others" over a snapshot that quietly aged, and nothing ever
    /// timed out to say otherwise.
    ///
    /// Eight seconds, against a five-second tick. Long enough that a slow-but-working relay is
    /// not cut off, short enough that a dead one is noticed within two ticks.
    /// </remarks>
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How long a stale snapshot keeps showing the group before it gives them up.
    /// </summary>
    /// <remarks>
    /// Three minutes, matching GroupRooms.MemberLifetime on the server. Past that the relay
    /// would have dropped these members anyway, so continuing to draw them would be inventing
    /// a group rather than remembering one.
    /// </remarks>
    private static readonly TimeSpan StaleLimit = TimeSpan.FromMinutes(3);

    /// <summary>How long a goodbye may take while the session carries on (a rename, sharing off).</summary>
    private static readonly TimeSpan WithdrawBudget = TimeSpan.FromSeconds(2);

    /// <summary>How long a goodbye may take while the application closes (#786).</summary>
    private static readonly TimeSpan ClosingWithdrawBudget = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IGroupSettingsStore _settings;
    private readonly IRuntimeStateStore _stateStore;
    private readonly HttpClient _httpClient;
    private readonly ILogger<GroupSessionService> _logger;
    private int _published;
    private GroupSnapshot? _lastGood;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;

    /// <summary>What was last published, so a change to it can end a hold rather than wait one out.</summary>
    /// <remarks>A reference, so the notification thread reads one whole value or none of it.</remarks>
    private PublishedShape? _publishedShape;

    /// <summary>Bumped whenever the local state changes something this service publishes.</summary>
    private long _localChanges;

    /// <summary>The exchange in flight, so a local change can cut its hold short.</summary>
    private CancellationTokenSource? _holding;

    /// <summary>The room revision the relay last answered with, or null from one that holds nothing.</summary>
    private long? _roomRevision;

    /// <summary>
    /// Whether the relay was observed to actually hold the last exchange it was asked to.
    /// </summary>
    /// <remarks>
    /// Not the same question as whether it answered with a revision. A relay behind something
    /// that buffers, or one already holding as many exchanges as it will, answers at once and
    /// unchanged — and a client that took the revision as permission to ask again immediately
    /// would be a busy loop against somebody else's server, caused by an upgrade at this end.
    /// So it is measured: an answer that carried nothing new and came back well inside the hold
    /// was not a hold, and this client goes back to its own tick.
    /// </remarks>
    private bool _relayHolds;

    /// <summary>
    /// Whether the wait after this exchange must be waited out rather than cut short.
    /// </summary>
    /// <remarks>
    /// Publishing on change and backing off on failure pull in opposite directions, and the
    /// failure wins. A relay that refuses instantly, or sharing that is switched off, would
    /// otherwise be revisited as often as the local state moved — which during a raid is
    /// several times a second, and is the busy loop the backoff exists to prevent.
    /// </remarks>
    private bool _waitOutTheTick;

    /// <summary>The wait-out-the-tick delay in progress, so a settings save can end it.</summary>
    private CancellationTokenSource? _backingOff;

    /// <summary>Whether the relay's last answer was that the owner removed this member (409).</summary>
    private bool _removedByOwner;

    /// <summary>1 when the group settings were saved since the relay last refused this member.</summary>
    /// <remarks>
    /// [#936] The removed member is told to turn sharing off and on. Done inside one tick, the
    /// loop never read the switch as off, so no DELETE went, the removal never lifted, and the
    /// next POST was refused again for the rest of the relay's thirty minutes. A save after the
    /// refusal now sends the goodbye first, and ends the back-off so it goes at once.
    /// </remarks>
    private int _settingsSaved;

    /// <summary>1 when the next exchange should come straight back with the room (a mark was just sent).</summary>
    private int _roomWanted;

    /// <summary>When the last exchange started, for the rate bound, on the monotonic clock.</summary>
    /// <remarks>
    /// [#799] This was a wall time. The PC's clock was set back four hours while the companion
    /// ran, so "300 ms since the last exchange" came out as four hours to wait, and the loop sat
    /// in that one Task.Delay: the squad vanished from this map and this player from theirs.
    /// </remarks>
    private long? _lastExchangeStarted;

    /// <summary>When the group was first shown stale, on the monotonic clock, for <see cref="StaleLimit"/>.</summary>
    private long? _staleSinceStarted;

    private readonly TimeProvider _clock;

    /// <summary>How long squadmate positions are taking to arrive, measured on the way in.</summary>
    private readonly GroupPositionLatency _latency = new();

    /// <summary>
    /// The name this service last published, and where, or null when nothing is registered.
    /// </summary>
    /// <remarks>
    /// Kept because withdrawing needs the *previous* identity, not the current settings. When a
    /// player renames themselves, the settings already say the new name by the time anything
    /// notices, and a DELETE built from them would remove the marker that was just created and
    /// leave the old one standing for the full three minutes — the exact stale marker this is
    /// meant to prevent, with an extra step.
    /// </remarks>
    private (string Server, string Key, string Name)? _registered;
    private bool _disposed;

    public GroupSessionService(
        IGroupSettingsStore settings,
        IRuntimeStateStore stateStore,
        HttpClient httpClient,
        ILogger<GroupSessionService> logger,
        // Optional so a composition without quest storage still shares a position, which is
        // what every test that builds this by hand relies on.
        GroupQuestShare? quests = null,
        GroupKitShare? kits = null,
        TimeProvider? clock = null,
        // [#289] The extract, note and ready state set on the Team workspace. Optional like the rest.
        GroupSquadStatus? status = null,
        // [#292] Local only and the squad/report switches; null allows everything, as before.
        INetworkPolicy? network = null,
        // #712 0-12: when an exchange carried a new screenshot position, for the Position timeline.
        CaptureSessions.ICaptureStageTimeline? stageTimeline = null,
        // [#712 T7] This player's own Loadout check and level for the squad's ready check.
        GroupReadyCheckShare? readyCheck = null)
    {
        _readyCheck = readyCheck;
        _stageTimeline = stageTimeline;
        _network = network;
        _clock = clock ?? TimeProvider.System;
        _settings = settings;
        _stateStore = stateStore;
        _httpClient = httpClient;
        _logger = logger;
        _quests = quests;
        _kits = kits;
        _status = status;
    }

    private readonly GroupSquadStatus? _status;
    private readonly GroupReadyCheckShare? _readyCheck;
    private readonly INetworkPolicy? _network;
    private readonly CaptureSessions.ICaptureStageTimeline? _stageTimeline;

    /// <summary>The position the relay last accepted, so only a new one is reported as sent.</summary>
    private (DateTimeOffset? Taken, double X, double Y, double Z)? _positionSent;

    /// <summary>[#292] Local only or squad sharing switched off: nothing goes to the relay, not even a goodbye.</summary>
    private NetworkVerdict SharingVerdict => _network?.Check(NetworkService.SquadSharing) ?? NetworkVerdict.Allowed;

    /// <summary>[#286] The lines this player shares with the squad, set by the Raid map.</summary>
    public GroupDrawingShare Drawings { get; } = new();

    private readonly GroupQuestShare? _quests;
    private readonly GroupKitShare? _kits;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_worker is not null)
        {
            return;
        }

        // Subscribed before the loop starts, so the first position of a raid is a change this
        // notices rather than one it discovers on the next tick.
        _stateStore.Changed += RuntimeStateChanged;
        if (_quests is not null)
        {
            _quests.Changed += QuestsChanged;
        }

        if (_status is not null)
        {
            // [#289] A ready toggle is sent now, the same way a quest change is.
            _status.Changed += QuestsChanged;
        }

        if (_readyCheck is not null)
        {
            // [#712 T7] A new check (another map planned) is sent now, like a ready toggle.
            _readyCheck.Changed += QuestsChanged;
        }

        // [#286] A line drawn or removed is sent now, like a ready toggle.
        Drawings.Changed += QuestsChanged;
        // [#936] A switch flipped goes now too, even through a back-off.
        _settings.Changed += SettingsChanged;
        if (_network is not null)
        {
            // [#292] Local only switched off: say hello now rather than at the end of a tick.
            _network.Changed += NetworkChanged;
        }

        _worker = Task.Run(() => RunAsync(_stopping.Token));
    }

    /// <summary>
    /// [#780] A quest or objective changed: the group hears now, not on the next tick.
    /// </summary>
    /// <remarks>
    /// Change-driven like a position: the hold is cut short and the next exchange carries the new
    /// list. Nothing is sent when nothing changed, and the relay wakes the others only when what
    /// this member says differs from what it said last.
    /// </remarks>
    private void NetworkChanged(object? sender, EventArgs eventArgs) => QuestsChanged();

    private void SettingsChanged(object? sender, EventArgs eventArgs)
    {
        Volatile.Write(ref _settingsSaved, 1);
        try
        {
            Volatile.Read(ref _backingOff)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The back-off ended on its own between the read and the cancel.
        }

        QuestsChanged();
    }

    private void QuestsChanged()
    {
        Interlocked.Increment(ref _localChanges);
        Interrupt();
    }

    /// <summary>
    /// Ends the current hold when the local state changes something this service publishes.
    /// </summary>
    /// <remarks>
    /// Most runtime changes are nothing to do with the group — a data sync, a scan, a quest
    /// list — and waking for those would be a busy loop with the relay on the other end of it.
    /// Only the shape that goes on the wire counts.
    /// </remarks>
    private void RuntimeStateChanged(object? sender, EventArgs eventArgs)
    {
        var shape = PublishedShape.Of(_stateStore.Current);
        if (shape == Volatile.Read(ref _publishedShape))
        {
            return;
        }

        Interlocked.Increment(ref _localChanges);
        Interrupt();
    }

    /// <summary>
    /// [#799] The PC's clock was set: publish now, with the position ages the raid state was
    /// just re-stamped to, rather than at the end of whatever hold is running.
    /// </summary>
    public void ClockJumped()
    {
        Interlocked.Increment(ref _localChanges);
        Interrupt();
    }

    /// <summary>
    /// This companion just added or removed a mark: the next exchange brings the room back now.
    /// </summary>
    /// <remarks>
    /// Counted as a local change, like a position, rather than a bare interrupt. The loop reads an
    /// exchange cut short with no local change as a relay that did not answer, so every ping
    /// placed or removed during a hold logged "Group publish failed: Server did not answer in
    /// time", marked the squad stale and waited out a whole tick before looking again. Found
    /// following one ping end to end (PingLifetimeEndToEndTests); the same failure line sits
    /// beside the pings in the #799 log.
    ///
    /// The exchange after it is not held either: the room has a mark this companion has not been
    /// shown yet, and a relay that does not end holds on marks would keep it back a whole hold.
    /// </remarks>
    private void MarksChangedHere()
    {
        Volatile.Write(ref _roomWanted, 1);
        Interlocked.Increment(ref _localChanges);
        Interrupt();
    }

    /// <summary>Cuts short whatever exchange is being held open, if one is.</summary>
    private void Interrupt()
    {
        try
        {
            Volatile.Read(ref _holding)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The exchange finished on its own between the read and the cancel. Nothing to end.
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // Whether the last exchange was a hold cut short by a local change, and so never answered.
        var cutShort = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            // The rate bound, and the only wait this loop takes that nothing can cut short.
            var gap = _lastExchangeStarted is { } last
                ? MinimumExchangeGap - _clock.GetElapsedTime(last)
                : TimeSpan.Zero;
            if (gap > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(gap, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            var generation = Interlocked.Read(ref _localChanges);
            // One source for the exchange and the tick that may follow it, so a position that
            // arrives during either goes up now. A backoff that could not be cut short would
            // put the five-second wait back for exactly the case this exists for.
            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Volatile.Write(ref _holding, cycle);
            var onATick = true;
            var held = false;
            try
            {
                // Read after the interrupt is armed, so a change landing in between is still
                // caught: either it cancels this source, or it is seen here.
                //
                // The exchange after a hold that was cut short is not held, and a local change
                // does not cut it short: it waits on nothing and comes back in a round trip.
                // Every change used to end whatever exchange was in flight, so while positions
                // kept arriving faster than the relay answered, no answer was ever read. A relay
                // refusing every request was then never seen to fail, the back-off never ran, and
                // it was asked again at the rate bound for as long as the player moved. The
                // change still ends the tick after it, through the cycle, so the next exchange
                // goes as soon as this one is back.
                var hold = Interlocked.Read(ref _localChanges) == generation
                    && Interlocked.Exchange(ref _roomWanted, 0) == 0
                    && !cutShort
                    ? HoldFor
                    : TimeSpan.Zero;
                held = hold > TimeSpan.Zero;
                cutShort = false;
                using (var exchange = CancellationTokenSource.CreateLinkedTokenSource(
                           held ? cycle.Token : cancellationToken))
                {
                    exchange.CancelAfter(ExchangeTimeout + hold);
                    _lastExchangeStarted = _clock.GetTimestamp();
                    await PublishOnceAsync(hold, exchange.Token).ConfigureAwait(false);
                }

                // A relay that holds its answers is already doing the waiting, so this loop
                // does none of its own.
                onATick = !_relayHolds;
            }
            // The filter tests the loop's own token rather than the exception's type. A
            // per-request timeout throws TaskCanceledException, which *is* an
            // OperationCanceledException, so the old filter would have let every timeout
            // escape, fault the worker and end sharing silently for the session — the trap
            // that made adding a timeout worse than not having one.
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (held && exception is OperationCanceledException && Interlocked.Read(ref _localChanges) != generation)
                {
                    // A hold this loop cut short on purpose is not a failure and must not be
                    // reported as one: the group is about to be told something newer.
                    Volatile.Write(ref _holding, null);
                    cutShort = true;
                    continue;
                }

                // Deliberately swallowed after reporting. The group is an extra; a server that
                // is down must not take the map with it.
                //
                // Reported to the log as well as to the interface, which it was not. Sharing
                // wrote no line of any kind, so when a member's state was being refused there
                // was nothing to read: the whole diagnosis had to come from reading a config
                // file on the machine and probing the server from outside. Every other part of
                // this application says what it did; this one was silent.
                _logger.LogWarning(exception, "Group publish failed: {Detail}", Explain(exception));
                PublishStale(StatusOf(exception));
                // A relay that is unwell goes back on the slow tick, which is the existing
                // backoff and the thing that stops a failure becoming a busy loop.
                _roomRevision = null;
                _relayHolds = false;
                _waitOutTheTick = true;
            }

            try
            {
                if (onATick && _waitOutTheTick)
                {
                    // A failed exchange waits out the whole interval, and a local change does
                    // not shorten it. Otherwise a player taking screenshots against a relay
                    // that refuses instantly would retry as fast as the rate bound allowed,
                    // which is the busy loop the backoff exists to prevent. A settings save
                    // does (#936): it is the player acting, once, not the raid moving.
                    using var backingOff = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    Volatile.Write(ref _backingOff, backingOff);
                    if (_removedByOwner && Volatile.Read(ref _settingsSaved) == 1)
                    {
                        // Saved before this wait began, so nothing was there to end it.
                        await backingOff.CancelAsync().ConfigureAwait(false);
                    }

                    try
                    {
                        await Task.Delay(PublishInterval, backingOff.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        Volatile.Write(ref _backingOff, null);
                    }
                }
                else if (onATick)
                {
                    await Task.Delay(PublishInterval, cycle.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Either the application is closing, which the loop condition catches, or a
                // local change wants the next exchange now.
            }
            finally
            {
                Volatile.Write(ref _holding, null);
            }
        }
    }

    /// <summary>
    /// Everything about the local state that reaches the relay, as one comparable value.
    /// </summary>
    /// <remarks>
    /// The runtime snapshot changes several times a second for reasons that have nothing to do
    /// with the group, and an exchange per change would be a request per frame. This is what
    /// <see cref="Describe"/> actually sends, so a difference here is a difference the group
    /// would see and anything else is not worth a round trip.
    /// </remarks>
    private sealed record PublishedShape(
        string? MapId,
        RaidLifecycleState State,
        string? Side,
        DateTimeOffset? PositionTakenUtc,
        double X,
        double Y,
        double Z,
        double Heading,
        int TrailPoints,
        int Extracts,
        int Transits)
    {
        public static PublishedShape Of(ApplicationRuntimeSnapshot snapshot)
        {
            var raid = snapshot.Raid;
            var position = raid.LastKnownPosition;
            return new(
                raid.MapId,
                raid.State,
                raid.Side,
                position?.Timestamp.ToUniversalTime(),
                position?.Position.X ?? 0,
                position?.Position.Y ?? 0,
                position?.Position.Z ?? 0,
                position?.HeadingDegrees ?? 0,
                raid.PositionTrail.Count,
                raid.ActiveExtracts.Count,
                raid.Transits.Count);
        }
    }

    /// <summary>
    /// Says what went wrong in terms of what to do about it.
    /// </summary>
    /// <remarks>
    /// "The group server could not be reached" was the message for every failure including the
    /// one that actually happened, which was that the server understood perfectly and refused
    /// the credentials. Somebody reading that goes looking at their network, and the server is
    /// fine and the network is fine and the thing that is wrong is a value they typed.
    /// </remarks>
    private static string Explain(Exception exception) => exception switch
    {
        // A 401 from this server means the key failed GroupKey.IsAcceptable, which is a
        // length test: under eight characters or over 128. It cannot mean "wrong key" — the
        // key *is* the room, so on an open relay a different key is a different room, which
        // answers 200 with nobody in it, and a closed relay answers 403 below. Saying "wrong
        // group key" sent people to compare keys that were fine.
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
            $"The group key must be between {GroupKeyLimits.Minimum} and {GroupKeyLimits.Maximum} characters",
        // A 403 is the relay's operator saying this room is not one they registered, which is a
        // different thing from the key being malformed and a different thing again from the
        // relay being unwell. Somebody told this person a key; it is not the one in use.
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
            "This relay only serves rooms its operator registered",
        HttpRequestException { StatusCode: HttpStatusCode.BadRequest } =>
            "Server rejected the key or the display name",
        HttpRequestException { StatusCode: { } status } =>
            $"Server answered {(int)status}",
        HttpRequestException =>
            $"Server unreachable · {exception.Message}",
        TaskCanceledException =>
            "Server did not answer in time",
        _ => $"Sharing failed · {exception.Message}",
    };

    /// <summary>[#314] <see cref="Explain"/> for the status line, as a code the App words.</summary>
    /// <remarks><see cref="Explain"/> stays English: it goes to the log and into the problem report.</remarks>
    private static Phrase StatusOf(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => KeyLength,
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => new(GroupStatus.OperatorRegisteredOnly),
        HttpRequestException { StatusCode: HttpStatusCode.BadRequest } => new(GroupStatus.ServerRejected),
        HttpRequestException { StatusCode: { } status } => new(GroupStatus.ServerAnswered, (int)status),
        HttpRequestException => new(GroupStatus.ServerUnreachable, exception.Message),
        TaskCanceledException => new(GroupStatus.NoAnswerInTime),
        _ => new(GroupStatus.SharingFailed, exception.Message),
    };

    private static Phrase KeyLength => new(GroupStatus.KeyLength, GroupKeyLimits.Minimum, GroupKeyLimits.Maximum);

    /// <summary>
    /// Marks a place for the group: a waypoint that stays, or a ping that fades.
    /// </summary>
    /// <remarks>
    /// Sent immediately rather than folded into the next tick. A ping is somebody saying "look
    /// here" and five seconds of silence is long enough for that to stop being useful, which
    /// is the same reason the server expires them quickly.
    ///
    /// Nothing is drawn locally in response. The mark comes back on the very next exchange
    /// like everybody else's, so one code path draws every mark and a sender never sees a
    /// version of the group's state that the group does not have.
    /// </remarks>
    public async Task<bool> MarkAsync(
        string mapId,
        WorldPosition position,
        string? label,
        bool isPing,
        CancellationToken cancellationToken) =>
        await SendMarkAsync(mapId, position, label, isPing, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// The same as <see cref="MarkAsync"/>, answering with the id the relay gave the mark.
    /// </summary>
    /// <remarks>
    /// [#707] The V2 map's own marks are forwarded to the group, and a forwarded mark has to be
    /// taken off the relay when it is taken off the map, which needs its id. Zero when the relay
    /// accepted the mark but its answer carried none; null when it was not sent.
    /// </remarks>
    public async Task<long?> SendMarkAsync(
        string mapId,
        WorldPosition position,
        string? label,
        bool isPing,
        CancellationToken cancellationToken,
        string? colour = null)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.IsUsable || string.IsNullOrWhiteSpace(mapId) || SharingVerdict != NetworkVerdict.Allowed)
        {
            return null;
        }

        try
        {
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(new Uri(settings.ServerUri!), isPing ? "pings" : "waypoints"))
            {
                Content = JsonContent.Create(new MarkDto(
                    settings.DisplayName!.Trim(), mapId, position.X, position.Y, position.Z, label)
                {
                    Color = TarkovCompanion.Core.Common.MarkPalette.Normalize(colour),
                }),
            };
            request.Headers.Add("X-Group-Key", settings.Key!.Trim());
            var started = _clock.GetTimestamp();
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var id = await ReadMarkIdAsync(response, cancellationToken).ConfigureAwait(false);
            // With its id and how long the relay took, so this line and the removal that follows
            // it can be paired, and a send that sat waiting is visible as one (#799).
            _logger.LogInformation(
                "Marked {Kind} {Id} on {Map} for the group in {Elapsed:0} ms.",
                isPing ? "a ping" : "a waypoint",
                id,
                mapId,
                _clock.GetElapsedTime(started).TotalMilliseconds);
            // The mark is drawn from the next exchange like everybody else's, so that exchange
            // happens now rather than at the end of whatever hold was already running.
            MarksChangedHere();
            return id;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Could not mark a place for the group.");
            return null;
        }
    }

    /// <summary>
    /// Takes one of the group's marks off the map.
    /// </summary>
    /// <remarks>
    /// The server has served DELETE /waypoints/{id} since the marks were written and no client
    /// has ever called it, so every waypoint a squad has ever dropped is still on its map. A
    /// plan that can only be added to stops being a plan somewhere around the fifth one.
    ///
    /// Anybody may remove anybody's, because the marks belong to the group rather than to
    /// whoever dropped them — the server's own rule, and it has no identities to enforce a
    /// different one with.
    ///
    /// Nothing is removed locally. The next exchange brings back a room without it, which is
    /// the same single code path that draws every mark, so a remover never sees a map the
    /// group does not have.
    /// </remarks>
    /// <param name="why">What removed it, for the log line: a ping leaving early is otherwise a
    /// "Removed waypoint" line with no cause beside it (#799).</param>
    public async Task<bool> RemoveMarkAsync(long id, CancellationToken cancellationToken, string? why = null)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.IsUsable || id <= 0 || SharingVerdict != NetworkVerdict.Allowed)
        {
            // Zero is the id a mark carries while it is still on its way to the server. There
            // is nothing there to remove yet, and asking would delete whatever the server
            // happened to number zero if it ever numbered anything zero.
            return false;
        }

        var because = string.IsNullOrWhiteSpace(why) ? string.Empty : $" ({why})";
        // #886: a removal with a cause is the map forwarder taking back a mark this client sent
        // (a ping that expired here, a mark removed or moved locally), so it is scoped to marks
        // under our own name. After a relay restart an old id could name a squadmate's new mark,
        // and an unscoped delete removed it. A relay older than this ignores the parameter.
        var scope = string.IsNullOrWhiteSpace(why) || string.IsNullOrWhiteSpace(settings.DisplayName)
            ? string.Empty
            : $"?by={Uri.EscapeDataString(settings.DisplayName.Trim())}";
        return await SendAsync(
            settings,
            new Uri(new Uri(settings.ServerUri!), $"waypoints/{id}{scope}"),
            $"Removed mark {id} for the group{because}.",
            $"Could not remove mark {id} for the group{because}.",
            cancellationToken,
            goneIsDone: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the group's marks, either the reached ones or all of them.
    /// </summary>
    /// <remarks>
    /// Two buttons rather than one, because they answer different questions. "Clear reached"
    /// is tidying after a run and throws nothing away that anybody still wants; "Clear all" is
    /// starting again. Scoped to the open map, so clearing after a Customs raid does not take
    /// the plan somebody made for Lighthouse with it.
    /// </remarks>
    public async Task<bool> ClearMarksAsync(string? mapId, bool reachedOnly, CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.IsUsable || SharingVerdict != NetworkVerdict.Allowed)
        {
            return false;
        }

        var query = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(mapId))
        {
            query.Add($"mapId={Uri.EscapeDataString(mapId)}");
        }

        if (reachedOnly)
        {
            query.Add("reachedOnly=true");
        }

        var relative = query.Count == 0 ? "waypoints" : $"waypoints?{string.Join('&', query)}";
        return await SendAsync(
            settings,
            new Uri(new Uri(settings.ServerUri!), relative),
            reachedOnly ? "Cleared the group's reached marks." : "Cleared the group's marks.",
            "Could not clear the group's marks.",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ReadMarkIdAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var added = await response.Content
                .ReadFromJsonAsync<MarkIdDto>(Json, cancellationToken)
                .ConfigureAwait(false);
            return added?.Id ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private sealed record MarkIdDto([property: JsonPropertyName("id")] long Id);

    /// <summary>The start of a refusal's body, for the log: a relay's reason is usually one short line.</summary>
    private static async Task<string> ReadRefusalAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            return body.Length == 0 ? string.Empty : $": {(body.Length > 200 ? body[..200] : body)}";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    /// <summary>One DELETE, said once, because the two above differ only in where they point.</summary>
    private async Task<bool> SendAsync(
        GroupSharingSettings settings,
        Uri uri,
        string done,
        string failed,
        CancellationToken cancellationToken,
        bool goneIsDone = false)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
            request.Headers.Add("X-Group-Key", settings.Key!.Trim());
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (goneIsDone && response.StatusCode == HttpStatusCode.NotFound)
            {
                // The relay forgets a ping by itself after 45 s, and a squadmate may have removed
                // the mark first. Either way it is gone, which is what was asked for; a warning
                // here only ever reported the relay agreeing.
                _logger.LogInformation("{Done} It was already gone from the relay.", done);
                MarksChangedHere();
                return true;
            }

            if (!response.IsSuccessStatusCode)
            {
                // The status and whatever the relay said, which "Response status code does not
                // indicate success" never carried: 401, 403 and 404 each mean something different.
                var said = await ReadRefusalAsync(response, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "{Failed} The relay answered {Status} {Reason}{Said}",
                    failed,
                    (int)response.StatusCode,
                    response.ReasonPhrase,
                    said);
                return false;
            }

            _logger.LogInformation("{Done}", done);
            MarksChangedHere();
            return true;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "{Failed}", failed);
            return false;
        }
    }

    private async Task PublishOnceAsync(TimeSpan hold, CancellationToken cancellationToken)
    {
        _waitOutTheTick = false;
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (settings.IsEnabled && SharingVerdict is var verdict && verdict != NetworkVerdict.Allowed)
        {
            // [#292] Asked before every exchange, so the switch takes effect within one tick and
            // the panel says why rather than "Server unreachable". No goodbye is sent: that is
            // traffic too, and the relay forgets a silent member by itself in three minutes.
            _roomRevision = null;
            _relayHolds = false;
            _waitOutTheTick = true;
            _lastGood = null;
            Publish(GroupSnapshot.Off.Saying(new(verdict == NetworkVerdict.LocalOnly
                    ? GroupStatus.LocalOnly
                    : GroupStatus.SwitchedOff)) with
            {
                UpdatedUtc = _clock.GetUtcNow(),
            });
            return;
        }

        if (!settings.IsEnabled)
        {
            // Nothing is being exchanged, so there is no revision and nothing to hold against:
            // the loop goes back to its tick rather than spinning on a relay it is not calling.
            _roomRevision = null;
            _relayHolds = false;
            _waitOutTheTick = true;
            // Turning sharing off is a thing to say, not a thing to stop saying. Left to time
            // out, the player vanishes from everybody's map three minutes after they thought
            // they had gone.
            await WithdrawRegisteredAsync().ConfigureAwait(false);
            // Deliberate, not a failure, so there is nothing to keep warm.
            _lastGood = null;
            Publish(settings.ResetReason is { } reason
                ? GroupSnapshot.Off.Saying(reason) with { UpdatedUtc = _clock.GetUtcNow() }
                : GroupSnapshot.Off);
            return;
        }

        if (!settings.IsUsable)
        {
            // Half-edited settings are the same situation as switched off: nothing more will be
            // published under the old identity, so it should not be left standing.
            _roomRevision = null;
            _relayHolds = false;
            _waitOutTheTick = true;
            await WithdrawRegisteredAsync().ConfigureAwait(false);
            Publish(GroupSnapshot.Off.Saying(new(GroupStatus.Needs, settings.Gap)) with
            {
                UpdatedUtc = _clock.GetUtcNow(),
            });
            return;
        }

        // A renamed member is a new member to the relay, which keys a room by display name. The
        // old name keeps its marker until the room forgets it, so the group sees the player
        // twice — once where they are and once where they were.
        var identity = (settings.ServerUri!.Trim(), settings.Key!.Trim(), settings.DisplayName!.Trim());
        if (_registered is { } previous && previous != identity)
        {
            await WithdrawAsync(previous).ConfigureAwait(false);
            // A different room, or a different name in it. Whatever revision the last one was
            // at says nothing about this one, and the deliveries timed against it were not
            // this group's.
            _roomRevision = null;
            _relayHolds = false;
            _latency.Reset();
        }
        else if (_removedByOwner && _registered is { } removed && Interlocked.Exchange(ref _settingsSaved, 0) == 1)
        {
            // [#936] Removed, and the player has touched the switch since: the DELETE is what
            // lifts a removal on the relay, and the loop never saw the switch off to send it.
            await WithdrawAsync(removed).ConfigureAwait(false);
            _removedByOwner = false;
        }

        _registered = identity;

        var snapshot = _stateStore.Current;
        // Taken before the payload is built and from the same snapshot, so a change arriving
        // while this exchange is in flight is seen as a change rather than as this one.
        Volatile.Write(ref _publishedShape, PublishedShape.Of(snapshot));
        // Read before the payload is assembled, and cached for a minute inside, because the
        // publish loop runs every few seconds and a quest board does not.
        var sharedQuests = settings.SharesQuests && _quests is not null
            ? await _quests.GetAsync(cancellationToken).ConfigureAwait(false)
            : SharedQuests.None;
        // What this game has said about the others, which is the one thing each of them cannot
        // read about themselves. Sent whenever sharing is on, because it is the only route any
        // of them has to their own kit. It is the whole in-game party, strangers from
        // matchmaking included; the relay drops anybody not in the room only after it arrives.
        // The loadout switch governs what is said about the sender, not about others.
        var observed = _kits is null
            ? []
            : await _kits.GetAsync(cancellationToken).ConfigureAwait(false);
        // [#269] Sent whatever the quest switch says: a squadmate on another mode is told so even
        // when nobody shares quests, and it is what keeps their quests apart when somebody does.
        var ownMode = _quests is null ? null : await _quests.GameModeAsync(cancellationToken).ConfigureAwait(false);
        var status = _status?.Current ?? SquadStatus.None;
        // [#712 T7] On by default; the "My ready check" switch sends neither the check nor the level.
        var readiness = settings.SharesReadyCheck && _readyCheck is not null
            ? await _readyCheck.GetAsync(cancellationToken).ConfigureAwait(false)
            : SharedReadiness.None;
        var payload = Describe(snapshot, settings, sharedQuests, observed, _clock.GetUtcNow()) with
        {
            GameMode = ownMode,
            // [#289] Only what the player set on the Team workspace; absent (not false) when unset.
            Ready = status.Ready,
            PlannedExtract = status.ExtractFor(snapshot.Raid.MapId),
            Note = status.Note,
            // [#286] Absent, not empty, when there is nothing drawn: the publish is byte for byte
            // what it was before lines existed.
            Drawings = GroupDrawingWire.Describe(Drawings.Current),
            // [#712 T7] Absent, not empty, when not shared: the publish is what it was before.
            LoadoutCheck = LoadoutCheckWire.Describe(readiness.Check, _clock.GetUtcNow()),
            Level = readiness.Level,
        };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(new Uri(settings.ServerUri!), Exchange(hold)))
        {
            Content = JsonContent.Create(payload, options: Json),
        };
        request.Headers.Add("X-Group-Key", settings.Key!.Trim());

        var asked = _roomRevision;
        var sent = Stopwatch.GetTimestamp();
        var sentOnClock = _clock.GetTimestamp();
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // A 401 and a 403 are both answers: the key is unusable here and no amount of
            // waiting fixes it, so the group really is off. Everything else is the relay having
            // a bad moment, and the squad that was on the map a second ago should stay on it.
            _roomRevision = null;
            _relayHolds = false;
            _waitOutTheTick = true;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _lastGood = null;
                Publish(GroupSnapshot.Off.Saying(response.StatusCode == HttpStatusCode.Forbidden
                        ? new(GroupStatus.OperatorRegisteredOnly)
                        : KeyLength) with
                {
                    UpdatedUtc = _clock.GetUtcNow(),
                });
                return;
            }

            // [#920] The relay owner removed this member. Not a fault and not the key: the group
            // is off until sharing is turned off and on, which leaves the room and lifts it.
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                // Only a save from here on counts as the player acting on the message.
                _removedByOwner = true;
                Volatile.Write(ref _settingsSaved, 0);
                _lastGood = null;
                Publish(GroupSnapshot.Off.Saying(new(GroupStatus.RemovedByOwner)) with
                {
                    UpdatedUtc = _clock.GetUtcNow(),
                });
                return;
            }

            PublishStale(new(GroupStatus.ServerAnswered, (int)response.StatusCode));
            return;
        }

        _removedByOwner = false;
        if (snapshot.Raid.LastKnownPosition is { } carried)
        {
            var position = (Taken: (DateTimeOffset?)carried.Timestamp.ToUniversalTime(), carried.Position.X, carried.Position.Y, carried.Position.Z);
            if (_positionSent != position)
            {
                _positionSent = position;
                _stageTimeline?.PositionPublished(sentOnClock);
            }
        }

        var room = await response.Content.ReadFromJsonAsync<RoomStateDto>(Json, cancellationToken).ConfigureAwait(false);
        // A relay that answers with one can hold the next exchange until the room moves. One
        // that does not is an older build, and this client keeps to its own tick against it —
        // which is the whole of the compatibility story from this end.
        _roomRevision = room?.Revision;
        _relayHolds = _roomRevision is { } answered
            && (asked is not { } requested
                || answered > requested
                || Stopwatch.GetElapsedTime(sent) >= hold / 2);
        var seen = (room?.Members ?? [])
            .Select(member => (IReadOnlyList<ObservedKit>)(member.Observed ?? [])
                .Select(kit => new ObservedKit(kit.Name, kit.Loadout ?? [])
                {
                    Level = kit.Level,
                    Side = kit.Side,
                    ScavLockedUntil = kit.ScavLockedUntilUnix is { } unix
                        ? DateTimeOffset.FromUnixTimeSeconds(unix)
                        : null,
                })
                .ToArray())
            .ToArray();
        // Each member's kit, from whoever could see it. Their own report wins where they have
        // one; otherwise it comes from the people whose game named it.
        var members = GroupModeCheck.Separate(
            ownMode,
            [.. (room?.Members ?? []).Select(member => Fill(Read(member), seen))]);
        var mine = GroupKitMirror.FindAll(seen, settings.DisplayName);
        // Timed on the way in, before anything is drawn: this is the number that says whether
        // a squadmate's screenshot is reaching this map quickly, and it is the only honest way
        // to have one.
        var arrived = _clock.GetUtcNow();
        foreach (var member in members)
        {
            _latency.Observe(member.Name, member.PositionAge, member.Since, arrived);
        }

        // Occasionally, not every five seconds. Three lines at the start answer "is it working
        // at all", which is the question, and one every ten minutes after that shows it still
        // is, without filling an evening's log.
        if (Interlocked.Increment(ref _published) is 1 or 2 or 3 || _published % 120 == 0)
        {
            // The key is never logged. It is the only thing protecting the group now that it
            // is also the room, and a log file is the easiest place to read one out of.
            _logger.LogInformation(
                "Group publish {Count} succeeded as {Name}; {Members} other member(s) present.",
                _published,
                settings.DisplayName,
                members.Count);
        }

        // Version skew is said on the same line the group is described on, because it is about
        // this exchange rather than about the application, and because a second place to look
        // is a place nobody looks.
        var describe = DescribeSharing(settings.DisplayName, members.Count, snapshot);
        var published = new GroupSnapshot(
            true,
            members,
            string.Empty,
            _clock.GetUtcNow())
        {
            Status = Skew(room?.Protocol) is { } skew ? new(GroupStatus.WithSkew, describe, skew) : describe,
            // The things this companion cannot read about its own player, handed back by the
            // people whose game named them.
            MyLoadout = mine?.Loadout ?? [],
            MyLevel = mine?.Level,
            MySide = mine?.Side,
            MyScavLockedUntil = mine?.ScavLockedUntil,
            MyGameMode = ownMode,
            // The server expires pings for us, so whatever comes back is current by
            // definition and the client needs no timer of its own.
            Waypoints = (room?.Waypoints ?? []).Select(w =>
                new GroupWaypointView(w.Id, w.By, w.MapId, w.X, w.Y, w.Z, w.Label, w.CompletedBy)
                {
                    CreatedUtc = w.CreatedUtc,
                    Colour = TarkovCompanion.Core.Common.MarkPalette.Normalize(w.Color),
                }).ToArray(),
            Pings = (room?.Pings ?? []).Select(p =>
                new GroupPingView(p.Id, p.By, p.MapId, p.X, p.Y, p.Z, p.Label, p.CreatedUtc)
                {
                    Colour = TarkovCompanion.Core.Common.MarkPalette.Normalize(p.Color),
                }).ToArray(),
            PositionLatency = _latency.Current,
            // #889: so a reader can tell a new group key's room from this one's.
            Room = GroupSnapshot.RoomOf(settings.ServerUri, settings.Key?.Trim()),
        };
        // Kept so the next failed exchange has something true to keep showing. StaleSince is
        // null here by construction: this read worked, so nothing on screen is old.
        _lastGood = published;
        _staleSinceStarted = null;
        Publish(published);

        await CompleteReachedAsync(room, snapshot, settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ticks off any waypoint the player is standing on.
    /// </summary>
    /// <remarks>
    /// A waypoint somebody reached is drawn quiet rather than removed, because "we went there"
    /// is worth keeping on screen, and until now nothing ever decided that anybody had. The
    /// group's own plan was a list that only ever grew.
    ///
    /// Decided here rather than on the server. The server holds every member's published
    /// position and trail, but it has no business measuring distances between people and
    /// places. The client knows its own screenshot position and says so once, and that report is
    /// itself a record of where somebody was: the relay keeps who reached the waypoint and when,
    /// in marks.json once it has a state directory.
    ///
    /// The state that comes back is a moment old, so the same waypoint can be reported twice
    /// before the next exchange catches up. The server refuses the second, which is why that
    /// answer is not treated as a failure.
    /// </remarks>
    private async Task CompleteReachedAsync(
        RoomStateDto? room,
        ApplicationRuntimeSnapshot snapshot,
        GroupSharingSettings settings,
        CancellationToken cancellationToken)
    {
        var raid = snapshot.Raid;
        // [#707] A player who has left the raid reaches nothing: their last screenshot would
        // tick off a waypoint they drop beside their own extract for a squadmate still inside.
        if (SquadRaidPresence.HasLeftRaid(raid.State) ||
            room?.Waypoints is not { Count: > 0 } waypoints ||
            raid.MapId is not { Length: > 0 } mapId ||
            raid.LastKnownPosition is not { } position)
        {
            return;
        }

        foreach (var waypoint in waypoints)
        {
            if (waypoint.CompletedBy is not null ||
                !string.Equals(waypoint.MapId, mapId, StringComparison.OrdinalIgnoreCase) ||
                !GroupWaypointReach.IsReached(position.Position, waypoint.X, waypoint.Y, waypoint.Z))
            {
                continue;
            }

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    new Uri(new Uri(settings.ServerUri!), $"waypoints/{waypoint.Id}/reached"))
                {
                    Content = JsonContent.Create(new ReachedDto(settings.DisplayName!.Trim()), options: Json),
                };
                request.Headers.Add("X-Group-Key", settings.Key!.Trim());
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Reached waypoint {Id} on {Map}.", waypoint.Id, mapId);
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // One waypoint left unticked, and the next exchange tries again.
                _logger.LogDebug(exception, "Could not report reaching waypoint {Id}.", waypoint.Id);
            }
        }
    }


    /// <summary>
    /// Assembles everything this companion sends, in one place.
    /// </summary>
    /// <remarks>
    /// Deliberately one method and deliberately explicit. Somebody asking "what does this send
    /// about me" deserves an answer they can read, and the answer is this and nothing else.
    /// The player's own loadout and quests are each behind their own switch, so agreeing to share
    /// a position is not agreeing to share a kit.
    ///
    /// Observed is behind no switch. What this game logged about the rest of the in-game party
    /// (kit, level, side, scav timer) goes to the relay whenever sharing is on, whatever the
    /// people it describes chose. The relay returns every entry naming somebody in the room to
    /// every holder of the room key, and Fill below shows the observed kit as other members'
    /// loadouts, so it is not handed only to the person it is about. docs/SAFETY.md does not
    /// allow that yet; it is RISK-RELAY-OBSERVED-DATA-POLICY, owned by #310.
    /// </remarks>
    private static MemberStateDto Describe(
        ApplicationRuntimeSnapshot snapshot,
        GroupSharingSettings settings,
        SharedQuests sharedQuests,
        IReadOnlyList<ObservedKit> observed,
        DateTimeOffset now)
    {
        var raid = snapshot.Raid;
        // [#707] Out of the raid, the last screenshot is where the player *was*. Published, it drew
        // a "you" marker at an extract on the maps of squadmates still inside, looking live.
        var hasLeft = SquadRaidPresence.HasLeftRaid(raid.State);
        var position = hasLeft ? null : raid.LastKnownPosition;
        return new(
            settings.DisplayName!,
            raid.MapId,
            raid.State.ToString(),
            raid.Side,
            position?.Position.X,
            position?.Position.Z,
            position?.HeadingDegrees,
            position is null ? null : Math.Max(0, (now - position.Timestamp.ToUniversalTime()).TotalSeconds),
            settings.SharesLoadout ? DescribeLoadout(snapshot) : [],
            sharedQuests.Names)
        {
            // The same quests the names above are the head of, by catalog id. Names are for
            // a squadmate to read and ids are for their companion to place, and tonight's map
            // is a question about where the group's lists overlap rather than what they say.
            QuestIds = sharedQuests.TaskIds,
            // [#780] The open objectives of those quests, by id, with a count where one is kept.
            Objectives = [.. sharedQuests.Objectives.Select(objective =>
                new ObjectiveDto(objective.TaskId, objective.ObjectiveId) { Count = objective.Count })],
            // Published because a map with floors cannot place somebody without it, and the
            // waypoints beside them have carried one from the beginning.
            Y = position?.Position.Y,
            Observed = observed
                .Select(kit => new ObservedKitDto(kit.Name, kit.Loadout)
                {
                    Level = kit.Level,
                    Side = kit.Side,
                    ScavLockedUntilUnix = kit.ScavLockedUntil?.ToUnixTimeSeconds(),
                })
                .ToArray(),
            Trail = hasLeft ? [] : DescribeTrail(snapshot, now),
            // Only a scav's own screen differs. In a PMC party the offered exits are the same
            // for everybody, which is what makes this shareable; a scav's are not, so a scav
            // publishes none and nobody is handed a list that was never theirs.
            Extracts = IsScav(raid) ? [] : [.. raid.ActiveExtracts.Select(extract => extract.Name)],
            Transits = IsScav(raid) ? [] : raid.Transits,
            // #886: left out once the player has left the raid, like the trail. The raid state
            // keeps the last reading through PostRaid, and a clock for a raid that is over is
            // both wrong to show and, with its age growing, a new state on every publish.
            RaidClockSeconds = hasLeft ? null : raid.RaidClock?.TotalSeconds,
            RaidClockAgeSeconds = !hasLeft && raid.RaidClockReadUtc is { } read
                ? Math.Max(0, (now - read.ToUniversalTime()).TotalSeconds)
                : null,
        };
    }

    /// <summary>
    /// Whether this raid is being run as a scav.
    /// </summary>
    /// <remarks>
    /// The one case where the offered exits are a fact about one player rather than about the
    /// raid. A scav's exit list differs from a PMC's on the same map, so publishing one would
    /// hand four other people a list that was never theirs — and they would have no way to
    /// tell, because it arrives looking exactly like a correct one.
    ///
    /// Unknown counts as not a scav. The side is established from the log and is usually
    /// known; treating an unknown as a scav would silence the common case to guard the rare
    /// one, and the receiver checks the side again before applying anything.
    /// </remarks>
    private static bool IsScav(RaidSnapshot raid) =>
        string.Equals(raid.Side, "scav", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The last few places this player has been, so the group can see a path rather than a dot.
    /// </summary>
    /// <remarks>
    /// The tail of the raid's own trail, which is already kept and already emptied when a raid
    /// begins — so a group trail cannot survive into the next raid and draw last raid's route
    /// on this raid's map.
    ///
    /// The current position is left out: it is published separately and would otherwise be
    /// drawn twice, once as a marker and once as the end of a line.
    ///
    /// Each point carries its own age rather than a timestamp. A clock that is wrong by an hour
    /// is common enough, and an age is a duration either end agrees on.
    /// </remarks>
    private static IReadOnlyList<TrailPointDto> DescribeTrail(ApplicationRuntimeSnapshot snapshot, DateTimeOffset now)
    {
        var trail = snapshot.Raid.PositionTrail;
        if (trail.Count < 2)
        {
            return [];
        }

        return
        [
            .. trail
                .Take(trail.Count - 1)
                .TakeLast(MaximumTrailPoints)
                .Select(step => new TrailPointDto(
                    step.Position.X,
                    step.Position.Z,
                    Math.Max(0, (now - step.Timestamp.ToUniversalTime()).TotalSeconds))
                {
                    Y = step.Position.Y,
                }),
        ];
    }

    /// <summary>How many places back is worth sending, which is what fits on a map.</summary>
    /// <remarks>
    /// Ten, against the server's bound of twelve. Five members with ten points each is fifty
    /// extra marks on a map whose whole design argument is that a map covered in markers
    /// answers nothing quickly.
    /// </remarks>
    private const int MaximumTrailPoints = 10;

    /// <summary>
    /// What the player is carrying, as far as the companion knows it.
    /// </summary>
    /// <remarks>
    /// The game writes the player's own inventory nowhere the companion can read, which
    /// docs/research/EFT_LOG_FACTS.md records in full, so there is still nothing honest to
    /// send. A squadmate running a companion that reads its quick bar out of a screenshot does
    /// share a kit, which is why one member of a group can show one and another cannot; that
    /// is the same reading, from the same picture, that #35 is for.
    ///
    /// The switch sends an empty list rather than pretending. The Group page says so beside
    /// it, because a switch that silently does nothing is worse than one that is not there.
    /// </remarks>
    private static IReadOnlyList<string> DescribeLoadout(ApplicationRuntimeSnapshot snapshot) => [];


    /// <summary>
    /// Fills in a member's kit from whoever could see it, when they could not see it themselves.
    /// </summary>
    private static GroupMemberView Fill(GroupMemberView member, IReadOnlyList<IReadOnlyList<ObservedKit>> seen) =>
        member.Loadout.Count > 0
            ? member
            : member with { Loadout = GroupKitMirror.Find(seen, member.Name) };

    private static GroupMemberView Read(MemberStateDto member) => new(
        member.Name,
        member.MapId,
        Enum.TryParse<RaidLifecycleState>(member.RaidState, out var state) ? state : RaidLifecycleState.Unknown,
        member.Side,
        member.X is { } x && member.Z is { } z ? new WorldPosition(x, member.Y ?? 0, z) : null,
        member.Heading,
        member.PositionAge is { } age ? TimeSpan.FromSeconds(age) : null,
        member.Loadout ?? [],
        member.Quests ?? [])
    {
        QuestIds = member.QuestIds ?? [],
        GameMode = GroupModeCheck.Normalize(member.GameMode),
        // [#289] Absent from a companion or relay that predates them, which reads as "not said".
        Ready = member.Ready,
        PlannedExtract = string.IsNullOrWhiteSpace(member.PlannedExtract) ? null : member.PlannedExtract.Trim(),
        Note = string.IsNullOrWhiteSpace(member.Note) ? null : member.Note.Trim(),
        // [#712 T7] Absent from a companion or relay that predates them, or one switched off.
        LoadoutCheck = LoadoutCheckWire.Read(member.LoadoutCheck),
        Level = member.Level is >= 1 and <= 79 ? member.Level : null,
        // [#780] Absent from a companion or relay that predates it, which reads as no objectives.
        Objectives = [.. (member.Objectives ?? [])
            .Where(objective => objective is { TaskId.Length: > 0, ObjectiveId.Length: > 0 })
            .Select(objective => new GroupObjectiveView(objective.TaskId, objective.ObjectiveId, objective.Count))],
        // The relay's own measure of how long since it heard from them, which is the only
        // honest one: a member's own report cannot say how long ago it arrived.
        Since = member.SinceSeconds is { } quiet ? TimeSpan.FromSeconds(quiet) : null,
        HasKnownHeight = member.Y is not null,
        Extracts = member.Extracts ?? [],
        Transits = member.Transits ?? [],
        RaidClock = member.RaidClockSeconds is { } clock ? TimeSpan.FromSeconds(clock) : null,
        RaidClockAge = member.RaidClockAgeSeconds is { } clockAge ? TimeSpan.FromSeconds(Math.Max(0, clockAge)) : null,
        Trail = (member.Trail ?? [])
            .Select(step => new GroupTrailPointView(
                step.X,
                step.Z,
                TimeSpan.FromSeconds(Math.Max(0, step.AgeSeconds)),
                step.Y))
            .ToArray(),
        Drawings = GroupDrawingWire.Read(member.Drawings),
    };

    /// <summary>
    /// Keeps the last good picture of the group on screen, saying how old it is.
    /// </summary>
    /// <remarks>
    /// One failed exchange used to publish <see cref="GroupSnapshot.Off"/>, which empties
    /// Members, Waypoints and Pings; ApplySnapshot then cleared every squadmate and every mark
    /// from the map until the next tick five seconds later. A single dropped packet made the
    /// whole group disappear and come back.
    ///
    /// Past <see cref="StaleLimit"/> it gives them up, because by then the relay has dropped
    /// them too and drawing them would be inventing a group rather than remembering one.
    /// </remarks>
    private void PublishStale(Phrase detail)
    {
        var now = _clock.GetUtcNow();
        if (_lastGood is not { } good)
        {
            Publish(GroupSnapshot.Off.Saying(detail) with { UpdatedUtc = now });
            return;
        }

        // [#799] How long it has been stale is measured on the monotonic clock: a wall clock set
        // forward would otherwise drop the whole group on the first failure after it.
        var staleStarted = _staleSinceStarted ??= _clock.GetTimestamp();
        var staleFor = _clock.GetElapsedTime(staleStarted);
        var since = good.StaleSince ?? now;
        if (staleFor > StaleLimit)
        {
            _lastGood = null;
            Publish(GroupSnapshot.Off.Saying(detail) with { UpdatedUtc = now });
            return;
        }

        var stale = good.Saying(new(GroupStatus.LastHeard, detail, staleFor)) with
        {
            StaleSince = since,
            UpdatedUtc = now,
        };
        _lastGood = stale;
        Publish(stale);
    }

    /// <summary>
    /// What this companion is sharing, and what it is not sharing yet.
    /// </summary>
    /// <remarks>
    /// "Sharing as Geo · 1 other" was the whole of it, and it is true while being useless: a
    /// client with no position publishes anyway — name, map and raid state go up with null
    /// coordinates — so a member appears in everybody's list with no marker on anybody's map,
    /// and the person it is happening to is told nothing at all.
    ///
    /// That happened to a second player on a fresh install, and the only way to find out why
    /// was for somebody else to notice the absence and ask. The client that has the problem is
    /// the one that can see the cause, so it says so.
    ///
    /// Only while something is actually wrong. Outside a raid there is no position to have,
    /// and nagging about it would make the normal state look broken.
    /// </remarks>
    public static Phrase DescribeSharing(string? name, int others, ApplicationRuntimeSnapshot snapshot)
    {
        var sharing = others == 0
            ? new Phrase(GroupStatus.SharingAlone, name)
            : Phrase.Counted(GroupStatus.SharingWith, others, name);

        if (snapshot.Raid.LastKnownPosition is not null)
        {
            return sharing;
        }

        if (!snapshot.Observation.IsSupported)
        {
            return new(GroupStatus.NoPositionUnsupported, sharing);
        }

        // The screenshot folder specifically, not "the folders". A position comes only from a
        // screenshot filename, so a companion watching the logs and not the screenshots knows
        // the map and the raid state — which is enough to appear in everybody's member list —
        // and has no position to put on anybody's map. That is exactly the shape of it when
        // the game writes its screenshots somewhere the default search does not look, which is
        // what OneDrive does, and Settings takes an explicit path for it.
        if (!snapshot.Observation.IsWatchingScreenshots)
        {
            return new(GroupStatus.NoPositionFolder, sharing);
        }

        if (snapshot.Raid.State != RaidLifecycleState.InRaid)
        {
            return sharing;
        }

        // Named, because "take a screenshot" is unhelpful to somebody who just did.
        //
        // Escape from Tarkov's screenshot key defaults to F12 and so does Steam's. A player who
        // added the game to Steam as a non-Steam shortcut has the overlay active, and the
        // overlay takes F12 before the game sees it: Steam writes a screenshot into its own
        // userdata folder, the game writes nothing, and this companion has a folder it is
        // watching correctly with nothing ever arriving in it.
        //
        // That is indistinguishable from "has not taken one yet" without saying so, and it
        // cost a player an evening. Rebinding either key, or turning the overlay off, fixes it.
        return HasBeenInRaidLongEnoughToExpectOne(snapshot)
            ? new(GroupStatus.NoScreenshotSteam, sharing)
            : new(GroupStatus.NoPositionYet, sharing);
    }

    /// <summary>
    /// Whether this raid has run long enough that a missing screenshot is worth explaining.
    /// </summary>
    /// <remarks>
    /// Two minutes. Long enough that somebody who meant to photograph something has had the
    /// chance, short enough to be useful while the raid is still on. Before that, "take a
    /// screenshot" is the honest answer and a paragraph about Steam would be noise.
    /// </remarks>
    private static bool HasBeenInRaidLongEnoughToExpectOne(ApplicationRuntimeSnapshot snapshot) =>
        snapshot.Raid.StartedUtc is { } started &&
        DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(2);

    /// <summary>How large a report body the relay will accept.</summary>
    /// <remarks>
    /// Must match ProblemReports.MaximumBytes on the relay. Kestrel's own limit there is
    /// smaller still, at 32 KiB, and the relay has to raise it per-endpoint to actually accept
    /// this much; sending past this figure only ever asks for a 413 the endpoint never sees.
    /// </remarks>
    private const int MaximumReportBytes = 64 * 1024;

    /// <summary>
    /// Sends a diagnostic report to the relay, which files it where the work happens.
    /// </summary>
    /// <remarks>
    /// Here rather than in its own service because this is the one component that already
    /// knows the relay's address and holds the key, and a second thing that did would be a
    /// second thing to keep in step.
    ///
    /// Nothing is added to the report here and nothing is removed. The ordinary desktop caller
    /// supplies SupportBundle's closed projection, but this transport still accepts a raw string
    /// and the relay keeps it as sent. #310 owns server-side schema enforcement; until then an
    /// alternate caller remains part of RISK-REPORT-REDACTION. What comes back is a sentence for
    /// the player and, when GitHub was reachable, a link.
    ///
    /// Every failure ends by pointing at Copy diagnostics, because the whole point is that the
    /// person with the problem can get the report out — and a relay they cannot reach is one
    /// of the problems they might be reporting.
    /// </remarks>
    public async Task<string> ReportProblemAsync(string report, CancellationToken cancellationToken) =>
        (await TrySendReportAsync(report, cancellationToken).ConfigureAwait(false)).Message;

    /// <summary>
    /// <see cref="ReportProblemAsync"/> with the outcome kept apart from its sentence, so the
    /// problem-report outbox (#314) can tell "the relay was not there" (worth retrying later)
    /// from "the relay said no" (not worth retrying at all).
    /// </summary>
    /// <remarks>
    /// A 502, 503 or 504 is a proxy in front of a relay that is down, so it counts as unreachable.
    /// The 30-second send timeout does too; before this it escaped as a bare cancellation.
    /// </remarks>
    public async Task<ReportSendResult> TrySendReportAsync(string report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        // [#292] Refused, not queued: a player who chose Local only must not have it sent later.
        switch (_network?.Check(NetworkService.ProblemReports))
        {
            case NetworkVerdict.LocalOnly:
                return new(ReportDelivery.Refused, "Not sent · Local only is on. Use Copy diagnostics instead.");
            case NetworkVerdict.SwitchedOff:
                return new(ReportDelivery.Refused, "Not sent · problem reports are off in Data & Privacy.");
        }

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.IsUsable)
        {
            return new(
                ReportDelivery.Refused,
                settings.MissingPiece is { } missing
                    ? $"Cannot send: the group needs {missing}. Use Copy diagnostics instead."
                    : "Cannot send: group sharing is not set up. Use Copy diagnostics instead.");
        }

        try
        {
            using var sending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sending.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(new Uri(settings.ServerUri!), "report"))
            {
                Content = new StringContent(TrimToFit(report, MaximumReportBytes), Encoding.UTF8, "text/markdown"),
            };
            request.Headers.Add("X-Group-Key", settings.Key!.Trim());
            using var response = await _httpClient.SendAsync(request, sending.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // A 413 is Kestrel refusing the request before the endpoint ever ran, so its
                // body is whatever the framework happened to write, not a useful detail. Said
                // plainly instead: "the relay refused it (413)" answered nothing about what
                // had gone wrong.
                if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                {
                    return new(ReportDelivery.Refused, "Could not send: the report was too large for the relay. Use Copy diagnostics instead.");
                }

                if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    return new(ReportDelivery.Unreachable, $"Could not reach the relay ({(int)response.StatusCode}).");
                }

                var detail = await response.Content.ReadAsStringAsync(sending.Token).ConfigureAwait(false);
                return new(ReportDelivery.Refused, $"The relay refused it ({(int)response.StatusCode}). {detail}");
            }

            // The relay has the report from here on. A body that cannot be read must not turn
            // this into "unreachable", or the outbox would send the same report a second time.
            ReportOutcomeDto? outcome = null;
            try
            {
                outcome = await response.Content
                    .ReadFromJsonAsync<ReportOutcomeDto>(Json, sending.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException or OperationCanceledException or HttpRequestException or IOException &&
                                              !cancellationToken.IsCancellationRequested)
            {
                outcome = null; // Taken, but without a reference to show.
            }

            return new(
                ReportDelivery.Sent,
                outcome is null
                    ? "Sent · the relay took it."
                    : $"Sent · reference {outcome.Reference} · {outcome.Detail}");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ReportDelivery.Unreachable, $"Could not reach the relay: {Explain(exception)}. Use Copy diagnostics instead.");
        }
    }

    /// <summary>
    /// Where to send this exchange, and whether the relay may hold it open.
    /// </summary>
    /// <remarks>
    /// Both parts or neither. A revision with nothing to compare it against is a wait with
    /// nothing at the end of it, and a relay that has never answered with one is an older build
    /// that would ignore both — which is the point: the query is additive, so the request this
    /// client sends against a relay that predates it is byte-for-byte the request it always
    /// sent.
    /// </remarks>
    private string Exchange(TimeSpan hold) =>
        hold > TimeSpan.Zero && _roomRevision is { } since
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"state?wait={hold.TotalSeconds:0.###}&since={since}")
            : "state";

    /// <summary>What squadmate positions have been measured at, for diagnostics.</summary>
    public GroupPositionLatencySnapshot PositionLatency => _latency.Current;

    /// <summary>
    /// Cuts a report down to a byte budget rather than let the relay refuse the whole thing.
    /// </summary>
    /// <remarks>
    /// The ordinary desktop report is a closed, bounded projection nowhere near this size, but
    /// the transport still accepts a raw string from whatever else builds one, and a report
    /// that arrives untrimmed is worth less than a shorter one that actually arrives. Cut from
    /// the end rather than refuse outright, so what already fits -- typically the summary at
    /// the top -- still reaches the relay, and say plainly that it happened.
    /// </remarks>
    private static string TrimToFit(string report, int maximumBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(report);
        if (bytes.Length <= maximumBytes)
        {
            return report;
        }

        const string notice = "\n\n(trimmed to fit the relay's size limit)";
        var budget = Math.Max(0, maximumBytes - Encoding.UTF8.GetByteCount(notice));
        return Encoding.UTF8.GetString(bytes, 0, budget) + notice;
    }

    private sealed record ReportOutcomeDto(string Reference, string Detail);

    private void Publish(GroupSnapshot group) =>
        _stateStore.Update(current => current with { Group = group });

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stateStore.Changed -= RuntimeStateChanged;
        if (_quests is not null)
        {
            _quests.Changed -= QuestsChanged;
        }

        if (_status is not null)
        {
            _status.Changed -= QuestsChanged;
        }

        if (_readyCheck is not null)
        {
            _readyCheck.Changed -= QuestsChanged;
        }

        Drawings.Changed -= QuestsChanged;
        _settings.Changed -= SettingsChanged;
        if (_network is not null)
        {
            _network.Changed -= NetworkChanged;
        }
        // Said out loud rather than left to time out. DELETE /state/{name} has been served
        // since the relay was written and called by nothing, so a member who closed the
        // application stayed on everybody else's map for the full three-minute lifetime,
        // apparently still in the raid.
        //
        // After the worker has stopped (#889): withdrawn first, a held exchange that the DELETE
        // itself ended let the loop POST once more after the goodbye and re-register this member
        // for the full lifetime. Stopped first, no exchange can follow the DELETE. On its own
        // short budget, so a relay that has gone away cannot hold the application open while it
        // closes. Half a second, not the two a rename gets (#786): this runs inside the
        // application's teardown, and a goodbye that does not arrive costs the group a stale
        // marker, never the player a hung close.
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_worker is { } worker)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await WithdrawRegisteredAsync(ClosingWithdrawBudget).ConfigureAwait(false);
        _stopping.Dispose();
    }

    /// <summary>Withdraws whatever this service last registered, if anything.</summary>
    private async Task WithdrawRegisteredAsync(TimeSpan? budget = null)
    {
        if (_registered is { } registered)
        {
            _registered = null;
            await WithdrawAsync(registered, budget).ConfigureAwait(false);
        }
    }

    /// <summary>Tells the relay this member is going, so the others stop drawing them.</summary>
    /// <remarks>
    /// Takes the identity explicitly rather than reading the settings, because two of the three
    /// callers are withdrawing something the settings no longer describe: a renamed member and a
    /// switched-off session.
    ///
    /// Best effort and silent. Failing to say goodbye costs the group three minutes of a stale
    /// marker, which is what happened every time before this; it must not cost anybody a hung
    /// close, so it runs on its own short budget and swallows everything.
    /// </remarks>
    private async Task WithdrawAsync((string Server, string Key, string Name) identity, TimeSpan? budget = null)
    {
        if (SharingVerdict != NetworkVerdict.Allowed)
        {
            return;
        }

        try
        {
            using var leaving = new CancellationTokenSource(budget ?? WithdrawBudget);
            using var request = new HttpRequestMessage(
                HttpMethod.Delete,
                new Uri(new Uri(identity.Server), $"state/{Uri.EscapeDataString(identity.Name)}"));
            request.Headers.Add("X-Group-Key", identity.Key);
            using var response = await _httpClient.SendAsync(request, leaving.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Nothing to do about it and, by the time it matters, nobody to tell.
        }
    }

    private sealed record MemberStateDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("mapId")] string? MapId,
        [property: JsonPropertyName("raidState")] string RaidState,
        [property: JsonPropertyName("side")] string? Side,
        [property: JsonPropertyName("x")] double? X,
        [property: JsonPropertyName("z")] double? Z,
        [property: JsonPropertyName("heading")] double? Heading,
        [property: JsonPropertyName("positionAge")] double? PositionAge,
        [property: JsonPropertyName("loadout")] IReadOnlyList<string>? Loadout,
        [property: JsonPropertyName("quests")] IReadOnlyList<string>? Quests)
    {
        /// <summary>
        /// How long since the relay heard from this member, which only the relay can say.
        /// </summary>
        /// <remarks>
        /// Read but never sent: a member has no idea how long ago its own last message arrived,
        /// and a client that sent a number here would be asserting something about a clock it
        /// does not have.
        /// </remarks>
        [JsonPropertyName("sinceSeconds")]
        public double? SinceSeconds { get; init; }

        /// <summary>Which quests those are, so a receiver can place them on a map.</summary>
        [JsonPropertyName("questIds")]
        public IReadOnlyList<string>? QuestIds { get; init; }

        /// <summary>[#269] "pvp", "pve" or "seasonal"; absent from clients and relays that predate it.</summary>
        [JsonPropertyName("gameMode")]
        public string? GameMode { get; init; }

        /// <summary>[#289] Ready or not, as the member said on Team; absent when they have not said.</summary>
        [JsonPropertyName("ready")]
        public bool? Ready { get; init; }

        /// <summary>[#289] The extract the member plans to leave by.</summary>
        [JsonPropertyName("plannedExtract")]
        public string? PlannedExtract { get; init; }

        /// <summary>[#289] A short line for the squad.</summary>
        [JsonPropertyName("note")]
        public string? Note { get; init; }

        /// <summary>How high they were standing, absent from clients that predate it.</summary>
        [JsonPropertyName("y")]
        public double? Y { get; init; }

        /// <summary>[#780] The open objectives of their active quests, absent from clients that predate it.</summary>
        [JsonPropertyName("objectives")]
        public IReadOnlyList<ObjectiveDto>? Objectives { get; init; }

        /// <summary>What this member's game said about everybody else in their party.</summary>
        [JsonPropertyName("observed")]
        public IReadOnlyList<ObservedKitDto>? Observed { get; init; }

        /// <summary>Where this member has been this raid, oldest first.</summary>
        [JsonPropertyName("trail")]
        public IReadOnlyList<TrailPointDto>? Trail { get; init; }

        /// <summary>The exits their own scan of the extract screen read.</summary>
        [JsonPropertyName("extracts")]
        public IReadOnlyList<string>? Extracts { get; init; }

        [JsonPropertyName("transits")]
        public IReadOnlyList<string>? Transits { get; init; }

        /// <summary>Their clock reading, with how old it is.</summary>
        [JsonPropertyName("raidClockSeconds")]
        public double? RaidClockSeconds { get; init; }

        [JsonPropertyName("raidClockAge")]
        public double? RaidClockAgeSeconds { get; init; }

        /// <summary>[#712 T7] Their own Loadout check; left out of the JSON when not shared.</summary>
        [JsonPropertyName("loadoutCheck")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LoadoutCheckDto? LoadoutCheck { get; init; }

        /// <summary>[#712 T7] Their own level; left out of the JSON when not shared.</summary>
        [JsonPropertyName("level")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Level { get; init; }

        /// <summary>[#286] The lines this member shares; left out of the JSON when there are none.</summary>
        [JsonPropertyName("drawings")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<GroupDrawingDto>? Drawings { get; init; }
    }

    private sealed record ObjectiveDto(
        [property: JsonPropertyName("task")] string TaskId,
        [property: JsonPropertyName("id")] string ObjectiveId)
    {
        [JsonPropertyName("count")]
        public decimal? Count { get; init; }
    }

    /// <summary>[#712 T7] The wire form of a Loadout check (GroupLoadoutCheckState on the relay).</summary>
    internal sealed record LoadoutCheckDto(
        [property: JsonPropertyName("items")] IReadOnlyList<LoadoutCheckItemDto>? Items)
    {
        [JsonPropertyName("mapId")]
        public string? MapId { get; init; }

        [JsonPropertyName("age")]
        public double? AgeSeconds { get; init; }
    }

    internal sealed record LoadoutCheckItemDto(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("ok")] bool? Ok)
    {
        [JsonPropertyName("missing")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Missing { get; init; }
    }

    /// <summary>[#712 T7] A Loadout check to and from the wire, inside the relay's bounds.</summary>
    internal static class LoadoutCheckWire
    {
        public static LoadoutCheckDto? Describe(SharedLoadoutCheck? check, DateTimeOffset nowUtc) => check is null
            ? null
            : new LoadoutCheckDto([.. check.Items
                .Where(item => item.Kind.Length is > 0 and <= 16)
                .Take(SharedLoadoutCheck.MaximumItems)
                .Select(item => new LoadoutCheckItemDto(item.Kind, item.Ok)
                {
                    Missing = item.Missing is { Length: > SharedLoadoutCheck.MissingLimit } text
                        ? text[..SharedLoadoutCheck.MissingLimit]
                        : item.Missing,
                })])
            {
                MapId = check.MapId is { Length: <= 64 } map ? map : null,
                AgeSeconds = Math.Round(Math.Clamp((nowUtc - check.CheckedUtc).TotalSeconds, 0, 86_400), 1),
            };

        public static GroupLoadoutCheckView? Read(LoadoutCheckDto? check) => check?.Items is not { } items
            ? null
            : new(
                string.IsNullOrWhiteSpace(check.MapId) ? null : check.MapId,
                [.. items
                    .Where(item => item is { Kind.Length: > 0 })
                    .Take(SharedLoadoutCheck.MaximumItems)
                    .Select(item => new LoadoutCheckItem(
                        item.Kind,
                        item.Ok,
                        item.Ok == false && !string.IsNullOrWhiteSpace(item.Missing) ? item.Missing.Trim() : null))],
                check.AgeSeconds is { } age && double.IsFinite(age) && age >= 0 ? TimeSpan.FromSeconds(age) : null);
    }

    private sealed record TrailPointDto(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("z")] double Z,
        [property: JsonPropertyName("age")] double AgeSeconds)
    {
        [JsonPropertyName("y")]
        public double? Y { get; init; }
    }

    private sealed record ObservedKitDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("loadout")] IReadOnlyList<string>? Loadout)
    {
        [JsonPropertyName("level")]
        public int? Level { get; init; }

        [JsonPropertyName("side")]
        public string? Side { get; init; }

        [JsonPropertyName("scavLockedUntil")]
        public long? ScavLockedUntilUnix { get; init; }
    }

    /// <summary>
    /// What this build speaks, against what the relay does.
    /// </summary>
    /// <remarks>
    /// Everything on this wire is additive, so a mismatch is almost never fatal — which is why
    /// it is worth saying. A client quietly missing a field it was never sent looks exactly like
    /// a feature that does not work, and there is no way to tell them apart from inside the app.
    ///
    /// Said once and without alarm: the relay updates itself every half hour, so a relay behind
    /// this build fixes itself, and a relay ahead of it means this build is the one due an
    /// update. Neither is an error and neither stops sharing.
    /// </remarks>
    public const int Protocol = 1;

    private static Phrase? Skew(int? relay) => relay is { } spoken && spoken != Protocol
        ? new(GroupStatus.RelaySkew, spoken, Protocol)
        : null;

    private sealed record RoomStateDto(
        [property: JsonPropertyName("room")] string Room,
        [property: JsonPropertyName("members")] IReadOnlyList<MemberStateDto> Members)
    {
        [JsonPropertyName("waypoints")]
        public IReadOnlyList<WaypointDto> Waypoints { get; init; } = [];

        [JsonPropertyName("pings")]
        public IReadOnlyList<PingDto> Pings { get; init; } = [];

        /// <summary>What the relay says it speaks, or null from one too old to say.</summary>
        [JsonPropertyName("protocol")]
        public int? Protocol { get; init; }

        /// <summary>
        /// How many times this room has changed for this reader, or null from a relay that does
        /// not hold exchanges open.
        /// </summary>
        [JsonPropertyName("revision")]
        public long? Revision { get; init; }
    }

    private sealed record ReachedDto([property: JsonPropertyName("by")] string By);

    private sealed record MarkDto(
        [property: JsonPropertyName("by")] string By,
        [property: JsonPropertyName("mapId")] string MapId,
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("z")] double Z,
        [property: JsonPropertyName("label")] string? Label)
    {
        /// <summary>#290: a palette colour; not written when there is none, so the body older relays bound is unchanged.</summary>
        [JsonPropertyName("color")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Color { get; init; }
    }

    private sealed record WaypointDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("by")] string By,
        [property: JsonPropertyName("mapId")] string MapId,
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("z")] double Z,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("completedBy")] string? CompletedBy)
    {
        /// <summary>When the relay says this was marked, for the Team workspace's mark list.</summary>
        [JsonPropertyName("createdUtc")]
        public DateTimeOffset CreatedUtc { get; init; }

        /// <summary>#290: the colour the marker chose, from a relay that carries it.</summary>
        [JsonPropertyName("color")]
        public string? Color { get; init; }
    }

    private sealed record PingDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("by")] string By,
        [property: JsonPropertyName("mapId")] string MapId,
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("z")] double Z,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc)
    {
        [JsonPropertyName("color")]
        public string? Color { get; init; }
    }
}
