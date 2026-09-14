using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>How much of one person's quest list is on one map.</summary>
/// <param name="Name">Who, as they are named in the group.</param>
/// <param name="Count">How many of their quests have something to do here.</param>
public sealed record TonightMemberQuests(string Name, int Count);

/// <summary>
/// One map, and what the group has to do on it.
/// </summary>
/// <param name="MapId">The map, as the quest catalog names it.</param>
/// <param name="Yours">How many of your own quests have something to do here.</param>
/// <param name="YoursPinned">How many of those you pinned, which is you saying they matter.</param>
/// <param name="Others">Everyone else who has anything to do here, most first.</param>
public sealed record TonightMapRow(
    string MapId,
    int Yours,
    int YoursPinned,
    IReadOnlyList<TonightMemberQuests> Others)
{
    /// <summary>Quests across the whole group, which is what ranks the map.</summary>
    public int Total => Yours + Others.Sum(other => other.Count);
}

/// <summary>
/// Ranks maps by where the group's quests overlap.
/// </summary>
/// <remarks>
/// <para>
/// The decision a squad makes before any other one: which map to queue. Everything else this
/// companion does helps with a raid already chosen, and the choosing was done by somebody
/// reading four quest lists out loud over voice.
/// </para>
/// <para>
/// A quest counts for a map when that map is where its objectives are — the same rule the map
/// layer uses to decide what to draw, so a row's count is exactly what clicking it lights up.
/// An objective that names no map falls back to the task's primary map, and a task whose
/// objectives name nothing anywhere counts nowhere, because there is nothing to go and do.
/// </para>
/// <para>
/// Counted per quest rather than per objective. A quest with four things to do on Customs is
/// one reason to queue Customs, and counting it four times would rank a map by how finely its
/// quests happen to be broken up.
/// </para>
/// <para>
/// Squadmates arrive as catalog ids and are resolved here against the local board, so a
/// squadmate running a newer catalog contributes the quests this machine knows about and is
/// silent about the rest. That is the honest answer: this cannot rank a map by a quest it has
/// never heard of.
/// </para>
/// </remarks>
public static class TonightMaps
{
    /// <summary>How many maps are worth offering.</summary>
    /// <remarks>
    /// There are about a dozen and the point is a decision, not a table. Past the sixth the
    /// counts are low enough that the list is answering a question nobody asked.
    /// </remarks>
    public const int Limit = 6;

    /// <summary>
    /// Which maps the group's quests point at, best first.
    /// </summary>
    /// <param name="board">Every task the local catalog knows, with this profile's progress.</param>
    /// <param name="members">The rest of the group, as they last described themselves.</param>
    /// <param name="limit">How many rows to return.</param>
    public static IReadOnlyList<TonightMapRow> Rank(
        IReadOnlyList<QuestSummaryReadModel> board,
        IReadOnlyList<GroupMemberView> members,
        int limit = Limit)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(members);
        if (limit <= 0)
        {
            return [];
        }

        var maps = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<string, QuestSummaryReadModel>(StringComparer.Ordinal);
        foreach (var task in board)
        {
            byId[task.TaskId] = task;
            if (!Working(task))
            {
                continue;
            }

            foreach (var mapId in MapsOf(task))
            {
                var tally = Get(maps, mapId);
                tally.Yours++;
                if (task.IsPinned)
                {
                    tally.YoursPinned++;
                }
            }
        }

        foreach (var member in members)
        {
            // Once per quest per member, however many times they sent the id. A duplicate is
            // a sender's mistake and must not become a squadmate who wants a map twice.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var taskId in member.QuestIds)
            {
                if (!seen.Add(taskId) || !byId.TryGetValue(taskId, out var task))
                {
                    continue;
                }

                foreach (var mapId in MapsOf(task))
                {
                    Get(maps, mapId).Add(member.Name);
                }
            }
        }

        return
        [
            .. maps
                .Select(entry => entry.Value.ToRow(entry.Key))
                .Where(row => row.Total > 0)
                .OrderByDescending(row => row.Total)
                .ThenByDescending(row => row.Yours)
                .ThenByDescending(row => row.YoursPinned)
                .ThenBy(row => row.MapId, StringComparer.OrdinalIgnoreCase)
                .Take(limit),
        ];
    }

    /// <summary>Pinned or active, which is the same list the group is sent.</summary>
    private static bool Working(QuestSummaryReadModel task) =>
        task.IsPinned || task.RecordedState == RecordedTaskState.Active;

    /// <summary>Where this quest asks you to go, once per map.</summary>
    private static IEnumerable<string> MapsOf(QuestSummaryReadModel task) => task.Objectives
        .SelectMany(objective => MapsOf(objective, task.PrimaryMapId))
        .Where(mapId => !string.IsNullOrWhiteSpace(mapId))
        .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where one objective is, falling back to the quest's own map when it says nothing.
    /// </summary>
    /// <remarks>
    /// The fallback is per objective rather than per quest, because that is how the map layer
    /// places them: a quest with one objective on Customs and one that names nowhere puts the
    /// second at its primary map, and this must count the same two places the map would draw.
    /// </remarks>
    private static IReadOnlyList<string> MapsOf(QuestObjectiveReadModel objective, string? primaryMapId) =>
        objective.MapIds.Count > 0
            ? objective.MapIds
            : primaryMapId is { } primary
                ? [primary]
                : [];

    private static Tally Get(Dictionary<string, Tally> maps, string mapId)
    {
        if (!maps.TryGetValue(mapId, out var tally))
        {
            tally = new();
            maps[mapId] = tally;
        }

        return tally;
    }

    /// <summary>One map's running counts while the lists are being walked.</summary>
    private sealed class Tally
    {
        private readonly Dictionary<string, int> _others = new(StringComparer.CurrentCultureIgnoreCase);

        public int Yours { get; set; }

        public int YoursPinned { get; set; }

        public void Add(string name) =>
            _others[name] = _others.GetValueOrDefault(name) + 1;

        public TonightMapRow ToRow(string mapId) => new(
            mapId,
            Yours,
            YoursPinned,
            [
                .. _others
                    .OrderByDescending(entry => entry.Value)
                    .ThenBy(entry => entry.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(entry => new TonightMemberQuests(entry.Key, entry.Value)),
            ]);
    }
}
