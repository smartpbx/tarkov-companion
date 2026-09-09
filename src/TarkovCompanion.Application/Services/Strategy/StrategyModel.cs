using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Strategy;

namespace TarkovCompanion.Application.Services.Strategy;

/// <summary>Produces educational traffic estimates from static map knowledge only.</summary>
public sealed class StrategyModel : IStrategyModel
{
    public const string NonLiveDisclaimer = "Predicted traffic based on map and game knowledge—not live player data.";

    public TrafficPrediction Predict(
        TimeSpan elapsed,
        TimeSpan raidDuration,
        IReadOnlyList<StrategyZone> zones,
        MapPoint? lastKnownPlayerPosition,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(zones);
        if (raidDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(raidDuration), "Raid duration must be positive.");
        }

        var progress = Math.Clamp(elapsed.TotalSeconds / raidDuration.TotalSeconds, 0, 1);
        var phase = progress switch
        {
            < 1d / 3d => RaidPhase.Early,
            < 2d / 3d => RaidPhase.Mid,
            _ => RaidPhase.Late,
        };
        var samples = zones.Select(zone => Score(zone, progress)).ToArray();
        var currentRisk = ClassifyCurrentRisk(zones, samples, lastKnownPlayerPosition);
        var notes = phase switch
        {
            RaidPhase.Early => new[]
            {
                "Spawn influence is strongest early and decays throughout the raid.",
                "Initial routes toward objectives and loot are generic expectations, not observations.",
            },
            RaidPhase.Mid => new[]
            {
                "Points of interest and chokepoints carry more weight in the middle phase.",
                "Actual players may take different routes or already have left the raid.",
            },
            _ => new[]
            {
                "Extract attraction increases late while spawn influence approaches zero.",
                "Treat highlighted rotations as planning context, never as detected movement.",
            },
        };

        return new TrafficPrediction(phase, samples, currentRisk, notes, nowUtc.ToUniversalTime(), NonLiveDisclaimer);
    }

    private static TrafficSample Score(StrategyZone zone, double progress)
    {
        var spawn = Weight(zone.SpawnWeight) * Math.Pow(1 - progress, 2);
        var poi = Weight(zone.PoiWeight) * (0.55 + (0.45 * (1 - Math.Abs(progress - 0.5) * 2)));
        var choke = Weight(zone.ChokepointWeight) * (0.70 + (0.30 * Math.Sin(Math.PI * progress)));
        var quest = Weight(zone.QuestWeight) * (1 - (0.35 * progress));
        var extract = Weight(zone.ExtractWeight) * Math.Pow(progress, 2);
        var score = Math.Clamp((spawn + poi + choke + quest + extract) / 5, 0, 1);
        var factors = new[]
        {
            $"spawn={spawn:F3}",
            $"poi={poi:F3}",
            $"chokepoint={choke:F3}",
            $"quest={quest:F3}",
            $"extract={extract:F3}",
        };
        return new TrafficSample(zone.Position, score, factors);
    }

    private static string ClassifyCurrentRisk(
        IReadOnlyList<StrategyZone> zones,
        IReadOnlyList<TrafficSample> samples,
        MapPoint? playerPosition)
    {
        if (playerPosition is null || zones.Count == 0)
        {
            return "Unknown — no recent player position is available.";
        }

        var estimatedRisk = zones.Zip(samples)
            .Max(pair => pair.Second.Score * DistanceInfluence(playerPosition.Value, pair.First));
        return estimatedRisk switch
        {
            >= 0.60 => "High predicted traffic",
            >= 0.25 => "Moderate predicted traffic",
            _ => "Low predicted traffic",
        };
    }

    private static double DistanceInfluence(MapPoint player, StrategyZone zone)
    {
        var distance = Math.Sqrt(
            Math.Pow(player.X - zone.Position.X, 2)
            + Math.Pow(player.Y - zone.Position.Y, 2));
        var radius = Math.Max(zone.Radius, 1);
        return Math.Exp(-distance / radius);
    }

    private static double Weight(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
