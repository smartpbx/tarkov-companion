using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Runtime;

/// <summary>
/// What a scan is allowed to do to the raid record: read it, and add to it.
/// </summary>
/// <remarks>
/// A seam rather than the whole service, because the recognition path used to hold
/// <c>IRaidStateService</c> and call <c>ApplyExtracts</c> on it directly. That skipped the
/// coordinator entirely, so the map waited for the next log line to redraw and the only writer
/// of an <c>extracts</c> raid event was left with no callers — no raid has ever recorded which
/// exits it was offered. Handing the scan something that cannot mutate state on its own is
/// what stops that coming back.
/// </remarks>
public interface IRaidActivityRecorder
{
    /// <summary>The raid as it stands, for a scan that needs to know which map it is on.</summary>
    RaidSnapshot Current { get; }

    Task<RaidSnapshot> ApplyExtractsAsync(
        IReadOnlyList<ActiveExtract> extracts,
        DateTimeOffset observedUtc,
        CancellationToken cancellationToken,
        TimeSpan? raidClock = null,
        IReadOnlyList<string>? linesNotMatched = null,
        IReadOnlyList<string>? transits = null);

    /// <summary>Ties a flea sale to the raid it happened during, if one was open.</summary>
    Task RecordSaleAsync(FleaSaleObservation sale, CancellationToken cancellationToken);

    /// <summary>Ties a quest the game announced to the raid it was announced during.</summary>
    Task RecordQuestAsync(QuestStatusObservation quest, CancellationToken cancellationToken);

    /// <summary>Holds a matchmaking time until the raid it belongs to starts.</summary>
    Task RecordLoadTimeAsync(LoadTimeObservation loadTime, CancellationToken cancellationToken);
}

/// <summary>
/// Applies what was observed to raid state and records it without making durable acceptance
/// optional.
/// </summary>
/// <remarks>
/// <para>
/// Direct v1 stores retain their show-first behavior. The at-least-once adapter instead works a
/// transition out on a private copy of the raid state, has its bounded store accept every command
/// the transition produces as one batch, and only then makes that copy current and publishes it.
/// It does not wait for SQLite delivery, but a full or failed outbox can no longer be mistaken for
/// accepted state — and, unlike applying first, a refused transition leaves the live raid exactly
/// as it was, so the next observation does not build on a raid the record never heard of.
/// </para>
/// <para>
/// Staged transitions are serialized: two observations staged against the same raid would each
/// commit over the other.
/// </para>
/// <para>
/// A raid start is still recorded before any event against it: raid_events.raid_id is NOT NULL
/// REFERENCES raids(id), so the raid row has to exist first. That ordering is a database
/// constraint and is not what moved.
/// </para>
/// </remarks>
public sealed class RaidActivityCoordinator(
    IRaidStateService raidStateService,
    IRaidHistoryService raidHistoryService,
    IPlayerProfileService profileService,
    IRuntimeStateStore stateStore,
    // Optional, and only for the length of a raid on a map. Without it the resume falls back
    // to the longest raid in the game, which is generous rather than wrong.
    IMapDataService? mapDataService = null,
    // Only the startup sweep needs one: every other clock here comes in on the evidence.
    TimeProvider? timeProvider = null) : IRaidActivityRecorder
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);

    /// <summary>
    /// The most recent matchmaking time, waiting for the raid it belongs to.
    /// </summary>
    /// <remarks>
    /// The game writes it before that raid has an id, so it cannot be recorded against one yet.
    /// A plain field is enough: it has one writer (the log watcher, sequentially) and one
    /// reader (the next raid start), and losing a race would at worst attach the wrong one of
    /// two matches made in the same few seconds to the raid that followed.
    /// </remarks>
    private LoadTimeObservation? _pendingLoadTime;

    /// <summary>How long after MatchingCompleted a raid may start and still be the one it timed.</summary>
    private static readonly TimeSpan MaximumLoadTimeAge = TimeSpan.FromMinutes(5);

    /// <summary>Raised when durable raid-history delivery health changes.</summary>
    public event EventHandler? OutboxChanged
    {
        add
        {
            if (raidHistoryService is IAtLeastOnceRaidHistoryService outbox)
            {
                outbox.Changed += value;
            }
        }
        remove
        {
            if (raidHistoryService is IAtLeastOnceRaidHistoryService outbox)
            {
                outbox.Changed -= value;
            }
        }
    }

    /// <summary>Raid-history delivery health, or empty for a direct store.</summary>
    public OutboxSnapshot OutboxSnapshot =>
        raidHistoryService is IAtLeastOnceRaidHistoryService outbox
            ? outbox.Snapshot
            : OutboxSnapshot.Empty;

    /// <summary>Manual recovery: wakes or restarts paused raid-history delivery.</summary>
    public bool RequestOutboxRecovery() =>
        raidHistoryService is IAtLeastOnceRaidHistoryService outbox && outbox.RequestPumpRecovery();

    /// <summary>Manual recovery: returns one dead-lettered raid-history command to delivery.</summary>
    public Task<bool> RetryOutboxDeadLetterAsync(
        OperationId operationId,
        CancellationToken cancellationToken) =>
        raidHistoryService is IAtLeastOnceRaidHistoryService outbox
            ? outbox.RetryDeadLetterAsync(operationId, cancellationToken)
            : Task.FromResult(false);

    /// <inheritdoc />
    public RaidSnapshot Current => raidStateService.Current;

    public Task<RaidSnapshot> ApplyEvidenceAsync(RaidEvidence evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var adopted = false;
        return TransitionAsync(
            async state =>
            {
                var previous = state.Current;
                var current = state.Apply(evidence);
                // Before anything is published or written. The whole point is that the raid
                // keeps the identity it already had, so publishing the new one first would put a
                // raid on screen under an id that is about to change and record a state event
                // against it.
                if (evidence.ResumesSession)
                {
                    // A raid that is still loading has no id yet and is a new raid by definition,
                    // so there is nothing to take over — but the rows a previous run left open
                    // are still there, and this is still the moment to close them.
                    var canAdopt = current.State == RaidLifecycleState.InRaid
                        && current.RaidId is not null
                        && previous.RaidId != current.RaidId;
                    (current, adopted) = await ResumeAsync(state, current, canAdopt, evidence, cancellationToken)
                        .ConfigureAwait(false);
                }

                return current;
            },
            (previous, current, commands) =>
                AddTransitionCommandsAsync(commands, previous, current, evidence, adopted, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Takes over the raid the previous run of this companion was recording, if it is this one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The state service is memory. A companion restarted mid-raid — by a crash, a Windows
    /// update, or the restart Velopack asks for after installing one, which this application
    /// checks for on every launch — recovers the raid from the log and then gives it a new
    /// identity. The row the previous run opened keeps <c>end_utc NULL</c> for ever, History
    /// shows it as in progress, and the map draws an empty trail over screenshots already on
    /// disk.
    /// </para>
    /// <para>
    /// Whichever way it goes, the rows that are not this raid are closed. They were opened by
    /// raids that are over, and left alone they accumulate for the rest of the wipe.
    /// </para>
    /// <para>
    /// Failing here must not cost the raid. A resume is a repair of the record, and a database
    /// that cannot be read is exactly when observation matters most: the session carries on
    /// with the new identity and the row stays open, which is what happened before this
    /// existed.
    /// </para>
    /// </remarks>
    /// <param name="state">The raid state the transition is being applied to.</param>
    /// <returns>The raid as it now stands, and whether its row already exists.</returns>
    private async Task<(RaidSnapshot Raid, bool Adopted)> ResumeAsync(
        IRaidStateService state,
        RaidSnapshot current,
        bool canAdopt,
        RaidEvidence evidence,
        CancellationToken cancellationToken)
    {
        try
        {
            // Passing no map when nothing can be adopted, which is how the decision is asked
            // to close everything open rather than a second way of saying the same thing.
            var mapId = canAdopt ? current.MapId : null;
            var history = await raidHistoryService.ListAsync(cancellationToken).ConfigureAwait(false);
            var resumption = RaidResume.Choose(
                history,
                mapId,
                current.UpdatedUtc,
                await RaidLengthAsync(mapId, current.Side, cancellationToken).ConfigureAwait(false));
            foreach (var abandoned in resumption.Close)
            {
                // The startup replay found the raid itself, unreported and dead: the game is not
                // running, or the raid began longer ago than any raid lasts. That is a better
                // account of this row than "closed on restart", and a better end time than now:
                // the last moment the game session wrote anything (#568).
                var row = history.FirstOrDefault(raid => raid.Id == abandoned);
                var lastSeen = evidence.RaidLastSeenUtc is { } seen
                    && seen <= current.UpdatedUtc
                    && (row?.StartedUtc is not { } rowStart || seen >= rowStart)
                        ? seen
                        : current.UpdatedUtc;
                await raidHistoryService.EndAsync(
                    abandoned,
                    evidence.EndsUnreported ? lastSeen : current.UpdatedUtc,
                    evidence.EndsUnreported ? RaidClosure.NotReportedOutcome : RaidClosure.ClosedOnRestartOutcome,
                    evidence.EndsUnreported ? RaidClosure.NotReportedNotes : RaidClosure.ClosedOnRestartNotes,
                    cancellationToken).ConfigureAwait(false);
            }

            if (resumption.Adopt is not { } adopted)
            {
                return (current, false);
            }

            // The trail is reloaded rather than rebuilt, because every point is already a row:
            // the screenshots were recorded as they were taken by the run that is over.
            var trail = await raidHistoryService
                .ListPositionsAsync(adopted.Id, cancellationToken)
                .ConfigureAwait(false);
            return (state.Adopt(adopted.Id, adopted.StartedUtc, trail), true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (current, false);
        }
    }

    /// <summary>
    /// Closes the raid rows no raid could still be, whatever the player is doing now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The resume only runs when the game says a raid is in progress, and most launches are
    /// into the menu. A player who crashed mid-raid on Tuesday and opened the companion to look
    /// at the flea on Wednesday left Tuesday's row open for ever, with History showing it as in
    /// progress for the rest of the wipe.
    /// </para>
    /// <para>
    /// Safe to run before observation starts because it is the conservative half of the same
    /// question: anything recent enough for the resume to adopt is left alone.
    /// </para>
    /// <para>
    /// A failure is allowed to reach the lifecycle coordinator. Observation treats repair as an
    /// optional dependency, so play remains usable while runtime health truthfully shows that the
    /// historical record was not repaired.
    /// </para>
    /// </remarks>
    public async Task CloseAbandonedAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var history = await raidHistoryService.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var abandoned in RaidResume.Abandoned(history, now))
        {
            await raidHistoryService.EndAsync(
                abandoned,
                now,
                RaidClosure.ClosedOnRestartOutcome,
                RaidClosure.ClosedOnRestartNotes,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>How long this raid runs, for the side it is being run as.</summary>
    /// <remarks>
    /// This returned the PMC duration whatever the side, so a scav raid's expected end was
    /// computed from a length it never had.
    /// </remarks>
    private async Task<TimeSpan?> RaidLengthAsync(
        string? mapId,
        string? side,
        CancellationToken cancellationToken)
    {
        if (mapDataService is null || mapId is null)
        {
            return null;
        }

        var map = await mapDataService.GetAsync(mapId, cancellationToken).ConfigureAwait(false);
        return RaidTimer.LengthFor(side, map?.PmcRaidDuration, map?.ScavRaidDuration);
    }

    /// <summary>
    /// [#799] The PC's clock was set: moves the raid's held wall times by the same jump.
    /// </summary>
    /// <remarks>
    /// Through the same gate and publication as any other transition, so the map, the group and
    /// the raid card all see the moved times at once. Nothing is recorded: the raid's stored
    /// start stays what the PC said when it happened.
    /// </remarks>
    public Task<RaidSnapshot> RebaseClockAsync(TimeSpan jump, CancellationToken cancellationToken) =>
        TransitionAsync(
            state => Task.FromResult(state is RaidStateService raid ? raid.RebaseClock(jump) : state.Current),
            (_, _, _) => Task.CompletedTask,
            cancellationToken);

    public Task<RaidSnapshot> ApplyPositionAsync(ScreenshotPosition position, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(position);
        return TransitionAsync(
            state => Task.FromResult(state.ApplyPosition(position)),
            async (previous, current, commands) =>
            {
                await AddStartIfNewAsync(commands, previous, current, alreadyRecorded: false, cancellationToken)
                    .ConfigureAwait(false);
                if (current.RaidId is { } raidId)
                {
                    commands.Add(RaidHistoryCommand.RecordPosition(raidId, position));
                }
            },
            cancellationToken);
    }

    /// <param name="raidClock">The remaining time the same screen printed, where it was read.</param>
    /// <param name="linesNotMatched">What the scan read and could not match, so a scan that
    /// matched one exit out of eight is distinguishable from a screen that had one on it.</param>
    /// <param name="transits">Ways to another map, which no extract catalog contains.</param>
    /// <remarks>
    /// These three exist because the scan used to reach past this and call
    /// <c>IRaidStateService.ApplyExtracts</c> itself, which carried them. Routing the scan
    /// through here without them would have been a regression dressed as a refactor.
    /// </remarks>
    public Task<RaidSnapshot> ApplyExtractsAsync(
        IReadOnlyList<ActiveExtract> extracts,
        DateTimeOffset observedUtc,
        CancellationToken cancellationToken,
        TimeSpan? raidClock = null,
        IReadOnlyList<string>? linesNotMatched = null,
        IReadOnlyList<string>? transits = null)
    {
        ArgumentNullException.ThrowIfNull(extracts);
        return TransitionAsync(
            state => Task.FromResult(state.ApplyExtracts(extracts, observedUtc, raidClock, linesNotMatched, transits)),
            async (previous, current, commands) =>
            {
                await AddStartIfNewAsync(commands, previous, current, alreadyRecorded: false, cancellationToken)
                    .ConfigureAwait(false);
                if (current.RaidId is { } raidId)
                {
                    commands.Add(RaidHistoryCommand.RecordExtracts(raidId, observedUtc, extracts));
                }
            },
            cancellationToken);
    }

    public Task RecordScanAsync(ScanExecutionResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        return RecordForOpenRaidAsync(raidId => RaidHistoryCommand.RecordScan(raidId, result), cancellationToken);
    }

    /// <summary>
    /// Records a flea sale against the raid it happened during, if one was open.
    /// </summary>
    /// <remarks>
    /// FleaSaleStateService keeps every sale and says in its own remark that it keeps them for
    /// the session only, so a sale survived until the application closed and was then gone.
    /// Nothing ever tied one to a raid.
    ///
    /// No open raid means no record, deliberately. Most selling is done in the menu, and
    /// attaching a menu sale to the last raid would put it in the record of something that had
    /// already finished.
    /// </remarks>
    public Task RecordSaleAsync(FleaSaleObservation sale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sale);
        return RecordForOpenRaidAsync(raidId => RaidHistoryCommand.RecordSale(raidId, sale), cancellationToken);
    }

    /// <summary>
    /// Records a quest the game announced against the raid it was announced during.
    /// </summary>
    /// <remarks>
    /// QuestLogProgressService already applies these to recorded progress, which answers "what
    /// have I done"; this answers "what happened in that raid", and they are different
    /// questions. The first is a running total and the second is a record.
    /// </remarks>
    public Task RecordQuestAsync(QuestStatusObservation quest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quest);
        return RecordForOpenRaidAsync(raidId => RaidHistoryCommand.RecordQuest(raidId, quest), cancellationToken);
    }

    /// <summary>
    /// Remembers a matchmaking time until the raid it measured begins.
    /// </summary>
    /// <remarks>
    /// The game writes this line before the raid it belongs to has an id, so it cannot be
    /// recorded against one yet. <see cref="AddStartIfNewAsync"/> collects it when that raid
    /// starts and clears it, so a raid that never follows simply lets it go stale rather than
    /// attaching to the wrong one.
    /// </remarks>
    public Task RecordLoadTimeAsync(LoadTimeObservation loadTime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loadTime);
        _pendingLoadTime = loadTime;
        return Task.CompletedTask;
    }

    /// <summary>Applies one transition and records it, in the order the history store requires.</summary>
    /// <param name="apply">Applies the observation to the given raid state and returns the result.</param>
    /// <param name="describe">Adds the commands that record the move from the first snapshot to the second.</param>
    private async Task<RaidSnapshot> TransitionAsync(
        Func<IRaidStateService, Task<RaidSnapshot>> apply,
        Func<RaidSnapshot, RaidSnapshot, List<RaidHistoryCommand>, Task> describe,
        CancellationToken cancellationToken)
    {
        var commands = new List<RaidHistoryCommand>();
        if (raidHistoryService is IAtLeastOnceRaidHistoryService outbox)
        {
            var staged = raidStateService as IStagedRaidStateService
                ?? throw new InvalidOperationException(
                    "Durable raid history needs a raid state that can stage a transition before it is accepted.");
            await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var stage = staged.Stage();
                var previous = stage.Current;
                var current = await apply(stage).ConfigureAwait(false);
                await describe(previous, current, commands).ConfigureAwait(false);
                if (commands.Count > 0)
                {
                    await outbox.AcceptAsync(commands, cancellationToken).ConfigureAwait(false);
                }

                // Accepted, so the transition is now true. Nothing between acceptance and
                // publication can fail: the commit is an assignment and publication isolates
                // its subscribers.
                staged.Commit(stage);
                Publish(current);
                return current;
            }
            finally
            {
                _transitionGate.Release();
            }
        }

        // Compatibility for direct v1 stores. Production composition uses the outbox above;
        // direct callers keep the old fail-soft display behavior until they migrate.
        var previousDirect = raidStateService.Current;
        var currentDirect = await apply(raidStateService).ConfigureAwait(false);
        Publish(currentDirect);
        await describe(previousDirect, currentDirect, commands).ConfigureAwait(false);
        foreach (var command in commands)
        {
            await command.WriteAsync(raidHistoryService, cancellationToken).ConfigureAwait(false);
        }

        return currentDirect;
    }

    private async Task RecordForOpenRaidAsync(
        Func<Guid, RaidHistoryCommand> create,
        CancellationToken cancellationToken)
    {
        if (raidHistoryService is not IAtLeastOnceRaidHistoryService outbox)
        {
            if (raidStateService.Current.RaidId is { } directRaidId)
            {
                await create(directRaidId).WriteAsync(raidHistoryService, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // Read under the transition gate so a record cannot attach itself to a raid whose start
        // is staged but not yet accepted.
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (raidStateService.Current.RaidId is { } raidId)
            {
                await outbox.AcceptAsync([create(raidId)], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task AddTransitionCommandsAsync(
        List<RaidHistoryCommand> commands,
        RaidSnapshot previous,
        RaidSnapshot current,
        RaidEvidence evidence,
        bool alreadyRecorded,
        CancellationToken cancellationToken)
    {
        await AddStartIfNewAsync(commands, previous, current, alreadyRecorded, cancellationToken).ConfigureAwait(false);
        if (current.RaidId is { } raidId)
        {
            // The queue/load time rides on the state event that begins the raid, because that
            // event already has a durable route and a new command kind would need a schema change.
            // Taken (and cleared) on every raid start, but only carried if it is recent: a match
            // whose raid never began (queue cancelled, game closed) must not label a later raid.
            if (previous.RaidId != raidId
                && Interlocked.Exchange(ref _pendingLoadTime, null) is { } loadTime
                && evidence.ObservedUtc - loadTime.ObservedUtc <= MaximumLoadTimeAge)
            {
                evidence = evidence with { LoadSeconds = loadTime.RealSeconds };
            }

            commands.Add(RaidHistoryCommand.RecordState(raidId, evidence));
        }

        if (previous.RaidId is { } previousRaidId
            && previous.State == RaidLifecycleState.InRaid
            && current.State is RaidLifecycleState.Menu or RaidLifecycleState.PostRaid)
        {
            // An end the game never reported is dated to the raid's own last activity and says
            // so, instead of reading like a raid that ended normally just now (#568).
            commands.Add(evidence.EndsUnreported
                ? NotReported(previousRaidId, previous, evidence.ObservedUtc)
                : RaidHistoryCommand.EndRaid(previousRaidId, evidence.ObservedUtc, null, null));
        }
        else if (previous.RaidId is { } displacedRaidId
            && previous.State == RaidLifecycleState.InRaid
            && current.State == RaidLifecycleState.InRaid
            && current.RaidId != displacedRaidId)
        {
            // Another raid began while this one was still open, so the game never reported its
            // end: the process died, or the machine did. Its row used to stay open until the next
            // restart swept it up, with Debrief showing it in progress all the while.
            commands.Add(NotReported(displacedRaidId, previous, evidence.ObservedUtc));
        }
    }

    private static RaidHistoryCommand NotReported(Guid raidId, RaidSnapshot raid, DateTimeOffset noticedUtc) =>
        RaidHistoryCommand.EndRaid(
            raidId,
            raid.LastActivityUtc is { } last && last <= noticedUtc ? last : noticedUtc,
            RaidClosure.NotReportedOutcome,
            RaidClosure.NotReportedNotes);

    /// <summary>
    /// Ends the open raid as not reported once it has run longer than any raid on its map can.
    /// </summary>
    /// <remarks>
    /// The game writes nothing when its process dies, so without this a raid the player was
    /// thrown out of stays "In raid" until the next one starts, which may be tomorrow (#568).
    /// The bound is <see cref="RaidResume"/>'s own: the map's raid length where the catalog has
    /// it, the longest raid in the game where it does not, plus the same margin.
    /// </remarks>
    /// <returns>Whether a raid was ended.</returns>
    public async Task<bool> ExpireOverdueRaidAsync(CancellationToken cancellationToken)
    {
        var raid = raidStateService.Current;
        if (raid.State != RaidLifecycleState.InRaid || raid.StartedUtc is not { } started)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        var length = await RaidLengthAsync(raid.MapId, raid.Side, cancellationToken).ConfigureAwait(false);
        if (now - started <= (length ?? RaidResume.LongestRaid) + RaidResume.Margin)
        {
            return false;
        }

        await ApplyEvidenceAsync(
            new RaidEvidence(
                RaidEvidenceKind.LogLine,
                now,
                raid.MapId,
                RaidLifecycleState.PostRaid,
                new Confidence(0.80),
                "This raid has run longer than any raid on its map can, so it is over. The game never reported its end.")
            {
                EndsUnreported = true,
                RaidKey = raid.RaidKey,
            },
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <param name="alreadyRecorded">
    /// Set when the raid was adopted from the record rather than begun, so its row exists.
    /// Inserting it again is a primary key violation, and the identity being new to this
    /// process is exactly what adoption makes untrue.
    /// </param>
    private async Task AddStartIfNewAsync(
        List<RaidHistoryCommand> commands,
        RaidSnapshot previous,
        RaidSnapshot current,
        bool alreadyRecorded,
        CancellationToken cancellationToken)
    {
        if (alreadyRecorded || current.RaidId is not { } raidId || previous.RaidId == raidId)
        {
            return;
        }

        var profile = await profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        commands.Add(RaidHistoryCommand.StartRaid(
            new(raidId, profile.Id, current.MapId, profile.GameMode.ToString(), current.StartedUtc, null, null, null)));
    }

    private void Publish(RaidSnapshot snapshot) =>
        stateStore.Update(current => current with { Raid = snapshot });
}
