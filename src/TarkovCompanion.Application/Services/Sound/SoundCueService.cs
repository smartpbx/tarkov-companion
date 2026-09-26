using Microsoft.Extensions.Logging;

namespace TarkovCompanion.Application.Services.Sound;

/// <summary>
/// The one way anything in the companion makes a sound (#712 T5): the settings gate, the rate
/// limit and the queue.
/// </summary>
/// <remarks>
/// <para>
/// Three rules, each held here so no trigger can break it. Nothing is heard while the master
/// switch or the cue's own switch is off, checked when a cue is asked for and again just before
/// it plays, so turning sound off mid-queue silences what was waiting. No two cues start less than
/// <see cref="MinimumGap"/> apart. And one thing plays at a time: a line is never spoken over the
/// previous one; while one plays, at most <see cref="QueueCapacity"/> wait, and a newer one pushes
/// out the oldest, because the newest scan or ping is the one the player is thinking about.
/// </para>
/// <para>
/// Output only. Nothing here opens a microphone or listens to the game.
/// </para>
/// </remarks>
public sealed class SoundCueService : ISoundCues, IDisposable
{
    public static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(1);
    public const int QueueCapacity = 2;

    private readonly SoundSettingsStore _settings;
    private readonly ICueOutput _cues;
    private readonly ISpeechOutput _speech;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger? _logger;
    private readonly Lock _gate = new();
    private readonly LinkedList<(SoundCue Cue, string? Spoken)> _queue = new();
    private readonly CancellationTokenSource _stop = new();
    private DateTimeOffset? _lastAccepted;
    private DateTimeOffset? _lastStarted;
    private Task _pump = Task.CompletedTask;
    private bool _pumping;

    public SoundCueService(
        SoundSettingsStore settings,
        ICueOutput cues,
        ISpeechOutput speech,
        TimeProvider? time = null,
        ILogger<SoundCueService>? logger = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _cues = cues ?? throw new ArgumentNullException(nameof(cues));
        _speech = speech ?? throw new ArgumentNullException(nameof(speech));
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((wait, token) => Task.Delay(wait, _time, token));
        _logger = logger;
        _settings.Changed += SettingsChanged;
    }

    /// <summary>Raised as each cue starts, for Diagnostics and tests: the cue and the line spoken, if any.</summary>
    public event Action<SoundCue, string?>? Started;

    /// <summary>Completes when nothing is queued or playing.</summary>
    public Task Idle
    {
        get
        {
            lock (_gate)
            {
                return _pump;
            }
        }
    }

    public bool Play(SoundCue cue, string? spoken = null)
    {
        if (!_settings.Current.Allows(cue) || _stop.IsCancellationRequested)
        {
            return false;
        }

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_lastAccepted is { } last && now - last < MinimumGap && now >= last)
            {
                return false;
            }

            _lastAccepted = now;
            _queue.AddLast((cue, string.IsNullOrWhiteSpace(spoken) ? null : spoken.Trim()));
            while (_queue.Count > QueueCapacity)
            {
                _queue.RemoveFirst();
            }

            if (!_pumping)
            {
                _pumping = true;
                _pump = Task.Run(PumpAsync);
            }
        }

        return true;
    }

    public void Dispose()
    {
        _settings.Changed -= SettingsChanged;
        _stop.Cancel();
        lock (_gate)
        {
            _queue.Clear();
        }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            (SoundCue Cue, string? Spoken) next;
            TimeSpan wait;
            lock (_gate)
            {
                if (_queue.Count == 0 || _stop.IsCancellationRequested)
                {
                    _pumping = false;
                    return;
                }

                next = _queue.First!.Value;
                _queue.RemoveFirst();
                var now = _time.GetUtcNow();
                wait = _lastStarted is { } started && now >= started && now - started < MinimumGap
                    ? MinimumGap - (now - started)
                    : TimeSpan.Zero;
            }

            try
            {
                if (wait > TimeSpan.Zero)
                {
                    await _delay(wait, _stop.Token).ConfigureAwait(false);
                }

                var settings = _settings.Current;
                if (!settings.Allows(next.Cue))
                {
                    continue;
                }

                lock (_gate)
                {
                    _lastStarted = _time.GetUtcNow();
                }

                await PlayOneAsync(next.Cue, next.Spoken, settings).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                lock (_gate)
                {
                    _pumping = false;
                }

                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A missing audio device is not worth more than a log line; the next cue tries again.
                _logger?.LogInformation(exception, "Sound cue {Cue} could not play.", next.Cue);
            }
        }
    }

    private async Task PlayOneAsync(SoundCue cue, string? spoken, SoundSettings settings)
    {
        var output = settings.Output;
        var speak = spoken is not null && _speech.IsAvailable && (cue != SoundCue.LootVerdict || settings.SpeakLootVerdicts);
        Started?.Invoke(cue, speak ? spoken : null);
        // A spoken verdict is its own cue; the Test button plays the tone and then says its line.
        if (!speak || cue == SoundCue.Test)
        {
            await _cues.PlayAsync(Earcons.Wav(cue), output, _stop.Token).ConfigureAwait(false);
        }

        if (speak)
        {
            await _speech.SpeakAsync(spoken!, output, _stop.Token).ConfigureAwait(false);
        }
    }

    private void SettingsChanged(object? sender, EventArgs eventArgs)
    {
        if (_settings.Current.Enabled)
        {
            return;
        }

        lock (_gate)
        {
            _queue.Clear();
        }
    }
}
