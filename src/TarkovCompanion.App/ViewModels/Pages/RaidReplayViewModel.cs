using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.App.ViewModels;

/// <summary>
/// Walks a finished raid back across the map, a screenshot at a time.
/// </summary>
/// <remarks>
/// Every position has been written to the database on every scan since the first raid, so the
/// data has always been there; this is a viewer over it rather than new capture.
///
/// A step is one screenshot rather than a second of clock time, because that is the resolution
/// of the evidence. Interpolating between two screenshots four minutes apart would draw a walk
/// nobody observed, which is the same reason the trail itself is dotted.
/// </remarks>
public sealed class RaidReplayViewModel : BindableViewModel
{
    /// <summary>How long one step is held when it plays itself.</summary>
    /// <remarks>
    /// Fast enough to watch a raid in under a minute, slow enough to read where each one was.
    /// </remarks>
    private static readonly TimeSpan StepFor = TimeSpan.FromSeconds(1);

    private readonly MapViewModel _map;
    private readonly DispatcherTimer _timer;
    private IReadOnlyList<ScreenshotPosition> _positions = [];
    private int _step;
    private bool _isPlaying;
    private string _title = string.Empty;

    public RaidReplayViewModel(MapViewModel map)
    {
        _map = map;
        _timer = new DispatcherTimer { Interval = StepFor };
        _timer.Tick += (_, _) => Advance();
        PlayCommand = new DelegateCommand(TogglePlay);
        BackCommand = new DelegateCommand(() => Step -= 1);
        ForwardCommand = new DelegateCommand(() => Step += 1);
        CloseCommand = new DelegateCommand(Close);
    }

    public bool IsOpen => _positions.Count > 0;

    /// <summary>The raid being replayed, for the bar to name.</summary>
    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>Which screenshot is being shown, from zero.</summary>
    public int Step
    {
        get => _step;
        set
        {
            var clamped = _positions.Count == 0 ? 0 : Math.Clamp(value, 0, _positions.Count - 1);
            if (!SetProperty(ref _step, clamped))
            {
                return;
            }

            OnPropertyChanged(nameof(Position));
            _map.ShowReplay(_positions, clamped);
        }
    }

    /// <summary>The last step, so a slider knows where to stop.</summary>
    public int LastStep => Math.Max(0, _positions.Count - 1);

    /// <summary>Where in the raid this is, and when.</summary>
    public string Position => _positions.Count == 0
        ? string.Empty
        : string.Create(
            CultureInfo.CurrentCulture,
            $"{_step + 1} of {_positions.Count} · {LocalTime.Time(_positions[_step].Timestamp)}");

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            SetProperty(ref _isPlaying, value);
            OnPropertyChanged(nameof(PlayLabel));
        }
    }

    public string PlayLabel => IsPlaying ? "Pause" : "Play";

    public ICommand PlayCommand { get; }

    public ICommand BackCommand { get; }

    public ICommand ForwardCommand { get; }

    public ICommand CloseCommand { get; }

    /// <summary>Opens a raid, or closes the bar when it has no screenshots to show.</summary>
    public void Open(string title, IReadOnlyList<ScreenshotPosition> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        Stop();
        _positions = positions;
        _step = 0;
        Title = title;
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(LastStep));
        OnPropertyChanged(nameof(Step));
        OnPropertyChanged(nameof(Position));
        if (positions.Count == 0)
        {
            _map.ClearReplay();
            return;
        }

        _map.ShowReplay(positions, 0);
    }

    public void Close()
    {
        Stop();
        _positions = [];
        _step = 0;
        Title = string.Empty;
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(LastStep));
        OnPropertyChanged(nameof(Step));
        OnPropertyChanged(nameof(Position));
        _map.ClearReplay();
    }

    /// <summary>
    /// Stops at the end rather than looping.
    /// </summary>
    /// <remarks>
    /// A replay that starts over is a replay somebody has to catch to stop, and the last
    /// screenshot is where the raid ended, which is the frame worth resting on.
    /// </remarks>
    private void Advance()
    {
        if (_step >= LastStep)
        {
            Stop();
            return;
        }

        Step += 1;
    }

    private void TogglePlay()
    {
        if (IsPlaying)
        {
            Stop();
            return;
        }

        if (!IsOpen)
        {
            return;
        }

        // From the start again when the last frame is already showing, because pressing play
        // on a finished replay means watch it, not do nothing.
        if (_step >= LastStep)
        {
            Step = 0;
        }

        IsPlaying = true;
        _timer.Start();
    }

    private void Stop()
    {
        _timer.Stop();
        IsPlaying = false;
    }
}
