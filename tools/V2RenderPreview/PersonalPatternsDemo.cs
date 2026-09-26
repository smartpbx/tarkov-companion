using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.Application.Services.Personal;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// #712 2-4: three recent Customs raids with trails, so Debrief's one line of the player's own
/// patterns has deaths to place, an exit used, and legs to measure a pace from. Fixture data,
/// written through the real store as the runtime writes positions. Dev tool only.
/// </summary>
internal static class PersonalPatternsDemo
{
    /// <summary>--debrief-patterns-demo (with --debrief-demo): seeds the raids.</summary>
    public static void Seed(IServiceProvider services, Action<Task> drain) => drain(SeedAsync(services));

    private static async Task SeedAsync(IServiceProvider services)
    {
        var history = services.GetRequiredService<SqliteRaidHistoryService>();
        var profile = await services.GetRequiredService<IPlayerProfileService>().GetActiveAsync(CancellationToken.None);
        var map = await services.GetRequiredService<IMapDataService>().GetAsync("customs", CancellationToken.None);
        var exit = map?.Extracts.FirstOrDefault(extract => extract.Position is not null);
        var target = exit?.Position ?? new MapPoint(0, 0);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var now = DateTimeOffset.UtcNow;

        (int DaysAgo, string Outcome, string? Used)[] raids = [(1, "Died", null), (2, "Survived", exit?.Name), (3, "Died", null)];
        var index = 0;
        foreach (var (daysAgo, outcome, used) in raids)
        {
            var started = now.AddDays(-daysAgo);
            var id = await history.StartAsync(new(Guid.NewGuid(), profile.Id, "customs", "Regular", started, null, null, null), CancellationToken.None);
            await history.EndAsync(id, started.AddMinutes(30), outcome, null, CancellationToken.None);
            if (used is not null)
            {
                var at = started.AddMinutes(30);
                await history.RecordEventAsync(id, RaidExtractUsed.EventType, at, new RaidExtractUsed(used, at).ToPayload(), CancellationToken.None);
            }

            // Eight screenshots walking toward the exit at about 1.3 m/s, the last 20 m short of it.
            var seconds = 120.0;
            for (var step = 8; step >= 0; step--)
            {
                var real = started.AddSeconds(seconds);
                var named = new DateTimeOffset(real.Year, real.Month, real.Day, real.Hour, real.Minute, 0, real.Offset);
                var hour = Math.Round((10 + index + seconds * PersonalPace.InGameClockRate / 3600) % 24, 2);
                var shot = new ScreenshotPosition(
                    named,
                    new WorldPosition(target.X + 20 + step * 110, 0, target.Y),
                    new QuaternionOrientation(0, 0, 0, 1),
                    0,
                    TimeSpan.FromSeconds(hour),
                    null,
                    "demo.png");
                await history.RecordEventAsync(id, "position", named, JsonSerializer.Serialize(shot, json), CancellationToken.None);
                seconds += 110 / (1.3 * (0.85 + 0.1 * (step % 4)));
            }

            index++;
        }
    }
}
