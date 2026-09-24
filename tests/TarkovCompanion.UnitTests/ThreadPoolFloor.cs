using System.Runtime.CompilerServices;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Starts this test process with enough pool threads that a queued work item runs when it is queued.
/// </summary>
/// <remarks>
/// Nearly five thousand tests run in parallel here, and a fair number of them block a pool thread
/// on purpose (a headless UI session's GetResult, a hand-over's bounded Wait). On a four-core CI
/// runner the pool starts with four threads and adds roughly one a second after that, so work
/// queued behind the blocked ones waited seconds to start. That is how UpdateHandOverTests saw a
/// synchronous flush miss its two-second budget entirely (#818's gate: "flush 2s" never recorded),
/// and it is the shape of the other one-off timeouts in the suite. Nothing measured here is meant
/// to include the pool's injection delay, which belongs to this process, not to the application.
/// </remarks>
internal static class ThreadPoolFloor
{
    private const int Minimum = 64;

#pragma warning disable CA2255 // A test assembly is the process's entry point in all but name.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Raise()
    {
        ThreadPool.GetMinThreads(out var workers, out var completions);
        ThreadPool.SetMinThreads(Math.Max(workers, Minimum), Math.Max(completions, Minimum));
    }
}
