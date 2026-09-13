using System.Text.Json;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
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
public sealed class RaidActivityCoordinator(
    IRaidStateService raidStateService,
    IRaidHistoryService raidHistoryService,
    IPlayerProfileService profileService,
    IRuntimeStateStore stateStore)
{
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

    public async Task<RaidSnapshot> ApplyExtractsAsync(
        IReadOnlyList<ActiveExtract> extracts,
        DateTimeOffset observedUtc,
        CancellationToken cancellationToken)
    {
        var previous = raidStateService.Current;
        var current = raidStateService.ApplyExtracts(extracts, observedUtc);
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
