namespace TarkovCompanion.Application.Services.Devices;

/// <summary>The last difference measured from a relay HTTP <c>Date</c> header.</summary>
/// <param name="OffsetSeconds">Relay time minus this PC's time, in whole seconds.</param>
public sealed record RelayClockOffset(long OffsetSeconds)
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
    private readonly RelayLinkLog? _log;
    private RelayClockOffset? _current;
    private bool _loggedMeasurement;

    public RelayClockOffsetTracker(RelayLinkLog? log = null) => _log = log;

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

    public void Observe(DateTimeOffset relayDateUtc, DateTimeOffset localReceivedUtc) =>
        ObserveOffsetSeconds(checked((long)Math.Round(
            (relayDateUtc - localReceivedUtc).TotalSeconds,
            MidpointRounding.AwayFromZero)));

    /// <summary>Accepts the claim route's more precise server-minus-claim measurement.</summary>
    public void ObserveOffsetSeconds(long offsetSeconds)
    {
        if (offsetSeconds is < -MaximumAcceptedOffsetSeconds or > MaximumAcceptedOffsetSeconds)
        {
            return;
        }

        var next = new RelayClockOffset(offsetSeconds);
        RelayClockOffset? previous;
        var logMeasurement = false;
        lock (_gate)
        {
            previous = _current;
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
        if (previous?.IsSkewed == true && !next.IsSkewed)
        {
            _log?.Write("clock-corrected", "PC clock now agrees with the relay; retrying registration.");
            ClockCorrected?.Invoke();
        }
    }
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
            _tracker.Observe(relayDate, _clock.GetUtcNow());
        }

        return response;
    }
}
