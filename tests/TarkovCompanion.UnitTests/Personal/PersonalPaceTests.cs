using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Personal;
using TarkovCompanion.Application.Services.Strategy.Prior;
using TarkovCompanion.Core.Domain.Maps;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.Personal;

public sealed class PersonalPaceTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Start = new(2026, 9, 23, 20, 0, 0, TimeSpan.Zero);

    /// <summary>A screenshot as the game names it: the minute of real time, and the in-game hour at seven times real.</summary>
    internal static ScreenshotPosition Shot(double realSeconds, double x, double z, double startHour = 12)
    {
        var real = Start.AddSeconds(realSeconds);
        var named = new DateTimeOffset(real.Year, real.Month, real.Day, real.Hour, real.Minute, 0, real.Offset);
        var hour = Math.Round(startHour + realSeconds * PersonalPace.InGameClockRate / 3600, 2) % 24;
        return new ScreenshotPosition(named, new WorldPosition(x, 0, z), new QuaternionOrientation(0, 0, 0, 1), 0,
            TimeSpan.FromSeconds(hour), null, "fixture.png");
    }

    [Fact]
    public void ElapsedReadsTheInGameClockAtSevenTimesRealTime()
    {
        // 45 real seconds inside one named minute: the names say nothing, the clock says 45 s.
        var seconds = PersonalPace.Elapsed(Shot(0, 0, 0), Shot(45, 0, 0));

        Assert.NotNull(seconds);
        Assert.InRange(seconds!.Value, 40, 50);
    }

    [Fact]
    public void ElapsedCrossesMidnightOnTheInGameClock()
    {
        var seconds = PersonalPace.Elapsed(Shot(0, 0, 0, startHour: 23.9), Shot(120, 0, 0, startHour: 23.9));

        Assert.NotNull(seconds);
        Assert.InRange(seconds!.Value, 110, 130);
    }

    [Fact]
    public void ElapsedRefusesAClockThatDisagreesWithTheNames()
    {
        var from = Shot(0, 0, 0);
        var to = Shot(600, 0, 0) with { InGameTime = TimeSpan.FromSeconds(3) };

        Assert.Null(PersonalPace.Elapsed(from, to));
    }

    [Fact]
    public void LegsKeepOnlyMovingStretches()
    {
        var trail = new[]
        {
            Shot(0, 0, 0),
            Shot(60, 90, 0), // 90 m in 60 s: moving
            Shot(300, 100, 0), // 10 m in 4 min: looting, too short
            Shot(420, 130, 0), // 30 m in 2 min: 0.25 m/s, standing about
            Shot(430, 200, 0), // 10 s: too quick to time
        };

        var legs = PersonalPace.Legs(trail);

        var leg = Assert.Single(legs);
        Assert.InRange(leg.Metres, 89.9, 90.1);
        Assert.InRange(leg.Seconds, 55, 65);
    }

    [Fact]
    public void TooFewLegsFallBackToTheFixedPace()
    {
        var oneRaid = Enumerable.Range(0, 12).Select(i => Shot(i * 60, i * 100, 0)).ToArray();

        Assert.Null(PersonalPace.Measure([oneRaid]));
        var minutes = PersonalPace.MinutesFor(600, null);
        Assert.False(minutes.IsPersonal);
        Assert.Equal(TrafficRoute.MinutesFor(600), (minutes.Low, minutes.High));
    }

    [Fact]
    public void MeasuredPaceIsThePlayersOwnMedianAndRange()
    {
        // Two raids at 1.2 m/s, one stretch quicker and one slower in each.
        IReadOnlyList<ScreenshotPosition> Raid(double startHour) =>
        [
            Shot(0, 0, 0, startHour), Shot(100, 120, 0, startHour), Shot(200, 240, 0, startHour),
            Shot(300, 360, 0, startHour), Shot(400, 480, 0, startHour), Shot(460, 600, 0, startHour),
            Shot(660, 720, 0, startHour),
        ];

        var pace = PersonalPace.Measure([Raid(10), Raid(15)]);

        Assert.NotNull(pace);
        Assert.Equal(2, pace!.Raids);
        Assert.Equal(12, pace.Legs);
        Assert.InRange(pace.MetresPerSecond, 1.1, 1.3);
        Assert.True(pace.FastSecondsPerMetre <= pace.SecondsPerMetre && pace.SecondsPerMetre <= pace.SlowSecondsPerMetre);
        var minutes = PersonalPace.MinutesFor(600, pace);
        Assert.True(minutes.IsPersonal);
        Assert.True(minutes.Low < minutes.High);
    }

    /// <summary>
    /// FIXTURE, not real data: a player who moves at 1.2 m/s in a straight line. The fixed careful
    /// pace (1.8 m/s with a quarter for obstacles, 1.44 m/s in effect) is off by a sixth for them;
    /// the measured pace, taken from their earlier raids, is not.
    /// </summary>
    [Fact]
    public void OnAFixtureTheMeasuredPaceBeatsTheFixedPace()
    {
        var random = new Random(7);
        IReadOnlyList<ScreenshotPosition> Raid(int index)
        {
            var shots = new List<ScreenshotPosition> { Shot(0, 0, 0, 8 + index) };
            double t = 0, x = 0;
            for (var leg = 0; leg < 6; leg++)
            {
                var metres = 60 + random.NextDouble() * 140;
                t += metres / (1.2 * (0.85 + random.NextDouble() * 0.3));
                x += metres;
                shots.Add(Shot(t, x, 0, 8 + index));
            }

            return shots;
        }

        var raids = Enumerable.Range(0, 6).Select(Raid).ToArray();
        var pace = PersonalPace.Measure(raids.Take(3).ToArray());
        var held = raids.Skip(3).SelectMany((trail, index) => PersonalPace.Legs(trail, index)).ToArray();

        var personal = held.Average(leg => Math.Abs(PersonalPace.Seconds(leg.Metres, pace) - leg.Seconds));
        var fixedPace = held.Average(leg => Math.Abs(PersonalPace.Seconds(leg.Metres, null) - leg.Seconds));
        output.WriteLine($"[pace-fixture] {held.Length} legs: personal MAE {personal:0.0} s, fixed MAE {fixedPace:0.0} s");
        Assert.True(personal < fixedPace);
    }

    /// <summary>
    /// The acceptance measured on real screenshots of the player's own raids, when present
    /// (<c>TARKOV_REAL_TRAILS</c>, a Screenshots folder; skips without it). File times are the truth;
    /// the estimators only see what the app stores: the name's minute and the in-game clock.
    /// Each raid is estimated from the raids before it only.
    /// </summary>
    [Fact]
    public void ReportsWalkTimeErrorOnRealTrails()
    {
        var directory = Environment.GetEnvironmentVariable("TARKOV_REAL_TRAILS");
        if (directory is null || !Directory.Exists(directory))
        {
            output.WriteLine("[pace-real] skipped: TARKOV_REAL_TRAILS not set.");
            return;
        }

        var parser = new ScreenshotFilenameParser();
        var shots = new List<(DateTimeOffset Truth, ScreenshotPosition Position)>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.png"))
        {
            if (parser.TryParse(path, TimeSpan.Zero, out var position) && position is not null)
            {
                shots.Add((new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero), position));
            }
        }

        shots.Sort((a, b) => a.Truth.CompareTo(b.Truth));
        var raids = new List<List<(DateTimeOffset Truth, ScreenshotPosition Position)>>();
        foreach (var shot in shots)
        {
            if (raids.Count == 0 || (shot.Truth - raids[^1][^1].Truth).TotalMinutes > 20
                || PersonalPace.Elapsed(raids[^1][^1].Position, shot.Position) is null)
            {
                raids.Add([]);
            }

            raids[^1].Add(shot);
        }

        // Truth legs: the same moving-leg rule, timed by file time.
        var truth = raids.Select((raid, index) => raid.Zip(raid.Skip(1), (a, b) => (Raid: index,
                Metres: Math.Sqrt(Math.Pow(b.Position.Position.X - a.Position.Position.X, 2) + Math.Pow(b.Position.Position.Z - a.Position.Position.Z, 2)),
                Seconds: (b.Truth - a.Truth).TotalSeconds))
            .Where(leg => leg.Seconds is >= PersonalPace.MinimumLegSeconds and <= PersonalPace.MaximumLegSeconds
                && leg.Metres >= PersonalPace.MinimumLegMetres && leg.Metres / leg.Seconds >= PersonalPace.MinimumMovingMetresPerSecond)
            .ToArray()).ToArray();

        var personal = new List<double>();
        var careful = new List<double>();
        var middle = new List<double>();
        int inPersonal = 0, inFixed = 0;
        for (var index = 0; index < raids.Count; index++)
        {
            if (truth[index].Length == 0)
            {
                continue;
            }

            var pace = PersonalPace.Measure(raids.Take(index).Select(raid => (IReadOnlyList<ScreenshotPosition>)[.. raid.Select(shot => shot.Position)]));
            if (pace is null)
            {
                continue;
            }

            foreach (var leg in truth[index])
            {
                personal.Add(Math.Abs(PersonalPace.Seconds(leg.Metres, pace) - leg.Seconds));
                careful.Add(Math.Abs(PersonalPace.Seconds(leg.Metres, null) - leg.Seconds));
                middle.Add(Math.Abs(leg.Metres * TrafficRoute.ObstacleAllowance / 2.5 - leg.Seconds));
                inPersonal += leg.Seconds >= leg.Metres * pace.FastSecondsPerMetre && leg.Seconds <= leg.Metres * pace.SlowSecondsPerMetre ? 1 : 0;
                inFixed += leg.Seconds >= leg.Metres * TrafficRoute.ObstacleAllowance / TrafficRoute.BriskPace
                    && leg.Seconds <= leg.Metres * TrafficRoute.ObstacleAllowance / TrafficRoute.CarefulPace ? 1 : 0;
            }
        }

        static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
        var overall = PersonalPace.Measure(raids.Select(raid => (IReadOnlyList<ScreenshotPosition>)[.. raid.Select(shot => shot.Position)]));
        output.WriteLine($"[pace-real] {shots.Count} screenshots, {raids.Count} raids, {truth.Sum(legs => legs.Length)} moving legs; overall pace {overall?.MetresPerSecond:0.00} m/s from {overall?.Legs} legs");
        output.WriteLine($"[pace-real] {personal.Count} legs scored: personal MAE {personal.Average():0.0} s (median {Median(personal):0.0}); fixed careful MAE {careful.Average():0.0} s (median {Median(careful):0.0}); fixed 2.5 m/s MAE {middle.Average():0.0} s (median {Median(middle):0.0})");
        output.WriteLine($"[pace-real] inside the shown range: personal {100.0 * inPersonal / personal.Count:0}%, fixed 1.8-3.2 m/s {100.0 * inFixed / personal.Count:0}%");
    }
}
