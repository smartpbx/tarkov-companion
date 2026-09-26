using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace TarkovCompanion.App.Views.V2.Raid;

/// <summary>[#712 0-9] The pre-raid brief: hosted by the Raid panel now, by the Now panel later.</summary>
public sealed partial class PreRaidBriefView : UserControl
{
    public PreRaidBriefView() => AvaloniaXamlLoader.Load(this);
}
