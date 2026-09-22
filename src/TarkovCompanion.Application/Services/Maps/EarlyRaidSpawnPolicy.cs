using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

public enum EarlyRaidSpawnPhase
{
    Unaffected,
    Active,
    Expired,
}

public sealed record EarlyRaidSpawnSelection(
    EarlyRaidSpawnPhase Phase,
    IReadOnlyList<NearbySpawn> Areas);

/// <summary>Limits PMC spawn context to nearby areas during the raid's opening five minutes.</summary>
/// <remarks>
/// The time comes from the same injected clock as the rest of the raid workspace. A scav run is
/// deliberately unaffected: it joins after the opening and the map already suppresses its spawn
/// layer. An unknown side is also unaffected rather than guessed to be PMC.
/// </remarks>
public sealed class EarlyRaidSpawnPolicy(TimeProvider timeProvider)
{
    public static readonly TimeSpan VisibleFor = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public EarlyRaidSpawnSelection Select(
        IReadOnlyList<MapFeature> features,
        WorldPosition? raidStart,
        WorldPosition? player,
        MapFeatureFaction side,
        DateTimeOffset? startedUtc)
    {
        ArgumentNullException.ThrowIfNull(features);
        var phase = Phase(side, startedUtc);
        return new(
            phase,
            phase == EarlyRaidSpawnPhase.Active && raidStart is { } start
                ? SpawnProximity.Near(features, start, player, side)
                : []);
    }

    /// <summary>Applies the time/side rule to areas the map has already grouped and measured.</summary>
    public EarlyRaidSpawnSelection Select(
        IReadOnlyList<NearbySpawn> nearbyAreas,
        MapFeatureFaction side,
        DateTimeOffset? startedUtc)
    {
        ArgumentNullException.ThrowIfNull(nearbyAreas);
        var phase = Phase(side, startedUtc);
        return new(phase, phase == EarlyRaidSpawnPhase.Active ? nearbyAreas : []);
    }

    private EarlyRaidSpawnPhase Phase(MapFeatureFaction side, DateTimeOffset? startedUtc)
    {
        if (side != MapFeatureFaction.Pmc || startedUtc is null)
        {
            return EarlyRaidSpawnPhase.Unaffected;
        }

        return _timeProvider.GetUtcNow() - startedUtc.Value < VisibleFor
            ? EarlyRaidSpawnPhase.Active
            : EarlyRaidSpawnPhase.Expired;
    }
}
