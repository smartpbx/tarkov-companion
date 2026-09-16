using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.Application.Services.Devices;

/// <summary>
/// Serializes every paired-device mutation through the desktop's sole canonical authority and
/// persists canonical state, lifecycle records, and delivery history as one record.
/// </summary>
public sealed class DesktopCompanionAuthority : IDisposable
{
    private readonly IDesktopCompanionAuthorityStore _store;
    private readonly IDisposable _lease;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DesktopCompanionAuthorityState _state;
    private bool _disposed;
    private int _disposeStarted;

    private DesktopCompanionAuthority(
        IDesktopCompanionAuthorityStore store,
        IDisposable lease,
        DesktopCompanionAuthorityState state)
    {
        _store = store;
        _lease = lease;
        _state = state;
    }

    public DesktopCompanionAuthorityState Snapshot => Volatile.Read(ref _state);

    public static async ValueTask<DesktopCompanionAuthority> OpenAsync(
        IDesktopCompanionAuthorityStore store,
        CanonicalCompanionState initialCanonicalState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(initialCanonicalState);
        var lease = await store.AcquireExclusiveLeaseAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                state = DesktopCompanionAuthorityState.Create(initialCanonicalState);
                await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }

            return new DesktopCompanionAuthority(store, lease, state);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public async ValueTask<AuthorityMutation> RegisterPairingAsync(
        PairingAttempt completedPairing,
        string approvedDisplayName,
        DeviceAuthorizationRole role,
        IReadOnlyList<DeviceCapability> deviceCapabilities,
        IReadOnlyList<DeviceCapability> sessionCapabilities,
        DateTimeOffset deviceExpiresUtc,
        CompanionTransportKind transport,
        CompanionSurfaceKind surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completedPairing);
        ArgumentNullException.ThrowIfNull(deviceCapabilities);
        ArgumentNullException.ThrowIfNull(sessionCapabilities);
        return await MutateAsync(state =>
        {
            var device = DeviceLifecycle.Pair(
                completedPairing,
                approvedDisplayName,
                role,
                deviceCapabilities,
                deviceExpiresUtc);
            if (state.Devices.Count >= ProtocolBounds.MaxDevices ||
                state.Devices.Any(existing =>
                    existing.DeviceId == device.DeviceId || existing.DeviceKey.KeyId == device.DeviceKey.KeyId))
            {
                throw new InvalidOperationException("The paired-device limit or identity uniqueness boundary was reached.");
            }

            var establishment = completedPairing.Establishment!;
            if (establishment.Assignment.SessionExpiresUtc > device.ExpiresUtc)
            {
                throw new InvalidOperationException("The initial session cannot outlive its paired device.");
            }

            var session = new DeviceSession(
                establishment,
                DeviceSessionStatus.Active,
                transport,
                surface,
                sessionCapabilities.Intersect(device.Capabilities).ToArray(),
                establishment.EstablishedUtc);
            var ledger = state.DeliveryLedger.Enqueue(
                device.DeviceId,
                state.CanonicalState.DesktopDeviceId,
                new CanonicalSnapshotMessage(state.CanonicalState),
                establishment.EstablishedUtc);
            var next = state.With(
                devices: state.Devices.Append(device).ToArray(),
                sessions: state.Sessions.Append(session).ToArray(),
                deliveryLedger: ledger.Ledger);
            return (next, (IReadOnlyList<AuthorityDelivery>)[new(device.DeviceId, ledger.Item)]);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AuthorityMutation> RegisterResumedSessionAsync(
        SessionResumeAttempt completedResume,
        IReadOnlyList<DeviceCapability> sessionCapabilities,
        CompanionTransportKind transport,
        CompanionSurfaceKind surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completedResume);
        ArgumentNullException.ThrowIfNull(sessionCapabilities);
        return await MutateAsync(state =>
        {
            if (completedResume.Stage != SessionResumeStage.Completed || completedResume.Establishment is not { } establishment)
            {
                throw new InvalidOperationException("Only a completed, device-proved resume can register a session.");
            }

            var deviceIndex = IndexOfDevice(state, establishment.Assignment.DeviceId);
            var device = DeviceLifecycle.RecordSession(state.Devices[deviceIndex], establishment);
            var sessions = state.Sessions.ToList();
            var canonical = state.CanonicalState;
            var updates = new List<CanonicalUpdate>();
            for (var index = 0; index < sessions.Count; index++)
            {
                var existing = sessions[index];
                if (existing.DeviceId != device.DeviceId || existing.Status != DeviceSessionStatus.Active)
                {
                    continue;
                }

                var ended = DeviceLifecycle.EndSession(
                    existing,
                    DeviceSessionStatus.Replaced,
                    establishment.EstablishedUtc,
                    "session-resumed");
                sessions[index] = ended;
                var reduction = DesktopCanonicalStateMachine.ApplySessionTermination(canonical, ended);
                canonical = reduction.State;
                updates.AddRange(reduction.Updates);
            }

            sessions.Add(new DeviceSession(
                establishment,
                DeviceSessionStatus.Active,
                transport,
                surface,
                sessionCapabilities.Intersect(device.Capabilities).ToArray(),
                establishment.EstablishedUtc));
            sessions = RetainSessions(sessions);
            var devices = state.Devices.ToArray();
            devices[deviceIndex] = device;
            var next = state.With(canonicalState: canonical, devices: devices, sessions: sessions);
            var delivered = Broadcast(next, updates, canonical.DesktopDeviceId, establishment.EstablishedUtc);
            return (delivered.State, delivered.Deliveries);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PairedCommandApplication> ApplyCommandAsync(
        AuthenticatedPairedFrame frame,
        ClientCommandEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(envelope);
        await WaitForMutationGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _state;
            var resolved = ResolveFrame(state, frame);
            var use = DeviceLifecycle.RecordUse(resolved.Device, resolved.Session, frame.ReceivedUtc);
            var context = AuthenticatedCommandContext.ForPairedSession(
                use.Device,
                use.Session,
                frame.ClientInstanceId,
                frame.ReceivedUtc);
            var reduction = DesktopCanonicalStateMachine.Apply(state.CanonicalState, envelope, context);
            var devices = ReplaceDevice(state.Devices, use.Device);
            var sessions = ReplaceSession(state.Sessions, use.Session);
            var next = state.With(canonicalState: reduction.State, devices: devices, sessions: sessions);
            var deliveries = new List<AuthorityDelivery>();
            if (reduction.Update is not null)
            {
                var broadcast = Broadcast(next, [reduction.Update], context.DeviceId, frame.ReceivedUtc);
                next = broadcast.State;
                deliveries.AddRange(broadcast.Deliveries);
            }

            var acknowledgement = next.DeliveryLedger.Enqueue(
                context.DeviceId,
                context.DeviceId,
                new CommandAcknowledgementMessage(reduction.Acknowledgement),
                frame.ReceivedUtc);
            next = next.With(deliveryLedger: acknowledgement.Ledger);
            deliveries.Add(new AuthorityDelivery(context.DeviceId, acknowledgement.Item));
            await CommitAsync(next, cancellationToken).ConfigureAwait(false);
            return new PairedCommandApplication(next, reduction.Acknowledgement, deliveries.AsReadOnly());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<DeliveryAcknowledgementApplication> AcknowledgeDeliveryAsync(
        AuthenticatedPairedFrame frame,
        ClientDeliveryAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        await WaitForMutationGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _state;
            var resolved = ResolveFrame(state, frame);
            var use = DeviceLifecycle.RecordUse(resolved.Device, resolved.Session, frame.ReceivedUtc);
            var result = state.DeliveryLedger.Acknowledge(
                use.Device.DeviceId,
                use.Session.SessionId,
                use.Session.ProtocolVersion,
                acknowledgement,
                state.CanonicalState);
            var next = state.With(
                devices: ReplaceDevice(state.Devices, use.Device),
                sessions: ReplaceSession(state.Sessions, use.Session),
                deliveryLedger: result.Ledger);
            await CommitAsync(next, cancellationToken).ConfigureAwait(false);
            return new DeliveryAcknowledgementApplication(next, result.Accepted, result.Code);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ReconnectApplication> PlanReconnectAsync(
        AuthenticatedPairedFrame frame,
        ReconnectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(request);
        await WaitForMutationGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _state;
            var resolved = ResolveFrame(state, frame);
            var use = DeviceLifecycle.RecordUse(resolved.Device, resolved.Session, frame.ReceivedUtc);
            var planning = ReconnectPlanner.Plan(
                state.CanonicalState,
                request,
                state.DeliveryLedger,
                use.Device.DeviceId,
                use.Session.SessionId,
                use.Session.ProtocolVersion);
            var next = state.With(
                devices: ReplaceDevice(state.Devices, use.Device),
                sessions: ReplaceSession(state.Sessions, use.Session),
                deliveryLedger: planning.Ledger);
            await CommitAsync(next, cancellationToken).ConfigureAwait(false);
            return new ReconnectApplication(next, planning.Plan);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<AuthorityMutation> RevokeDeviceAsync(
        CompanionDeviceId deviceId,
        DateTimeOffset nowUtc,
        string reason,
        CancellationToken cancellationToken = default) =>
        await MutateAsync(state =>
        {
            var deviceIndex = IndexOfDevice(state, deviceId);
            var device = DeviceLifecycle.Revoke(state.Devices[deviceIndex], nowUtc, reason);
            var devices = state.Devices.ToArray();
            devices[deviceIndex] = device;
            var sessions = state.Sessions.ToArray();
            var canonical = state.CanonicalState;
            var updates = new List<CanonicalUpdate>();
            for (var index = 0; index < sessions.Length; index++)
            {
                if (sessions[index].DeviceId != deviceId || sessions[index].Status != DeviceSessionStatus.Active)
                {
                    continue;
                }

                sessions[index] = DeviceLifecycle.EndSession(
                    sessions[index],
                    DeviceSessionStatus.Revoked,
                    nowUtc,
                    reason);
                var sessionReduction = DesktopCanonicalStateMachine.ApplySessionTermination(canonical, sessions[index]);
                canonical = sessionReduction.State;
                updates.AddRange(sessionReduction.Updates);
            }

            var deviceReduction = DesktopCanonicalStateMachine.ApplyDeviceTermination(canonical, device);
            canonical = deviceReduction.State;
            updates.AddRange(deviceReduction.Updates);
            var next = state.With(
                canonicalState: canonical,
                devices: devices,
                sessions: sessions,
                deliveryLedger: state.DeliveryLedger.RemoveDevice(deviceId));
            var delivered = Broadcast(next, updates, canonical.DesktopDeviceId, nowUtc);
            return (delivered.State, delivered.Deliveries);
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<AuthorityMutation> RunMaintenanceAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        await MutateAsync(state =>
        {
            var devices = state.Devices.ToArray();
            var sessions = state.Sessions.ToArray();
            var canonical = state.CanonicalState;
            var ledger = state.DeliveryLedger;
            var updates = new List<CanonicalUpdate>();

            for (var index = 0; index < sessions.Length; index++)
            {
                var session = sessions[index];
                if (session.Status != DeviceSessionStatus.Active || nowUtc < session.ExpiresUtc)
                {
                    continue;
                }

                sessions[index] = DeviceLifecycle.EndSession(
                    session,
                    DeviceSessionStatus.Expired,
                    nowUtc,
                    "session-expired");
                var reduction = DesktopCanonicalStateMachine.ApplySessionTermination(canonical, sessions[index]);
                canonical = reduction.State;
                updates.AddRange(reduction.Updates);
            }

            for (var index = 0; index < devices.Length; index++)
            {
                var device = devices[index];
                if (device.Status != DeviceLifecycleStatus.Active || DeviceLifecycle.IsLive(device, nowUtc))
                {
                    continue;
                }

                devices[index] = DeviceLifecycle.Expire(device, nowUtc);
                ledger = ledger.RemoveDevice(device.DeviceId);
                for (var sessionIndex = 0; sessionIndex < sessions.Length; sessionIndex++)
                {
                    var session = sessions[sessionIndex];
                    if (session.DeviceId == device.DeviceId && session.Status == DeviceSessionStatus.Active)
                    {
                        sessions[sessionIndex] = DeviceLifecycle.EndSession(
                            session,
                            DeviceSessionStatus.Expired,
                            nowUtc,
                            "device-expired");
                    }
                }

                var reduction = DesktopCanonicalStateMachine.ApplyDeviceTermination(canonical, devices[index]);
                canonical = reduction.State;
                updates.AddRange(reduction.Updates);
            }

            var maintained = DesktopCanonicalStateMachine.ApplyMaintenance(canonical, nowUtc, devices, sessions);
            canonical = maintained.State;
            updates.AddRange(maintained.Updates);
            var next = state.With(
                canonicalState: canonical,
                devices: devices,
                sessions: RetainSessions(sessions),
                deliveryLedger: ledger);
            var delivered = Broadcast(next, updates, canonical.DesktopDeviceId, nowUtc);
            return (delivered.State, delivered.Deliveries);
        }, cancellationToken).ConfigureAwait(false);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _gate.Wait();
        try
        {
            _disposed = true;
            _lease.Dispose();
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }

    private async ValueTask<AuthorityMutation> MutateAsync(
        Func<DesktopCompanionAuthorityState, (DesktopCompanionAuthorityState State, IReadOnlyList<AuthorityDelivery> Deliveries)> mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        await WaitForMutationGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = mutation(_state);
            await CommitAsync(result.State, cancellationToken).ConfigureAwait(false);
            return new AuthorityMutation(result.State, result.Deliveries);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask CommitAsync(
        DesktopCompanionAuthorityState next,
        CancellationToken cancellationToken)
    {
        await _store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _state, next);
    }

    private async ValueTask WaitForMutationGateAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private static (PairedDevice Device, DeviceSession Session) ResolveFrame(
        DesktopCompanionAuthorityState state,
        AuthenticatedPairedFrame frame)
    {
        var session = state.Sessions.FirstOrDefault(candidate => candidate.SessionId == frame.SessionId)
            ?? throw new UnauthorizedAccessException("The authenticated session is unknown.");
        if (session.KeyEpoch != frame.KeyEpoch)
        {
            throw new UnauthorizedAccessException("The authenticated frame used another key epoch.");
        }

        var device = state.Devices.FirstOrDefault(candidate => candidate.DeviceId == session.DeviceId)
            ?? throw new UnauthorizedAccessException("The authenticated session has no paired device.");
        if (!DeviceLifecycle.IsLive(session, device, frame.ReceivedUtc))
        {
            throw new UnauthorizedAccessException("The authenticated session is not live.");
        }

        return (device, session);
    }

    private static (DesktopCompanionAuthorityState State, IReadOnlyList<AuthorityDelivery> Deliveries) Broadcast(
        DesktopCompanionAuthorityState state,
        IReadOnlyList<CanonicalUpdate> updates,
        CompanionDeviceId originDeviceId,
        DateTimeOffset nowUtc)
    {
        if (updates.Count == 0)
        {
            return (state, []);
        }

        var ledger = state.DeliveryLedger;
        var deliveries = new List<AuthorityDelivery>();
        var liveDeviceIds = state.Devices
            .Where(device => DeviceLifecycle.IsLive(device, nowUtc) &&
                             state.Sessions.Any(session => DeviceLifecycle.IsLive(session, device, nowUtc)))
            .Select(device => device.DeviceId)
            .ToArray();
        foreach (var update in updates)
        {
            foreach (var deviceId in liveDeviceIds)
            {
                var enqueued = ledger.Enqueue(
                    deviceId,
                    originDeviceId,
                    new CanonicalUpdateMessage(update),
                    nowUtc);
                ledger = enqueued.Ledger;
                deliveries.Add(new AuthorityDelivery(deviceId, enqueued.Item));
            }
        }

        return (state.With(deliveryLedger: ledger), deliveries.AsReadOnly());
    }

    private static PairedDevice[] ReplaceDevice(IReadOnlyList<PairedDevice> devices, PairedDevice replacement)
    {
        var copy = devices.ToArray();
        var index = Array.FindIndex(copy, device => device.DeviceId == replacement.DeviceId);
        if (index < 0)
        {
            throw new InvalidOperationException("The paired device disappeared during its authenticated operation.");
        }

        copy[index] = replacement;
        return copy;
    }

    private static DeviceSession[] ReplaceSession(IReadOnlyList<DeviceSession> sessions, DeviceSession replacement)
    {
        var copy = sessions.ToArray();
        var index = Array.FindIndex(copy, session => session.SessionId == replacement.SessionId);
        if (index < 0)
        {
            throw new InvalidOperationException("The paired session disappeared during its authenticated operation.");
        }

        copy[index] = replacement;
        return copy;
    }

    private static int IndexOfDevice(DesktopCompanionAuthorityState state, CompanionDeviceId deviceId)
    {
        for (var index = 0; index < state.Devices.Count; index++)
        {
            if (state.Devices[index].DeviceId == deviceId)
            {
                return index;
            }
        }

        throw new KeyNotFoundException("The paired device was not found.");
    }

    private static List<DeviceSession> RetainSessions(IEnumerable<DeviceSession> sessions)
    {
        var all = sessions.ToList();
        if (all.Count <= DesktopCompanionAuthorityState.MaximumRetainedSessions)
        {
            return all;
        }

        var active = all.Where(session => session.Status == DeviceSessionStatus.Active).ToArray();
        var terminal = all
            .Where(session => session.Status != DeviceSessionStatus.Active)
            .OrderByDescending(session => session.EndedUtc)
            .Take(DesktopCompanionAuthorityState.MaximumRetainedSessions - active.Length);
        return active.Concat(terminal).OrderBy(session => session.CreatedUtc).ToList();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
