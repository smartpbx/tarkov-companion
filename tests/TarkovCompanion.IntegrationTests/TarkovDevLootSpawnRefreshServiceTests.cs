using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Infrastructure.GameData.LootSpawns;
using TarkovCompanion.Infrastructure.Maps;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.IntegrationTests;

public sealed class TarkovDevLootSpawnRefreshServiceTests
{
    [Fact]
    public async Task Offline_refresh_is_skipped_as_local_only_without_quarantine_and_preserves_the_empty_head()
    {
        var time = new ManualTimeProvider(new(2026, 9, 16, 20, 0, 0, TimeSpan.Zero));
        using var jsonHttpClient = new HttpClient(new FixtureApiHandler());
        using var catalogHttpClient = new HttpClient(new FixtureApiHandler());
        await using var jsonClient = new TarkovDevJsonClient(
            jsonHttpClient,
            new InMemoryTarkovDevResponseCache(),
            new DataTranslationService(),
            new TarkovDevJsonClientOptions
            {
                BaseAddress = new("https://fixture.invalid/"),
                OfflineProbe = static () => true,
            },
            time);
        var catalogClient = new TarkovDevMapCatalogClient(
            catalogHttpClient,
            TarkovDevMapCatalogClientOptions.CreateDefault(
                Path.Combine(Path.GetTempPath(), $"tarkov-companion-unused-{Guid.NewGuid():N}")),
            time);
        using var store = new AtomicLootSpawnPublicationStore();
        var service = new TarkovDevLootSpawnRefreshService(
            GameMode.Regular,
            "en",
            jsonClient,
            catalogClient,
            new TarkovDevLootSpawnNormalizer(),
            store,
            time);

        var result = await service.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken);

        // [#292 follow-up] Offline is Local only in the app: not a failed candidate, so nothing is
        // quarantined and the panel can say "Local only · off" rather than "refresh failed".
        Assert.Equal(
            LootSpawnSourceImportDisposition.SkippedLocalOnly,
            result.Disposition);
        Assert.Null(result.Published);
        Assert.Null(result.LastKnownGood);
        Assert.Equal(LootSpawnRefreshCodes.LocalOnly, Assert.Single(result.Diagnostics).Code);
        Assert.Empty(await store.ReadQuarantineAsync());
    }
}
