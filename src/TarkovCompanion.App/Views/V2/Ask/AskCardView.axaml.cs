using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace TarkovCompanion.App.Views.V2.Ask;

/// <summary>[#712 2-5] The palette's answer card; see <see cref="ViewModels.V2.Ask.AskViewModel"/>.</summary>
public sealed partial class AskCardView : UserControl
{
    public AskCardView() => AvaloniaXamlLoader.Load(this);
}
