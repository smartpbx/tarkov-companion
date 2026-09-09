using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests;

public sealed class MapObservationServiceTests
{
    private static string ScreenshotName => File.ReadLines(Fixture("fixtures/screenshot-filenames/observed.txt"))
        .First(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'));

    [Fact]
    public void CachedFixtureToleratesUnknownFieldsAndProvidesTransform()
    {
        var map = LoadMap();

        Assert.Equal("training-ground", map.Id);
        Assert.Equal(TimeSpan.FromMinutes(40), map.PmcRaidDuration);
        Assert.NotNull(map.Transform);
        Assert.Equal(2, map.Floors.Count);
        Assert.Null(map.Extracts[1].Position);
    }

    [Fact]
    public void CachedMapFailsClearlyWhenRequiredIdentityIsMissing()
    {
        var error = Assert.Throws<InvalidDataException>(() => MapCacheJsonParser.Parse(
            """{ "name": "Missing identifier" }""",
            new DataProvenance("fixture", DateTimeOffset.UnixEpoch)));

        Assert.Contains("id and name", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScreenshotFilenameProducesFreshPositionHeadingAndFloor()
    {
        var service = new MapObservationService(new ScreenshotFilenameParser(), new MapTransformService());
        var observed = service.TryObserve(
            ScreenshotName,
            TimeSpan.FromHours(-4),
            LoadMap(),
            new DateTimeOffset(2026, 9, 4, 22, 34, 0, TimeSpan.Zero),
            out var result);

        Assert.True(observed);
        Assert.NotNull(result);
        Assert.Equal(PositionFreshness.Fresh, result.Freshness);
        Assert.Equal("upper", result.Floor?.Id);
        Assert.NotNull(result.MapPoint);
        Assert.InRange(result.Position.HeadingDegrees, 0, 360);
    }

    [Fact]
    public void StaleScreenshotRemainsExplicitlyLastKnown()
    {
        var service = new MapObservationService(
            new ScreenshotFilenameParser(),
            new MapTransformService(),
            TimeSpan.FromMinutes(2));

        service.TryObserve(
            ScreenshotName,
            TimeSpan.FromHours(-4),
            LoadMap(),
            new DateTimeOffset(2026, 9, 4, 22, 40, 0, TimeSpan.Zero),
            out var result);

        Assert.NotNull(result);
        Assert.Equal(PositionFreshness.Stale, result.Freshness);
        Assert.Contains("stale", result.Guidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingTransformNeverGuessesPosition()
    {
        var map = LoadMap() with { Transform = null };
        var service = new MapObservationService(new ScreenshotFilenameParser(), new MapTransformService());

        service.TryObserve(
            ScreenshotName,
            TimeSpan.FromHours(-4),
            map,
            new DateTimeOffset(2026, 9, 4, 22, 34, 0, TimeSpan.Zero),
            out var result);

        Assert.NotNull(result);
        Assert.Equal(PositionFreshness.MissingTransform, result.Freshness);
        Assert.Null(result.MapPoint);
        Assert.False(result.CanPlot);
    }

    [Fact]
    public void OverlappingFloorRangesInvalidateTransformUse()
    {
        var map = LoadMap() with
        {
            Floors =
            [
                new("a", "A", 0, 10, null),
                new("b", "B", 5, 20, null),
            ],
        };
        var service = new MapObservationService(new ScreenshotFilenameParser(), new MapTransformService());

        service.TryObserve(
            ScreenshotName,
            TimeSpan.FromHours(-4),
            map,
            new DateTimeOffset(2026, 9, 4, 22, 34, 0, TimeSpan.Zero),
            out var result);

        Assert.NotNull(result);
        Assert.Equal(PositionFreshness.InvalidTransform, result.Freshness);
        Assert.Null(result.MapPoint);
    }

    [Fact]
    public void ActiveExtractWithoutVerifiedPositionRemainsVisibleWithoutMarker()
    {
        var active = new ActiveExtract("conditional", "Conditional Exit", new Confidence(0.9), "fixture OCR");

        var view = Assert.Single(ActiveExtractState.Resolve(LoadMap(), [active]));

        Assert.False(view.HasKnownPosition);
        Assert.Contains("without a marker", view.PositionGuidance, StringComparison.OrdinalIgnoreCase);
    }

    private static MapDefinition LoadMap()
    {
        var json = File.ReadAllText(Fixture("fixtures/maps/training-ground.json"));
        return MapCacheJsonParser.Parse(json, new DataProvenance("fixture", DateTimeOffset.UnixEpoch));
    }

    private static string Fixture(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate test fixture {relativePath}.");
    }
}
