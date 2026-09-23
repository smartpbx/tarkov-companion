using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace TarkovCompanion.App.Views.V2.LootScan;

public sealed partial class LootScanHistoryListView : UserControl
{
    public LootScanHistoryListView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
