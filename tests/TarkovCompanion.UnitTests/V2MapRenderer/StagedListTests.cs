using System.Collections.ObjectModel;
using System.Collections.Specialized;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.Views.V2.Primitives;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [#678] The staged mirror builds at most a batch of entries per turn, keeps a scrolled list's
/// length while it catches up, and ends up equal to the source.
/// </summary>
public sealed class StagedListTests
{
    private sealed class Turns
    {
        private readonly Queue<Action> _pending = new();

        public void Later(Action action) => _pending.Enqueue(action);

        public int Count => _pending.Count;

        /// <summary>Runs one queued turn; false when there was none.</summary>
        public bool RunOne()
        {
            if (_pending.Count == 0)
            {
                return false;
            }

            _pending.Dequeue()();
            return true;
        }

        public int Drain()
        {
            var turns = 0;
            while (RunOne())
            {
                turns++;
                Assert.True(turns < 10_000, "The mirror never settled.");
            }

            return turns;
        }
    }

    /// <summary>A copy kept only from the mirror's own events, and how many entries each built.</summary>
    private sealed class Recorder
    {
        public List<object?> Copy { get; } = [];

        public int Built { get; set; }

        public Recorder(StagedList list)
        {
            list.CollectionChanged += (_, e) =>
            {
                switch (e.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                        Copy.InsertRange(e.NewStartingIndex, e.NewItems!.Cast<object?>());
                        Built += e.NewItems!.Count;
                        break;
                    case NotifyCollectionChangedAction.Remove:
                        Assert.Equal(e.OldItems!.Cast<object?>(), Copy.GetRange(e.OldStartingIndex, e.OldItems!.Count));
                        Copy.RemoveRange(e.OldStartingIndex, e.OldItems!.Count);
                        break;
                    case NotifyCollectionChangedAction.Replace:
                        Assert.Same(e.OldItems![0], Copy[e.NewStartingIndex]);
                        Copy[e.NewStartingIndex] = e.NewItems![0];
                        Built += e.NewItems!.Count;
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected {e.Action}.");
                }
            };
        }
    }

    [Fact]
    public void A_long_list_arrives_a_batch_per_turn()
    {
        var turns = new Turns();
        var list = new StagedList(8, turns.Later);
        var recorder = new Recorder(list);
        var source = Enumerable.Range(0, 49).Select(index => (object)$"mark {index}").ToArray();

        list.SetSource(source);

        // Nothing in the turn that made the list (the page around it paints first), then 8.
        Assert.Empty(list);
        Assert.True(turns.RunOne());
        Assert.Equal(8, list.Count);
        Assert.True(list.IsBehind);
        var built = new List<int> { recorder.Built };
        while (turns.RunOne())
        {
            built.Add(recorder.Built - built.Sum());
        }

        Assert.All(built, count => Assert.InRange(count, 0, 8));
        Assert.Equal(source, list.Cast<object>());
        Assert.Equal(source, recorder.Copy.Cast<object>());
        Assert.False(list.IsBehind);
    }

    [Fact]
    public void One_entry_changing_shows_in_the_same_turn()
    {
        var turns = new Turns();
        var source = new ReconciledList<string>();
        source.Reconcile(Enumerable.Range(0, 30).Select(index => $"mark {index}").ToArray());
        var list = new StagedList(8, turns.Later);
        list.SetSource(source);
        turns.Drain();

        var moved = source.Select(item => item == "mark 17" ? "mark 17 moved" : item).ToArray();
        source.Reconcile(moved);

        Assert.Equal(moved, list.Cast<string>());
    }

    [Fact]
    public void A_new_map_replaces_in_place_and_never_shrinks_the_list_below_the_source()
    {
        var turns = new Turns();
        var list = new StagedList(6, turns.Later);
        var recorder = new Recorder(list);
        list.SetSource(Enumerable.Range(0, 20).Select(index => (object)$"customs {index}").ToArray());
        turns.Drain();

        var reserve = Enumerable.Range(0, 20).Select(index => (object)$"reserve {index}").ToArray();
        list.SetSource(reserve);

        // Six rows are rebuilt now; the other fourteen keep their old rows until their turn.
        Assert.Equal(20, list.Count);
        Assert.Equal(reserve.Take(6), list.Cast<object>().Take(6));
        Assert.Equal("customs 6", list[6]);
        turns.Drain();
        Assert.Equal(reserve, list.Cast<object>());
        Assert.Equal(reserve, recorder.Copy.Cast<object>());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Any_run_of_changes_settles_on_the_source(int seed)
    {
        var random = new Random(seed);
        var turns = new Turns();
        var source = new ObservableCollection<object>();
        var list = new StagedList(1 + random.Next(10), turns.Later);
        var recorder = new Recorder(list);
        list.SetSource(source);
        var next = 0;
        for (var step = 0; step < 400; step++)
        {
            switch (random.Next(6))
            {
                case 0:
                    source.Insert(random.Next(source.Count + 1), next++);
                    break;
                case 1 when source.Count > 0:
                    source.RemoveAt(random.Next(source.Count));
                    break;
                case 2 when source.Count > 0:
                    source[random.Next(source.Count)] = next++;
                    break;
                case 3 when source.Count > 1:
                    source.Move(random.Next(source.Count), random.Next(source.Count));
                    break;
                case 4:
                    if (random.Next(10) == 0)
                    {
                        source.Clear();
                    }

                    for (var count = random.Next(25); count > 0; count--)
                    {
                        source.Add(next++);
                    }

                    break;
                default:
                    turns.RunOne();
                    break;
            }

            // Every entry the mirror holds is either the source's entry at that place or one
            // still waiting to be replaced, and the view's copy agrees with the mirror.
            Assert.True(list.Count <= source.Count);
            Assert.Equal(list.Cast<object?>(), recorder.Copy);
        }

        turns.Drain();
        Assert.Equal(source, list.Cast<object>());
        Assert.Equal(source.Cast<object?>(), recorder.Copy);
    }
}
