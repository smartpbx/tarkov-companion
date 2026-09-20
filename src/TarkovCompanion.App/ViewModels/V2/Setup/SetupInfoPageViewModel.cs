using System.Collections.ObjectModel;
using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Setup;
using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>One item on About or Data &amp; Privacy: the summary always shows, the detail opens.</summary>
public sealed class SetupDisclosureItemViewModel : BindableViewModel
{
    private bool _isExpanded;

    private IReadOnlyList<string> _facts;

    public SetupDisclosureItemViewModel(SetupDisclosure content, IReadOnlyList<string> facts)
    {
        _facts = facts;
        Id = content.Id;
        Title = content.Title;
        Summary = content.Summary;
        Detail = content.Detail;
        ToggleCommand = new DelegateCommand(() => IsExpanded = !IsExpanded);
    }

    public string Id { get; }

    public string Title { get; }

    public string Summary { get; }

    public string Detail { get; }

    /// <summary>What is true of this thing right now, when the page can say: "Sharing: off".</summary>
    public IReadOnlyList<string> Facts
    {
        get => _facts;
        internal set
        {
            if (SetProperty(ref _facts, value))
            {
                OnPropertyChanged(nameof(HasFacts));
            }
        }
    }

    public bool HasFacts => Facts.Count > 0;

    public string AutomationId => $"v2-setup-disclosure-{Id}";

    public string ToggleLabel => IsExpanded ? V2ShellText.Get("V2.Setup.Info.Less") : V2ShellText.Get("V2.Setup.Info.More");

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ToggleLabel));
            }
        }
    }

    public ICommand ToggleCommand { get; }
}

/// <summary>
/// About, or Data &amp; Privacy (#292): the same layered shape for both. A deep link opens the page at
/// one item, expanded, so a "Why?" beside a control can land on the sentence that answers it.
/// </summary>
public sealed class SetupInfoPageViewModel : BindableViewModel
{
    public SetupInfoPageViewModel(
        string title,
        IReadOnlyList<SetupDisclosure> content,
        Func<string, IReadOnlyList<string>>? factsFor = null)
    {
        Title = title;
        _factsFor = factsFor;
        Items = new ObservableCollection<SetupDisclosureItemViewModel>(
            content.Select(item => new SetupDisclosureItemViewModel(item, factsFor?.Invoke(item.Id) ?? [])));
    }

    private readonly Func<string, IReadOnlyList<string>>? _factsFor;

    public string Title { get; }

    public ObservableCollection<SetupDisclosureItemViewModel> Items { get; }

    /// <summary>Asks for each item's facts again; called when the page is opened, so they are not the last visit's.</summary>
    public void Refresh()
    {
        foreach (var item in Items)
        {
            item.Facts = _factsFor?.Invoke(item.Id) ?? [];
        }
    }

    /// <summary>Expands the item with this id and collapses the rest; false when the page has no such item.</summary>
    public bool Open(string anchor)
    {
        var found = Items.FirstOrDefault(item => item.Id == anchor);
        if (found is null)
        {
            return false;
        }

        foreach (var item in Items)
        {
            item.IsExpanded = ReferenceEquals(item, found);
        }

        return true;
    }
}
