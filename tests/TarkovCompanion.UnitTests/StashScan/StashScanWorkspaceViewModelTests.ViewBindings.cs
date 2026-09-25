using Avalonia.Controls;
using Avalonia.Logging;
using Avalonia.Threading;
using TarkovCompanion.App.ViewModels.V2.StashScan;
using TarkovCompanion.App.Views.V2.StashScan;
using TarkovCompanion.Application.Services.StashScan;

namespace TarkovCompanion.UnitTests.StashScan;

public sealed partial class StashScanWorkspaceViewModelTests
{
    /// <summary>
    /// [#910 fallout] The real view, with nothing selected, logs no binding fault through the
    /// selected item.
    /// </summary>
    /// <remarks>
    /// #910's "Open in Intel" button bound <c>SelectedItem.HasItem</c> and <c>SelectedItem.ItemId</c>
    /// with a FallbackValue. The card around it is hidden with no selection, but its bindings still
    /// evaluate, and a FallbackValue does not stop Avalonia logging "Value is null" for the null
    /// link in the path. Windows verification treats every logged binding fault as an interface
    /// fault, so main stopped publishing. A view-model test cannot see this: it lives in the view.
    /// </remarks>
    [Fact]
    public async Task The_view_with_nothing_selected_logs_no_binding_fault_through_the_selected_item()
    {
        using var session = HeadlessSessions.StartNew(
            typeof(TarkovCompanion.UnitTests.V2MapRenderer.MapMarkClipTests.MarkClipApp));
        var faults = await session.Dispatch(HostWithNothingSelected, CancellationToken.None);

        Assert.DoesNotContain(faults, fault => fault.Contains("SelectedItem", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> HostWithNothingSelected()
    {
        var store = new FakeSnapshotStore();
        var reviewCommands = new InMemoryStashReviewCommandSink();
        var viewModel = new StashScanWorkspaceViewModel(
            store,
            Workflow(store, reviewCommands),
            reviewCommands,
            new FakeItemFactCatalog([], []),
            new FakeRuntimeStateStore(RuntimeSnapshot()));
        Assert.Null(viewModel.SelectedItem);

        var sink = new BindingFaultSink();
        var previous = Logger.Sink;
        Logger.Sink = sink;
        var window = new Window { Width = 1920, Height = 1080 };
        try
        {
            window.Content = new StashScanWorkspaceView { DataContext = viewModel };
            window.Show();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            window.Close();
            Logger.Sink = previous;
        }

        return sink.Faults;
    }

    /// <summary>Collects binding warnings and errors, formatted enough to name the path.</summary>
    private sealed class BindingFaultSink : ILogSink
    {
        private readonly List<string> _faults = [];

        public IReadOnlyList<string> Faults => _faults;

        public bool IsEnabled(LogEventLevel level, string area) =>
            level >= LogEventLevel.Warning && area == LogArea.Binding;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Log(level, area, source, messageTemplate, []);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (IsEnabled(level, area))
            {
                _faults.Add($"{messageTemplate} | {string.Join(" | ", propertyValues)}");
            }
        }
    }
}
