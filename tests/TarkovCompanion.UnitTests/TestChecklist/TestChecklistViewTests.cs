using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TarkovCompanion.App.Services.TestChecklist;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.App.Views.V2.Setup;
using TarkovCompanion.UnitTests.V2MapRenderer;
using TarkovCompanion.UnitTests.V2Shell;

namespace TarkovCompanion.UnitTests.TestChecklist;

/// <summary>The real view, in the app's styles: a press and a note land on disk and in the progress line.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class TestChecklistViewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tarkov-checklist-view-{Guid.NewGuid():N}");

    private string ResultsPath => Path.Combine(_root, TestChecklistResultsStore.FileName);

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
    public async Task Marking_an_item_Broken_with_a_note_saves_both_and_updates_the_progress_line()
    {
        var catalog = TestChecklistCatalog.Parse(
        [
            ("10-raid.json", """
                { "version": 1, "items": [
                  { "id": "raid.one", "area": "Raid", "feature": "First", "needs": ["game"], "steps": ["Do it"], "expect": "It works", "goTo": "#/raid" },
                  { "id": "raid.two", "area": "Raid", "feature": "Second", "steps": ["Do it"], "expect": "It works" } ] }
                """),
        ]);
        using var session = HeadlessSessions.StartNew(typeof(V2SettingsIndexLandingTests.SetupViewApp));
        var (before, after, saved) = await session.Dispatch(
            () =>
            {
                var page = new TestChecklistViewModel(catalog, new TestChecklistResultsStore(ResultsPath), "2.0.1400", navigate: _ => true);
                var window = new Window { Width = 1920, Height = 1080, Content = new TestChecklistView { DataContext = page } };
                window.Show();
                try
                {
                    Dispatcher.UIThread.RunJobs();
                    var progress = Assert.IsType<TextBlock>(Find(window, "v2-test-checklist-progress"));
                    var before = progress.Text;
                    var card = Find(window, "v2-test-checklist-item-raid.one")!;
                    // A real pointer press, so the button's Command binding is what is tested.
                    var broken = Assert.IsType<Button>(Find(card, "v2-test-checklist-broken"));
                    var centre = broken.TranslatePoint(new Point(broken.Bounds.Width / 2, broken.Bounds.Height / 2), window)!.Value;
                    window.MouseDown(centre, MouseButton.Left);
                    window.MouseUp(centre, MouseButton.Left);
                    Assert.IsType<TextBox>(Find(card, "v2-test-checklist-note")).Text = "Shows the wrong exit";
                    Dispatcher.UIThread.RunJobs();
                    page.FlushNotes();
                    Dispatcher.UIThread.RunJobs();
                    return (before, progress.Text, new TestChecklistResultsStore(ResultsPath).Get("raid.one"));
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        Assert.Equal("0 of 2 tested · 0 broken · 0 need work", before);
        Assert.Equal("1 of 2 tested · 1 broken · 0 need work", after);
        Assert.NotNull(saved);
        Assert.Equal(TestStatus.Broken, saved.Status);
        Assert.Equal("Shows the wrong exit", saved.Note);
        Assert.Equal("2.0.1400", saved.Build);
    }

    [Fact]
    public async Task The_shipped_items_load_from_the_package_with_no_problems_and_every_Go_there_is_an_address()
    {
        using var session = HeadlessSessions.StartNew(typeof(V2SettingsIndexLandingTests.SetupViewApp));
        var catalog = await session.Dispatch(() => TestChecklistAssets.Load(), CancellationToken.None);

        Assert.True(catalog.Problems.Count == 0, string.Join(Environment.NewLine, catalog.Problems));
        Assert.Contains(catalog.Items, item => item.Id == "checklist.open");
        var codec = new V2AddressCodec(V2ShellVariants.A, V2RouteRegistry.Default);
        Assert.All(
            catalog.Items.Where(item => item.GoTo is not null),
            item => Assert.True(codec.Parse(item.GoTo).Location is not null, $"{item.Id}: '{item.GoTo}' is not an address"));
    }

    [Fact]
    public void A_status_on_an_older_build_shows_Retest_and_pressing_it_again_records_this_build()
    {
        Directory.CreateDirectory(_root);
        var store = new TestChecklistResultsStore(ResultsPath);
        store.Set("raid.one", new(TestStatus.Works, "fine", "2.0.1399", DateTimeOffset.UtcNow));
        var catalog = TestChecklistCatalog.Parse([("10-raid.json", """
            { "version": 1, "items": [ { "id": "raid.one", "area": "Raid", "feature": "First", "steps": ["Do it"], "expect": "It works" } ] }
            """)]);
        var page = new TestChecklistViewModel(catalog, store, "2.0.1400");
        var item = Assert.Single(page.Items);

        Assert.True(item.NeedsRetest);
        page.Select(TestChecklistFilter.Retest);
        Assert.Single(page.Areas);
        Assert.Contains("1 to retest", page.ProgressLine, StringComparison.Ordinal);

        item.WorksCommand.Execute(null);

        Assert.False(item.NeedsRetest);
        Assert.Equal(TestStatus.Works, item.Status);
        Assert.Equal("2.0.1400", store.Get("raid.one")!.Build);
        Assert.Equal("fine", store.Get("raid.one")!.Note);
        item.WorksCommand.Execute(null);
        Assert.Equal(TestStatus.Untested, item.Status);
    }

    [Fact]
    public void Clear_results_asks_first_and_then_empties_everything()
    {
        Directory.CreateDirectory(_root);
        var store = new TestChecklistResultsStore(ResultsPath);
        store.Set("raid.one", new(TestStatus.Broken, "bad", "b", DateTimeOffset.UtcNow));
        var catalog = TestChecklistCatalog.Parse([("10-raid.json", """
            { "version": 1, "items": [ { "id": "raid.one", "area": "Raid", "feature": "First", "steps": ["Do it"], "expect": "It works" } ] }
            """)]);
        var page = new TestChecklistViewModel(catalog, store, "b");

        Assert.Equal("1 of 1 tested", page.Areas[0].CountLine);
        page.ClearCommand.Execute(null);
        Assert.True(page.IsConfirmingClear);
        page.CancelClearCommand.Execute(null);
        Assert.Equal(TestStatus.Broken, store.Get("raid.one")!.Status);

        page.ClearCommand.Execute(null);
        page.ConfirmClearCommand.Execute(null);

        Assert.False(page.IsConfirmingClear);
        Assert.Empty(new TestChecklistResultsStore(ResultsPath).All);
        Assert.Equal(TestStatus.Untested, page.Items[0].Status);
        Assert.Equal(string.Empty, page.Items[0].Note);
        Assert.StartsWith("0 of 1 tested", page.ProgressLine, StringComparison.Ordinal);
        Assert.Equal("0 of 1 tested", page.Areas[0].CountLine);
    }

    private static Control? Find(Visual root, string automationId) =>
        root.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == automationId);
}
