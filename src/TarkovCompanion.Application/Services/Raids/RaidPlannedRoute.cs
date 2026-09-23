using System.Text.Json;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>A route the player explicitly chose on the Raid page.</summary>
/// <remarks>
/// Stored as an append-only raid event so a finished raid keeps the plan it was played against.
/// Points are world positions rather than pixels or one artwork variant's plan coordinates: that
/// lets Debrief draw the plan through whichever reviewed variant is current when the raid is
/// replayed, and compare it with screenshot positions in the same coordinate space.
/// </remarks>
public sealed record RaidPlannedRoute(
    string MapId,
    string Extract,
    DateTimeOffset PlannedUtc,
    IReadOnlyList<WorldPosition> Points)
{
    public const string EventType = "planned-route";
    private const int MaximumTextLength = 128;
    private const int MaximumPoints = 512;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ToPayload()
    {
        if (!IsValid(this))
        {
            throw new ArgumentException("A planned route needs a bounded map, extract, UTC time, and finite points.");
        }

        return JsonSerializer.Serialize(this, JsonOptions);
    }

    /// <summary>The newest valid plan, because choosing another extract supersedes the old plan.</summary>
    public static RaidPlannedRoute? Latest(IEnumerable<string> payloads)
    {
        RaidPlannedRoute? latest = null;
        foreach (var payload in payloads)
        {
            try
            {
                var route = JsonSerializer.Deserialize<RaidPlannedRoute>(payload, JsonOptions);
                if (route is not null && IsValid(route) && (latest is null || route.PlannedUtc >= latest.PlannedUtc))
                {
                    latest = route;
                }
            }
            catch (JsonException)
            {
                // One damaged historical event cannot hide another usable plan.
            }
        }

        return latest;
    }

    private static bool IsValid(RaidPlannedRoute route) =>
        IsBoundedText(route.MapId)
        && IsBoundedText(route.Extract)
        && route.PlannedUtc != default
        && route.PlannedUtc.Offset == TimeSpan.Zero
        && route.Points is { Count: >= 2 and <= MaximumPoints }
        && route.Points.All(point => IsFinite(point.X) && IsFinite(point.Y) && IsFinite(point.Z));

    private static bool IsBoundedText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumTextLength && !value.Any(char.IsControl);

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

/// <summary>How far screenshot observations fell from the selected plan, in world metres.</summary>
public sealed record RaidRouteDeviation(double AverageMetres, double FurthestMetres, int ObservationCount)
{
    public static RaidRouteDeviation? Measure(
        IReadOnlyList<WorldPosition> planned,
        IReadOnlyList<ScreenshotPosition> observed)
    {
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(observed);
        if (planned.Count < 2 || observed.Count == 0)
        {
            return null;
        }

        var distances = observed
            .Select(position => planned
                .Zip(planned.Skip(1), (start, end) => DistanceToSegment(position.Position, start, end))
                .Min())
            .ToArray();
        return new(distances.Average(), distances.Max(), distances.Length);
    }

    private static double DistanceToSegment(WorldPosition point, WorldPosition start, WorldPosition end)
    {
        var dx = end.X - start.X;
        var dz = end.Z - start.Z;
        var lengthSquared = (dx * dx) + (dz * dz);
        if (lengthSquared <= double.Epsilon)
        {
            return Math.Sqrt(Math.Pow(point.X - start.X, 2) + Math.Pow(point.Z - start.Z, 2));
        }

        var t = Math.Clamp(((point.X - start.X) * dx + (point.Z - start.Z) * dz) / lengthSquared, 0, 1);
        var nearestX = start.X + (t * dx);
        var nearestZ = start.Z + (t * dz);
        return Math.Sqrt(Math.Pow(point.X - nearestX, 2) + Math.Pow(point.Z - nearestZ, 2));
    }
}
