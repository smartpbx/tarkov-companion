using System.Diagnostics;
using System.Globalization;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// Runs each step of shutdown under its own deadline and records what it cost.
/// </summary>
/// <remarks>
/// Every wait on the way out used to be either unbounded or bounded against nothing in
/// particular, and the budgets nested rather than shared: the feature lifecycle and the
/// background-work supervisor each stop within ten seconds, the interface waited five more for
/// initialisation work that its own remark says can never complete once the dispatcher has
/// stopped, and the whole lot sat inside a fifteen-second total. Fifteen seconds is exactly what
/// those two add up to, so a shutdown that used its budget used all of it.
///
/// That is what the Windows page gallery has been reporting as "The packaged app required forced
/// termination after '&lt;page&gt;'": it closes the main window and gives the process twenty
/// seconds, which has to cover the window closing, the whole teardown and the process ending. A
/// five-second margin over a budget that is routinely spent is not a margin, which is why the
/// failure moves from page to page between runs and why the same shape turns up in Clayton's own
/// logs as a run that never reached its own shutdown.
///
/// So: one budget for the whole of shutdown, taken from it by each step in turn, and a line in
/// the log saying where it went. The numbers are the point. Before this, a slow close left
/// nothing behind but the fact that it had been slow.
/// </remarks>
internal sealed class ShutdownStages(TimeSpan budget)
{
    private readonly List<string> _costs = [];
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();

    /// <summary>How much of the budget is left, never less than zero.</summary>
    public TimeSpan Remaining
    {
        get
        {
            var left = budget - _elapsed.Elapsed;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>Whether every step so far finished inside its share of the budget.</summary>
    public bool WithinBudget { get; private set; } = true;

    /// <summary>
    /// Awaits one step for at most <paramref name="allowance"/>, or whatever is left.
    /// </summary>
    /// <remarks>
    /// The step is abandoned rather than cancelled when it overruns: by this point cancellation
    /// has already been requested, everything durable has committed in its own transaction, and
    /// what remains is a courtesy. Abandoning it is the only thing that is actually bounded —
    /// waiting on a cancellation that a step never observes is how this got slow in the first
    /// place.
    /// </remarks>
    public async Task RunAsync(string name, Func<Task> step, TimeSpan allowance)
    {
        var deadline = allowance < Remaining ? allowance : Remaining;
        var started = _elapsed.Elapsed;
        var overran = false;
        try
        {
            if (deadline <= TimeSpan.Zero)
            {
                overran = true;
            }
            else
            {
                await step().WaitAsync(deadline).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            overran = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            CrashLog.Write("shutdown-failure", $"{name}: {exception}");
        }

        WithinBudget &= !overran;
        _costs.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"{name} {(_elapsed.Elapsed - started).TotalMilliseconds:0}ms{(overran ? " (abandoned)" : string.Empty)}"));
    }

    /// <summary>
    /// Records a step that was deliberately not run, and why.
    /// </summary>
    /// <remarks>
    /// A step nobody waited for is not the same as a step that finished, and the log should not
    /// let the two look alike. The one this exists for is a startup still in flight at shutdown:
    /// its continuations are posted to a dispatcher that has stopped, so it cannot complete, and
    /// waiting on it was five seconds of a fifteen-second budget spent on a certainty.
    /// </remarks>
    public void Skip(string name, string reason) => _costs.Add($"{name} skipped ({reason})");

    /// <summary>Where the time went, in the order it was spent.</summary>
    public string Report() => string.Create(
        CultureInfo.InvariantCulture,
        $"{_elapsed.Elapsed.TotalMilliseconds:0}ms total · {string.Join(" · ", _costs)}");
}
