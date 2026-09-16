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
    public async Task Offline_refresh_is_quarantined_and_preserves_the_empty_last_known_good_head()
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

        Assert.Equal(
            LootSpawnSourceImportDisposition.QuarantinedRetainedLastKnownGood,
            result.Disposition);
        Assert.Null(result.Published);
        Assert.Null(result.LastKnownGood);
        Assert.Equal("source.refresh-failed", Assert.Single(result.Diagnostics).Code);
        Assert.Equal("source.refresh-failed", Assert.Single(await store.ReadQuarantineAsync()).Diagnostic.Code);
    }
}
