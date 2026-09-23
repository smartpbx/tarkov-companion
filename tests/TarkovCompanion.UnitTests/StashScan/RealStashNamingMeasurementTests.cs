using System.Globalization;
using System.Text;
using System.Text.Json;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.UnitTests.LootScanMeasurement;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.StashScan;

/// <summary>
/// The shipped stash reader, lattice to names, scored per frame against the hand-read labels:
/// how many labelled items it placed (the right footprint in the right cells) and how many of
/// those it named, rightly or wrongly.
/// </summary>
/// <remarks>
/// <para>
/// The measurements beside this one score footprints on the labelled panel, or naming policies on
/// labelled crops; neither says what a player gets from one screenshot. This runs the builder the
/// capture pipeline runs, with the lattice it finds itself and the icon index built from the local
/// icon corpus (<c>TARKOV_ICON_CORPUS</c>), so a lattice miss shows up as every item missed.
/// </para>
/// <para>
/// A frame whose labels are the stash panel is read as the Stash surface. A frame whose labels are
/// an opened case window over the stash (frames 7 and 8, a Junk box) is read as the Container
/// surface, which is what an Ammo or Keys sub-scan asks for: the case, not the stash behind it.
/// OCR is Windows-only, so stack counts and caption text play no part here. Reports only, and
/// skips without the screenshots or the corpus; never commit either.
/// </para>
/// </remarks>
public sealed class RealStashNamingMeasurementTests(ITestOutputHelper output)
{
    private const string DefaultFrames = "/root/orca/recognition-corpus/real-2026-09-18";
    private const int StashPanelX = 2225;
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReportsPlacedAndNamedPerRealFrame()
    {
        var folder = Environment.GetEnvironmentVariable("TARKOV_STASH_REAL_FRAMES") ?? DefaultFrames;
        if (!Directory.Exists(folder) || IconCorpus.TryLoad() is not { } corpus)
        {
            output.WriteLine("[stash-naming] skipped: no screenshots or no icon corpus.");
            return;
        }

        var cache = await LootScanMeasurementIndex.OpenAsync(corpus, Now);
        var builder = new GridPixelReconstructionBuilder(cache, corpus.CreateRepository(Now), new StashScanMeasurement.UnavailableOcrEngine());
        var loader = new SkiaScreenshotImageLoader();
        var report = new StringBuilder();
        var totals = new Dictionary<InventoryGridSurface, int[]>();
        foreach (var labelPath in Directory.EnumerateFiles(folder, "*.expected.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            var image = await loader.LoadAsync(labelPath[..^".expected.json".Length], CancellationToken.None);
            if (image is null)
            {
                continue;
            }

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(labelPath));
            var panel = document.RootElement.GetProperty("panel");
            var (originX, originY, pitch) = (panel.GetProperty("originX").GetInt32(), panel.GetProperty("originY").GetInt32(), panel.GetProperty("pitch").GetInt32());
            var surface = originX == StashPanelX ? InventoryGridSurface.Stash : InventoryGridSurface.Container;
            var labels = new Dictionary<(int Row, int Column, int Width, int Height), IReadOnlySet<string>?>();
            foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
            {
                var state = item.GetProperty("state").GetString();
                if (state is not ("ok" or "unknown"))
                {
                    continue;
                }

                labels[(item.GetProperty("row").GetInt32(), item.GetProperty("column").GetInt32(), item.GetProperty("width").GetInt32(), item.GetProperty("height").GetInt32())] =
                    state == "ok"
                        ? item.GetProperty("canonicalIds").EnumerateArray().Select(id => id.GetString()!).ToHashSet(StringComparer.Ordinal)
                        : null;
            }

            var window = surface == InventoryGridSurface.Container ? new CaseWindowLocator().Locate(image)?.Bounds : null;
            var request = await builder.BuildAsync(image, surface, Now, cancellationToken: CancellationToken.None);
            var line = Score(request, labels, originX, originY, pitch, window);
            if (surface == InventoryGridSurface.Container)
            {
                // What the same case screenshot gave before a case had a surface of its own.
                var asStash = await builder.BuildAsync(image, InventoryGridSurface.Stash, Now, cancellationToken: CancellationToken.None);
                report.AppendLine(CultureInfo.InvariantCulture, $"[stash-naming] {Path.GetFileName(labelPath)[^21..^18]} as Stash   {Score(asStash, labels, originX, originY, pitch, window).Text}");
            }

            var sum = totals.TryGetValue(surface, out var existing) ? existing : totals[surface] = new int[6];
            for (var index = 0; index < 6; index++)
            {
                sum[index] += line.Counts[index];
            }

            report.AppendLine(CultureInfo.InvariantCulture, $"[stash-naming] {Path.GetFileName(labelPath)[^21..^18]} {surface,-9} {line.Text}");
        }

        foreach (var (surface, sum) in totals)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"[stash-naming] total {surface,-9} placed {sum[1]}/{sum[0]} · named {sum[3]} of {sum[2]} readable ({sum[4]} right, {sum[5]} WRONG)");
        }

        output.WriteLine(report.ToString());
        Console.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("TARKOV_STASH_REAL_REPORT") is { } reportDirectory)
        {
            Directory.CreateDirectory(reportDirectory);
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "naming.txt"), report.ToString());
        }
    }

    /// <summary>Counts: labelled, placed, placed-and-readable, named, named rightly, named wrongly.</summary>
    private static (string Text, int[] Counts) Score(
        GridReconstructionRequest request,
        IReadOnlyDictionary<(int Row, int Column, int Width, int Height), IReadOnlySet<string>?> labels,
        int originX,
        int originY,
        int pitch,
        TarkovCompanion.Core.Domain.Recognition.PixelRect? window)
    {
        if (request.Lattice is not { } lattice)
        {
            return ($"no lattice · placed 0/{labels.Count}", [labels.Count, 0, 0, 0, 0, 0]);
        }

        var placed = 0;
        var readable = 0;
        var named = 0;
        var right = 0;
        var wrong = 0;
        var wrongNames = new List<string>();
        var outside = 0;
        var outsideNamed = 0;
        foreach (var cell in request.OccupiedCells)
        {
            if (cell.Item.Bounds is not { } bounds)
            {
                continue;
            }

            var key = (
                Row: (int)Math.Round((bounds.Y - originY) / (double)pitch),
                Column: (int)Math.Round((bounds.X - originX) / (double)pitch),
                Width: Math.Max(1, (int)Math.Round(bounds.Width / (double)lattice.CellWidthPixels)),
                Height: Math.Max(1, (int)Math.Round(bounds.Height / (double)lattice.CellHeightPixels)));
            if (!labels.TryGetValue(key, out var truth))
            {
                // A case frame is labelled over part of the window: what counts as an error is a
                // footprint outside the window, which is the stash or the gear behind it.
                var outsideWindow = window is { } rect &&
                                    (bounds.X < rect.X - 2 || bounds.Y < rect.Y - 2 ||
                                     bounds.X + bounds.Width > rect.X + rect.Width + 2 || bounds.Y + bounds.Height > rect.Y + rect.Height + 2);
                if (window is null
                        ? !labels.Keys.Any(label => key.Row < label.Row + label.Height && label.Row < key.Row + key.Height &&
                                                    key.Column < label.Column + label.Width && label.Column < key.Column + key.Width)
                        : outsideWindow)
                {
                    outside++;
                    outsideNamed += cell.Item.Value is null ? 0 : 1;
                }

                continue;
            }

            placed++;
            if (truth is null)
            {
                continue;
            }

            readable++;
            if (cell.Item.Value?.CanonicalId.Value is not { } id)
            {
                continue;
            }

            named++;
            if (truth.Contains(id))
            {
                right++;
            }
            else
            {
                wrong++;
                wrongNames.Add($"r{key.Row}c{key.Column}={cell.Item.Value.DisplayName.Value}");
            }
        }

        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"lattice {lattice.Columns}x{lattice.Rows} at ({lattice.Bounds.X},{lattice.Bounds.Y}) · placed {placed}/{labels.Count} · named {named} of {readable} readable ({right} right, {wrong} WRONG) · {(window is null ? "off the labels" : "outside the case")} {outside} ({outsideNamed} named){(wrongNames.Count == 0 ? string.Empty : " · wrong: " + string.Join(' ', wrongNames.Take(6)))}");
        return (text, [labels.Count, placed, readable, named, right, wrong]);
    }
}
