using TarkovCompanion.App.Services;

namespace TarkovCompanion.UnitTests;

public sealed class SingleInstanceTests
{
    [Fact]
    public void TheSecondCopyIsTurnedAwayAndTheLockComesBackAfterwards()
    {
        // Named locks are a Windows facility here and the companion only runs there. xUnit 2
        // has no conditional skip, so this returns rather than pretending to assert; the
        // Windows job in CI is where it actually runs.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = "TarkovCompanion.Tests." + Guid.NewGuid().ToString("N");

        var first = SingleInstance.TryAcquire(name);
        Assert.NotNull(first);
        Assert.False(AcquiredOnAnotherThread(name));

        // Releasing has to actually release, or a crash would lock the player out until they
        // restarted the machine, which is far worse than the problem this solves.
        first.Dispose();
        Assert.True(AcquiredOnAnotherThread(name));
    }

    [Fact]
    public void TwoDifferentLocksDoNotBlockEachOther()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var one = SingleInstance.TryAcquire("TarkovCompanion.Tests." + Guid.NewGuid().ToString("N"));

        Assert.NotNull(one);
        Assert.True(AcquiredOnAnotherThread("TarkovCompanion.Tests." + Guid.NewGuid().ToString("N")));
    }

    /// <summary>
    /// Tries to take the lock from somewhere that is not this thread.
    /// </summary>
    /// <remarks>
    /// A mutex belongs to a thread, not to a process, and the thread holding one may take it
    /// again as often as it likes. Asking twice from the test thread therefore always succeeds
    /// and proves nothing, which is exactly how the first version of this test passed locally
    /// and failed to describe what the guard does. A second thread stands in for the second
    /// copy of the application, because ownership is per-thread either way.
    /// </remarks>
    private static bool AcquiredOnAnotherThread(string name)
    {
        var acquired = false;
        var thread = new Thread(() =>
        {
            using var instance = SingleInstance.TryAcquire(name);
            acquired = instance is not null;
        });
        thread.Start();
        thread.Join();
        return acquired;
    }
}
