using TarkovCompanion.App.Services;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// [#893] A second launch hands itself to the running copy instead of exiting with nothing on screen.
/// </summary>
/// <remarks>
/// The event is unnamed here because named events do not exist on this test host; the real
/// launch uses <see cref="InstanceActivation.EventName"/>. What is tested is the hand-over: the
/// page travels, the running copy is woken, and a launch with nobody to hand to says so.
/// </remarks>
public sealed class InstanceActivationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tc-activate-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TheRunningCopyIsWokenWithThePageTheSecondLaunchNamed()
    {
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset);
        var woken = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var running = InstanceActivation.Listen(_directory, page => woken.TrySetResult(page), () => signal);

        Assert.NotNull(running);
        Assert.True(InstanceActivation.TrySignal(_directory, "raid", () => signal));
        Assert.Equal("raid", await woken.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task APlainSecondLaunchWakesItWithNoPage()
    {
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset);
        var woken = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var running = InstanceActivation.Listen(_directory, page => woken.TrySetResult(page), () => signal);

        Assert.True(InstanceActivation.TrySignal(_directory, page: null, () => signal));
        Assert.Null(await woken.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void WithNobodyListeningTheSecondLaunchIsToldSo() =>
        Assert.False(InstanceActivation.TrySignal(_directory, "raid", () => null));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
