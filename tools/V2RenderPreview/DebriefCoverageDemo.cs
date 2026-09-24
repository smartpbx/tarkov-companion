using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels.V2.Debrief;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// #291: enough raids for Debrief's coverage and charts to have something to say. Written through
/// the real store as the runtime and Debrief write them: "extracts" lists as the extract-list scan
/// records them, the extract used, the value typed by hand, and one archived raid. Dev tool only.
/// </summary>
internal static class DebriefCoverageDemo
{
    private static Guid _withOffered;
    private static Guid _archived;

    private static readonly (string Map, int DaysAgo, string? Outcome, string[]? Offered, string? Used, long? Value)[] Raids =
    [
        ("customs", 18, "Survived", ["Crossroads", "RUAF Roadblock", "Smuggler's Boat", "Old Gas Station"], "Crossroads", 180_000),
        ("customs", 15, "Died", ["Crossroads", "ZB-1011", "Old Gas Station"], null, 0),
        ("customs", 12, "Survived", ["RUAF Roadblock", "Smuggler's Boat", "Trailer Park"], "RUAF Roadblock", 310_000),
        ("customs", 9, "Survived", null, "Crossroads", 95_000),
        ("customs", 5, "Died", null, null, null),
        ("woods", 17, "Survived", ["Outskirts", "UN Roadblock", "South V-Ex"], "Outskirts", 240_000),
        ("woods", 11, "MIA", ["Outskirts", "Bridge V-Ex"], null, null),
        ("woods", 6, "Survived", ["UN Roadblock", "Scav House"], "Scav House", 420_000),
        ("interchange", 14, "Died", null, null, 0),
        ("interchange", 8, "Survived", ["Emercom Checkpoint", "Railway Exfil"], "Emercom Checkpoint", 510_000),
        ("interchange", 4, "Died", ["Emercom Checkpoint", "Railway Exfil", "Hole in the Fence"], null, null),
        ("factory", 10, "Run-through", ["Gate 3", "Cellars"], "Gate 3", 60_000),
        ("factory", 7, "Died", ["Gate 3", "Gate 0"], null, 0),
        ("factory", 2, "Survived", null, null, 150_000),
    ];

    /// <summary>--debrief-coverage-demo: seeds the raids above plus one archived raid.</summary>
    public static void Seed(IServiceProvider services, Action<Task> drain) => drain(SeedAsync(services));

    /// <summary>--debrief-stats / --debrief-tables / --debrief-archived: the states a render shows.</summary>
    public static void Show(DebriefWorkspaceViewModel debrief, string[] args, Action<int> pump)
    {
        if (args.Contains("--debrief-stats"))
        {
            debrief.IsStatsView = true;
        }

        if (args.Contains("--debrief-tables"))
        {
            debrief.ValueChart.ShowTable = true;
            debrief.SurvivalChart.ShowTable = true;
        }

        if (args.Contains("--debrief-archived"))
        {
            debrief.ShowArchived = true;
            Wait(debrief.SelectRaidAsync(_archived, CancellationToken.None), pump);
        }

        // A raid whose extract list was photographed, so "Extract used" offers its picks.
        if (args.Contains("--debrief-select-offered"))
        {
            Wait(debrief.SelectRaidAsync(_withOffered, CancellationToken.None), pump);
        }

        pump(20);
    }

    private static void Wait(Task task, Action<int> pump)
    {
        while (!task.IsCompleted)
        {
            pump(1);
        }

        task.GetAwaiter().GetResult();
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        var history = services.GetRequiredService<SqliteRaidHistoryService>();
        var profile = await services.GetRequiredService<IPlayerProfileService>().GetActiveAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var index = 0;
        foreach (var (map, daysAgo, outcome, offered, used, value) in Raids)
        {
            var started = now.AddDays(-daysAgo).AddHours(-index);
            var id = await history.StartAsync(
                new(Guid.NewGuid(), profile.Id, map, "Regular", started, null, null, null),
                CancellationToken.None);
            await history.EndAsync(id, started.AddMinutes(25 + index), outcome, null, CancellationToken.None);
            if (offered is not null)
            {
                var list = offered
                    .Select(name => new ActiveExtract($"demo:{name}", name, new Confidence(0.9), "extract-list"))
                    .ToArray();
                await history.RecordEventAsync(id, RaidOfferedExtracts.EventType, started.AddMinutes(2), JsonSerializer.Serialize(list), CancellationToken.None);
            }

            if (used is not null)
            {
                var at = started.AddMinutes(30);
                await history.RecordEventAsync(id, RaidExtractUsed.EventType, at, new RaidExtractUsed(used, at).ToPayload(), CancellationToken.None);
            }

            if (value is not null)
            {
                await history.SetManualMetadataAsync(id, new RaidManualMetadata(null, null, null, value), CancellationToken.None);
            }

            if (map == "woods" && daysAgo == 6)
            {
                _withOffered = id;
            }

            index++;
        }

        // One archived raid: out of the list and every total until restored.
        var archivedStart = now.AddDays(-20);
        var archived = await history.StartAsync(
            new(Guid.NewGuid(), profile.Id, "reserve", "Regular", archivedStart, null, null, null),
            CancellationToken.None);
        await history.EndAsync(archived, archivedStart.AddMinutes(12), "Died", "Test raid, wrong gear", CancellationToken.None);
        _archived = archived;
        var archivedAt = now.AddMinutes(-5);
        await history.RecordEventAsync(archived, RaidArchive.EventType, archivedAt, new RaidArchive(true, archivedAt).ToPayload(), CancellationToken.None);
    }
}
