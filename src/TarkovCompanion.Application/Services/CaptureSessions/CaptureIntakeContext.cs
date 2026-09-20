namespace TarkovCompanion.Application.Services.CaptureSessions;

/// <summary>Where the player is working at the moment a capture arrives.</summary>
/// <remarks>
/// The screenshot watcher lives below the shell and cannot see it, so it described every frame
/// with a null workspace, plan, selection and prior scan. The shell implements this and the
/// watcher asks it, which keeps the dependency pointing the right way.
/// </remarks>
public interface ICaptureContextSource
{
    /// <param name="initiatingDevice">The device that asked for the capture, or null for this desktop.</param>
    CaptureContextMetadata Describe(string? initiatingDevice = null);
}

/// <summary>Chooses the context an arriving capture is submitted with.</summary>
public static class CaptureIntakeContext
{
    /// <summary>
    /// The armed session's own context while one is waiting for its capture, otherwise
    /// <paramref name="fresh"/>.
    /// </summary>
    /// <remarks>
    /// Intake refuses a capture whose context differs from the one its session was armed with
    /// (<c>capture_context_changed_since_arm</c>). The shell armed with the router's facts and
    /// the watcher submitted with its own, which never matched: the device alone was
    /// "this-desktop" on one side and "desktop" on the other. So an armed Loot intent followed
    /// by the game's screenshot key was refused every time, and the only captures that got
    /// through were unarmed ones. A capture that answers an armed request is taken in the
    /// context that request was made in, by definition, so it is submitted with exactly that.
    /// </remarks>
    public static CaptureContextMetadata For(ICaptureSessionService sessions, CaptureContextMetadata fresh)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(fresh);
        var armed = sessions.Snapshot.Sessions.LastOrDefault(session =>
            !session.IsTerminal && !session.IntentClaimed && !session.CancellationRequested);
        return armed?.Context ?? fresh;
    }
}
