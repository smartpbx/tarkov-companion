using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

public sealed class V2CaptureIntentViewModel : BindableViewModel
{
    private readonly Action<ScanIntent> _select;
    private bool _isSelected;

    public V2CaptureIntentViewModel(ScanIntent intent, Action<ScanIntent> select)
    {
        Intent = intent;
        _select = select ?? throw new ArgumentNullException(nameof(select));
        SelectCommand = new DelegateCommand(() => _select(intent));
    }

    public ScanIntent Intent { get; }
    public string Label => V2ShellText.Get($"V2.Shell.Intent.{Intent}");
    public string AutomationId => $"v2-shell-capture-intent-{Intent.ToString().ToLowerInvariant()}";
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !_isSelected)
            {
                _select(Intent);
            }
        }
    }
    public ICommand SelectCommand { get; }

    internal void SetSelected(bool value) => SetProperty(ref _isSelected, value);
}

public sealed record V2CaptureProgressViewModel(
    long Sequence,
    string Stage,
    string Detail,
    string Artifact,
    string Percent,
    string Reference)
{
    public string AutomationId => $"v2-shell-capture-progress-{Sequence.ToString(CultureInfo.InvariantCulture)}";
}

public sealed class V2CaptureActionViewModel(
    V2CaptureResolutionKind resolution,
    string label,
    string automationId,
    Action<V2CaptureResolutionKind> invoke)
{
    public V2CaptureResolutionKind Resolution { get; } = resolution;
    public string Label { get; } = label;
    public string AutomationId { get; } = automationId;
    public ICommand InvokeCommand { get; } = new DelegateCommand(() => invoke(resolution));
}

public enum V2ShellSuggestionKind
{
    All = 1,
    Recent,
    Pinned,
    Planned,
}

public sealed class V2ShellSuggestionFilterViewModel : BindableViewModel
{
    private bool _isSelected;

    public V2ShellSuggestionFilterViewModel(V2ShellSuggestionKind kind, Action<V2ShellSuggestionKind> select)
    {
        Kind = kind;
        SelectCommand = new DelegateCommand(() => select(kind));
    }

    public V2ShellSuggestionKind Kind { get; }
    public string Label => V2ShellText.Get($"V2.Shell.Suggestions.Filter.{Kind}");
    public string DisplayLabel => IsSelected ? $"› {Label}" : Label;
    public string AutomationId => $"v2-shell-suggestion-filter-{Kind.ToString().ToLowerInvariant()}";
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(DisplayLabel));
            }
        }
    }
    public ICommand SelectCommand { get; }
}

public sealed class V2ShellSuggestionViewModel(
    string key,
    string label,
    string provenance,
    string category,
    V2ShellSuggestionKind kind,
    Action open)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Provenance { get; } = provenance;
    public string Category { get; } = category;
    public V2ShellSuggestionKind Kind { get; } = kind;
    public string AutomationId => $"v2-shell-suggestion-{Kind.ToString().ToLowerInvariant()}-{StableToken(Key)}";
    public ICommand OpenCommand { get; } = new DelegateCommand(open);

    private static string StableToken(string value)
    {
        var token = new string(value
            .ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray());
        return token.Length <= 64 ? token.Trim('-') : token[..64].Trim('-');
    }
}

public sealed class V2BrowseCategoryViewModel(
    V2RouteId route,
    string label,
    string detail,
    Action<V2RouteId> navigate)
{
    public V2RouteId Route { get; } = route;
    public string Label { get; } = label;
    public string Detail { get; } = detail;
    public string AutomationId => $"v2-shell-browse-{route.Value}";
    public ICommand OpenCommand { get; } = new DelegateCommand(() => navigate(route));

    public static IReadOnlyList<V2BrowseCategoryViewModel> Create(
        V2ShellVariantDefinition variant,
        Action<V2RouteId> navigate)
    {
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(navigate);
        return new V2BrowseCategoryViewModel[]
        {
            new(V2Routes.Items, V2ShellText.Get("V2.Shell.Suggestions.Category.Items"), V2ShellText.Get("V2.Shell.Suggestions.Category.ItemsDetail"), navigate),
            new(V2Routes.Ammo, V2ShellText.Get("V2.Shell.Suggestions.Category.Ammo"), V2ShellText.Get("V2.Shell.Suggestions.Category.AmmoDetail"), navigate),
            new(V2Routes.Keys, V2ShellText.Get("V2.Shell.Suggestions.Category.Keys"), V2ShellText.Get("V2.Shell.Suggestions.Category.KeysDetail"), navigate),
            new(V2Routes.Flea, V2ShellText.Get("V2.Shell.Suggestions.Category.Flea"), V2ShellText.Get("V2.Shell.Suggestions.Category.FleaDetail"), navigate),
        }.Where(category => variant.Addresses.ContainsKey(category.Route)).ToArray();
    }
}
