using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// <c>TARKOV_ALLOC_TICKS=1</c>: which types were allocated, on which thread, between two stall
/// reports, from the runtime's own allocation sample (one event per ~100 KB).
/// </summary>
/// <remarks>
/// [#678] A map switch spent as long paused for the collector as it did working, and a CPU
/// profile shows the pause but not who filled the heap. In-process, so it needs no trace tool.
/// </remarks>
internal sealed class AllocationTicks : EventListener
{
    private static AllocationTicks? _listener;
    private readonly ConcurrentDictionary<(string Type, bool Ui), long> _bytes = new();
    private static int _uiThreadId;

    public static void StartIfAsked()
    {
        if (Environment.GetEnvironmentVariable("TARKOV_ALLOC_TICKS") == "1")
        {
            _uiThreadId = Environment.CurrentManagedThreadId;
            _listener = new AllocationTicks();
        }
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        if (data.EventName is not { } name || !name.StartsWith("GCAllocationTick", StringComparison.Ordinal) || data.Payload is null)
        {
            return;
        }

        var type = data.PayloadNames?.IndexOf("TypeName") is { } typeIndex and >= 0 ? data.Payload[typeIndex] as string ?? "?" : "?";
        var amount = data.PayloadNames?.IndexOf("AllocationAmount64") is { } amountIndex and >= 0 ? Convert.ToInt64(data.Payload[amountIndex], CultureInfo.InvariantCulture) : 100_000;
        _bytes.AddOrUpdate((type, Environment.CurrentManagedThreadId == _uiThreadId), amount, (_, running) => running + amount);
    }

    /// <summary>The top types since the last call, then starts counting again.</summary>
    public static void Report()
    {
        if (_listener is not { } listener)
        {
            return;
        }

        var snapshot = listener._bytes.ToArray();
        listener._bytes.Clear();
        var ui = snapshot.Where(entry => entry.Key.Ui).Sum(entry => entry.Value);
        var all = snapshot.Sum(entry => entry.Value);
        var top = string.Join(", ", snapshot.OrderByDescending(entry => entry.Value).Take(10)
            .Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Key.Type}{(entry.Key.Ui ? "" : " [worker]")} {entry.Value / 1048576.0:0.0}")));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  allocated ~{all / 1048576} MB ({ui / 1048576} MB on the UI thread): {top}"));
    }
}
