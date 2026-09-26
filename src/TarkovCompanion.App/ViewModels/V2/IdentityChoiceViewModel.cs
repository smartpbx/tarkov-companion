using System.Windows.Input;

namespace TarkovCompanion.App.ViewModels.V2;

/// <summary>
/// One lookalike the player can say an unnamed cell or a misread name was (#712 1-12). Only the
/// recognizer's own lookalikes are offered, so a pick can never be an item it did not consider.
/// </summary>
public sealed class IdentityChoiceViewModel(string itemId, string label, string automationId, Func<Task> choose)
{
    public string ItemId { get; } = itemId;

    public string Label { get; } = label;

    public string AutomationId { get; } = automationId;

    public ICommand ChooseCommand { get; } = new AsyncDelegateCommand(choose);

    public override string ToString() => Label;
}
