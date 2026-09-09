using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Infrastructure.Profile;

namespace TarkovCompanion.IntegrationTests;

public sealed class ProfilePersistenceTests
{
    [Fact]
    public async Task SaveExportImportAndReloadPreserveOnlyProfileState()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tarkov-profile-{Guid.NewGuid():N}");
        var profilePath = Path.Combine(directory, "profile.json");
        try
        {
            var profile = CreateProfile();
            using (var service = new JsonFilePlayerProfileService(new(profilePath)))
            {
                await service.SaveAsync(profile, CancellationToken.None);
                var exported = await service.ExportJsonAsync(CancellationToken.None);
                var rootEnd = exported.LastIndexOf('}');
                var importJson = exported.Insert(rootEnd, ",\"apiToken\":\"must-not-survive\"");
                var imported = await service.ImportJsonAsync(importJson, CancellationToken.None);

                Assert.Equal(profile.Id, imported.Id);
                Assert.DoesNotContain("apiToken", await service.ExportJsonAsync(CancellationToken.None), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("must-not-survive", await File.ReadAllTextAsync(profilePath), StringComparison.Ordinal);
            }

            using var reloadedService = new JsonFilePlayerProfileService(new(profilePath));
            var reloaded = await reloadedService.GetActiveAsync(CancellationToken.None);
            Assert.Equal(profile.Name, reloaded.Name);
            Assert.Equal(EventItemState.Allergic, reloaded.EventItemStates["event:item"]);
            Assert.Equal(TimeSpan.Zero, reloaded.UpdatedUtc.Offset);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ImportFailsClearlyWhenRequiredProfileDataIsMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tarkov-profile-{Guid.NewGuid():N}.json");
        try
        {
            using var service = new JsonFilePlayerProfileService(new(path));
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.ImportJsonAsync("{\"schemaVersion\":1}", CancellationToken.None));
            Assert.Contains("profile", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static PlayerProfile CreateProfile() => new(
        Guid.Parse("940d35d5-47a2-4a25-afb9-94145166d65b"),
        "Persistent profile",
        GameMode.Pve,
        42,
        Faction.Bear,
        "Edge of Darkness",
        new Dictionary<string, int> { ["prapor"] = 4 },
        new HashSet<string> { "task" },
        new Dictionary<string, int> { ["objective"] = 2 },
        new Dictionary<string, int> { ["workbench"] = 3 },
        new HashSet<string> { "item" },
        new Dictionary<string, int> { ["item"] = 5 },
        new Dictionary<string, EventItemState> { ["event:item"] = EventItemState.Allergic },
        new Dictionary<string, string> { ["item"] = "Keep" },
        new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(-4)));
}
