using TarkovCompanion.Application.Services.Network;
using TarkovCompanion.Core.Network;
using TarkovCompanion.Infrastructure.GameData.LootSpawns;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.UnitTests.LootSpawns;

/// <summary>[#292 follow-up] Which refusals make a loot refresh "Local only · off" rather than a failure.</summary>
public sealed class LootRefreshLocalOnlyTests
{
    [Fact]
    public void LocalOnlyRefusalsAreLocalOnlyWhereverTheyAreWrapped()
    {
        Assert.True(TarkovDevLootSpawnRefreshService.IsLocalOnly(new TarkovDevOfflineException()));
        Assert.True(TarkovDevLootSpawnRefreshService.IsLocalOnly(
            new NetworkBlockedException(null, NetworkVerdict.LocalOnly)));
        Assert.True(TarkovDevLootSpawnRefreshService.IsLocalOnly(
            new InvalidOperationException("wrapped", new NetworkBlockedException(null, NetworkVerdict.LocalOnly))));
    }

    [Fact]
    public void ARealFailureOrAServiceSwitchIsNotLocalOnly()
    {
        Assert.False(TarkovDevLootSpawnRefreshService.IsLocalOnly(new HttpRequestException("No such host is known.")));
        Assert.False(TarkovDevLootSpawnRefreshService.IsLocalOnly(
            new NetworkBlockedException(NetworkService.SquadSharing, NetworkVerdict.SwitchedOff)));
    }
}
