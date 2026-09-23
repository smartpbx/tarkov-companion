using System.Collections;
using System.Collections.Specialized;
using Avalonia.Utilities;

namespace TarkovCompanion.App.Views.V2.Primitives;

/// <summary>
/// A mirror of a list that builds at most a few new entries per turn of the interface thread and
/// catches up on the following turns.
/// </summary>
/// <remarks>
/// [#678] A list control builds a control for every new entry in the turn that added it. On the
/// first Raid visit, and on a map switch, the marks and the side panel's rows arrived together:
/// about 1,100 controls built and styled in one turn, which held input for 0.4 to 1.7 s. Shown
/// through this list, the same controls are built a batch at a time, and a click waits for one
/// batch rather than for all of them.
///
/// The mirror follows the source position by position, the way <c>ReconciledList</c> changes:
/// an entry that is the same instance is left alone, a different one is replaced, and the tail
/// is added or removed. Removing costs nothing, so it always happens at once. Replacing and
/// adding build controls, so each turn may do <c>batch</c> of them; an entry still waiting to be
/// replaced keeps showing what it showed, so the list never shrinks and a scrolled list keeps its
/// place. An update that changes one or two entries (a squadmate moving) fits in one turn and
/// shows at once, as before. The very first entries wait for the turn after the list is made,
/// so a page that is being built paints before its lists fill in.
///
/// It follows the source through a weak subscription: the source is usually a view model that
/// outlives the control, and must not keep a mirror (and every control behind it) alive (#666).
/// </remarks>
public sealed class StagedList : IList, IReadOnlyList<object?>, INotifyCollectionChanged, IDisposable,
    IWeakEventSubscriber<NotifyCollectionChangedEventArgs>
{
    private readonly List<object?> _shown = [];
    private readonly Action<Action> _later;
    private readonly int _batch;
    private IList _source = Array.Empty<object?>();
    private int _allowance;
    private bool _growScheduled;
    private bool _disposed;

    /// <param name="batch">How many entries may be built in one turn.</param>
    /// <param name="later">Runs an action on a later turn, after input and rendering.</param>
    public StagedList(int batch, Action<Action> later)
    {
        ArgumentNullException.ThrowIfNull(later);
        ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);
        _batch = batch;
        _later = later;

        // Nothing is built in the turn that creates the list: that turn is also building the page
        // or the map around it, and paints first; the first batch follows on the next turn.
        _allowance = 0;
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>True while some of the source is not shown yet, or shows an older entry.</summary>
    public bool IsBehind => !Matches();

    public int Count => _shown.Count;

    public bool IsReadOnly => true;

    public bool IsFixedSize => false;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public object? this[int index]
    {
        get => _shown[index];
        set => throw new NotSupportedException();
    }

    /// <summary>Follows <paramref name="source"/> from now on, reusing what is already shown.</summary>
    public void SetSource(IList? source)
    {
        if (_source is INotifyCollectionChanged old)
        {
            WeakEvents.CollectionChanged.Unsubscribe(old, this);
        }

        _source = source ?? Array.Empty<object?>();
        if (!_disposed && _source is INotifyCollectionChanged changes)
        {
            WeakEvents.CollectionChanged.Subscribe(changes, this);
        }

        Sync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_source is INotifyCollectionChanged changes)
        {
            WeakEvents.CollectionChanged.Unsubscribe(changes, this);
        }
    }

    public IEnumerator<object?> GetEnumerator() => _shown.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Contains(object? value) => _shown.Contains(value);

    public int IndexOf(object? value) => _shown.IndexOf(value);

    public void CopyTo(Array array, int index) => ((ICollection)_shown).CopyTo(array, index);

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();

    void IWeakEventSubscriber<NotifyCollectionChangedEventArgs>.OnEvent(object? sender, WeakEvent ev, NotifyCollectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _source))
        {
            Sync();
        }
    }

    /// <summary>Brings the mirror as close to the source as this turn's allowance lets it.</summary>
    private void Sync()
    {
        if (_disposed)
        {
            return;
        }

        var source = _source;
        if (_shown.Count > source.Count)
        {
            var start = source.Count;
            var removed = _shown.GetRange(start, _shown.Count - start);
            _shown.RemoveRange(start, removed.Count);
            Raise(new(NotifyCollectionChangedAction.Remove, removed, start));
        }

        for (var index = 0; index < _shown.Count && _allowance > 0; index++)
        {
            var next = source[index];
            if (ReferenceEquals(_shown[index], next))
            {
                continue;
            }

            var old = _shown[index];
            _shown[index] = next;
            _allowance--;
            Raise(new(NotifyCollectionChangedAction.Replace, next, old, index));
        }

        var take = Math.Min(_allowance, source.Count - _shown.Count);
        if (take > 0)
        {
            var start = _shown.Count;
            var added = new List<object?>(take);
            for (var index = start; index < start + take; index++)
            {
                added.Add(source[index]);
            }

            _shown.AddRange(added);
            _allowance -= take;
            Raise(new(NotifyCollectionChangedAction.Add, added, start));
        }

        if (_allowance < _batch)
        {
            ScheduleGrow();
        }
    }

    private void ScheduleGrow()
    {
        if (_growScheduled || _disposed)
        {
            return;
        }

        _growScheduled = true;
        _later(Grow);
    }

    private void Grow()
    {
        _growScheduled = false;
        _allowance = _batch;
        Sync();
    }

    private bool Matches()
    {
        if (_shown.Count != _source.Count)
        {
            return false;
        }

        for (var index = 0; index < _shown.Count; index++)
        {
            if (!ReferenceEquals(_shown[index], _source[index]))
            {
                return false;
            }
        }

        return true;
    }

    private void Raise(NotifyCollectionChangedEventArgs e) => CollectionChanged?.Invoke(this, e);
}
