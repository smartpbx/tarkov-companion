using System.Security.Cryptography;
using TarkovCompanion.App.Services.Updates;

namespace TarkovCompanion.UnitTests.Updates;

/// <summary>
/// #888: the automatic update check survives a state file it cannot write, and the kept rollback
/// package is copied off the caller's thread.
/// </summary>
public sealed class UpdateResilienceTests : IDisposable
{
    private readonly string _stateFolder = Path.Combine(Path.GetTempPath(), "tc-update-resilience-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_stateFolder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task A_pin_that_cannot_be_cleared_still_lets_the_newer_build_be_offered()
    {
        var inner = new UpdateStateFile(_stateFolder);
        inner.Write(new UpdateState(Pin: new UpdatePin("1.0.100", "1.0.300", DateTimeOffset.UnixEpoch)));
        var store = new GatedStore(inner) { WritesFail = true };
        using var harness = new RoughChannelHarness(installedVersion: "1.0.100");
        harness.PublishAll("1.0.400", "1.0.300");
        var gateway = GatewayOver(harness, store);

        // Used to throw IOException out of the check, which ended the automatic loop for the run.
        var offered = await gateway.CheckAsync(CancellationToken.None);

        Assert.True(offered.CanDownload, offered.Status);
        Assert.Equal("1.0.400", offered.Available);
    }

    [Fact]
    public async Task The_kept_package_copy_does_not_run_on_the_callers_thread()
    {
        var store = new GatedStore(new UpdateStateFile(_stateFolder));
        using var harness = new RoughChannelHarness(installedVersion: "1.0.100");
        var keptName = $"{RoughChannelHarness.PackId}-1.0.100-full.nupkg";
        await File.WriteAllBytesAsync(Path.Combine(harness.Packages, keptName), RandomNumberGenerator.GetBytes(16 * 1024));
        harness.Publish("1.0.200");
        var gateway = GatewayOver(harness, store);
        Assert.True((await gateway.CheckAsync(CancellationToken.None)).CanDownload);

        store.HoldReads();
        Task<UpdateProgress>? download = null;
        using var returned = new ManualResetEventSlim();
        var caller = new Thread(() =>
        {
            download = gateway.DownloadAsync(CancellationToken.None);
            returned.Set();
        });
        caller.Start();
        try
        {
            // The copy is held inside the state read. The call must still hand back a task at once,
            // as a UI thread would need it to; on the old code it sat in the copy instead.
            Assert.True(returned.Wait(TimeSpan.FromSeconds(10)), "DownloadAsync blocked its caller on the kept-package copy");
        }
        finally
        {
            store.ReleaseReads();
            caller.Join(TimeSpan.FromSeconds(30));
        }

        var result = await download!;
        Assert.True(result.CanApply, result.Status);
        // Still before the download: the copy exists once the download has finished.
        Assert.Equal("1.0.100", store.Read().Kept?.Version);
        Assert.True(File.Exists(Path.Combine(store.KeptFolder, keptName)));
    }

    [Fact]
    public async Task A_failed_check_is_retried_sooner_and_the_loop_keeps_running()
    {
        using var stop = new CancellationTokenSource();
        var waits = new List<TimeSpan>();
        var outcomes = new Queue<bool>([false, false, true, false]);
        var checks = 0;

        await UpdateWatchLoop.RunAsync(
            static () => false,
            _ =>
            {
                checks++;
                return outcomes.Dequeue() ? Task.CompletedTask : throw new IOException("update-state.json is in use");
            },
            stop.Token,
            delay: (wait, _) =>
            {
                waits.Add(wait);
                if (waits.Count == 5)
                {
                    stop.Cancel();
                    throw new OperationCanceledException(stop.Token);
                }

                return Task.CompletedTask;
            });

        Assert.Equal(4, checks);
        Assert.Equal(
            [UpdateWatchLoop.FirstDelay, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), UpdateWatchLoop.Interval, TimeSpan.FromMinutes(15)],
            waits);
    }

    [Fact]
    public async Task The_loop_skips_a_check_while_an_update_is_in_progress()
    {
        using var stop = new CancellationTokenSource();
        var waits = 0;
        var checks = 0;

        await UpdateWatchLoop.RunAsync(
            static () => true,
            _ =>
            {
                checks++;
                return Task.CompletedTask;
            },
            stop.Token,
            delay: (_, _) =>
            {
                if (++waits == 3)
                {
                    stop.Cancel();
                }

                return Task.CompletedTask;
            });

        Assert.Equal(0, checks);
    }

    private static VelopackUpdateGateway GatewayOver(RoughChannelHarness harness, IUpdateStateStore store)
    {
        var transport = new DirectoryUpdateFeedTransport(harness.Feed);
        return VelopackUpdateGateway.Create(
            UpdateChannel.Rough,
            new HashVerifiedUpdateSource(transport, harness.Log),
            harness.Locator,
            harness.Log,
            transport,
            store);
    }

    /// <summary>A real state file whose writes can fail and whose reads can be held.</summary>
    private sealed class GatedStore(UpdateStateFile inner) : IUpdateStateStore
    {
        private readonly ManualResetEventSlim _reads = new(initialState: true);

        public bool WritesFail { get; init; }

        public string KeptFolder => inner.KeptFolder;

        public void HoldReads() => _reads.Reset();

        public void ReleaseReads() => _reads.Set();

        public UpdateState Read()
        {
            _reads.Wait(TimeSpan.FromSeconds(30));
            return inner.Read();
        }

        public void Write(UpdateState state)
        {
            if (WritesFail)
            {
                throw new IOException("update-state.json is in use by another process");
            }

            inner.Write(state);
        }
    }
}
