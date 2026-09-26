using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.Application.Services.Profiles;

/// <summary>What the follower did about the game's session mode, for the one line with Undo.</summary>
public enum ProfileFollowKind
{
    /// <summary>The active profile was switched to one in the game's mode.</summary>
    Switched,

    /// <summary>No profile is in the game's mode; the line offers to create one.</summary>
    NoProfile,
}

/// <summary>The one line the player sees after the game's mode moved the profile, and what Undo goes back to.</summary>
/// <param name="GameMode">The mode the game said it is in.</param>
/// <param name="ProfileName">The profile switched to; null for <see cref="ProfileFollowKind.NoProfile"/>.</param>
/// <param name="PreviousProfileId">The profile that was active before; Undo switches back to it.</param>
/// <param name="PreviousName">That profile's name, for the Undo label.</param>
public sealed record ProfileFollowNotice(
    ProfileFollowKind Kind,
    ProfileGameMode GameMode,
    string? ProfileName,
    Guid? PreviousProfileId,
    string? PreviousName,
    DateTimeOffset ObservedUtc);

/// <summary>
/// The active companion profile follows the game's own session mode (#712 decision 4, #403).
/// </summary>
/// <remarks>
/// <para>
/// Clayton's decision (2026-09-25): follow automatically, say so in one line with Undo, and keep the
/// other profiles browsable. So a switch only ever moves between profiles the player already has,
/// and never archives or edits one. With no profile in the game's mode it switches nothing and offers
/// to create one, because an empty new profile silently replacing a full one would look like lost
/// progress.
/// </para>
/// <para>
/// Each mode value is acted on once per run: the game writes the line into <c>application</c> and
/// mirrors it into <c>output</c>, the startup replay reads it again, and after an Undo or a manual
/// switch the player's choice has to hold until the game says something different.
/// </para>
/// </remarks>
public sealed class ProfileModeFollower(ProfileManagementService profiles)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ProfileManagementService _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    private string? _lastValue;
    private ProfileFollowNotice? _notice;

    public ProfileFollowNotice? Notice => Volatile.Read(ref _notice);

    public event Action<ProfileFollowNotice?>? NoticeChanged;

    public async Task ObserveAsync(GameSessionMode session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Mode is not { } mode || mode == ProfileGameMode.Unknown)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(_lastValue, session.GameValue, StringComparison.Ordinal))
            {
                return;
            }

            _lastValue = session.GameValue;
            var current = await _profiles.LoadAsync(cancellationToken).ConfigureAwait(false);
            var active = current.ActiveProfile;
            if (active?.Context.Mode == mode)
            {
                return;
            }

            var target = current.Workspace.Profiles
                .Where(profile => profile.Lifecycle == ProfileLifecycle.Active && profile.Context.Mode == mode)
                .OrderByDescending(profile => profile.UpdatedUtc)
                .FirstOrDefault();
            if (target is null)
            {
                Publish(new(ProfileFollowKind.NoProfile, mode, null, active?.Context.Identity.ProfileId, active?.Name, session.ObservedUtc));
                return;
            }

            await _profiles.SwitchAsync(target.Context.Identity.ProfileId, cancellationToken).ConfigureAwait(false);
            Publish(new(ProfileFollowKind.Switched, mode, target.Name, active?.Context.Identity.ProfileId, active?.Name, session.ObservedUtc));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Goes back to the profile that was active before the switch, and closes the line.</summary>
    public async Task UndoAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Notice is { Kind: ProfileFollowKind.Switched, PreviousProfileId: { } previous })
            {
                await _profiles.SwitchAsync(previous, cancellationToken).ConfigureAwait(false);
            }

            Publish(null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Creates a profile in the game's mode, named after the mode, and makes it active.</summary>
    public async Task CreateForGameModeAsync(string name, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Notice is not { Kind: ProfileFollowKind.NoProfile } notice)
            {
                return;
            }

            await _profiles.CreateAsync(name, notice.GameMode, ProfileManagementService.DefaultWipe, cancellationToken)
                .ConfigureAwait(false);
            Publish(notice with { Kind = ProfileFollowKind.Switched, ProfileName = name });
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dismiss() => Publish(null);

    private void Publish(ProfileFollowNotice? notice)
    {
        Volatile.Write(ref _notice, notice);
        NoticeChanged?.Invoke(notice);
    }
}
