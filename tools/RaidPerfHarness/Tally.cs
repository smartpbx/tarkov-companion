using System.ComponentModel;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.RaidPerfHarness;

/// <summary>
/// Counts what fired, by name, so "a scene rebuild every second" can be traced to the property
/// that asked for it instead of guessed at.
/// </summary>
internal sealed class Tally
{
    private readonly Dictionary<string, int> _counts = [];
    private readonly object _gate = new();

    public Tally(HarnessHost host)
    {
        Watch("map", host.Main.Map);
        Watch("raid-page", host.Main.Raid);
        Watch("cockpit", host.Raid);
        Watch("main", host.Main);
        Watch("shell", host.Shell);
        var store = host.Services.GetService(typeof(IRuntimeStateStore)) as IRuntimeStateStore;
        if (store is not null)
        {
            store.Changed += (_, _) => Add("store.Changed");
        }
    }

    public Dictionary<string, int> Snapshot()
    {
        lock (_gate)
        {
            return new(_counts);
        }
    }

    /// <summary>What fired between two snapshots, busiest first.</summary>
    public static Dictionary<string, int> Since(Dictionary<string, int> before, Dictionary<string, int> after) => after
        .Select(pair => (pair.Key, Count: pair.Value - before.GetValueOrDefault(pair.Key)))
        .Where(item => item.Count > 0)
        .OrderByDescending(item => item.Count)
        .Take(60)
        .ToDictionary(item => item.Key, item => item.Count);

    private void Watch(string owner, INotifyPropertyChanged source) =>
        source.PropertyChanged += (_, args) => Add($"{owner}.{args.PropertyName}");

    private void Add(string name)
    {
        lock (_gate)
        {
            _counts[name] = _counts.GetValueOrDefault(name) + 1;
        }
    }
}
