using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.LootSpawns;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// <c>--loot-coverage-table &lt;file.md&gt;</c>: the high-value loot layer's per-map coverage as a
/// Markdown table, measured by opening every map in the Raid page with the layer on.
/// </summary>
/// <remarks>
/// [Issue 318] The all-maps coverage table was written by hand once (C26, #724) and then went
/// stale with the next publication. This writes it from what the app itself shows: the import's
/// own per-map counts (<see cref="LootSpawnCoverageReport"/>), the layer's object count with the
/// default filter, and the exact status text the Layers menu puts beside the switch.
/// <c>scripts/loot-coverage-table.sh</c> runs it against a real publication cache and splices the
/// result into <c>docs/MAPS.md</c>. Needs <c>--seed-loot-cache</c> and <c>--raid-demo</c>.
/// </remarks>
internal static class LootCoverageTable
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static void Write(IServiceProvider services, MainWindowViewModel viewModel, V2ShellViewModel shell, string outputPath)
    {
        if (shell.RaidCockpit is not RaidCockpitViewModel raid)
        {
            Console.Error.WriteLine("--loot-coverage-table needs the Raid cockpit.");
            return;
        }

        var head = services.GetRequiredService<IHighValueLootRuntimeSource>().LastKnownGood;
        if (head is null)
        {
            Console.Error.WriteLine("--loot-coverage-table: no loot publication loaded (pass --seed-loot-cache).");
            return;
        }

        var coverage = LootSpawnCoverageReport.From(head)
            .ToDictionary(row => row.MapId, StringComparer.OrdinalIgnoreCase);
        shell.Router.NavigateToAddress("raid");
        LootTour.Pump();

        var table = new StringBuilder();
        table.AppendLine(string.Create(Invariant,
            $"Publication `{head.Identity.DatasetVersion}`, data through {head.Identity.DataThroughUtc:yyyy-MM-dd}, default filter."));
        table.AppendLine();
        table.AppendLine("| Map | Source records | Published | Positioned | Shown | Layers menu says |");
        table.AppendLine("| --- | ---: | ---: | ---: | ---: | --- |");
        foreach (var map in raid.MapPicker.ToArray())
        {
            LootTour.Select(raid, map.MapId);
            LootTour.WaitForMap(viewModel, raid, map.MapId);
            LootTour.SetLoot(raid, on: true);
            LootTour.Pump();
            var shown = raid.Renderer?.Layers
                .FirstOrDefault(layer => layer.Layer.Id == HighValueLootLayerService.LayerId)?.Count ?? 0;
            var status = raid.Renderer?.HighValueLoot?.LayerMenuStatus ?? "No loot layer";
            table.AppendLine(coverage.TryGetValue(map.MapId, out var row)
                ? string.Create(Invariant, $"| {map.Name} | {row.Known:N0} | {row.Published:N0} | {row.Positioned:N0} | {shown:N0} | {status} |")
                : string.Create(Invariant, $"| {map.Name} | 0 | 0 | 0 | {shown:N0} | {status} |"));
        }

        File.WriteAllText(outputPath, table.ToString());
        Console.WriteLine($"Loot coverage table: {outputPath}");
        Console.Write(table);
    }
}
