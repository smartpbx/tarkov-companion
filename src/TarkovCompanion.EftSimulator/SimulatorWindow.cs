using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace TarkovCompanion.EftSimulator;

public sealed class SimulatorWindow : Window
{
    public SimulatorWindow(SimulatorScenario scenario)
    {
        Title = "Tarkov Companion EFT Simulator";
        Width = 1280;
        Height = 720;
        Background = new SolidColorBrush(Color.Parse("#18212A"));
        Content = BuildScene(scenario);
    }

    private static Grid BuildScene(SimulatorScenario scenario)
    {
        return new Grid
        {
            Margin = new Thickness(48),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Children =
            {
                new TextBlock
                {
                    Text = "Tarkov Companion test simulator",
                    FontSize = 24,
                    Foreground = Brushes.White,
                },
                new Border
                {
                    [Grid.RowProperty] = 1,
                    Margin = new Thickness(0, 32),
                    BorderBrush = new SolidColorBrush(Color.Parse("#8796A5")),
                    BorderThickness = new Thickness(2),
                    Background = new SolidColorBrush(Color.Parse("#202B36")),
                    Padding = new Thickness(32),
                    Child = new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Spacing = 16,
                        Children =
                        {
                            BuildGenericArtwork(scenario),
                            new TextBlock
                            {
                                Text = scenario.Heading,
                                FontSize = 42,
                                Foreground = Brushes.White,
                                HorizontalAlignment = HorizontalAlignment.Center,
                            },
                            new TextBlock
                            {
                                Text = scenario.Detail,
                                FontSize = 18,
                                Foreground = Brushes.LightGray,
                                HorizontalAlignment = HorizontalAlignment.Center,
                            },
                            new TextBlock
                            {
                                Text = $"Scenario: {scenario.Id}",
                                FontSize = 18,
                                Foreground = new SolidColorBrush(Color.Parse(scenario.Accent)),
                                HorizontalAlignment = HorizontalAlignment.Center,
                            },
                        },
                    },
                },
                new TextBlock
                {
                    [Grid.RowProperty] = 2,
                    Text = "Generic development artwork only · no EFT assets · visible pixels intended for capture tests",
                    Foreground = Brushes.LightGray,
                },
            },
        };
    }

    private static Canvas BuildGenericArtwork(SimulatorScenario scenario)
    {
        var accent = new SolidColorBrush(Color.Parse(scenario.Accent));
        return new Canvas
        {
            Width = 260,
            Height = 130,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new Rectangle
                {
                    Width = 220,
                    Height = 92,
                    RadiusX = 12,
                    RadiusY = 12,
                    Fill = new SolidColorBrush(Color.Parse("#293746")),
                    Stroke = accent,
                    StrokeThickness = 3,
                    [Canvas.LeftProperty] = 20,
                    [Canvas.TopProperty] = 18,
                },
                new Ellipse
                {
                    Width = 54,
                    Height = 54,
                    Fill = accent,
                    [Canvas.LeftProperty] = 45,
                    [Canvas.TopProperty] = 37,
                },
                new Rectangle
                {
                    Width = 105,
                    Height = 14,
                    Fill = Brushes.LightGray,
                    [Canvas.LeftProperty] = 116,
                    [Canvas.TopProperty] = 44,
                },
                new Rectangle
                {
                    Width = 75,
                    Height = 14,
                    Fill = new SolidColorBrush(Color.Parse("#8796A5")),
                    [Canvas.LeftProperty] = 116,
                    [Canvas.TopProperty] = 70,
                },
            },
        };
    }
}
