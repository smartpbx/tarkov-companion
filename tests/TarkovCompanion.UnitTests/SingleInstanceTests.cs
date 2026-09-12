using TarkovCompanion.App.Services;

namespace TarkovCompanion.UnitTests;

public sealed class SingleInstanceTests
{
    [Fact]
    public void TheSecondCopyIsTurnedAwayAndTheLockComesBackAfterwards()
    {
        // Named locks behave differently off Windows, and the companion only runs there.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = "TarkovCompanion.Tests." + Guid.NewGuid().ToString("N");

        var first = SingleInstance.TryAcquire(name);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(name));

        // Releasing has to actually release, or a crash would lock the player out until they
        // restarted the machine, which is far worse than the problem this solves.
        first.Dispose();
        using var afterwards = SingleInstance.TryAcquire(name);
        Assert.NotNull(afterwards);
    }

    [Fact]
    public void DifferentNamesDoNotBlockEachOther()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var one = SingleInstance.TryAcquire("TarkovCompanion.Tests." + Guid.NewGuid().ToString("N"));
        using var two = SingleInstance.TryAcquire("TarkovCompanion.Tests." + Guid.NewGuid().ToString("N"));

        Assert.NotNull(one);
        Assert.NotNull(two);
    }
}
