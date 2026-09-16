using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.Profile;

namespace TarkovCompanion.IntegrationTests.DataV2;

[Collection(SqliteCollection.Name)]
public sealed class ScreenshotRetentionMigrationTests
{
    [Fact]
    public async Task ExistingJsonOptOutIsImportedBeforeFirstRetentionRead()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var directory = Path.Combine(Path.GetTempPath(), $"tarkov-retention-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "screenshots.json");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                path,
                "{\"enabled\":false,\"retentionHours\":72}",
                TestContext.Current.CancellationToken);
            var migrated = new SqliteScreenshotRetentionStore(database.Factory, legacySettingsPath: path);

            var firstRead = await migrated.GetAsync(TestContext.Current.CancellationToken);
            File.Delete(path);
            var afterRestart = await new SqliteScreenshotRetentionStore(database.Factory)
                .GetAsync(TestContext.Current.CancellationToken);

            Assert.False(firstRead.IsEnabled);
            Assert.Equal(72, firstRead.RetentionHours);
            Assert.Equal(firstRead, afterRestart);
            Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
                "SELECT COUNT(*) FROM local_json_recovery WHERE document_key = 'settings:screenshot-retention' AND state = 'current' AND diagnostic_code = 'legacy-settings-imported';"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task MalformedLegacySettingsBecomeExplicitRecoverableState()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var directory = Path.Combine(Path.GetTempPath(), $"tarkov-retention-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "screenshots.json");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(path, "{not-json", TestContext.Current.CancellationToken);
            var store = new SqliteScreenshotRetentionStore(database.Factory, legacySettingsPath: path);

            var settings = await store.GetAsync(TestContext.Current.CancellationToken);

            Assert.Equal(TarkovCompanion.Application.Services.Raids.ScreenshotRetentionSettings.Default, settings);
            Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
                "SELECT COUNT(*) FROM local_json_recovery WHERE document_key = 'settings:screenshot-retention' AND state = 'malformed' AND diagnostic_code = 'legacy-settings-json-invalid';"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task MalformedStoredProfileIsBackedUpRecordedAndReplacedWithAUsableDefault()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var directory = Path.Combine(Path.GetTempPath(), $"tarkov-profile-recovery-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "profile.json");
        Directory.CreateDirectory(directory);
        try
        {
            const string malformed = "{\"schemaVersion\":2,\"profile\":";
            await File.WriteAllTextAsync(path, malformed, TestContext.Current.CancellationToken);
            var clock = new ManualTimeProvider(new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero));
            using var service = new JsonFilePlayerProfileService(
                new(path),
                clock,
                database.Factory);

            var recovered = await service.GetActiveAsync(TestContext.Current.CancellationToken);

            Assert.Equal("Local profile", recovered.Name);
            Assert.NotEqual(Guid.Empty, recovered.Id);
            Assert.NotEqual(malformed, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

            // A prior preservation can outlive a later failed recovery-state write. Retrying the
            // same bytes at the same injected instant must preserve another artifact instead of
            // colliding with the first backup name.
            await File.WriteAllTextAsync(path, malformed, TestContext.Current.CancellationToken);
            _ = await service.GetActiveAsync(TestContext.Current.CancellationToken);
            var backups = Directory.GetFiles(Path.Combine(directory, "Recovery"), "*.invalid.json");
            Assert.Equal(2, backups.Length);
            foreach (var backup in backups)
            {
                Assert.Equal(malformed, await File.ReadAllTextAsync(backup, TestContext.Current.CancellationToken));
            }

            Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
                "SELECT COUNT(*) FROM local_json_recovery WHERE document_key = 'profile:active' AND state = 'malformed' AND diagnostic_code = 'profile-json-invalid' AND length(content_sha256) = 64;"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task InvalidUnicodeStoredProfileIsPreservedByteForByteBeforeRecovery()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var directory = Path.Combine(Path.GetTempPath(), $"tarkov-profile-encoding-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "profile.json");
        Directory.CreateDirectory(directory);
        try
        {
            byte[] malformed = [0x7B, 0x22, 0x78, 0x22, 0x3A, 0x22, 0xC3, 0x28, 0x22, 0x7D];
            await File.WriteAllBytesAsync(path, malformed, TestContext.Current.CancellationToken);
            using var service = new JsonFilePlayerProfileService(
                new(path),
                new ManualTimeProvider(new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero)),
                database.Factory);

            var recovered = await service.GetActiveAsync(TestContext.Current.CancellationToken);
            var backup = Assert.Single(Directory.GetFiles(Path.Combine(directory, "Recovery"), "*.invalid.json"));

            Assert.Equal("Local profile", recovered.Name);
            Assert.Equal(malformed, await File.ReadAllBytesAsync(backup, TestContext.Current.CancellationToken));
            Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
                "SELECT COUNT(*) FROM local_json_recovery WHERE document_key = 'profile:active' AND state = 'malformed' AND diagnostic_code = 'profile-json-invalid' AND length(content_sha256) = 64;"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task OversizedStoredProfileIsMovedWithoutReadingOrDuplicatingIt()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var directory = Path.Combine(Path.GetTempPath(), $"tarkov-profile-oversize-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "profile.json");
        Directory.CreateDirectory(directory);
        try
        {
            var oversized = new string('x', 4_097);
            await File.WriteAllTextAsync(path, oversized, TestContext.Current.CancellationToken);
            using var service = new JsonFilePlayerProfileService(
                new(path, MaximumImportBytes: 4_096),
                new ManualTimeProvider(new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero)),
                database.Factory);

            var recovered = await service.GetActiveAsync(TestContext.Current.CancellationToken);
            var backups = Directory.GetFiles(Path.Combine(directory, "Recovery"), "*.oversize.*.invalid.json");

            Assert.Equal("Local profile", recovered.Name);
            var backup = Assert.Single(backups);
            Assert.Equal(oversized, await File.ReadAllTextAsync(backup, TestContext.Current.CancellationToken));
            Assert.NotEqual(oversized, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
                "SELECT COUNT(*) FROM local_json_recovery WHERE document_key = 'profile:active' AND state = 'malformed' AND diagnostic_code = 'profile-json-oversize' AND content_sha256 IS NULL;"));

            _ = await service.GetActiveAsync(TestContext.Current.CancellationToken);
            Assert.Single(Directory.GetFiles(Path.Combine(directory, "Recovery"), "*.oversize.*.invalid.json"));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
