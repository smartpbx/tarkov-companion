namespace TarkovCompanion.RaidPerfHarness;

/// <summary>A run of measurements, summarised the way a frame budget is argued about.</summary>
internal sealed class Samples
{
    private readonly List<double> _values = [];

    public int Count => _values.Count;

    public void Add(double value) => _values.Add(value);

    public double Mean => _values.Count == 0 ? 0 : _values.Average();

    public double Max => _values.Count == 0 ? 0 : _values.Max();

    public double Sum => _values.Sum();

    public double Percentile(double fraction)
    {
        if (_values.Count == 0)
        {
            return 0;
        }

        var ordered = _values.Order().ToArray();
        return ordered[Math.Clamp((int)Math.Ceiling(fraction * ordered.Length) - 1, 0, ordered.Length - 1)];
    }

    public int CountAbove(double threshold) => _values.Count(value => value > threshold);

    public Dictionary<string, double> Summary(string unit = "") => new()
    {
        ["count"] = Count,
        ["mean" + unit] = Math.Round(Mean, 3),
        ["p50" + unit] = Math.Round(Percentile(0.50), 3),
        ["p95" + unit] = Math.Round(Percentile(0.95), 3),
        ["p99" + unit] = Math.Round(Percentile(0.99), 3),
        ["max" + unit] = Math.Round(Max, 3),
    };
}
