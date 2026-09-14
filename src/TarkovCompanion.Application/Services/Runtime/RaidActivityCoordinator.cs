using System.Text.Json;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Runtime;

/// <summary>
/// Applies what was observed to the raid state, shows it, and then records it.
/// </summary>
/// <remarks>
/// Show first, record second. Every one of these used to await the database before publishing,
/// so each marker waited behind its own write even when nothing was wrong — and when something
/// was wrong (a full disk, an antivirus lock, or the catalog refresh holding SQLite's write
/// lock past the five-second busy timeout on the evening's first launch) the write threw, the
/// watcher rethrew, and observation was torn down: the squad list cleared and "events read"
/// reset, because a row could not be inserted.
///
/// Publishing is in-memory and cannot fail, and the snapshot is complete before any of this
/// runs, so nothing shown is waiting on the disk to confirm it. What a failure still costs is
/// the recording; the queue that stops it costing the watcher as well is a separate change.
///
/// <see cref="EnsureStartedAsync"/> still runs before any event is recorded: raid_events.raid_id
/// is NOT NULL REFERENCES raids(id), so the raid row has to exist first. That ordering is a
/// database constraint and is not what moved.
/// </remarks>
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
}

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

    /// <inheritdoc />
    public RaidSnapshot Current => raidStateService.Current;

    public async Task<RaidSnapshot> ApplyEvidenceAsync(RaidEvidence evidence, CancellationToken cancellationToken)
    {
        var previous = raidStateService.Current;
        var current = raidStateService.Apply(evidence);
        // Before anything is published or written. The whole point is that the raid keeps the
        // identity it already had, so publishing the new one first would put a raid on screen
        // under an id that is about to change and record a state event against it.
        var adopted = false;
        if (evidence.ResumesSession)
        {
            // A raid that is still loading has no id yet and is a new raid by definition, so
            // there is nothing to take over — but the rows a previous run left open are still
            // there, and this is still the moment to close them.
            var canAdopt = current.State == RaidLifecycleState.InRaid
                && current.RaidId is not null
                && previous.RaidId != current.RaidId;
            (current, adopted) = await ResumeAsync(current, canAdopt, cancellationToken).ConfigureAwait(false);
        }

        Publish(current);
        await PersistTransitionAsync(previous, current, evidence, adopted, cancellationToken).ConfigureAwait(false);
        return current;
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
    /// <returns>The raid as it now stands, and whether its row already exists.</returns>
    private async Task<(RaidSnapshot Raid, bool Adopted)> ResumeAsync(
        RaidSnapshot current,
        bool canAdopt,
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
                await RaidLengthAsync(mapId, cancellationToken).ConfigureAwait(false));
            foreach (var abandoned in resumption.Close)
            {
                await raidHistoryService.EndAsync(
                    abandoned,
                    current.UpdatedUtc,
                    "Closed on restart",
                    "The companion was not running when this raid ended.",
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
            return (raidStateService.Adopt(adopted.Id, adopted.StartedUtc, trail), true);
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
    /// Silent on failure, like the resume. Tidying the record is not worth failing startup for.
    /// </para>
    /// </remarks>
    public async Task CloseAbandonedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            var history = await raidHistoryService.ListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var abandoned in RaidResume.Abandoned(history, now))
            {
                await raidHistoryService.EndAsync(
                    abandoned,
                    now,
                    "Closed on restart",
                    "The companion was not running when this raid ended.",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }
    }

    private async Task<TimeSpan?> RaidLengthAsync(string? mapId, CancellationToken cancellationToken)
    {
        if (mapDataService is null || mapId is null)
        {
            return null;
        }

        var map = await mapDataService.GetAsync(mapId, cancellationToken).ConfigureAwait(false);
        return map?.PmcRaidDuration;
    }

    public async Task<RaidSnapshot> ApplyPositionAsync(ScreenshotPosition position, CancellationToken cancellationToken)
    {
        var previous = raidStateService.Current;
        var current = raidStateService.ApplyPosition(position);
        Publish(current);
        await EnsureStartedAsync(previous, current, alreadyRecorded: false, cancellationToken).ConfigureAwait(false);
        if (current.RaidId is { } raidId)
        {
            await raidHistoryService.RecordEventAsync(
                raidId,
                "position",
                position.Timestamp,
                JsonSerializer.Serialize(position),
                cancellationToken).ConfigureAwait(false);
        }

        return current;
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
    public async Task<RaidSnapshot> ApplyExtractsAsync(
        IReadOnlyList<ActiveExtract> extracts,
        DateTimeOffset observedUtc,
        CancellationToken cancellationToken,
        TimeSpan? raidClock = null,
        IReadOnlyList<string>? linesNotMatched = null,
        IReadOnlyList<string>? transits = null)
    {
        var previous = raidStateService.Current;
        var current = raidStateService.ApplyExtracts(extracts, observedUtc, raidClock, linesNotMatched, transits);
        Publish(current);
        await EnsureStartedAsync(previous, current, alreadyRecorded: false, cancellationToken).ConfigureAwait(false);
        if (current.RaidId is { } raidId)
        {
            await raidHistoryService.RecordEventAsync(
                raidId,
                "extracts",
                observedUtc,
                JsonSerializer.Serialize(extracts),
                cancellationToken).ConfigureAwait(false);
        }

        return current;
    }

    public Task RecordScanAsync(ScanExecutionResult result, CancellationToken cancellationToken)
    {
        var raidId = raidStateService.Current.RaidId;
        return raidId is null
            ? Task.CompletedTask
            : raidHistoryService.RecordEventAsync(
                raidId.Value,
                "scan",
                result.ObservedUtc,
                JsonSerializer.Serialize(result),
                cancellationToken);
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
        return raidStateService.Current.RaidId is not { } raidId
            ? Task.CompletedTask
            : raidHistoryService.RecordEventAsync(
                raidId,
                "sale",
                sale.ObservedUtc,
                JsonSerializer.Serialize(sale),
                cancellationToken);
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
        return raidStateService.Current.RaidId is not { } raidId
            ? Task.CompletedTask
            : raidHistoryService.RecordEventAsync(
                raidId,
                "quest",
                quest.ObservedUtc,
                JsonSerializer.Serialize(quest),
                cancellationToken);
    }

    private async Task PersistTransitionAsync(
        RaidSnapshot previous,
        RaidSnapshot current,
        RaidEvidence evidence,
        bool alreadyRecorded,
        CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(previous, current, alreadyRecorded, cancellationToken).ConfigureAwait(false);
        if (current.RaidId is { } raidId)
        {
            await raidHistoryService.RecordEventAsync(
                raidId,
                "state",
                evidence.ObservedUtc,
                JsonSerializer.Serialize(evidence),
                cancellationToken).ConfigureAwait(false);
        }

        if (previous.RaidId is { } previousRaidId
            && previous.State == RaidLifecycleState.InRaid
            && current.State is RaidLifecycleState.Menu or RaidLifecycleState.PostRaid)
        {
            await raidHistoryService.EndAsync(previousRaidId, evidence.ObservedUtc, null, null, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <param name="alreadyRecorded">
    /// Set when the raid was adopted from the record rather than begun, so its row exists.
    /// Inserting it again is a primary key violation, and the identity being new to this
    /// process is exactly what adoption makes untrue.
    /// </param>
    private async Task EnsureStartedAsync(
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
        await raidHistoryService.StartAsync(
            new(raidId, profile.Id, current.MapId, profile.GameMode.ToString(), current.StartedUtc, null, null, null),
            cancellationToken).ConfigureAwait(false);
    }

    private void Publish(RaidSnapshot snapshot) =>
        stateStore.Update(current => current with { Raid = snapshot });
}
