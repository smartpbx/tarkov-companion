using TarkovCompanion.App.Localization;
using System.Collections.ObjectModel;
using System.Windows.Input;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.Windowing;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.Shell;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

public sealed record SetupCaptureModeChoice(GameWindowPresentationMode Mode, string Label);

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
    private readonly ICaptureTargetCalibrationStore? _calibrations;
    private readonly TimeProvider _timeProvider;
    private string _captureTarget = string.Empty;
    private string _captureNote = string.Empty;
    private bool _isAvailable;
    private WindowDescriptor? _gameWindow;
    private DisplayDescriptor? _gameDisplay;
    private SetupCaptureModeChoice _selectedCaptureMode;
    private decimal _leftInset;
    private decimal _topInset;
    private decimal _rightInset;
    private decimal _bottomInset;
    private string _calibrationStatus = string.Empty;
    private string _previewDescription = string.Empty;
    private double _previewOuterWidth;
    private double _previewOuterHeight;
    private double _previewInnerWidth;
    private double _previewInnerHeight;
    private double _previewInnerLeft;
    private double _previewInnerTop;
    private bool _loadingCalibration;
    private CaptureTargetPreview? _preview;

    public SetupDisplaysViewModel(
        IMonitorService? monitors,
        IGameWindowLocator? windows,
        IDesktopWindowPlacementController? placement = null,
        ICaptureTargetCalibrationStore? calibrations = null,
        TimeProvider? timeProvider = null)
    {
        _monitors = monitors;
        _windows = windows;
        _placement = placement;
        _calibrations = calibrations;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _isAvailable = monitors is not null;
        CaptureModes =
        [
            new(GameWindowPresentationMode.Windowed, SetupText.DisplaysModeWindowed),
            new(GameWindowPresentationMode.Borderless, SetupText.DisplaysModeBorderless),
        ];
        _selectedCaptureMode = CaptureModes[0];
        RefreshCommand = new AsyncDelegateCommand(() => RefreshAsync(CancellationToken.None));
        SaveCalibrationCommand = new AsyncDelegateCommand(() => SaveCalibrationAsync(CancellationToken.None));
        ResetCalibrationCommand = new AsyncDelegateCommand(() => ResetCalibrationAsync(CancellationToken.None));
        if (_placement is not null)
        {
            _placement.CurrentDisplayChanged += PlacementCurrentDisplayChanged;
        }
    }

    public ObservableCollection<SetupDisplayRow> Displays { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand SaveCalibrationCommand { get; }
    public ICommand ResetCalibrationCommand { get; }
    public IReadOnlyList<SetupCaptureModeChoice> CaptureModes { get; }

    public string Heading => SetupText.DisplaysHeading;
    public string CaptureHeading => SetupText.DisplaysCaptureHeading;
    public string RefreshLabel => SetupText.DisplaysRefresh;
    public string UnavailableNote => SetupText.DisplaysNoInfo;
    public string CalibrationHeading => SetupText.DisplaysCalibrationHeading;
    public string CaptureModeLabel => SetupText.DisplaysCaptureMode;
    public string InsetsHeading => SetupText.DisplaysInsetsHeading;
    public string LeftInsetLabel => SetupText.DisplaysInsetLeft;
    public string TopInsetLabel => SetupText.DisplaysInsetTop;
    public string RightInsetLabel => SetupText.DisplaysInsetRight;
    public string BottomInsetLabel => SetupText.DisplaysInsetBottom;
    public string SaveCalibrationLabel => SetupText.DisplaysCalibrationSave;
    public string ResetCalibrationLabel => SetupText.DisplaysCalibrationReset;

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

    public bool CanCalibrate => _calibrations is not null && _gameWindow is { IsMinimized: false } && _gameDisplay is not null;

    public SetupCaptureModeChoice SelectedCaptureMode
    {
        get => _selectedCaptureMode;
        set
        {
            if (value is null || !SetProperty(ref _selectedCaptureMode, value))
            {
                return;
            }

            LoadCalibrationAsync(CancellationToken.None).Observe("setup", "load capture calibration");
        }
    }

    public decimal LeftInset
    {
        get => _leftInset;
        set => SetInset(ref _leftInset, value, nameof(LeftInset));
    }

    public decimal TopInset
    {
        get => _topInset;
        set => SetInset(ref _topInset, value, nameof(TopInset));
    }

    public decimal RightInset
    {
        get => _rightInset;
        set => SetInset(ref _rightInset, value, nameof(RightInset));
    }

    public decimal BottomInset
    {
        get => _bottomInset;
        set => SetInset(ref _bottomInset, value, nameof(BottomInset));
    }

    public string CalibrationStatus
    {
        get => _calibrationStatus;
        private set => SetProperty(ref _calibrationStatus, value);
    }

    public string PreviewDescription
    {
        get => _previewDescription;
        private set => SetProperty(ref _previewDescription, value);
    }

    public double PreviewOuterWidth { get => _previewOuterWidth; private set => SetProperty(ref _previewOuterWidth, value); }
    public double PreviewOuterHeight { get => _previewOuterHeight; private set => SetProperty(ref _previewOuterHeight, value); }
    public double PreviewInnerWidth { get => _previewInnerWidth; private set => SetProperty(ref _previewInnerWidth, value); }
    public double PreviewInnerHeight { get => _previewInnerHeight; private set => SetProperty(ref _previewInnerHeight, value); }
    public double PreviewInnerLeft { get => _previewInnerLeft; private set => SetProperty(ref _previewInnerLeft, value); }
    public double PreviewInnerTop { get => _previewInnerTop; private set => SetProperty(ref _previewInnerTop, value); }

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
        _gameWindow = game;
        _gameDisplay = holding;
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
                SetupText.DisplaysDetail(display.Bounds.Width, display.Bounds.Height, display.Scale, display.Id),
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
        OnPropertyChanged(nameof(CanCalibrate));
        if (game is { IsMinimized: false } && holding is not null)
        {
            var inferred = SameBounds(game.Bounds, holding.Bounds)
                ? GameWindowPresentationMode.Borderless
                : GameWindowPresentationMode.Windowed;
            _selectedCaptureMode = CaptureModes.Single(choice => choice.Mode == inferred);
            OnPropertyChanged(nameof(SelectedCaptureMode));
            await LoadCalibrationAsync(cancellationToken).ConfigureAwait(true);
        }
        else
        {
            ClearPreview();
        }
    }

    private async Task LoadCalibrationAsync(CancellationToken cancellationToken)
    {
        if (_calibrations is null || _gameWindow is null || _gameDisplay is null)
        {
            ClearPreview();
            return;
        }

        var key = CaptureTargetCalibration.KeyFor(_gameDisplay, _gameWindow, SelectedCaptureMode.Mode);
        var state = await _calibrations.GetAsync(cancellationToken).ConfigureAwait(true);
        var saved = state.Profiles.FirstOrDefault(profile => profile.Key == key);
        _loadingCalibration = true;
        try
        {
            ApplyInsets(saved?.Insets ?? CaptureTargetInsets.None);
        }
        finally
        {
            _loadingCalibration = false;
        }

        UpdatePreview();
        CalibrationStatus = saved is null
            ? SetupText.DisplaysCalibrationDefault
            : SetupText.DisplaysCalibrationLoaded;
    }

    private async Task SaveCalibrationAsync(CancellationToken cancellationToken)
    {
        if (_calibrations is null || _preview is not { CaptureBounds: not null } preview)
        {
            CalibrationStatus = SetupText.DisplaysCalibrationInvalid;
            return;
        }

        try
        {
            await _calibrations.SaveAsync(
                new(preview.Key, CurrentInsets(), _timeProvider.GetUtcNow().ToUniversalTime()),
                cancellationToken).ConfigureAwait(true);
            CalibrationStatus = SetupText.DisplaysCalibrationSaved;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CalibrationStatus = SetupText.DisplaysCalibrationSaveFailed;
            WorkspaceFault.Record("setup", "save capture calibration", exception);
        }
    }

    private async Task ResetCalibrationAsync(CancellationToken cancellationToken)
    {
        if (_calibrations is null || _gameWindow is null || _gameDisplay is null)
        {
            return;
        }

        var key = CaptureTargetCalibration.KeyFor(_gameDisplay, _gameWindow, SelectedCaptureMode.Mode);
        try
        {
            await _calibrations.DeleteAsync(key, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CalibrationStatus = SetupText.DisplaysCalibrationResetFailed;
            WorkspaceFault.Record("setup", "reset capture calibration", exception);
            return;
        }

        _loadingCalibration = true;
        try
        {
            ApplyInsets(CaptureTargetInsets.None);
        }
        finally
        {
            _loadingCalibration = false;
        }

        UpdatePreview();
        CalibrationStatus = SetupText.DisplaysCalibrationResetDone;
    }

    private void SetInset(ref decimal field, decimal value, string propertyName)
    {
        value = Math.Clamp(decimal.Round(value), 0, CaptureTargetCalibration.MaximumInsetPixels);
        if (!SetProperty(ref field, value, propertyName))
        {
            return;
        }

        UpdatePreview();
        if (!_loadingCalibration)
        {
            CalibrationStatus = SetupText.DisplaysCalibrationUnsaved;
        }
    }

    private void ApplyInsets(CaptureTargetInsets insets)
    {
        LeftInset = insets.Left;
        TopInset = insets.Top;
        RightInset = insets.Right;
        BottomInset = insets.Bottom;
    }

    private CaptureTargetInsets CurrentInsets() => new(
        decimal.ToInt32(LeftInset),
        decimal.ToInt32(TopInset),
        decimal.ToInt32(RightInset),
        decimal.ToInt32(BottomInset));

    private void UpdatePreview()
    {
        if (_gameWindow is null || _gameDisplay is null)
        {
            ClearPreview();
            return;
        }

        _preview = CaptureTargetCalibration.Preview(
            _gameDisplay,
            _gameWindow,
            SelectedCaptureMode.Mode,
            CurrentInsets());
        var scale = Math.Min(400d / Math.Max(1, _gameWindow.Bounds.Width), 200d / Math.Max(1, _gameWindow.Bounds.Height));
        PreviewOuterWidth = _gameWindow.Bounds.Width * scale;
        PreviewOuterHeight = _gameWindow.Bounds.Height * scale;
        if (_preview.CaptureBounds is not { } capture)
        {
            PreviewInnerWidth = 0;
            PreviewInnerHeight = 0;
            PreviewInnerLeft = 0;
            PreviewInnerTop = 0;
            PreviewDescription = SetupText.DisplaysCalibrationInvalid;
            return;
        }

        PreviewInnerWidth = capture.Width * scale;
        PreviewInnerHeight = capture.Height * scale;
        PreviewInnerLeft = (capture.X - _gameWindow.Bounds.X) * scale;
        PreviewInnerTop = (capture.Y - _gameWindow.Bounds.Y) * scale;
        PreviewDescription = SetupText.DisplaysCalibrationPreview(capture.Width, capture.Height, capture.X, capture.Y);
    }

    private void ClearPreview()
    {
        _preview = null;
        PreviewOuterWidth = PreviewOuterHeight = PreviewInnerWidth = PreviewInnerHeight = 0;
        PreviewInnerLeft = PreviewInnerTop = 0;
        PreviewDescription = string.Empty;
        CalibrationStatus = string.Empty;
        OnPropertyChanged(nameof(CanCalibrate));
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
        var x = (long)window.X + (window.Width / 2L);
        var y = (long)window.Y + (window.Height / 2L);
        return x >= display.X && x < (long)display.X + display.Width &&
               y >= display.Y && y < (long)display.Y + display.Height;
    }

    private static bool SameBounds(PixelRect left, PixelRect right) =>
        left.X == right.X && left.Y == right.Y && left.Width == right.Width && left.Height == right.Height;
}
