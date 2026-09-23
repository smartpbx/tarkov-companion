using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.LootScanMeasurement;

/// <summary>
/// Checks the private real Gear pixels against the public, label-only grid fixture. The pixels
/// carry a raid id and stay off-repository; CI discovers this test but skips its work without the
/// explicitly assembled eleven-frame corpus.
/// </summary>
public sealed class RealGearGridGroundTruthTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DetectsEveryVisibleBackpackAndTheVisibleRigAndPocketPouches()
    {
        var directory = Environment.GetEnvironmentVariable("TARKOV_REAL_GEAR_GRID_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            output.WriteLine("[real-gear-grid-ground-truth] skipped: no private screenshot corpus.");
            return;
        }

        var truth = ReadTruth(Path.Combine(AppContext.BaseDirectory, "Fixtures", "real-gear-grid-ground-truth.txt"));
        var paths = Directory.EnumerateFiles(directory, "*.png").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(truth.Count, paths.Length);

        var loader = new SkiaScreenshotImageLoader();
        var reader = new GearScreenLayoutReader();
        var mismatches = new List<string>();
        for (var index = 0; index < paths.Length; index++)
        {
            var image = await loader.LoadAsync(paths[index], CancellationToken.None);
            Assert.NotNull(image);
            var layout = reader.Read(image);
            Assert.NotNull(layout);
            CompareSection(mismatches, truth[index].Name, "backpack", truth[index].Backpack, layout!.In(GearGridSection.Backpack));
            CompareSection(mismatches, truth[index].Name, "rig", truth[index].Rig, layout.In(GearGridSection.TacticalRig));
            CompareSection(mismatches, truth[index].Name, "pockets", truth[index].Pockets, layout.In(GearGridSection.Pockets));
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    private static void CompareSection(
        ICollection<string> mismatches,
        string frame,
        string section,
        IReadOnlyList<string> expected,
        IEnumerable<GearGrid> actual)
    {
        var dimensions = actual.Select(grid => $"{grid.Columns}x{grid.Rows}").ToArray();
        if (!expected.SequenceEqual(dimensions, StringComparer.Ordinal))
        {
            mismatches.Add($"{frame} {section}: expected [{string.Join(", ", expected)}], actual [{string.Join(", ", dimensions)}]");
        }
    }

    private static IReadOnlyList<FrameTruth> ReadTruth(string path)
    {
        var frames = new List<FrameTruth>();
        string? name = null;
        IReadOnlyList<string> backpack = [];
        IReadOnlyList<string> rig = [];
        IReadOnlyList<string> pockets = [];
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("frame-", StringComparison.Ordinal))
            {
                if (name is not null)
                {
                    frames.Add(new(name, backpack, rig, pockets));
                }

                name = line;
                backpack = [];
                rig = [];
                pockets = [];
                continue;
            }

            var separator = line.IndexOf(':');
            Assert.True(separator > 0, $"Malformed real Gear grid label: {line}");
            var values = ParseDimensions(line[(separator + 1)..]);
            switch (line[..separator])
            {
                case "backpack": backpack = values; break;
                case "rig": rig = values; break;
                case "pockets": pockets = values; break;
                default: Assert.Fail($"Unknown real Gear grid section: {line[..separator]}"); break;
            }
        }

        if (name is not null)
        {
            frames.Add(new(name, backpack, rig, pockets));
        }

        return frames;
    }

    private static IReadOnlyList<string> ParseDimensions(string value)
    {
        var trimmed = value.Trim();
        return trimmed is "absent" or "none"
            ? []
            : trimmed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private sealed record FrameTruth(
        string Name,
        IReadOnlyList<string> Backpack,
        IReadOnlyList<string> Rig,
        IReadOnlyList<string> Pockets);
}
