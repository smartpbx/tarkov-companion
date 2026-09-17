using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using TarkovCompanion.App.ViewModels.V2.Tablet;

namespace TarkovCompanion.App.Views.V2.Tablet;

public sealed partial class CompanionPairingWindow : Window
{
    public CompanionPairingWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public CompanionPairingWindow(CompanionPairingViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
