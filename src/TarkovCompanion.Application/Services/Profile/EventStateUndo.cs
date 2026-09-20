using TarkovCompanion.Core.Domain.Events;

namespace TarkovCompanion.Application.Services.Profile;

/// <summary>One recorded change of an event item's state, and what it replaced.</summary>
public sealed record EventStateChange(
    string EventId,
    string ItemId,
    string ItemName,
    EventItemState Previous,
    EventItemState Current);

/// <summary>
/// The last state change made on the Events page, kept so one press takes it back (#285). One
/// step, not a history: "Allergic" pressed on the wrong row is noticed at once or not at all, and
/// a record that matters this much should not be reachable by pressing Undo six times.
/// </summary>
public sealed class EventStateUndo
{
    public EventStateChange? Last { get; private set; }

    public bool CanUndo => Last is not null;

    /// <summary>Remembers a change. Setting a state to what it already was is not a change.</summary>
    public void Record(EventStateChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Previous != change.Current)
        {
            Last = change;
        }
    }

    /// <summary>Hands back the change to reverse and forgets it, so an undo cannot be undone twice.</summary>
    public EventStateChange? Take()
    {
        var last = Last;
        Last = null;
        return last;
    }

    /// <summary>Drops a remembered change whose event or item no longer exists.</summary>
    public void Forget(string eventId, string? itemId = null)
    {
        if (Last is { } last &&
            string.Equals(last.EventId, eventId, StringComparison.Ordinal) &&
            (itemId is null || string.Equals(last.ItemId, itemId, StringComparison.Ordinal)))
        {
            Last = null;
        }
    }
}
