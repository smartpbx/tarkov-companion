using System.Text;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

/// <summary>
/// The text the quest search matches its words against, joined once per board rather than once per
/// keystroke.
/// </summary>
/// <remarks>
/// One quest's text is a join over its name, its trader, every objective's description and the name
/// of every map its objectives name. Building that inside the filter meant a synced board of 515
/// quests joined 515 strings — about 1.8 MB — for every character typed, and threw all of them
/// away. What the text is built from changes when the board is read and when the map catalog
/// finally names its maps. Neither of those is a keystroke.
///
/// Keyed by task id. A quest the index has not heard of falls back to its own name, so a board read
/// between two builds is searchable by name rather than not searchable at all.
/// </remarks>
internal sealed class PlanSearchIndex
{
    private readonly Dictionary<string, string> _text;

    private PlanSearchIndex(Dictionary<string, string> text) => _text = text;

    public static PlanSearchIndex Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    public int Count => _text.Count;

    /// <summary>Joins every quest's searchable text, naming maps through <paramref name="nameOfMap"/>.</summary>
    public static PlanSearchIndex Build(IReadOnlyList<QuestSummaryReadModel> tasks, Func<string, string> nameOfMap)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(nameOfMap);
        var text = new Dictionary<string, string>(tasks.Count, StringComparer.Ordinal);
        var builder = new StringBuilder();
        var maps = new List<string>();
        foreach (var task in tasks)
        {
            builder.Clear()
                .Append(task.Name)
                .Append('\n')
                .Append(task.TraderName ?? string.Empty)
                .Append('\n')
                .Append(task.TraderId ?? string.Empty);
            maps.Clear();
            foreach (var objective in task.Objectives)
            {
                builder.Append('\n').Append(objective.Description);
                foreach (var mapId in objective.MapIds)
                {
                    // Each map named once however many objectives are on it, as the joined text
                    // always did: the search reads names, and a name twice reads the same.
                    if (!maps.Contains(mapId, StringComparer.OrdinalIgnoreCase))
                    {
                        maps.Add(mapId);
                    }
                }
            }

            foreach (var mapId in maps)
            {
                builder.Append('\n').Append(nameOfMap(mapId));
            }

            text[task.TaskId] = builder.ToString();
        }

        return new(text);
    }

    /// <summary>What the search reads on one quest.</summary>
    public string TextFor(QuestSummaryReadModel task) =>
        _text.TryGetValue(task.TaskId, out var text) ? text : task.Name;
}
