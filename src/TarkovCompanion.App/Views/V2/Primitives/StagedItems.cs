using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace TarkovCompanion.App.Views.V2.Primitives;

/// <summary>
/// <c>prim:StagedItems.Source="{Binding …}"</c> in place of <c>ItemsSource</c>: the list control
/// builds its entries a batch per turn instead of all at once (see <see cref="StagedList"/>).
/// </summary>
/// <remarks>
/// [#678] For lists that can bring dozens of entries in one change: the marks on the plan and
/// the Raid panel's extract and objective rows. <c>StagedItems.Batch</c> sets how many entries
/// one turn may build; set it before <c>Source</c> in XAML.
/// </remarks>
public static class StagedItems
{
    public static readonly AttachedProperty<IEnumerable?> SourceProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, IEnumerable?>("Source", typeof(StagedItems));

    public static readonly AttachedProperty<int> BatchProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, int>("Batch", typeof(StagedItems), 8);

    static StagedItems()
    {
        SourceProperty.Changed.AddClassHandler<ItemsControl>(static (list, e) => Apply(list, e.NewValue as IEnumerable));
    }

    public static IEnumerable? GetSource(ItemsControl list) => list.GetValue(SourceProperty);

    public static void SetSource(ItemsControl list, IEnumerable? value) => list.SetValue(SourceProperty, value);

    public static int GetBatch(ItemsControl list) => list.GetValue(BatchProperty);

    public static void SetBatch(ItemsControl list, int value) => list.SetValue(BatchProperty, value);

    private static void Apply(ItemsControl list, IEnumerable? source)
    {
        if (source is not IList items)
        {
            (list.ItemsSource as StagedList)?.Dispose();
            list.ItemsSource = source;
            return;
        }

        // One mirror per list control for its whole life: a new source (a new array of rows) is
        // followed by the same mirror, which replaces only what differs.
        if (list.ItemsSource is not StagedList staged)
        {
            staged = new StagedList(Math.Max(1, GetBatch(list)), static grow => Dispatcher.UIThread.Post(grow, DispatcherPriority.Background));
            list.ItemsSource = staged;
        }

        staged.SetSource(items);
    }
}
