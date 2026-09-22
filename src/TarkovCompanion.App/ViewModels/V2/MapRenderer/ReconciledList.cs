using System.Collections.ObjectModel;

namespace TarkovCompanion.App.ViewModels.V2.MapRenderer;

/// <summary>
/// A list the map's markers and place names are bound to, brought up to date by changing only the
/// entries that are drawn differently.
/// </summary>
/// <remarks>
/// [#453] Clayton's build 2.0.1353 froze on the Raid page for 5 to 237 seconds at a time, over and
/// over, while three squadmates shared positions. The relay answered about three times a second,
/// every answer moved a teammate, and every move handed the view a new array of every marker and
/// every place name on the map. An ItemsControl given a new array throws all of its controls away
/// and builds them again from their templates, which is what the interface thread spent its time
/// on: measured headless on Shoreline, 93-96% busy and a click waiting up to 1.3 s.
///
/// Here a marker that is drawn the same is the same instance (see <see cref="Reuse"/>), so the
/// list stays the one the view already holds and a move replaces one entry, one control.
/// </remarks>
public sealed class ReconciledList<T> : ObservableCollection<T>
    where T : class
{
    /// <summary>Makes this list read as <paramref name="next"/>, replacing only positions that differ.</summary>
    public void Reconcile(IReadOnlyList<T> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var common = Math.Min(Count, next.Count);
        for (var index = 0; index < common; index++)
        {
            if (!ReferenceEquals(this[index], next[index]))
            {
                this[index] = next[index];
            }
        }

        while (Count > next.Count)
        {
            RemoveAt(Count - 1);
        }

        for (var index = Count; index < next.Count; index++)
        {
            Add(next[index]);
        }
    }

    /// <summary>
    /// <paramref name="next"/>, with each entry that <paramref name="drawsSame"/> as the entry of
    /// the same key in <paramref name="previous"/> swapped for that earlier instance.
    /// </summary>
    public static T[] Reuse(
        IEnumerable<T> previous,
        IReadOnlyList<T> next,
        Func<T, string> key,
        Func<T, T, bool> drawsSame)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(drawsSame);
        var earlier = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in previous)
        {
            earlier.TryAdd(key(item), item);
        }

        var result = new T[next.Count];
        for (var index = 0; index < next.Count; index++)
        {
            var item = next[index];
            result[index] = earlier.TryGetValue(key(item), out var old) && drawsSame(old, item) ? old : item;
        }

        return result;
    }
}
