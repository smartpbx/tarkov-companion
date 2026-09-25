using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.UnitTests.Diagnostics;

/// <summary>
/// [#893] The loot-layer heartbeat stays out of the crash ring unless it has something to say.
/// </summary>
public sealed class LootLayerBuildLogTests
{
    /// <summary>
    /// Fails on main, which wrote every minute: forty such lines were the whole replay after the
    /// owner's run died on 2026-09-24, and none of them said what the player was doing.
    /// </summary>
    [Fact]
    public void AQuietMinuteIsNotWritten() =>
        Assert.False(LootLayerBuildLog.ShouldWrite(built: 0, longestMilliseconds: 1, TimeSpan.FromSeconds(61)));

    [Fact]
    public void AQuietWindowIsStillWrittenOnceInTenMinutes() =>
        Assert.True(LootLayerBuildLog.ShouldWrite(built: 0, longestMilliseconds: 1, LootLayerBuildLog.QuietEvery));

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 40)]
    public void AMinuteWithABuildOrASlowCallIsWritten(int built, double longest) =>
        Assert.True(LootLayerBuildLog.ShouldWrite(built, longest, TimeSpan.FromSeconds(60)));

    [Fact]
    public void NothingIsWrittenBeforeAMinuteHasPassed() =>
        Assert.False(LootLayerBuildLog.ShouldWrite(built: 3, longestMilliseconds: 90, TimeSpan.FromSeconds(30)));
}
