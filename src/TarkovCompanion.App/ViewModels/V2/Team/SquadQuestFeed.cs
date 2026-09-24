using TarkovCompanion.App.Localization;
using Avalonia.Threading;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.App.ViewModels.V2.Team;

/// <summary>
/// [#780] The squad's quests, named from this player's own catalog, for Team, Raid and Plan.
/// </summary>
/// <remarks>
/// One picture for the three pages, recomputed only when what the squad said about quests changed:
/// the runtime snapshot changes several times a second (positions, the raid clock), and resolving
/// on each of those would read the catalog for nothing. <see cref="Changed"/> is raised on the
/// interface thread.
/// </remarks>
public sealed class SquadQuestFeed
{
    private readonly IRuntimeStateStore _runtime;
    private readonly SquadQuestResolver _resolver;
    private readonly GroupQuestShare? _own;
    private readonly Action<Action> _post;
    private readonly object _gate = new();
    private string? _signature;
    private int _generation;

    public SquadQuestFeed(
        IRuntimeStateStore runtime,
        SquadQuestResolver resolver,
        GroupQuestShare? own = null,
        // Where Changed is raised; the interface thread unless a test says otherwise.
        Action<Action>? post = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _own = own;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        _runtime.Changed += (_, _) => Refresh();
        if (_own is not null)
        {
            _own.Changed += () => Refresh(force: true);
        }

        Refresh();
    }

    /// <summary>The squad's quests as last resolved; empty with nobody in the group.</summary>
    public SquadQuestPicture Picture { get; private set; } = SquadQuestPicture.Empty;

    /// <summary>Raised on the interface thread after <see cref="Picture"/> changed.</summary>
    public event Action? Changed;

    /// <summary>Whether a squadmate (not this player) has this quest active.</summary>
    public bool SquadHas(string taskId) =>
        Picture.Members.Any(member => !member.IsSelf && member.Quests.Any(quest => quest.TaskId == taskId));

    /// <summary>The squadmates who have this quest active, by name.</summary>
    public IReadOnlyList<string> SquadmatesOn(string taskId) =>
        [.. Picture.Members
            .Where(member => !member.IsSelf && member.Quests.Any(quest => quest.TaskId == taskId))
            .Select(member => member.Name)];

    /// <summary>Resolves again when the squad's quest ids or objectives changed.</summary>
    public void Refresh(bool force = false)
    {
        var members = _runtime.Current.Group.Members;
        var signature = string.Join('|', members.Select(member =>
            $"{member.Name}:{string.Join(',', member.QuestIds)}:{string.Join(',', member.Objectives.Select(objective => $"{objective.ObjectiveId}={objective.Count}"))}"));
        int generation;
        lock (_gate)
        {
            if (!force && signature == _signature)
            {
                return;
            }

            _signature = signature;
            generation = ++_generation;
        }

        _ = ResolveAsync(members, generation);
    }

    private async Task ResolveAsync(IReadOnlyList<GroupMemberView> members, int generation)
    {
        try
        {
            var sharing = members.Where(member => member.QuestIds.Count > 0).ToList();
            SquadQuestPicture picture;
            if (sharing.Count == 0)
            {
                picture = SquadQuestPicture.Empty;
            }
            else
            {
                var input = new List<SquadMemberQuestIds>();
                if (_own is not null)
                {
                    var own = await _own.GetAsync(CancellationToken.None).ConfigureAwait(false);
                    input.Add(new(TeamText.You, true, own.TaskIds, [.. own.Objectives.Select(objective =>
                        new GroupObjectiveView(objective.TaskId, objective.ObjectiveId, objective.Count))]));
                }

                input.AddRange(sharing.Select(member => new SquadMemberQuestIds(member.Name, false, member.QuestIds, member.Objectives)));
                picture = await _resolver.ResolveAsync(input, CancellationToken.None).ConfigureAwait(false);
            }

            lock (_gate)
            {
                if (generation != _generation)
                {
                    return;
                }
            }

            _post(() =>
            {
                Picture = picture;
                Changed?.Invoke();
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // An extra: the squad's quests failing to resolve must not take a page with them.
            CrashLog.Write("workspace-fault/squad-quests", exception.ToString());
        }
    }
}
