using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TarkovCompanion.App.Views.V2.Shell;
using TarkovCompanion.UnitTests.V2MapRenderer;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>
/// [#453] A return to a workspace shows the view it left, instead of building the page again.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class WorkspaceViewCacheTests
{
    [Fact]
    public async Task Returning_to_a_workspace_shows_the_view_built_on_the_first_visit()
    {
        using var session = HeadlessSessions.StartNew(typeof(CacheApp));
        await session.Dispatch(
            () =>
            {
                var plan = new Workspace("plan");
                var intel = new Workspace("intel");
                var cache = new WorkspaceViewCache();
                var window = new Window { Width = 400, Height = 300, Content = cache };
                window.Show();
                try
                {
                    cache.Content = plan;
                    Dispatcher.UIThread.RunJobs();
                    var planView = Shown(cache);

                    cache.Content = intel;
                    Dispatcher.UIThread.RunJobs();
                    Assert.NotSame(planView, Shown(cache));

                    cache.Content = null;
                    Dispatcher.UIThread.RunJobs();
                    Assert.DoesNotContain(cache.Children, child => child.IsVisible);

                    cache.Content = plan;
                    Dispatcher.UIThread.RunJobs();
                    Assert.Same(planView, Shown(cache));
                    Assert.Equal(2, cache.KeptCount);
                    Assert.Single(cache.Children, child => child.IsVisible);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task Views_past_the_capacity_are_let_go_oldest_first()
    {
        using var session = HeadlessSessions.StartNew(typeof(CacheApp));
        await session.Dispatch(
            () =>
            {
                var cache = new WorkspaceViewCache();
                var first = new Workspace("first");
                cache.Content = first;
                for (var index = 0; index < WorkspaceViewCache.Capacity; index++)
                {
                    cache.Content = new Workspace($"other-{index}");
                }

                Assert.Equal(WorkspaceViewCache.Capacity, cache.KeptCount);
                Assert.DoesNotContain(cache.Children, child => child is ContentControl { Content: var content } && ReferenceEquals(content, first));
            },
            CancellationToken.None);
    }

    /// <summary>The view the cache is showing: what the visible host's template built.</summary>
    private static Control Shown(WorkspaceViewCache cache)
    {
        var host = Assert.Single(cache.Children, child => child.IsVisible);
        return Assert.IsType<TextBlock>(host.GetVisualDescendants().OfType<TextBlock>().First());
    }

    private sealed record Workspace(string Name)
    {
        public override string ToString() => Name;
    }

    public sealed class CacheApp : Avalonia.Application
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<CacheApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

        public override void Initialize() => Styles.Add(new FluentTheme());
    }
}
