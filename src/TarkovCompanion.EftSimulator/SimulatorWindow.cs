using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TarkovCompanion.EftSimulator;

public sealed class SimulatorWindow : Window
{
    public SimulatorWindow(IReadOnlyList<string> args)
    {
        Title = "Tarkov Companion EFT Simulator";
        Width = 1280;
        Height = 720;
        Background = new SolidColorBrush(Color.Parse("#18212A"));
        Content = BuildScene(ParseScenario(args));
    }

    private static Control BuildScene(string scenario)
    {
        var itemName = scenario switch
        {
            "Inspect_GraphicsCard" => "Graphics Card",
            "Inspect_AmmoPack" => "5.45x39 ammunition pack",
            "Inspect_Key" => "Dorm room 214 key",
            "Inspect_Consumable" => "Synthetic provision",
            _ => "Synthetic permitted-input scene",
        };

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
                            new TextBlock { Text = itemName, FontSize = 42, Foreground = Brushes.White },
                            new TextBlock { Text = $"Scenario: {scenario}", FontSize = 18, Foreground = Brushes.LightGray },
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

    private static string ParseScenario(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], "--scenario", StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return "Inspect_GraphicsCard";
    }
}
