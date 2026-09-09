using System.Text.Json;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Runtime;

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
        await PersistTransitionAsync(previous, current, evidence, cancellationToken).ConfigureAwait(false);
        Publish(current);
        return current;
    }

    public async Task<RaidSnapshot> ApplyPositionAsync(ScreenshotPosition position, CancellationToken cancellationToken)
    {
        var previous = raidStateService.Current;
        var current = raidStateService.ApplyPosition(position);
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

        Publish(current);
        return current;
    }

    public async Task<RaidSnapshot> ApplyExtractsAsync(
        IReadOnlyList<ActiveExtract> extracts,
        DateTimeOffset observedUtc,
        CancellationToken cancellationToken)
    {
        var previous = raidStateService.Current;
        var current = raidStateService.ApplyExtracts(extracts, observedUtc);
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

        Publish(current);
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
            && current.RaidId != previousRaidId
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
