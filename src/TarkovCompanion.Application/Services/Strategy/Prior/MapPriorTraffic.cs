using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Strategy;

namespace TarkovCompanion.Application.Services.Strategy.Prior;

/// <summary>What on a map a structural traffic prior is allowed to be built from.</summary>
public enum TrafficPriorSourceKind
{
    PlayerSpawn,
    BossArea,
    Extract,
    HighValueLoot,
    NamedPlace,
    Crossing,
}

/// <summary>One catalog fact with a place on the plan, in plan units.</summary>
public sealed record TrafficPriorSource(TrafficPriorSourceKind Kind, MapPoint Position, double Weight, string Name);

/// <summary>Everything a prior is built from. Nothing here is observed about another player.</summary>
/// <param name="UnitsPerMetre">How many plan units one metre of the game world covers.</param>
/// <param name="OwnRaidTrails">The player's own screenshot trails on this map, one list per raid.</param>
/// <param name="DataThroughUtc">When the catalog the sources came from was last refreshed.</param>
public sealed record TrafficPriorInputs(
    string MapId,
    MapPoint Minimum,
    MapPoint Maximum,
    double UnitsPerMetre,
    IReadOnlyList<TrafficPriorSource> Sources,
    IReadOnlyList<IReadOnlyList<MapPoint>> OwnRaidTrails,
    DateTimeOffset? DataThroughUtc);

/// <summary>What the prior was built from, in counts a player can check against the map.</summary>
public sealed record TrafficPriorBasis(
    int PlayerSpawns,
    int Extracts,
    int LootSpawns,
    int BossAreas,
    int NamedPlaces,
    int OwnRaids,
    double OwnRaidShare,
    DateTimeOffset? DataThroughUtc,
    DateTimeOffset GeneratedUtc)
{
    /// <summary>The kinds of fact that say where players start, go and leave.</summary>
    public const int StructuralKinds = 4;

    public int StructuralKindsPresent =>
        (PlayerSpawns > 0 ? 1 : 0) + (Extracts > 0 ? 1 : 0) + (LootSpawns > 0 ? 1 : 0) + (BossAreas > 0 ? 1 : 0);

    /// <summary>Place names alone say nothing about movement, so they are not a basis.</summary>
    public bool IsEmpty => StructuralKindsPresent == 0 && OwnRaids == 0;
}

/// <summary>A place the modelled field peaks, and which facts make it peak there.</summary>
/// <param name="Name">The nearest named place or crossing, or null where none is near; the App says "Unnamed area".</param>
public sealed record TrafficHotspot(
    MapPoint Position,
    double Intensity,
    string? Name,
    IReadOnlyList<TrafficDriver> Drivers,
    double RadiusUnits);

/// <summary>A fact the modelled field is built from; the App names each ("player spawns", "your raids").</summary>
[PhraseCodes("Raid.Traffic.Driver")]
public enum TrafficDriver
{
    PlayerSpawns,
    HighValueLoot,
    BossAreas,
    NamedPlaces,
    Crossings,
    Extracts,
    LinesFromSpawns,
    LinesToExtracts,
    YourRaids,
}

/// <summary>A traffic estimate for one map and raid phase, built only from map structure.</summary>
/// <remarks>
/// The governed snapshot pipeline (<c>TrafficSnapshotStore</c>) describes recorded raids: every
/// record in it needs a sample count and historical-aggregate provenance, and it names regions
/// without giving them a place. The project has no recorded raids, so nothing honest can be put
/// through it, and nothing it holds could be drawn as a field anyway. This is the other source
/// class the epic allows: a prior from where players start, what draws them and where they leave,
/// which is already on the machine as soon as the catalog is. It says so wherever it is shown.
/// </remarks>
public sealed record MapPriorTraffic(
    string MapId,
    RaidPhase Phase,
    TrafficField? Field,
    TrafficPriorBasis Basis,
    IReadOnlyList<TrafficHotspot> Hotspots,
    Confidence Confidence)
{
    public const string ModelVersion = "map-prior-1";

    public bool HasField => Field is not null;
}

/// <summary>Builds <see cref="MapPriorTraffic"/>. The phase weighting is V1's <see cref="IStrategyModel"/>.</summary>
/// <remarks>
/// Each kind of fact becomes its own field, normalised on its own, because the counts are not
/// comparable: Customs has a dozen player spawn areas and several hundred loot points, and summed
/// raw the loot would be the whole picture. How much each kind counts at a point in the raid is
/// asked of the strategy model V1 shipped (spawn influence decays, extract attraction grows), so
/// there is one statement of that assumption in the product rather than two.
/// </remarks>
public sealed class MapPriorTrafficModel
{
    /// <summary>Cells along the plan's longer edge.</summary>
    public const int LongEdgeCells = 64;

    /// <summary>The most the player's own raids can weigh, reached at <see cref="OwnRaidsForFullShare"/>.</summary>
    public const double MaximumOwnRaidShare = 0.35;

    public const int OwnRaidsForFullShare = 14;

    private const double HotspotThreshold = 0.5;
    private const double HotspotSeparationMetres = 110;
    private const int MaximumHotspots = 6;
    private const double HotspotNameReachMetres = 130;
    private const double AttractorSeparationMetres = 140;
    private const int MaximumAttractors = 6;
    private const double MovementRadiusMetres = 22;

    // A boss is not there every raid and a place name is only a name: both draw less than loot.
    private const double BossShare = 0.5;
    private const double PlaceShare = 0.35;

    private static readonly TimeSpan NominalRaid = TimeSpan.FromMinutes(40);

    private readonly IStrategyModel _strategy;
    private readonly TimeProvider _timeProvider;

    public MapPriorTrafficModel(IStrategyModel strategy, TimeProvider? timeProvider = null)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public MapPriorTraffic Build(TrafficPriorInputs inputs, RaidPhase phase)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var nowUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var sources = inputs.Sources
            .Where(source => double.IsFinite(source.Position.X) && double.IsFinite(source.Position.Y) && source.Weight > 0)
            .ToArray();
        var trails = inputs.OwnRaidTrails.Where(trail => trail.Count > 1).ToArray();
        var ownShare = Math.Min(MaximumOwnRaidShare, MaximumOwnRaidShare * trails.Length / OwnRaidsForFullShare);
        var basis = new TrafficPriorBasis(
            sources.Count(source => source.Kind == TrafficPriorSourceKind.PlayerSpawn),
            sources.Count(source => source.Kind == TrafficPriorSourceKind.Extract),
            sources.Count(source => source.Kind == TrafficPriorSourceKind.HighValueLoot),
            sources.Count(source => source.Kind == TrafficPriorSourceKind.BossArea),
            sources.Count(source => source.Kind is TrafficPriorSourceKind.NamedPlace or TrafficPriorSourceKind.Crossing),
            trails.Length,
            ownShare,
            inputs.DataThroughUtc,
            nowUtc);

        var width = inputs.Maximum.X - inputs.Minimum.X;
        var height = inputs.Maximum.Y - inputs.Minimum.Y;
        if (basis.IsEmpty || !(width > 0) || !(height > 0) || !(inputs.UnitsPerMetre > 0) || !double.IsFinite(inputs.UnitsPerMetre))
        {
            return new(inputs.MapId, phase, null, basis, [], Confidence.Unknown);
        }

        var cell = Math.Max(width, height) / LongEdgeCells;
        var columns = Math.Max(1, (int)Math.Ceiling(width / cell));
        var rows = Math.Max(1, (int)Math.Ceiling(height / cell));
        var grid = new GridShape(inputs.Minimum.X, inputs.Minimum.Y, cell, columns, rows);

        var multipliers = PhaseMultipliers(phase, nowUtc);
        var loot = Splat(grid, sources, inputs.UnitsPerMetre, TrafficPriorSourceKind.HighValueLoot);
        var boss = Splat(grid, sources, inputs.UnitsPerMetre, TrafficPriorSourceKind.BossArea);
        var places = Splat(grid, sources, inputs.UnitsPerMetre, TrafficPriorSourceKind.NamedPlace);
        var (arriving, leaving) = Movement(grid, sources, [loot, boss, places], inputs.UnitsPerMetre);
        var layers = new (TrafficDriver Driver, double Multiplier, double[] Values)[]
        {
            (TrafficDriver.PlayerSpawns, multipliers.Spawn, Splat(grid, sources, inputs.UnitsPerMetre, TrafficPriorSourceKind.PlayerSpawn)),
            (TrafficDriver.HighValueLoot, multipliers.Poi, loot),
            (TrafficDriver.BossAreas, multipliers.Poi * BossShare, boss),
            (TrafficDriver.NamedPlaces, multipliers.Poi * PlaceShare, places),
            (TrafficDriver.Crossings, multipliers.Choke * 0.6, Splat(grid, sources, inputs.UnitsPerMetre, TrafficPriorSourceKind.Crossing)),
            (TrafficDriver.Extracts, multipliers.Extract * 0.8, Splat(grid, sources, inputs.UnitsPerMetre, TrafficPriorSourceKind.Extract)),
            (TrafficDriver.LinesFromSpawns, (multipliers.Spawn + multipliers.Poi) / 2, arriving),
            (TrafficDriver.LinesToExtracts, (multipliers.Poi + multipliers.Extract) / 2, leaving),
        };

        var combined = new double[columns * rows];
        foreach (var layer in layers)
        {
            for (var index = 0; index < combined.Length; index++)
            {
                combined[index] += layer.Multiplier * layer.Values[index];
            }
        }

        Normalise(combined);
        var own = OwnRaidField(grid, trails);
        if (ownShare > 0)
        {
            for (var index = 0; index < combined.Length; index++)
            {
                combined[index] = ((1 - ownShare) * combined[index]) + (ownShare * own[index]);
            }

            Normalise(combined);
        }

        if (combined.All(value => value <= 0))
        {
            return new(inputs.MapId, phase, null, basis, [], Confidence.Unknown);
        }

        var field = new TrafficField(grid.MinimumX, grid.MinimumY, cell, columns, rows, combined) { Width = width, Height = height };
        var drivers = layers.ToList();
        if (ownShare > 0)
        {
            drivers.Add((TrafficDriver.YourRaids, ownShare * 2, own));
        }

        var hotspots = Hotspots(field, grid, drivers, sources, inputs.UnitsPerMetre);

        // A prior is never better than "low": nothing here has been checked against a raid. What
        // moves it is how much of the structure the catalog actually had, and the player's own raids.
        var confidence = new Confidence(Math.Round(
            0.15 + (0.05 * basis.StructuralKindsPresent) + (0.1 * ownShare / MaximumOwnRaidShare), 2));
        return new(inputs.MapId, phase, field, basis, hotspots, confidence);
    }

    /// <summary>How much each kind of fact counts in this phase, as V1's strategy model has it.</summary>
    private (double Spawn, double Poi, double Choke, double Extract) PhaseMultipliers(RaidPhase phase, DateTimeOffset nowUtc)
    {
        var elapsed = phase switch
        {
            RaidPhase.Early => NominalRaid / 6,
            RaidPhase.Mid => NominalRaid / 2,
            _ => NominalRaid * 5 / 6,
        };
        var provenance = new DataProvenance("map-catalog", nowUtc);
        StrategyZone Unit(string id, double spawn = 0, double poi = 0, double choke = 0, double extract = 0) =>
            new(id, id, default, 1, spawn, poi, choke, 0, extract, provenance);
        var prediction = _strategy.Predict(
            elapsed,
            NominalRaid,
            [Unit("spawn", spawn: 1), Unit("poi", poi: 1), Unit("choke", choke: 1), Unit("extract", extract: 1)],
            null,
            nowUtc);
        return (prediction.Samples[0].Score, prediction.Samples[1].Score, prediction.Samples[2].Score, prediction.Samples[3].Score);
    }

    /// <summary>
    /// Straight lines from every player spawn to the places that draw players, and from those to
    /// the extracts: where the structure says paths converge.
    /// </summary>
    /// <remarks>
    /// Straight, because the catalog has no roads, walls or water to bend them round; it is said
    /// as "lines", not routes. Each spawn is joined to its two nearest draws and each draw to its
    /// two nearest extracts, which is what makes a crossing between several of them busier than
    /// any one of them alone.
    /// </remarks>
    private static (double[] Arriving, double[] Leaving) Movement(
        GridShape grid,
        IReadOnlyList<TrafficPriorSource> sources,
        IReadOnlyList<double[]> drawLayers,
        double unitsPerMetre)
    {
        var draws = new double[grid.Columns * grid.Rows];
        double[] shares = [1, BossShare, PlaceShare];
        for (var layer = 0; layer < drawLayers.Count; layer++)
        {
            for (var index = 0; index < draws.Length; index++)
            {
                draws[index] += shares[layer] * drawLayers[layer][index];
            }
        }

        Normalise(draws);
        var attractors = Peaks(draws, grid, 0.3, AttractorSeparationMetres * unitsPerMetre, MaximumAttractors)
            .Select(index => grid.Centre(index % grid.Columns, index / grid.Columns))
            .ToArray();
        var arriving = new double[draws.Length];
        var leaving = new double[draws.Length];
        var sigma = MovementRadiusMetres * unitsPerMetre;
        foreach (var spawn in sources.Where(source => source.Kind == TrafficPriorSourceKind.PlayerSpawn))
        {
            foreach (var attractor in attractors.OrderBy(point => Squared(point, spawn.Position)).Take(2))
            {
                SplatLine(grid, arriving, spawn.Position, attractor, spawn.Weight, sigma);
            }
        }

        var extracts = sources.Where(source => source.Kind == TrafficPriorSourceKind.Extract).ToArray();
        foreach (var attractor in attractors)
        {
            foreach (var extract in extracts.OrderBy(source => Squared(source.Position, attractor)).Take(2))
            {
                SplatLine(grid, leaving, attractor, extract.Position, extract.Weight, sigma);
            }
        }

        // Every line to a draw ends on it, so summed raw the draw itself would be the only thing
        // left after normalising. The root keeps the lines visible beside the point they meet at.
        Compress(arriving, 0.5);
        Compress(leaving, 0.5);
        return (arriving, leaving);
    }

    private static void SplatLine(GridShape grid, double[] values, MapPoint from, MapPoint to, double weight, double sigma)
    {
        var length = Math.Sqrt(Squared(from, to));
        var steps = Math.Max(1, (int)Math.Ceiling(length / grid.Cell));
        var reach = (int)Math.Ceiling(2.5 * sigma / grid.Cell);
        for (var step = 0; step <= steps; step++)
        {
            var point = new MapPoint(from.X + ((to.X - from.X) * step / steps), from.Y + ((to.Y - from.Y) * step / steps));
            var (column, row) = grid.CellOf(point);
            for (var y = Math.Max(0, row - reach); y <= Math.Min(grid.Rows - 1, row + reach); y++)
            {
                for (var x = Math.Max(0, column - reach); x <= Math.Min(grid.Columns - 1, column + reach); x++)
                {
                    values[(y * grid.Columns) + x] += Math.Min(weight, 1) *
                        Math.Exp(-Squared(grid.Centre(x, y), point) / (2 * sigma * sigma));
                }
            }
        }
    }

    /// <summary>The highest cells, highest first, none closer to another than <paramref name="separation"/>.</summary>
    private static IReadOnlyList<int> Peaks(IReadOnlyList<double> values, GridShape grid, double threshold, double separation, int maximum)
    {
        var chosen = new List<int>();
        foreach (var index in Enumerable.Range(0, values.Count)
                     .Where(index => values[index] >= threshold)
                     .OrderByDescending(index => values[index])
                     .ThenBy(index => index))
        {
            var centre = grid.Centre(index % grid.Columns, index / grid.Columns);
            if (chosen.Any(existing =>
                    Squared(grid.Centre(existing % grid.Columns, existing / grid.Columns), centre) < separation * separation))
            {
                continue;
            }

            chosen.Add(index);
            if (chosen.Count == maximum)
            {
                break;
            }
        }

        return chosen;
    }

    private static double RadiusMetres(TrafficPriorSourceKind kind) => kind switch
    {
        TrafficPriorSourceKind.PlayerSpawn => 50,
        TrafficPriorSourceKind.BossArea => 60,
        TrafficPriorSourceKind.Extract => 55,
        TrafficPriorSourceKind.NamedPlace => 45,
        TrafficPriorSourceKind.Crossing => 40,
        _ => 32,
    };

    private static double[] Splat(GridShape grid, IReadOnlyList<TrafficPriorSource> sources, double unitsPerMetre, TrafficPriorSourceKind kind)
    {
        var values = new double[grid.Columns * grid.Rows];
        var sigma = RadiusMetres(kind) * unitsPerMetre;
        var reach = (int)Math.Ceiling(3 * sigma / grid.Cell);
        foreach (var source in sources)
        {
            if (source.Kind != kind)
            {
                continue;
            }

            var (column, row) = grid.CellOf(source.Position);
            for (var y = Math.Max(0, row - reach); y <= Math.Min(grid.Rows - 1, row + reach); y++)
            {
                for (var x = Math.Max(0, column - reach); x <= Math.Min(grid.Columns - 1, column + reach); x++)
                {
                    var centre = grid.Centre(x, y);
                    var squared = Squared(centre, source.Position);
                    values[(y * grid.Columns) + x] += Math.Min(source.Weight, 1) * Math.Exp(-squared / (2 * sigma * sigma));
                }
            }
        }

        Normalise(values);
        return values;
    }

    /// <summary>The share of the player's own raids that passed through each cell.</summary>
    private static double[] OwnRaidField(GridShape grid, IReadOnlyList<IReadOnlyList<MapPoint>> trails)
    {
        var values = new double[grid.Columns * grid.Rows];
        if (trails.Count == 0)
        {
            return values;
        }

        var visited = new HashSet<int>();
        foreach (var trail in trails)
        {
            visited.Clear();
            for (var index = 1; index < trail.Count; index++)
            {
                var from = trail[index - 1];
                var to = trail[index];
                var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Squared(from, to)) / (grid.Cell / 2)));
                for (var step = 0; step <= steps; step++)
                {
                    var point = new MapPoint(from.X + ((to.X - from.X) * step / steps), from.Y + ((to.Y - from.Y) * step / steps));
                    if (grid.Contains(point))
                    {
                        var (column, row) = grid.CellOf(point);
                        visited.Add((row * grid.Columns) + column);
                    }
                }
            }

            foreach (var cell in visited)
            {
                values[cell] += 1d / trails.Count;
            }
        }

        // One pass of a 3x3 mean: a screenshot trail is a thin line through a cell-wide corridor.
        var smoothed = new double[values.Length];
        for (var row = 0; row < grid.Rows; row++)
        {
            for (var column = 0; column < grid.Columns; column++)
            {
                double sum = 0;
                var count = 0;
                for (var y = Math.Max(0, row - 1); y <= Math.Min(grid.Rows - 1, row + 1); y++)
                {
                    for (var x = Math.Max(0, column - 1); x <= Math.Min(grid.Columns - 1, column + 1); x++)
                    {
                        sum += values[(y * grid.Columns) + x];
                        count++;
                    }
                }

                smoothed[(row * grid.Columns) + column] = sum / count;
            }
        }

        Normalise(smoothed);
        return smoothed;
    }

    private static IReadOnlyList<TrafficHotspot> Hotspots(
        TrafficField field,
        GridShape grid,
        IReadOnlyList<(TrafficDriver Driver, double Multiplier, double[] Values)> drivers,
        IReadOnlyList<TrafficPriorSource> sources,
        double unitsPerMetre)
    {
        var separation = HotspotSeparationMetres * unitsPerMetre;
        var reach = HotspotNameReachMetres * unitsPerMetre;
        var named = sources
            .Where(source => source.Kind is not (TrafficPriorSourceKind.HighValueLoot or TrafficPriorSourceKind.PlayerSpawn) &&
                !string.IsNullOrWhiteSpace(source.Name))
            .ToArray();
        var chosen = new List<TrafficHotspot>();
        foreach (var index in Peaks(field.Values, grid, HotspotThreshold, separation, MaximumHotspots))
        {
            var centre = grid.Centre(index % grid.Columns, index / grid.Columns);

            var nearest = named
                .Select(source => (source, Distance: Math.Sqrt(Squared(source.Position, centre))))
                .Where(pair => pair.Distance <= reach)
                // A place name says where this is better than the extract that happens to be closer.
                .OrderBy(pair => pair.source.Kind is TrafficPriorSourceKind.NamedPlace or TrafficPriorSourceKind.Crossing ? 0 : 1)
                .ThenBy(pair => pair.Distance)
                .Select(pair => pair.source.Name)
                .FirstOrDefault();
            var contributions = drivers
                .Select(driver => (driver.Driver, Value: driver.Multiplier * driver.Values[index]))
                .Where(pair => pair.Value > 0)
                .OrderByDescending(pair => pair.Value)
                .ToArray();
            var total = contributions.Sum(pair => pair.Value);
            chosen.Add(new(
                centre,
                field.Values[index],
                nearest,
                [.. contributions.Where(pair => pair.Value >= total * 0.25).Take(3).Select(pair => pair.Driver)],
                separation / 2));
        }

        return chosen;
    }

    private static void Compress(double[] values, double exponent)
    {
        Normalise(values);
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = Math.Pow(values[index], exponent);
        }
    }

    private static void Normalise(double[] values)
    {
        var peak = values.Length == 0 ? 0 : values.Max();
        if (peak <= 0)
        {
            return;
        }

        for (var index = 0; index < values.Length; index++)
        {
            values[index] /= peak;
        }
    }

    private static double Squared(MapPoint a, MapPoint b) => ((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y));

    private readonly record struct GridShape(double MinimumX, double MinimumY, double Cell, int Columns, int Rows)
    {
        public bool Contains(MapPoint point) =>
            point.X >= MinimumX && point.Y >= MinimumY &&
            point.X < MinimumX + (Columns * Cell) && point.Y < MinimumY + (Rows * Cell);

        public (int Column, int Row) CellOf(MapPoint point) => (
            Math.Clamp((int)Math.Floor((point.X - MinimumX) / Cell), 0, Columns - 1),
            Math.Clamp((int)Math.Floor((point.Y - MinimumY) / Cell), 0, Rows - 1));

        public MapPoint Centre(int column, int row) => new(MinimumX + ((column + 0.5) * Cell), MinimumY + ((row + 0.5) * Cell));
    }
}

/// <summary>Relative modelled traffic over the plan, 0 to 1, in square cells of plan units.</summary>
public sealed class TrafficField
{
    public TrafficField(double minimumX, double minimumY, double cell, int columns, int rows, IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (columns < 1 || rows < 1 || !(cell > 0) || values.Count != columns * rows)
        {
            throw new ArgumentException("A traffic field needs a positive cell and one value per cell.", nameof(values));
        }

        MinimumX = minimumX;
        MinimumY = minimumY;
        Cell = cell;
        Columns = columns;
        Rows = rows;
        Values = values;
        Width = columns * cell;
        Height = rows * cell;
    }

    public double MinimumX { get; }

    public double MinimumY { get; }

    public double Cell { get; }

    public int Columns { get; }

    public int Rows { get; }

    public IReadOnlyList<double> Values { get; }

    /// <summary>
    /// How much of the grid the plan covers. The last row or column of square cells usually hangs
    /// over the plan's edge, and a picture of the field has to stop where the plan does.
    /// </summary>
    public double Width { get; init; }

    public double Height { get; init; }

    public double this[int column, int row] => Values[(row * Columns) + column];

    public (int Column, int Row) CellOf(MapPoint point) => (
        Math.Clamp((int)Math.Floor((point.X - MinimumX) / Cell), 0, Columns - 1),
        Math.Clamp((int)Math.Floor((point.Y - MinimumY) / Cell), 0, Rows - 1));

    public MapPoint Centre(int column, int row) => new(MinimumX + ((column + 0.5) * Cell), MinimumY + ((row + 0.5) * Cell));

    public double ValueAt(MapPoint point)
    {
        var (column, row) = CellOf(point);
        return this[column, row];
    }
}
