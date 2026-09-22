using System.Collections.Specialized;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [#453] A squadmate moving is one marker moving. It handed the view a new array of every marker,
/// and an ItemsControl given a new array builds every control again; with three squadmates sharing
/// that held Clayton's interface thread for minutes. Now the view's list is changed in place.
/// </summary>
public sealed class MapSceneMarkerReconcileTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void One_marker_moving_replaces_that_marker_and_keeps_the_list_and_every_other_marker()
    {
        var renderer = MapScenePresentReuseTests.Renderer(MapScenePresentReuseTests.FullScene(1, T0));
        var markers = renderer.PointMarkers;
        var before = markers.ToArray();
        var changes = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)markers).CollectionChanged += (_, args) => changes.Add(args);

        renderer.Present(MapScenePresentReuseTests.FullScene(2, T0, movedMarkerX: 44));

        Assert.Same(markers, renderer.PointMarkers);
        var change = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Replace, change.Action);
        var moved = Assert.Single(renderer.PointMarkers, marker => !before.Contains(marker));
        Assert.Equal("object:m0", moved.ObjectId?.Value);
        Assert.NotEqual(before.Single(marker => marker.ObjectId?.Value == "object:m0").AnchorLeft, moved.AnchorLeft);
    }

    [Fact]
    public void Reconcile_changes_only_the_positions_that_differ()
    {
        string a = "a", b = "b", c = "c", d = "d", e = "e";
        var list = new ReconciledList<string>();
        list.Reconcile([a, b, c]);
        var changes = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, args) => changes.Add(args.Action);

        list.Reconcile([a, d, c, e]);
        Assert.Equal(new[] { a, d, c, e }, list);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Replace, NotifyCollectionChangedAction.Add }, changes);

        changes.Clear();
        list.Reconcile([a]);
        Assert.Equal(new[] { a }, list);
        Assert.All(changes, action => Assert.Equal(NotifyCollectionChangedAction.Remove, action));
        Assert.Equal(3, changes.Count);
    }

    [Fact]
    public void Reuse_keeps_the_earlier_instance_only_where_it_draws_the_same()
    {
        var earlier = new[] { new Item("x", 1), new Item("y", 2) };
        var next = new[] { new Item("x", 1), new Item("y", 3), new Item("z", 4) };

        var reused = ReconciledList<Item>.Reuse(earlier, next, item => item.Key, (old, fresh) => old.Value == fresh.Value);

        Assert.Same(earlier[0], reused[0]);
        Assert.Same(next[1], reused[1]);
        Assert.Same(next[2], reused[2]);
    }

    private sealed record Item(string Key, int Value);
}
