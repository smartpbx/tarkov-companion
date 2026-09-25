namespace TarkovCompanion.Application.Services.Devices;

/// <summary>The last difference measured from a relay HTTP <c>Date</c> header.</summary>
/// <param name="OffsetSeconds">Relay time minus this PC's time, in whole seconds.</param>
/// <param name="MeasuredOverTls">
/// [#891] Read from an HTTPS (or loopback) response, so nobody on the path could have set it.
/// Only such a measurement is used to correct this PC's time; any other is only reported.
/// </param>
public sealed record RelayClockOffset(long OffsetSeconds, bool MeasuredOverTls = false)
{
    public bool IsSkewed => OffsetSeconds is < -60 or > 60;
}

/// <summary>
/// Measures this PC against the relay's HTTP clock and publishes only the latest bounded fact.
/// </summary>
/// <remarks>
/// The relay's process-start time is not a clock sample. The HTTP <c>Date</c> header is stamped
/// for the response itself, so every relay request can refresh this without another polling loop.
/// </remarks>
public sealed class RelayClockOffsetTracker
{
    private const long MaximumAcceptedOffsetSeconds = 10L * 365 * 24 * 60 * 60;
    private readonly Lock _gate = new();
    /// <summary>
    /// How far the wall clock may drift from the monotonic one after a measurement before the
    /// measurement is assumed to describe a clock that has since been set.
    /// </summary>
    private static readonly TimeSpan StepTolerance = TimeSpan.FromMinutes(2);

    private readonly RelayLinkLog? _log;
    private readonly TimeProvider _clock;
    private RelayClockOffset? _current;
    private DateTimeOffset _measuredAtPcUtc;
    private long _measuredAtTimestamp;
    private bool _loggedMeasurement;

    /// <param name="clock">
    /// The PC's clock, the same one every caller's "now" comes from. Its monotonic timestamp is
    /// what notices the wall clock being set after a measurement.
    /// </param>
    public RelayClockOffsetTracker(RelayLinkLog? log = null, TimeProvider? clock = null)
    {
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    public RelayClockOffset? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event Action<RelayClockOffset>? Changed;

    /// <summary>Raised once a previously bad offset is observed inside the one-minute allowance.</summary>
    public event Action? ClockCorrected;

    /// <summary>
    /// [#891] Raised when a skew this PC can correct for is first known: registration that was
    /// refused for the clock can be tried again at once, signed with the corrected time.
    /// </summary>
    public event Action? CorrectionAvailable;

    /// <summary>Whether a response from <paramref name="relay"/> can be trusted to set the clock.</summary>
    public static bool IsTrustedTransport(Uri? relay) =>
        relay is { IsAbsoluteUri: true } uri &&
        (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || uri.IsLoopback);

    public void Observe(DateTimeOffset relayDateUtc, DateTimeOffset localReceivedUtc, bool overTls = false) =>
        ObserveOffsetSeconds(
            checked((long)Math.Round((relayDateUtc - localReceivedUtc).TotalSeconds, MidpointRounding.AwayFromZero)),
            overTls,
            localReceivedUtc);

    /// <summary>Accepts the claim route's more precise server-minus-claim measurement.</summary>
    /// <param name="pcUtc">This PC's time the offset was measured against; now when omitted.</param>
    public void ObserveOffsetSeconds(long offsetSeconds, bool overTls = false, DateTimeOffset? pcUtc = null)
    {
        if (offsetSeconds is < -MaximumAcceptedOffsetSeconds or > MaximumAcceptedOffsetSeconds)
        {
            return;
        }

        var next = new RelayClockOffset(offsetSeconds, overTls);
        RelayClockOffset? previous;
        var logMeasurement = false;
        lock (_gate)
        {
            previous = _current;
            _measuredAtPcUtc = pcUtc ?? _clock.GetUtcNow();
            _measuredAtTimestamp = _clock.GetTimestamp();
            if (previous == next)
            {
                return;
            }

            _current = next;
            if (!_loggedMeasurement)
            {
                _loggedMeasurement = true;
                logMeasurement = true;
            }
        }

        if (logMeasurement)
        {
            _log?.Write(
                "clock-offset",
                $"clock offset from relay Date header: {offsetSeconds} seconds (server minus PC).",
                next.IsSkewed
                    ? Microsoft.Extensions.Logging.LogLevel.Warning
                    : Microsoft.Extensions.Logging.LogLevel.Information);
        }

        Changed?.Invoke(next);
        if (next is { IsSkewed: true, MeasuredOverTls: true } && previous is not { IsSkewed: true, MeasuredOverTls: true })
        {
            _log?.Write("clock-correcting", "correcting relay times for the PC clock; retrying registration.");
            CorrectionAvailable?.Invoke();
        }

        if (previous?.IsSkewed == true && !next.IsSkewed)
        {
            _log?.Write("clock-corrected", "PC clock now agrees with the relay; retrying registration.");
            ClockCorrected?.Invoke();
        }
    }

    /// <summary>[#891] What this PC's clock is corrected by, at <paramref name="pcNowUtc"/>.</summary>
    /// <remarks>
    /// Zero unless a skew beyond the allowance was measured over a trusted transport, and zero
    /// again once the wall clock has moved differently from the monotonic clock since that
    /// measurement: the clock was set, and the old offset no longer describes it. The next relay
    /// response measures it again.
    /// </remarks>
    public TimeSpan CorrectionAt(DateTimeOffset pcNowUtc)
    {
        lock (_gate)
        {
            if (_current is not { IsSkewed: true, MeasuredOverTls: true } current)
            {
                return TimeSpan.Zero;
            }

            var wallElapsed = pcNowUtc - _measuredAtPcUtc;
            var monotonicElapsed = _clock.GetElapsedTime(_measuredAtTimestamp);
            return (wallElapsed - monotonicElapsed).Duration() > StepTolerance
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(current.OffsetSeconds);
        }
    }

    /// <summary>The relay's time for this PC's <paramref name="pcNowUtc"/>, where a skew is known.</summary>
    public DateTimeOffset ToRelayTime(DateTimeOffset pcNowUtc) => pcNowUtc + CorrectionAt(pcNowUtc);
}

/// <summary>Adds relay Date-header observation to an ordinary <see cref="HttpClient"/>.</summary>
public sealed class RelayClockTrackingHandler : DelegatingHandler
{
    private readonly RelayClockOffsetTracker _tracker;
    private readonly TimeProvider _clock;

    public RelayClockTrackingHandler(
        RelayClockOffsetTracker tracker,
        TimeProvider clock,
        HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Headers.Date is { } relayDate)
        {
            // The raw PC clock, never a corrected one: measuring against a corrected clock would
            // read back zero and switch the correction off again.
            _tracker.Observe(relayDate, _clock.GetUtcNow(), RelayClockOffsetTracker.IsTrustedTransport(request.RequestUri));
        }

        return response;
    }
}

/// <summary>
/// [#891] This PC's clock as the relay would read it: wall time corrected by the measured skew,
/// timers and the monotonic timestamp untouched.
/// </summary>
/// <remarks>
/// For the timestamps a relay or a tablet checks (invitations, grants, resumes). Never give it to
/// a <see cref="RelayClockTrackingHandler"/>: measured against itself, the skew reads as zero.
/// </remarks>
public sealed class RelayCorrectedTimeProvider(TimeProvider pcClock, RelayClockOffsetTracker tracker) : TimeProvider
{
    private readonly TimeProvider _pcClock = pcClock ?? throw new ArgumentNullException(nameof(pcClock));
    private readonly RelayClockOffsetTracker _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));

    public override DateTimeOffset GetUtcNow() => _tracker.ToRelayTime(_pcClock.GetUtcNow());

    public override TimeZoneInfo LocalTimeZone => _pcClock.LocalTimeZone;

    public override long TimestampFrequency => _pcClock.TimestampFrequency;

    public override long GetTimestamp() => _pcClock.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        _pcClock.CreateTimer(callback, state, dueTime, period);
}
