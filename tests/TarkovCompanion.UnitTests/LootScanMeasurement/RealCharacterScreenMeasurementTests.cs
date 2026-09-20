using System.Globalization;
using System.Text;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.LootScanMeasurement;

/// <summary>
/// What the nine real character screens show beside the stash: the carried panel, the gear
/// slots and the vitals strip. Reports, never asserts, and skips without the private corpus.
/// </summary>
/// <remarks>
/// <para>
/// #273 wants the carried grid read and #305 wants gear-slot occupancy from geometry. These are
/// the only real pixels of either, so this is where the figures in
/// <c>docs/research/EFT_SCREENSHOT_FACTS.md</c> come from and how to get them again.
/// </para>
/// <para>
/// It also reports the fact that decides what may be built on them: the carried panel and the
/// loadout are the same in every frame. Nine screenshots taken in one minute are one sample of
/// a backpack and one sample of a loadout, six times over, and a threshold chosen on one
/// sample has been tested on nothing.
/// </para>
/// <para>
/// Coordinates are for 3840x1080, where the interface is a 1920-wide panel centred in the frame.
/// </para>
/// </remarks>
public sealed class RealCharacterScreenMeasurementTests(ITestOutputHelper output)
{
    /// <summary>Gear slots as drawn: left, top, right, bottom, and what frame (4) shows in them.</summary>
    private static readonly (string Name, int Left, int Top, int Right, int Bottom, bool Occupied)[] Slots =
    [
        ("earpiece", 1027, 168, 1153, 294, true),
        ("headwear", 1211, 168, 1337, 294, true),
        ("face cover", 1395, 168, 1521, 294, true),
        ("armband", 1027, 334, 1153, 378, true),
        ("body armor", 1211, 334, 1337, 460, true),
        ("eyewear", 1395, 334, 1521, 460, false),
        ("dogtag", 1027, 414, 1153, 460, true),
        ("on sling", 1027, 502, 1337, 628, true),
        ("holster", 1395, 502, 1521, 628, false),
        ("on back", 1027, 672, 1337, 798, false),
        ("sheath", 1395, 672, 1521, 798, true),
    ];

    [Fact]
    public async Task ReportsTheCarriedPanelTheGearSlotsAndTheVitalsStrip()
    {
        var directory = Environment.GetEnvironmentVariable("TARKOV_REAL_SCREENSHOTS") ?? "/root/orca/recognition-corpus/real-2026-09-18";
        if (!Directory.Exists(directory))
        {
            output.WriteLine("[real-character-screen] skipped: no screenshots.");
            return;
        }

        var loader = new SkiaScreenshotImageLoader();
        var report = new StringBuilder();
        var frame = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.png").Order(StringComparer.Ordinal))
        {
            var image = await loader.LoadAsync(path, CancellationToken.None);
            if (image is null || image.Width != 3840 || image.Height != 1080)
            {
                continue;
            }

            report.AppendLine(CultureInfo.InvariantCulture, $"=== frame {frame++}: {Path.GetFileName(path)}");
            report.AppendLine(CultureInfo.InvariantCulture, $"backpack grid, vertical lines x:   {string.Join(" ", Ridges(image, vertical: true, 1700, 2000, 540, 900))}");
            report.AppendLine(CultureInfo.InvariantCulture, $"backpack grid, horizontal lines y: {string.Join(" ", Ridges(image, vertical: false, 480, 1000, 1755, 1930))}");
            foreach (var slot in Slots)
            {
                var values = Luminances(image, slot.Left + 8, slot.Top + 8, slot.Right - 8, slot.Bottom - 8);
                report.AppendLine(CultureInfo.InvariantCulture, $"slot {slot.Name,-11} {(slot.Occupied ? "occupied" : "empty"),-8} mean {values.Average(value => (double)value),5:0.0} p99 {values[values.Length * 99 / 100],3} max {values[^1],3} share over 90: {values.Count(value => value > 90) / (double)values.Length:0.000}");
            }

            var vitals = Luminances(image, 995, 875, 1575, 928);
            report.AppendLine(CultureInfo.InvariantCulture, $"vitals strip: max {vitals[^1]}, p99 {vitals[vitals.Length * 99 / 100]}, over 150: {vitals.Count(value => value > 150)} of {vitals.Length}");
        }

        output.WriteLine(report.ToString());
        var reports = Environment.GetEnvironmentVariable("TARKOV_REAL_SCREENSHOT_REPORTS") ?? Path.Combine(Path.GetDirectoryName(directory.TrimEnd('/'))!, "real-reports");
        Directory.CreateDirectory(reports);
        await File.WriteAllTextAsync(Path.Combine(reports, "character-screen.txt"), report.ToString());
    }

    /// <summary>Lines one pixel wide that stand clear of what is two pixels either side of them.</summary>
    private static List<int> Ridges(CapturedImage image, bool vertical, int from, int to, int acrossFrom, int acrossTo)
    {
        var means = new double[to - from];
        for (var along = from; along < to; along++)
        {
            long sum = 0;
            for (var across = acrossFrom; across < acrossTo; across++)
            {
                sum += vertical
                    ? CapturedImagePixels.GetLuminance(image, along, across)
                    : CapturedImagePixels.GetLuminance(image, across, along);
            }

            means[along - from] = sum / (double)(acrossTo - acrossFrom);
        }

        var ridges = new List<int>();
        for (var index = 2; index < means.Length - 2; index++)
        {
            if (means[index] > means[index - 2] + 6 && means[index] > means[index + 2] + 6 &&
                means[index] >= Math.Max(means[index - 1], means[index + 1]))
            {
                ridges.Add(from + index);
            }
        }

        return ridges;
    }

    private static byte[] Luminances(CapturedImage image, int left, int top, int right, int bottom)
    {
        var values = new byte[(right - left) * (bottom - top)];
        var next = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                values[next++] = CapturedImagePixels.GetLuminance(image, x, y);
            }
        }

        Array.Sort(values);
        return values;
    }
}
