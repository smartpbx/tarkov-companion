using TarkovCompanion.Application.Services.CaptureSessions;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>
/// The Loot page's one-line progress (#572): "Scanning · Matching icons" while a scan runs,
/// "Scanned in 0.9 s" for a moment after, then nothing.
/// </summary>
/// <remarks>
/// A scan used to be silent from screenshot to result, so a slow one read as a broken one.
/// Every screenshot begins a timeline but only a loot scan completes one, so a line that stops
/// hearing anything hides itself rather than claiming a scan is still running.
/// </remarks>
public sealed class LootScanProgressViewModel : BindableViewModel
{
    /// <summary>How long a finished scan's "Scanned in" stays up.</summary>
    public static readonly TimeSpan DoneShownFor = TimeSpan.FromSeconds(4);

    /// <summary>How long a scan may go without a stage before the line stops claiming it runs.</summary>
    public static readonly TimeSpan QuietAfter = TimeSpan.FromSeconds(6);

    private readonly Action<Action> _post;
    private readonly TimeProvider _clock;
    private ITimer? _hide;
    private int _generation;
    private CaptureCorrelationId? _current;
    private string _text = string.Empty;
    private bool _isScanning;

    public LootScanProgressViewModel(ICaptureStageTimeline? timeline, Action<Action> post, TimeProvider? clock = null)
    {
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _clock = clock ?? TimeProvider.System;
        if (timeline is not null)
        {
            timeline.Progressed += progress => _post(() => Apply(progress));
        }
    }

    public string Text
    {
        get => _text;
        private set
        {
            if (SetProperty(ref _text, value))
            {
                OnPropertyChanged(nameof(IsVisible));
            }
        }
    }

    public bool IsVisible => Text.Length > 0;

    /// <summary>Whether a scan is running right now, as opposed to just finished.</summary>
    public bool IsScanning
    {
        get => _isScanning;
        private set => SetProperty(ref _isScanning, value);
    }

    /// <summary>Applies one timeline step. Public so a test can drive it without a dispatcher.</summary>
    public void Apply(CaptureStageStep progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.LastStage is null && progress.Summary is null)
        {
            _current = progress.CorrelationId;
        }
        else if (_current != progress.CorrelationId && progress.Summary is null)
        {
            // A stage of a scan older than the one on screen: the newest screenshot wins.
            return;
        }

        if (progress.Summary is { IsLoot: false })
        {
            // #712 0-12: the screenshot was something else. Its line goes, rather than a stale
            // "Scanning" waiting out the quiet timer, and a Loot scan in progress is left alone.
            if (_current == progress.CorrelationId)
            {
                Text = string.Empty;
                IsScanning = false;
            }

            return;
        }

        Text = LootScanStageText.Progress(progress);
        IsScanning = progress.Summary is null;
        HideAfter(IsScanning ? QuietAfter : DoneShownFor);
    }

    private void HideAfter(TimeSpan delay)
    {
        _hide?.Dispose();
        var generation = ++_generation;
        _hide = _clock.CreateTimer(
            _ => _post(() =>
            {
                if (generation != _generation)
                {
                    return;
                }

                Text = string.Empty;
                IsScanning = false;
            }),
            null,
            delay,
            Timeout.InfiniteTimeSpan);
    }
}
