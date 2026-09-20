using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.UnitTests.Diagnostics;

/// <summary>
/// #453: work nobody awaits still reports what it threw, when it throws, in the player's log.
/// </summary>
[Collection(CrashLogCollection.Name)]
public sealed class ObservedTasksTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tc-observed-{Guid.NewGuid():N}");

    public ObservedTasksTests() => CrashLog.Install(_directory);

    public void Dispose()
    {
        CrashLog.Detach();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task AFaultInUnawaitedWorkIsWrittenToTheLogWithItsSurface()
    {
        var gate = new TaskCompletionSource();
        FailsLaterAsync(gate.Task).Observe("hideout-page", "reload");

        gate.SetResult();
        var log = await ReadLogUntilAsync("workspace-fault/hideout-page");

        Assert.Contains("reload", log, StringComparison.Ordinal);
        Assert.Contains("the catalog went away", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationAndSuccessWriteNothing()
    {
        Task.CompletedTask.Observe("plan", "load");
        ((Task?)null).Observe("plan", "load");
        Task.FromCanceled(new CancellationToken(canceled: true)).Observe("plan", "load");

        await Task.Delay(100);

        Assert.DoesNotContain("workspace-fault", File.ReadAllText(CrashLog.FilePath!), StringComparison.Ordinal);
    }

    private static async Task FailsLaterAsync(Task gate)
    {
        await gate.ConfigureAwait(false);
        throw new InvalidOperationException("the catalog went away");
    }

    private static async Task<string> ReadLogUntilAsync(string marker)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var text = File.ReadAllText(CrashLog.FilePath!);
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return text;
            }

            await Task.Delay(20);
        }

        return File.ReadAllText(CrashLog.FilePath!);
    }
}
