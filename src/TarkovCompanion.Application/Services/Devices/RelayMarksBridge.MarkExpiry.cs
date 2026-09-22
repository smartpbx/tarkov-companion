using TarkovCompanion.CompanionProtocol;

namespace TarkovCompanion.Application.Services.Devices;

// Takes a paired tablet's pings off its Marks list when they run out.
//
// [#584] The protocol's MarkAggregate is what a paired tablet's Marks list shows,
// and a ping in it carries an expiry. Nothing ever acted on it: the only transition that dropped
// an expired mark was ApplyMaintenance, which the running desktop never called. So a
// tablet's ping left the map after 45 seconds (#589) and stayed on its list for good. One timer,
// kept pointed at the next mark due, runs ExpireMarksAsync
// and sends the result to every paired tablet the same way a command's result goes. Each read of
// the relay also checks, so a timer that fired early or late costs at most one read.
public sealed partial class RelayMarksBridge
{
    /// <summary>Past the due time rather than on it, so the check never lands a tick early.</summary>
    private static readonly TimeSpan MarkExpirySlack = TimeSpan.FromMilliseconds(50);

    private readonly Lock _markExpiryGate = new();
    private ITimer? _markExpiryTimer;
    private bool _markExpiryStopped;

    /// <summary>Points the timer at the next paired mark due to expire, or stops it when none is.</summary>
    private void ScheduleMarkExpiry()
    {
        var due = DesktopCanonicalStateMachine.NextMarkExpiry(_authority.Snapshot.CanonicalState);
        lock (_markExpiryGate)
        {
            _markExpiryTimer?.Dispose();
            _markExpiryTimer = null;
            if (_markExpiryStopped || due is not { } dueUtc)
            {
                return;
            }

            var delay = dueUtc - _clock.GetUtcNow() + MarkExpirySlack;
            _markExpiryTimer = _clock.CreateTimer(
                static state => _ = ((RelayMarksBridge)state!).OnMarkExpiryDueAsync(),
                this,
                delay < TimeSpan.Zero ? TimeSpan.Zero : delay,
                Timeout.InfiniteTimeSpan);
        }
    }

    private async Task OnMarkExpiryDueAsync()
    {
        try
        {
            await ExpireDueMarksAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or ObjectDisposedException)
        {
            // The authority keeps the expiry in its state either way; the next read of the relay
            // or the next command tries the delivery again.
        }

        ScheduleMarkExpiry();
    }

    /// <summary>Drops every paired mark that is due and tells the paired tablets; nothing when none is.</summary>
    public async Task ExpireDueMarksAsync(CancellationToken cancellationToken)
    {
        var mutation = await _authority.ExpireMarksAsync(Now(), cancellationToken).ConfigureAwait(false);
        if (mutation is null)
        {
            return;
        }

        foreach (var delivery in mutation.Deliveries)
        {
            PairedSessionState? target;
            lock (_gate)
            {
                target = _sessionsById.Values.FirstOrDefault(candidate => candidate.DeviceId == delivery.DeviceId);
            }

            if (target is not null)
            {
                await PublishDeliveryAsync(target, delivery, mutation.State.CanonicalState, cancellationToken).ConfigureAwait(false);
            }
        }

        CanonicalStateChanged?.Invoke(mutation.State.CanonicalState);
        ScheduleMarkExpiry();
    }

    private void StopMarkExpiry()
    {
        lock (_markExpiryGate)
        {
            _markExpiryStopped = true;
            _markExpiryTimer?.Dispose();
            _markExpiryTimer = null;
        }
    }
}
