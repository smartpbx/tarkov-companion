using System.Diagnostics.Tracing;

namespace TarkovCompanion.RaidPerfHarness;

/// <summary>
/// Which types the process allocates, by bytes, from the runtime's own allocation-tick events.
/// </summary>
/// <remarks>
/// The runtime raises one event per ~100 KB allocated and names the type that crossed the
/// line, so over a few seconds the biggest allocators dominate the tally without any profiler
/// being attached. It is a sample, not a count: good for "what is the 8 MB a second", not for
/// exact bytes.
/// </remarks>
internal sealed class AllocationSampler : EventListener
{
    private readonly Dictionary<string, long> _bytes = [];
    private readonly object _gate = new();
    private EventSource? _runtime;

    public void Start()
    {
        if (_runtime is not null)
        {
            EnableEvents(_runtime, EventLevel.Verbose, (EventKeywords)0x1);
        }
    }

    public void Stop()
    {
        if (_runtime is not null)
        {
            DisableEvents(_runtime);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _bytes.Clear();
        }
    }

    public Dictionary<string, double> Top(int count)
    {
        lock (_gate)
        {
            return _bytes.OrderByDescending(pair => pair.Value).Take(count)
                .ToDictionary(pair => pair.Key, pair => Math.Round(pair.Value / 1048576.0, 2));
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
        {
            _runtime = eventSource;
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventName is not ("GCAllocationTick_V4" or "GCAllocationTick_V3") || eventData.Payload is null)
        {
            return;
        }

        var names = eventData.PayloadNames;
        var type = names is null ? null : eventData.Payload[names.IndexOf("TypeName")] as string;
        var amount = names is null ? 0 : Convert.ToInt64(eventData.Payload[names.IndexOf("AllocationAmount64")]);
        if (type is null)
        {
            return;
        }

        lock (_gate)
        {
            _bytes[type] = _bytes.GetValueOrDefault(type) + amount;
        }
    }
}
