using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>
/// Keeps the Stash intent armed while a guided scan is actively being taken, so each screenshot
/// of the scroll-through is a stash screenshot without a trip back to the capture dialog.
/// </summary>
/// <remarks>
/// <para>
/// An armed intent lives fifteen seconds and is spent by one capture. That suits a loot scan and
/// cannot suit a stash, which is several screenshots with scrolling in between: the second
/// screenshot arrived unarmed, was read as a generic grid, and went to Loot.
/// </para>
/// <para>
/// This only re-arms when the capture service has nothing in flight, and only while the player is
/// actively scanning: a scan read back from disk at start-up does not hold the one armed slot
/// until they say "keep going", and a scan left alone for <see cref="IdleAfter"/> lets it go, so
/// an abandoned stash scan can never swallow a loot scan in the next raid.
/// </para>
/// </remarks>
public sealed class GuidedStashScanArming : IDisposable
{
    public static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(10);

    private readonly ICaptureSessionService _captureSessions;
    private readonly GuidedStashScanService _guidedScan;
    private readonly WorkspaceOrigin _origin;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GuidedStashScanArming> _logger;
    private readonly Lock _gate = new();
    private bool _active;
    private bool _arming;
    private DateTimeOffset _lastActivityUtc;
    private bool _disposed;

    public GuidedStashScanArming(
        ICaptureSessionService captureSessions,
        GuidedStashScanService guidedScan,
        WorkspaceOrigin origin,
        TimeProvider? timeProvider = null,
        ILogger<GuidedStashScanArming>? logger = null)
    {
        _captureSessions = captureSessions ?? throw new ArgumentNullException(nameof(captureSessions));
        _guidedScan = guidedScan ?? throw new ArgumentNullException(nameof(guidedScan));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<GuidedStashScanArming>.Instance;
        _captureSessions.Changed += OnCaptureSessionsChanged;
        _guidedScan.Changed += OnGuidedScanChanged;
    }

    /// <summary>True while screenshots are being taken as stash screenshots.</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    public event EventHandler? Changed;

    /// <summary>The player started, or came back to, the scan: hold the Stash intent armed.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            _active = true;
            _lastActivityUtc = _timeProvider.GetUtcNow();
        }

        ArmIfIdle();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stop holding the intent. The scan itself is untouched.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            _active = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnGuidedScanChanged(object? sender, EventArgs eventArgs)
    {
        var collecting = _guidedScan.Current.IsCollecting;
        lock (_gate)
        {
            if (!collecting)
            {
                _active = false;
            }
            else
            {
                _lastActivityUtc = _timeProvider.GetUtcNow();
            }
        }
    }

    private void OnCaptureSessionsChanged(object? sender, EventArgs eventArgs) => ArmIfIdle();

    private void ArmIfIdle()
    {
        var wentIdle = false;
        lock (_gate)
        {
            if (_disposed || !_active || _arming || !_guidedScan.Current.IsCollecting)
            {
                return;
            }

            if (_timeProvider.GetUtcNow() - _lastActivityUtc > IdleAfter)
            {
                _active = false;
                wentIdle = true;
            }
            else
            {
                _arming = true;
            }
        }

        if (wentIdle)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            if (_captureSessions.Snapshot.Sessions.Any(session => !session.IsTerminal))
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            var receipt = _captureSessions.Arm(new(
                new(new CaptureSessionId(Guid.NewGuid()), ScanIntent.Stash, _origin, now, ProfileId: null, MapId: null, ExpiresUtc: null),
                new CaptureContextMetadata(
                    activeWorkspace: "stash",
                    activeProfile: null,
                    activeMap: null,
                    activePlan: null,
                    selectedEntity: null,
                    priorScan: null,
                    initiatingDevice: "desktop"),
                new("stash_scroll", "Scroll your stash and take the next screenshot.")));
            if (!receipt.Accepted && receipt.Code != "intent_already_armed")
            {
                _logger.LogInformation("The guided stash scan could not re-arm capture: {Code}.", receipt.Code);
            }
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or ArgumentException)
        {
            _logger.LogDebug(exception, "The guided stash scan skipped a re-arm.");
        }
        finally
        {
            lock (_gate)
            {
                _arming = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _captureSessions.Changed -= OnCaptureSessionsChanged;
        _guidedScan.Changed -= OnGuidedScanChanged;
    }
}
