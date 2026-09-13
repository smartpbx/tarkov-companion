using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Which scans are worth putting in front of somebody.
/// </summary>
/// <remarks>
/// The game's screenshot key fires on everything a player photographs, and most of that is the
/// game world. Now that those scans reach the interface, the filter is what stops a picture of
/// a wall replacing a good reading of an item seconds later.
/// </remarks>
public sealed class ScanReportingTests
{
    [Fact]
    public void AScanThatFoundSomethingIsWorthReporting()
    {
        Assert.True(Result(available: true, succeeded: true).IsWorthReporting);
    }

    [Fact]
    public void AScanOfAWallIsNot()
    {
        // Ran, worked, found nothing recognisable. Reporting it would overwrite the answer
        // somebody actually wanted with the news that a corridor is not an item.
        Assert.False(Result(available: true, succeeded: false).IsWorthReporting);
    }

    [Fact]
    public void AScanThatCouldNotRunAtAllIsWorthReporting()
    {
        // "The recogniser is unavailable" is the one failure worth interrupting for, because
        // it is the difference between finding nothing and being unable to look.
        Assert.True(Result(available: false, succeeded: false).IsWorthReporting);
    }

    private static ScanExecutionResult Result(bool available, bool succeeded) => new(
        available,
        succeeded,
        succeeded ? "item-1" : null,
        succeeded ? "Graphics card" : null,
        null,
        null,
        null,
        TarkovCompanion.Core.Common.Confidence.Unknown,
        DateTimeOffset.UnixEpoch,
        "game screenshot",
        "test");
}
