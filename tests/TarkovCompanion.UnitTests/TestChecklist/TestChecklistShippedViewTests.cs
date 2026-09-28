using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TarkovCompanion.App.Services.TestChecklist;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.App.Views.V2.Setup;
using TarkovCompanion.UnitTests.V2MapRenderer;
using TarkovCompanion.UnitTests.V2Shell;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.TestChecklist;

/// <summary>The real view with every shipped item: it opens fast, realises only what is on screen, and Next untested lands.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class TestChecklistShippedViewTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tarkov-checklist-shipped-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task Every_shipped_item_opens_in_under_a_second_realises_only_the_screen_and_Next_untested_brings_its_item_to_the_top()
    {
        Directory.CreateDirectory(_root);
        using var session = HeadlessSessions.StartNew(typeof(V2SettingsIndexLandingTests.SetupViewApp));
        var result = await session.Dispatch(
            () =>
            {
                var catalog = TestChecklistAssets.Load();
                var store = new TestChecklistResultsStore(Path.Combine(_root, TestChecklistResultsStore.FileName));
                // Warm the styles and templates on a one-item page, so the timing is the page's own cost.
                Warm();

                var clock = Stopwatch.StartNew();
                var page = new TestChecklistViewModel(catalog, store, "2.0.1400");
                var window = new Window { Width = 1920, Height = 1080, Content = new TestChecklistView { DataContext = page } };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var openMs = clock.Elapsed.TotalMilliseconds;
                try
                {
                    var realised = Cards(window).Count;

                    page.Items[0].Mark(TestStatus.Works);
                    page.Items[1].Mark(TestStatus.Skipped);
                    Dispatcher.UIThread.RunJobs();
                    var firstCompact = Find(Find(window, page.Items[0].AutomationId)!, "v2-test-checklist-row")!.IsEffectivelyVisible;

                    // Far down the list: the 300th row item, marked so that Next untested lands on the one after it.
                    var shown = page.Rows.OfType<TestChecklistItemViewModel>().ToList();
                    shown[299].Mark(TestStatus.Broken);
                    var next = Assert.IsType<Button>(Find(window, "v2-test-checklist-next"));
                    var centre = next.TranslatePoint(new Point(next.Bounds.Width / 2, next.Bounds.Height / 2), window)!.Value;
                    window.MouseDown(centre, MouseButton.Left);
                    window.MouseUp(centre, MouseButton.Left);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var list = Find(window, "v2-test-checklist-list")!;
                    var target = Find(window, shown[300].AutomationId);
                    var targetTop = target?.TranslatePoint(default, list)?.Y;

                    return (openMs, realised, total: catalog.Items.Count, firstCompact, current: page.Current?.Id, expected: shown[300].Id, targetTop);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        output.WriteLine($"{result.total} items: opened in {result.openMs:F0} ms with {result.realised} item rows realised");
        Assert.True(result.total >= 550, $"only {result.total} shipped items");
        Assert.InRange(result.realised, 1, 40);
        Assert.True(result.openMs < 1000, $"opening took {result.openMs:F0} ms");
        Assert.True(result.firstCompact);
        Assert.Equal(result.expected, result.current);
        Assert.NotNull(result.targetTop);
        Assert.InRange(result.targetTop!.Value, -1, 80);
    }

    private static void Warm()
    {
        var one = TestChecklistCatalog.Parse([("00.json", "{ \"version\": 1, \"items\": [ { \"id\": \"w.one\", \"area\": \"W\", \"feature\": \"W\", \"steps\": [\"S\"], \"expect\": \"E\" } ] }")]);
        var window = new Window { Width = 800, Height = 600, Content = new TestChecklistView { DataContext = new TestChecklistViewModel(one, new TestChecklistResultsStore(Path.Combine(Path.GetTempPath(), $"tarkov-checklist-warm-{Guid.NewGuid():N}.json")), "b") } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Close();
    }

    private static List<Control> Cards(Visual root) =>
        [.. root.GetVisualDescendants().OfType<Control>()
            .Where(control => AutomationProperties.GetAutomationId(control)?.StartsWith("v2-test-checklist-item-", StringComparison.Ordinal) == true)];

    private static Control? Find(Visual root, string automationId) =>
        root.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == automationId);
}
