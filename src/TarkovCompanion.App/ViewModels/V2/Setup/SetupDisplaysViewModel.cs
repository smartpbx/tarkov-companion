using TarkovCompanion.App.Localization;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Services.Windowing;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>One monitor, as Setup › Displays lists it.</summary>
public sealed class SetupDisplayRow(
    string id,
    string name,
    string detail,
    bool isPrimary,
    bool holdsGame,
    bool holdsCompanion,
    ICommand? moveCommand) : BindableViewModel
{
    private bool _holdsCompanion = holdsCompanion;

    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Detail { get; } = detail;
    public bool IsPrimary { get; } = isPrimary;
    public bool HoldsGame { get; } = holdsGame;
    public bool HoldsCompanion => _holdsCompanion;
    public ICommand? MoveCommand { get; } = moveCommand;
    public bool CanMove => MoveCommand is not null && !HoldsCompanion;
    public string MoveLabel => SetupText.DisplaysMoveHere(Name);

    public string Badges => string.Join(" · ", new[]
    {
        IsPrimary ? SetupText.DisplaysPrimary : null,
        HoldsCompanion ? SetupText.DisplaysCompanionHere : null,
        HoldsGame ? SetupText.DisplaysGameHere : null,
    }.Where(badge => badge is not null));

    public bool HasBadges => IsPrimary || HoldsCompanion || HoldsGame;

    public void SetCompanionHere(bool value)
    {
        if (SetProperty(ref _holdsCompanion, value, nameof(HoldsCompanion)))
        {
            OnPropertyChanged(nameof(CanMove));
            OnPropertyChanged(nameof(Badges));
            OnPropertyChanged(nameof(HasBadges));
        }
    }
}

/// <summary>
/// Setup › Displays (#292/#316): the monitors the machine has, where the companion is, and what a scan sees.
/// </summary>
/// <remarks>
/// A scan captures the game's window, not a monitor, so the capture target is the game window: whether it
/// is found, how large it is, which display it is on, and whether it is minimized. Without those a scan that
/// returns nothing looks identical whether the game was closed, minimized, or on the display nobody thought of.
/// Both services are Windows-only and are null elsewhere; the page then says so instead of showing nothing.
/// </remarks>
public sealed class SetupDisplaysViewModel : BindableViewModel
{
    private readonly IMonitorService? _monitors;
    private readonly IGameWindowLocator? _windows;
    private readonly IDesktopWindowPlacementController? _placement;
    private string _captureTarget = string.Empty;
    private string _captureNote = string.Empty;
    private bool _isAvailable;

    public SetupDisplaysViewModel(
        IMonitorService? monitors,
        IGameWindowLocator? windows,
        IDesktopWindowPlacementController? placement = null)
    {
        _monitors = monitors;
        _windows = windows;
        _placement = placement;
        _isAvailable = monitors is not null;
        RefreshCommand = new AsyncDelegateCommand(() => RefreshAsync(CancellationToken.None));
        if (_placement is not null)
        {
            _placement.CurrentDisplayChanged += PlacementCurrentDisplayChanged;
        }
    }

    public ObservableCollection<SetupDisplayRow> Displays { get; } = [];

    public ICommand RefreshCommand { get; }

    public string Heading => SetupText.DisplaysHeading;
    public string CaptureHeading => SetupText.DisplaysCaptureHeading;
    public string RefreshLabel => SetupText.DisplaysRefresh;
    public string UnavailableNote => SetupText.DisplaysNoInfo;

    public bool IsAvailable
    {
        get => _isAvailable;
        private set
        {
            if (SetProperty(ref _isAvailable, value))
            {
                OnPropertyChanged(nameof(IsUnavailable));
            }
        }
    }

    public bool IsUnavailable => !IsAvailable;

    /// <summary>The game window as a scan would find it, in one line.</summary>
    public string CaptureTarget
    {
        get => _captureTarget;
        private set => SetProperty(ref _captureTarget, value);
    }

    public string CaptureNote
    {
        get => _captureNote;
        private set => SetProperty(ref _captureNote, value);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_monitors is null)
        {
            IsAvailable = false;
            return;
        }

        IReadOnlyList<DisplayDescriptor> displays;
        WindowDescriptor? game = null;
        try
        {
            displays = await _monitors.GetDisplaysAsync(cancellationToken).ConfigureAwait(true);
            if (_windows is not null)
            {
                game = await _windows.FindAsync(false, cancellationToken).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            IsAvailable = false;
            CaptureTarget = exception.Message;
            return;
        }

        var holding = game is null ? null : displays.FirstOrDefault(display => Contains(display.Bounds, game.Bounds));
        Displays.Clear();
        foreach (var display in displays)
        {
            var moveCommand = _placement is null
                ? null
                : new AsyncDelegateCommand(async () =>
                {
                    await _placement.MoveToAsync(display.Id, CancellationToken.None).ConfigureAwait(true);
                });
            Displays.Add(new(
                display.Id,
                display.Name,
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"{display.Bounds.Width}×{display.Bounds.Height} · {display.Scale:P0} · {display.Id}"),
                display.IsPrimary,
                ReferenceEquals(display, holding),
                display.Id == _placement?.CurrentDisplayId,
                moveCommand));
        }

        IsAvailable = true;
        CaptureNote = SetupText.DisplaysCaptureNote;
        CaptureTarget = game switch
        {
            null => SetupText.DisplaysCaptureMissing,
            { IsMinimized: true } => SetupText.DisplaysCaptureMinimized,
            _ => SetupText.DisplaysCaptureFound(game.Bounds.Width, game.Bounds.Height, holding?.Name ?? SetupText.DisplaysUnknownDisplay),
        };
    }

    private void PlacementCurrentDisplayChanged(object? sender, EventArgs eventArgs)
    {
        foreach (var display in Displays)
        {
            display.SetCompanionHere(display.Id == _placement?.CurrentDisplayId);
        }
    }

    /// <summary>The display a window mostly sits on: the one holding its centre.</summary>
    private static bool Contains(PixelRect display, PixelRect window)
    {
        var x = window.X + (window.Width / 2);
        var y = window.Y + (window.Height / 2);
        return x >= display.X && x < display.X + display.Width && y >= display.Y && y < display.Y + display.Height;
    }
}
