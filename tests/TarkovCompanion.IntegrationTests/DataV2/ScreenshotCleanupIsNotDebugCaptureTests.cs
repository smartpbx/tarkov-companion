using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.IntegrationTests.DataV2;

/// <summary>
/// #309 kept cleanup of the game's own screenshots and retention of diagnostic pixels as two separate
/// capabilities, and docs/adr/0020 decided not to build the second. This pins the seam between them: turning
/// tidying on must never turn on anything that keeps pixels.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class ScreenshotCleanupIsNotDebugCaptureTests
{
    [Fact]
    public async Task TurningTidyingOnLeavesTheDebugCaptureFlagOff()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new SqliteScreenshotRetentionStore(database.Factory);

        await store.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT debug_capture_enabled FROM retention_policies;"));
        Assert.False((await store.GetAsync(TestContext.Current.CancellationToken)).IsEnabled);

        await store.SaveAsync(new ScreenshotRetentionSettings(true, 48), TestContext.Current.CancellationToken);

        Assert.True((await store.GetAsync(TestContext.Current.CancellationToken)).IsEnabled);
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT debug_capture_enabled FROM retention_policies;"));
    }
}
