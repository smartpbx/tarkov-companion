using System.Collections.Concurrent;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>
/// What this companion's game said about the rest of its party, named and ready to send.
/// </summary>
/// <remarks>
/// The selection rules are in <see cref="GroupKitMirror"/>, which is pure and tested. This is
/// the part that has to touch the world: it reads the current party out of the runtime state
/// and turns template ids into item names.
///
/// Names are cached for the life of the process because an id's name never changes, and the
/// publish loop restates the same party every few seconds; without the cache a squad of five
/// would be thirty database round trips a tick for an answer that is always the same. An id
/// that cannot be resolved is left out of the cache so a later catalog sync can still fill it,
/// and left out of the published kit so nobody is shown a raw id.
/// </remarks>
public sealed class GroupKitShare(IRuntimeStateStore state, IItemRepository items)
{
    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<ObservedKit>> GetAsync(CancellationToken cancellationToken)
    {
        var squad = state.Current.Squad;
        if (squad.Members.Count == 0)
        {
            return [];
        }

        await ResolveAsync(GroupKitMirror.UnresolvedIds(squad, _names.ContainsKey), cancellationToken)
            .ConfigureAwait(false);
        return GroupKitMirror.Describe(squad, id => _names.TryGetValue(id, out var name) ? name : null);
    }

    private async Task ResolveAsync(IReadOnlyList<string> unresolved, CancellationToken cancellationToken)
    {
        foreach (var templateId in unresolved)
        {
            try
            {
                if (await items.GetAsync(templateId, cancellationToken).ConfigureAwait(false) is { } item)
                {
                    _names[templateId] = string.IsNullOrWhiteSpace(item.ShortName) ? item.Name : item.ShortName;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A group exchange must not fail because the item catalog could not be read.
                // Whatever resolved so far still goes; the rest is left out and tried again.
                return;
            }
        }
    }
}
