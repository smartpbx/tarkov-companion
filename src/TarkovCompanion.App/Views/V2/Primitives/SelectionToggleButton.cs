using Avalonia.Controls.Primitives;

namespace TarkovCompanion.App.Views.V2.Primitives;

/// <summary>
/// [#902 P4] A chip whose lit state is only ever what its binding says. Pressing it runs its
/// command and changes nothing by itself.
/// </summary>
/// <remarks>
/// A plain ToggleButton flips its own IsChecked on every click, as a local value over a one-way
/// binding. When the command then changed nothing (Navigate while already navigating, the
/// traffic phase already chosen, the loot threshold already set) no property changed to put it
/// back, so the pressed chip went dark and the group looked switched off. Here the view model is
/// the only thing that lights or darkens it. Styled exactly as a ToggleButton.
/// </remarks>
public class SelectionToggleButton : ToggleButton
{
    protected override Type StyleKeyOverride => typeof(ToggleButton);

    protected override void Toggle()
    {
        // Deliberately nothing: the bound IsChecked is the state.
    }
}
