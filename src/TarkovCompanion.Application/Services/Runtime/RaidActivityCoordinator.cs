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
    IRuntimeStateStore stateStore) : IRaidActivityRecorder
{
    /// <inheritdoc />
    public RaidSnapshot Current => raidStateService.Current;

    public async Task<RaidSnapshot> ApplyEvidenceAsync(RaidEvidence evidence, CancellationToken cancellationToken)
    {
        var previous = raidStateService.Current;
        var current = raidStateService.Apply(evidence);
        Publish(current);
        await PersistTransitionAsync(previous, current, evidence, cancellationToken).ConfigureAwait(false);
        return current;
    }

    public async Task<RaidSnapshot> ApplyPositionAsync(ScreenshotPosition position, CancellationToken cancellationToken)
    {
        var previous = raidStateService.Current;
        var current = raidStateService.ApplyPosition(position);
        Publish(current);
        await EnsureStartedAsync(previous, current, cancellationToken).ConfigureAwait(false);
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
        await EnsureStartedAsync(previous, current, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(previous, current, cancellationToken).ConfigureAwait(false);
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

    private async Task EnsureStartedAsync(
        RaidSnapshot previous,
        RaidSnapshot current,
        CancellationToken cancellationToken)
    {
        if (current.RaidId is not { } raidId || previous.RaidId == raidId)
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
