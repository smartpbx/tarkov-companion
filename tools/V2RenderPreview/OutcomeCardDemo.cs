using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels.V2.Debrief;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// [#712 0-8] --outcome-demo: a Customs raid that ended a minute ago with no outcome, written
/// through the real store as the runtime writes it (two scans, Golden Swag moved, the last
/// screenshot a few metres from ZB-1011), then handed to Debrief as a raid-end signal would.
/// --outcome-answer survived|died|run|mia presses that button; --outcome-later presses Later.
/// Dev tool only; never shipped.
/// </summary>
internal static class OutcomeCardDemo
{
    public static void Show(IServiceProvider services, DebriefWorkspaceViewModel debrief, string[] args, Action<Task> drain, Action<int> pump)
    {
        var raidId = Seed(services, drain);
        var ended = DateTimeOffset.UtcNow.AddMinutes(-1);
        drain(debrief.OfferAfterRaidAsync(
            new RaidEnded(raidId, "customs", "PMC", ended.AddMinutes(-34), ended, ["Geo", "Riley"]),
            CancellationToken.None));
        pump(20);

        var answer = Array.IndexOf(args, "--outcome-answer") is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        if (answer is not null)
        {
            var bucket = answer switch
            {
                "died" => RaidOutcomeBucket.Died,
                "run" => RaidOutcomeBucket.RunThrough,
                "mia" => RaidOutcomeBucket.Mia,
                _ => RaidOutcomeBucket.Survived,
            };
            debrief.AfterRaid.Choices.First(choice => choice.Bucket == bucket).Command.Execute(null);
            pump(60);
        }
        else if (args.Contains("--outcome-later"))
        {
            debrief.AfterRaid.LaterCommand.Execute(null);
            pump(40);
        }

        Console.WriteLine(
            $"Outcome card: visible {debrief.AfterRaid.IsVisible}, asking {debrief.AfterRaid.IsAsking}, " +
            $"outcome '{debrief.AfterRaid.OutcomeLabel}' ({debrief.AfterRaid.OutcomeKindLabel}); " +
            $"recap: {string.Join(" | ", debrief.AfterRaid.Recap.Select(line => $"{line.Text} [{line.KindLabel}]"))}");
    }

    private static Guid Seed(IServiceProvider services, Action<Task> drain)
    {
        var seeded = SeedAsync(services);
        drain(seeded);
        return seeded.Result;
    }

    private static async Task<Guid> SeedAsync(IServiceProvider services)
    {
        IRaidHistoryService history = services.GetRequiredService<SqliteRaidHistoryService>();
        var profile = await services.GetRequiredService<IPlayerProfileService>().GetActiveAsync(CancellationToken.None);
        var ended = DateTimeOffset.UtcNow.AddMinutes(-1);
        var started = ended.AddMinutes(-34);
        var raidId = await history.StartAsync(
            new(Guid.NewGuid(), profile.Id, "customs", "Regular", started, null, null, null),
            CancellationToken.None);
        await history.EndAsync(raidId, ended, null, null, CancellationToken.None);
        await history.RecordEventAsync(raidId, "state", started, "{\"Side\":\"PMC\"}", CancellationToken.None);

        var scanned = started.AddMinutes(8);
        foreach (var (name, id, value) in new[]
        {
            ("Graphics card", "57347ca924597744596b4e71", 232_000L),
            ("Golden rooster figurine", "5bc9bc53d4351e00367fbcee", 180_000L),
        })
        {
            scanned += TimeSpan.FromMinutes(6);
            await history.RecordEventAsync(
                raidId,
                "scan",
                scanned,
                JsonSerializer.Serialize(new ScanExecutionResult(
                    true, true, id, name, value, value, "Take", new(0.93), scanned, "screenshot", "preview")),
                CancellationToken.None);
        }

        await history.RecordEventAsync(
            raidId,
            "quest",
            started.AddMinutes(19),
            JsonSerializer.Serialize(new QuestStatusObservation(
                "demo-quest-1", "5979eee086f774311955e614", RecordedTaskState.Completed, started.AddMinutes(19))),
            CancellationToken.None);

        // The last screenshot a few metres from ZB-1011 (catalog x 621.5, z -128.6).
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var last = ended.AddMinutes(-2);
        foreach (var (x, z, at) in new[] { (590.0, -110.0, last.AddMinutes(-6)), (616.0, -124.0, last) })
        {
            var position = new ScreenshotPosition(at, new WorldPosition(x, 1, z), new QuaternionOrientation(0, 0, 0, 1), 90, null, null, "demo.png");
            await history.RecordEventAsync(raidId, "position", at, JsonSerializer.Serialize(position, json), CancellationToken.None);
        }

        return raidId;
    }
}
