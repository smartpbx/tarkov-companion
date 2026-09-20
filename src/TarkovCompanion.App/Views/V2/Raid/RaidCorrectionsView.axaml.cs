using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace TarkovCompanion.App.Views.V2.Raid;

/// <summary>The Raid plan's Corrections card; see <see cref="ViewModels.V2.Raid.RaidCorrectionsViewModel"/>.</summary>
public sealed partial class RaidCorrectionsView : UserControl
{
    public RaidCorrectionsView() => AvaloniaXamlLoader.Load(this);
}
