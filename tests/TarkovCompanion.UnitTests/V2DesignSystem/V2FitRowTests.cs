using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Themes.Fluent;
using TarkovCompanion.App.Views.V2.Primitives;
using TarkovCompanion.UnitTests.V2MapRenderer;

namespace TarkovCompanion.UnitTests.V2DesignSystem;

/// <summary>[#314] A heading or box and its action share a line only while the first fits whole.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class V2FitRowTests
{
    [Fact]
    public async Task Side_by_side_while_the_first_fits_with_the_first_filling_the_rest()
    {
        using var session = HeadlessSessions.StartNew(typeof(FitRowApp));
        await session.Dispatch(
            () =>
            {
                var (row, first, second) = Row(firstWidth: 120, secondWidth: 80);
                Lay(row, 300);

                Assert.False(row.IsStacked);
                Assert.Equal(new Rect(0, 0, 214, 30), first.Bounds);
                Assert.Equal(new Rect(220, 0, 80, 20), second.Bounds);
                Assert.Equal(30, row.DesiredSize.Height);
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task Stacks_when_the_first_at_its_natural_width_would_be_squeezed()
    {
        using var session = HeadlessSessions.StartNew(typeof(FitRowApp));
        await session.Dispatch(
            () =>
            {
                var (row, first, second) = Row(firstWidth: 230, secondWidth: 80);
                Lay(row, 300);

                Assert.True(row.IsStacked);
                Assert.Equal(new Rect(0, 0, 300, 30), first.Bounds);
                // Its own alignment places it on the second line: right, here.
                Assert.Equal(new Rect(220, 34, 80, 20), second.Bounds);
                Assert.Equal(30 + 4 + 20, row.DesiredSize.Height);
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task A_hidden_second_child_leaves_the_first_the_whole_line()
    {
        using var session = HeadlessSessions.StartNew(typeof(FitRowApp));
        await session.Dispatch(
            () =>
            {
                var (row, first, second) = Row(firstWidth: 230, secondWidth: 80);
                second.IsVisible = false;
                Lay(row, 300);

                Assert.False(row.IsStacked);
                Assert.Equal(new Rect(0, 0, 300, 30), first.Bounds);
            },
            CancellationToken.None);
    }

    private static (V2FitRow Row, Control First, Control Second) Row(double firstWidth, double secondWidth)
    {
        // MinWidth, not Width: the first child is a stretchy thing with a natural width, like a
        // TextBlock or a TextBox's placeholder, so it can be handed more room than it asks for.
        var first = new Border { MinWidth = firstWidth, Height = 30 };
        var second = new Border { Width = secondWidth, Height = 20, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        var row = new V2FitRow { Spacing = 6, RowSpacing = 4 };
        row.Children.Add(first);
        row.Children.Add(second);
        return (row, first, second);
    }

    private static void Lay(V2FitRow row, double width)
    {
        row.Measure(new Size(width, double.PositiveInfinity));
        row.Arrange(new Rect(0, 0, width, row.DesiredSize.Height));
    }

    public sealed class FitRowApp : Avalonia.Application
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<FitRowApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

        public override void Initialize() => Styles.Add(new FluentTheme());
    }
}
