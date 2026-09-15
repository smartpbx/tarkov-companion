using System.Collections.Concurrent;
using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.UnitTests.V2Shell;

public sealed class V2ShellPersistenceQueueTests
{
    [Fact]
    public async Task Reset_is_ordered_between_the_save_already_running_and_the_final_state()
    {
        var observed = new ConcurrentQueue<string>();
        var firstSaveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new V2ShellPersistenceQueue(
            async (state, _) =>
            {
                observed.Enqueue($"save:{state.Address}");
                if (state.Address == "#/raid")
                {
                    firstSaveStarted.TrySetResult();
                    await releaseFirstSave.Task;
                }
            },
            _ =>
            {
                observed.Enqueue("reset");
                return Task.CompletedTask;
            });

        queue.QueueSave(State("#/raid"));
        await firstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var reset = queue.ResetAsync();
        queue.QueueSave(State("#/plan"));
        var dispose = queue.DisposeAsync(State("#/team"), suppressFinalSave: false).AsTask();

        releaseFirstSave.TrySetResult();
        await Task.WhenAll(reset, dispose).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["save:#/raid", "reset", "save:#/team"], observed);
    }

    [Fact]
    public async Task Close_during_reset_waits_for_reset_without_recreating_stale_state()
    {
        var observed = new ConcurrentQueue<string>();
        var resetStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new V2ShellPersistenceQueue(
            (state, _) =>
            {
                observed.Enqueue($"save:{state.Address}");
                return Task.CompletedTask;
            },
            async _ =>
            {
                observed.Enqueue("reset");
                resetStarted.TrySetResult();
                await releaseReset.Task;
            });

        var reset = queue.ResetAsync();
        await resetStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var dispose = queue.DisposeAsync(State("#/raid"), suppressFinalSave: true).AsTask();

        Assert.False(dispose.IsCompleted);
        releaseReset.TrySetResult();
        await Task.WhenAll(reset, dispose).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["reset"], observed);
    }

    private static V2ShellPreviewState State(string address) =>
        V2ShellPreviewState.For(V2ShellMode.VariantA) with { Address = address };
}
