using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Strategy;

namespace TarkovCompanion.Application.Services.Strategy;

public sealed record RiskPanelArea(string ZoneId, string Name, double PredictedScore, string RiskBand, IReadOnlyList<string> Factors);

public sealed record RiskPanelData(
    RaidPhase Phase,
    string CurrentAreaRisk,
    IReadOnlyList<RiskPanelArea> Areas,
    DateTimeOffset CalculatedUtc,
    string Disclaimer);

public static class TrafficRiskPanelService
{
    public static RiskPanelData Create(TrafficPrediction prediction, IReadOnlyList<StrategyZone> zones)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        ArgumentNullException.ThrowIfNull(zones);
        if (prediction.Samples.Count != zones.Count)
        {
            throw new ArgumentException("Traffic samples must correspond one-to-one with zones.", nameof(zones));
        }

        var areas = zones.Zip(prediction.Samples)
            .Select(pair => new RiskPanelArea(
                pair.First.Id,
                pair.First.Name,
                pair.Second.Score,
                RiskBand(pair.Second.Score),
                pair.Second.Factors))
            .OrderByDescending(area => area.PredictedScore)
            .ToArray();
        return new(
            prediction.Phase,
            prediction.CurrentAreaRisk,
            areas,
            prediction.CalculatedUtc,
            prediction.Disclaimer);
    }

    private static string RiskBand(double score) => score switch
    {
        >= 0.60 => "High",
        >= 0.25 => "Moderate",
        _ => "Low",
    };
}

public sealed record PredictedRotation(
    RaidPhase Phase,
    string FromZoneId,
    string ToZoneId,
    double RelativeStrength,
    string Rationale);

public sealed record PredictedRotationFlow(IReadOnlyList<PredictedRotation> Rotations, string Disclaimer);

public static class RotationFlowService
{
    public static PredictedRotationFlow Build(IReadOnlyList<StrategyZone> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        var spawns = Top(zones, zone => zone.SpawnWeight);
        var midpoints = Top(zones, zone => Math.Max(zone.PoiWeight, zone.ChokepointWeight));
        var extracts = Top(zones, zone => zone.ExtractWeight);
        var rotations = new List<PredictedRotation>();
        AddRotations(rotations, RaidPhase.Early, spawns, midpoints, "Generic spawn-to-objective tendency.");
        AddRotations(rotations, RaidPhase.Mid, midpoints, extracts, "Generic objective-to-extract tendency as the raid advances.");
        AddRotations(rotations, RaidPhase.Late, midpoints, extracts, "Late extract attraction; this is not detected movement.");
        return new(rotations, StrategyModel.NonLiveDisclaimer);
    }

    private static StrategyZone[] Top(IReadOnlyList<StrategyZone> zones, Func<StrategyZone, double> selector) =>
        zones.Where(zone => selector(zone) > 0)
            .OrderByDescending(selector)
            .Take(3)
            .ToArray();

    private static void AddRotations(
        ICollection<PredictedRotation> output,
        RaidPhase phase,
        IReadOnlyList<StrategyZone> from,
        IReadOnlyList<StrategyZone> to,
        string rationale)
    {
        foreach (var source in from)
        {
            var target = to
                .Where(candidate => candidate.Id != source.Id)
                .MinBy(candidate => Distance(source.Position, candidate.Position));
            if (target is null)
            {
                continue;
            }

            output.Add(new(
                phase,
                source.Id,
                target.Id,
                1 / (1 + Distance(source.Position, target.Position)),
                rationale));
        }
    }

    private static double Distance(MapPoint left, MapPoint right) =>
        Math.Sqrt(Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));
}
