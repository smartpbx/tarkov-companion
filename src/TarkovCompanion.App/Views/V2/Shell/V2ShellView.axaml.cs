using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.App.Views.V2.Shell;

public sealed partial class V2ShellView : UserControl
{
    public V2ShellView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += DataContextChangedHandler;
    }

    private void DataContextChangedHandler(object? sender, EventArgs eventArgs)
    {
        if (DataContext is V2ShellViewModel shell)
        {
            shell.FocusRequested += FocusRequested;
            shell.PropertyChanged += PropertyChanged;
        }
    }

    private void FocusRequested(object? sender, V2FocusRequest request) => Dispatcher.UIThread.Post(() =>
    {
        (request.Target == V2ShellRouter.IntelHeadingTarget ? this.FindControl<Control>("IntelHeading") : this.FindControl<Control>("PageHeading"))?.Focus();
    });

    private void PropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(V2ShellViewModel.IsCaptureOpen) && DataContext is V2ShellViewModel { IsCaptureOpen: true })
        {
            Dispatcher.UIThread.Post(() => this.FindControl<Control>("DialogTitle")?.Focus());
        }
    }
}
