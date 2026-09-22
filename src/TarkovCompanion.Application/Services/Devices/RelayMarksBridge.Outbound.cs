using TarkovCompanion.CompanionProtocol;

namespace TarkovCompanion.Application.Services.Devices;

// [#604] Everything this desktop sends a paired tablet, in one ordered queue.
//
// A tablet in Control sends a move many times a second, and each applied move produces two
// frames back: the workspace update every tablet gets and the acknowledgement for the one that
// sent it. They used to be posted one after the other before the next move was even read, so the
// desk fell a round trip further behind the finger with every move. Now the reader queues them
// and carries on, and one sender posts them in order: in order because the relay refuses a sender
// sequence lower than one it has already seen, and a sequence is taken when a frame is sealed.
//
// While the sender is behind, a move's frames that a later move's frames have made pointless are
// dropped before they are sealed: a workspace update carries the whole workspace, so only the
// newest matters, and an applied move's acknowledgement tells the tablet nothing it acts on (it
// does not wait for one). Nothing else is ever dropped: a refusal, a mark, a mode change or a
// snapshot always arrives.
public sealed partial class RelayMarksBridge
{
    private const string WorkspaceUpdateKey = "workspace";
    private const string AppliedMoveAcknowledgementKey = "applied-move-ack";

    private readonly Lock _outboundGate = new();
    private readonly List<OutboundFrame> _outbound = [];
    private bool _outboundDraining;
    private WorkspaceProjection? _pendingDesktopWorkspace;

    private sealed record OutboundFrame(
        PairedSessionState Session,
        RelayPayloadKind Kind,
        byte[] Json,
        string? SupersedeKey,
        TaskCompletionSource Sent);

    /// <summary>Hands the desktop's map the last Control move of a batch, once.</summary>
    private void RaisePendingDesktopWorkspace()
    {
        var projection = _pendingDesktopWorkspace;
        _pendingDesktopWorkspace = null;
        if (projection is not null)
        {
            DesktopWorkspaceRequested?.Invoke(projection);
        }
    }

    /// <summary>Queues a command's result for a tablet without waiting for it to be sent.</summary>
    private void QueueDelivery(
        PairedSessionState state,
        AuthorityDelivery delivery,
        CanonicalCompanionState current,
        bool fromControlMove)
    {
        var message = delivery.Item.Resolve(current);
        var envelope = new ServerEnvelope(
            CompanionProtocolVersion.Current,
            state.SessionId,
            current.DesktopDeviceId,
            Now(),
            delivery.Item.Sequence,
            message);
        var sent = EnqueueSealed(
            state,
            RelayPayloadKind.ServerEnvelope,
            CompanionProtocolJson.Serialize(envelope),
            fromControlMove ? SupersedeKeyOf(message) : null);
        // Nobody awaits this one; a failure is a stale tablet that its own resync heals.
        _ = sent.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Which later frame makes this one pointless, or null when none can.</summary>
    internal static string? SupersedeKeyOf(ServerMessage message) => message switch
    {
        CanonicalUpdateMessage { Update: WorkspaceCanonicalUpdate } => WorkspaceUpdateKey,
        CommandAcknowledgementMessage { Acknowledgement.Disposition: CommandDisposition.Applied } => AppliedMoveAcknowledgementKey,
        _ => null,
    };

    private Task EnqueueSealed(PairedSessionState state, RelayPayloadKind kind, byte[] json, string? supersedeKey)
    {
        var frame = new OutboundFrame(state, kind, json, supersedeKey, new(TaskCreationOptions.RunContinuationsAsynchronously));
        bool start;
        lock (_outboundGate)
        {
            _outbound.Add(frame);
            start = !_outboundDraining;
            _outboundDraining = true;
        }

        if (start)
        {
            _ = Task.Run(DrainOutboundAsync);
        }

        return frame.Sent.Task;
    }

    private async Task DrainOutboundAsync()
    {
        while (true)
        {
            OutboundFrame? next;
            List<OutboundFrame>? superseded = null;
            lock (_outboundGate)
            {
                while (_outbound.Count > 0 && IsSuperseded(_outbound, 0))
                {
                    (superseded ??= []).Add(_outbound[0]);
                    _outbound.RemoveAt(0);
                }

                if (_outbound.Count == 0)
                {
                    _outboundDraining = false;
                    next = null;
                }
                else
                {
                    next = _outbound[0];
                    _outbound.RemoveAt(0);
                }
            }

            foreach (var dropped in superseded ?? [])
            {
                dropped.Sent.TrySetResult();
            }

            if (next is null)
            {
                return;
            }

            try
            {
                await SendSealedNowAsync(next.Session, next.Kind, next.Json, CancellationToken.None).ConfigureAwait(false);
                next.Sent.TrySetResult();
            }
            catch (Exception exception)
            {
                next.Sent.TrySetException(exception);
            }
        }
    }

    /// <summary>Whether a later queued frame for the same tablet makes the one at <paramref name="index"/> pointless.</summary>
    private static bool IsSuperseded(List<OutboundFrame> queue, int index)
    {
        var frame = queue[index];
        if (frame.SupersedeKey is not { } key)
        {
            return false;
        }

        for (var later = index + 1; later < queue.Count; later++)
        {
            if (ReferenceEquals(queue[later].Session, frame.Session) &&
                string.Equals(queue[later].SupersedeKey, key, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
