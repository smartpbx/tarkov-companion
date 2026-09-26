using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Readiness;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>One item of the readiness strip: "Game logs ✓", or "Screenshots not found · Choose folder…".</summary>
public sealed class ReadinessStripItemViewModel(ReadinessItem item, string state, ICommand? fix)
{
    public ReadinessItem Item { get; } = item;
    public string Label { get; } = ReadinessText.Item(item.Kind);
    public string State { get; } = state;
    public bool IsReady => Item.State == ReadinessItemState.Ready;
    public bool IsAttention => Item.Blocks;
    public bool IsMuted => !IsReady && !IsAttention;
    public string? FixLabel { get; } = ReadinessText.Fix(item.Fix);
    public bool HasFix => FixCommand is not null && FixLabel is not null;
    public ICommand? FixCommand { get; } = fix;
    public bool HasSeparator { get; init; }
    public string AutomationId => $"v3-readiness-{Item.Kind}";
    public string AutomationName => string.Join(" ", Label, State);
}

/// <summary>
/// [#712 1-13] The slim readiness strip at the top of the Raid page; decided by
/// <see cref="ReadinessStripRules"/>, drawn by <c>Views/V2/Shell/ReadinessStripView</c>.
/// </summary>
/// <remarks>
/// The fixes are the shell's to carry out (navigation, the catalog sync, saving a folder), so they
/// come in as delegates; the folder picker is the view's, because only a window has a storage
/// provider, and the view sets <see cref="PickFolder"/> when it is attached.
/// </remarks>
public sealed class ReadinessStripViewModel : BindableViewModel
{
    private readonly Action<ReadinessFix> _run;
    private readonly Func<ReadinessItemKind, string, Task> _saveFolder;
    private ReadinessStripState _state = ReadinessStripState.Hidden;
    private bool _pinned;

    public ReadinessStripViewModel(Action<ReadinessFix> run, Func<ReadinessItemKind, string, Task> saveFolder)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _saveFolder = saveFolder ?? throw new ArgumentNullException(nameof(saveFolder));
        DetailsCommand = new DelegateCommand(() => _run(ReadinessFix.OpenDetails));
    }

    /// <summary>Opens a folder picker titled for the item; null when the player cancels.</summary>
    public Func<ReadinessItemKind, Task<string?>>? PickFolder { get; set; }

    /// <summary>Whether a folder exists; the file system unless a test says otherwise.</summary>
    public Func<string, bool> DirectoryExists { get; set; } = Directory.Exists;

    public ReadinessStripState State => _state;
    public bool IsVisible => _state.IsVisible;
    public IReadOnlyList<ReadinessStripItemViewModel> Items { get; private set; } = [];
    public string Name => ReadinessText.StripName;
    public string DetailsLabel => ReadinessText.Details;
    public ICommand DetailsCommand { get; }

    public void Apply(ReadinessStripState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_pinned || Same(_state, state))
        {
            return;
        }

        _state = state;
        Items = state.Items
            .Select((item, index) => new ReadinessStripItemViewModel(item, ReadinessText.State(item, state.Format), CommandFor(item.Fix))
            {
                HasSeparator = index > 0,
            })
            .ToArray();
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(IsVisible));
    }

    /// <summary>Holds one state whatever the runtime says next: V2RenderPreview's demo states only.</summary>
    public void PinForPreview(ReadinessStripState state)
    {
        _pinned = false;
        Apply(state);
        _pinned = true;
    }

    /// <summary>The fix's work: the picker then the save for a folder, the shell's action otherwise.</summary>
    public async Task RunFixAsync(ReadinessFix fix)
    {
        var kind = fix switch
        {
            ReadinessFix.ChooseLogFolder => ReadinessItemKind.GameLogs,
            ReadinessFix.ChooseScreenshotFolder => ReadinessItemKind.Screenshots,
            _ => (ReadinessItemKind?)null,
        };
        if (kind is null)
        {
            _run(fix);
            return;
        }

        if (PickFolder is not { } pick || await pick(kind.Value).ConfigureAwait(true) is not { Length: > 0 } picked)
        {
            return;
        }

        await _saveFolder(kind.Value, ChosenGameFolder.Resolve(kind.Value, picked, DirectoryExists)).ConfigureAwait(true);
    }

    private AsyncDelegateCommand? CommandFor(ReadinessFix fix) =>
        fix == ReadinessFix.None ? null : new AsyncDelegateCommand(() => RunFixAsync(fix));

    private static bool Same(ReadinessStripState left, ReadinessStripState right) =>
        left.IsVisible == right.IsVisible &&
        left.Items.SequenceEqual(right.Items) &&
        FormatWords(left) == FormatWords(right);

    private static string FormatWords(ReadinessStripState state) =>
        state.For(ReadinessItemKind.GameFormat) is { } item ? ReadinessText.State(item, state.Format) : string.Empty;
}
