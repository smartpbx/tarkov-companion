using System.Text.Json.Nodes;
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
                ScratchDirectory.Remove(directory);
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

    [Fact]
    public async Task StoredSchemaOneProfileUpgradesToStableGenerationAwareSchemaTwo()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tarkov-profile-v1-{Guid.NewGuid():N}");
        var profilePath = Path.Combine(directory, "profile.json");
        try
        {
            Directory.CreateDirectory(directory);
            string exported;
            using (var writer = new JsonFilePlayerProfileService(new(profilePath)))
            {
                await writer.SaveAsync(CreateProfile(), CancellationToken.None);
                exported = await writer.ExportJsonAsync(CancellationToken.None);
            }

            var root = JsonNode.Parse(exported)?.AsObject()
                ?? throw new InvalidDataException("Test profile export was not an object.");
            root["schemaVersion"] = 1;
            root["profile"]?.AsObject().Remove("profileGeneration");
            await File.WriteAllTextAsync(profilePath, root.ToJsonString());

            using var reader = new JsonFilePlayerProfileService(new(profilePath));
            var migrated = await reader.GetActiveAsync(CancellationToken.None);
            var persisted = JsonNode.Parse(await File.ReadAllTextAsync(profilePath))?.AsObject();

            Assert.Equal($"legacy-{migrated.Id:N}", migrated.ProfileGeneration);
            Assert.Equal(2, persisted?["schemaVersion"]?.GetValue<int>());
            Assert.Equal(migrated.ProfileGeneration, persisted?["profile"]?["profileGeneration"]?.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                ScratchDirectory.Remove(directory);
            }
        }
    }

    [Fact]
    public async Task SchemaTwoRequiresExplicitGeneration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tarkov-profile-v2-{Guid.NewGuid():N}.json");
        try
        {
            using var service = new JsonFilePlayerProfileService(new(path));
            await service.SaveAsync(CreateProfile(), CancellationToken.None);
            var root = JsonNode.Parse(await service.ExportJsonAsync(CancellationToken.None))?.AsObject()
                ?? throw new InvalidDataException("Test profile export was not an object.");
            root["profile"]?.AsObject().Remove("profileGeneration");

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.ImportJsonAsync(root.ToJsonString(), CancellationToken.None));

            Assert.Contains("generation", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OversizedSaveCannotReplaceAPreviouslyReadableProfile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tarkov-profile-save-budget-{Guid.NewGuid():N}");
        var profilePath = Path.Combine(directory, "profile.json");
        try
        {
            using var service = new JsonFilePlayerProfileService(
                new(profilePath, MaximumImportBytes: 4_096));
            var original = CreateProfile();
            await service.SaveAsync(original, CancellationToken.None);
            var originalBytes = await File.ReadAllBytesAsync(profilePath);

            var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.SaveAsync(
                    original with { Name = new string('x', 8_192) },
                    CancellationToken.None));

            Assert.Contains("Serialized profile", failure.Message, StringComparison.Ordinal);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(profilePath));
            var restored = await service.GetActiveAsync(CancellationToken.None);
            Assert.Equal(original.Id, restored.Id);
            Assert.Equal(original.Name, restored.Name);
            Assert.Equal(original.ProfileGeneration, restored.ProfileGeneration);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                ScratchDirectory.Remove(directory);
            }
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
