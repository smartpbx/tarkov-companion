namespace TarkovCompanion.Application.Services.ReleaseExperience;

/// <summary>Enough history to distinguish a first install from an update and remember dismissal.</summary>
public sealed record ReleaseExperienceState(string LastSeenVersion, string? DismissedVersion);

public interface IReleaseExperienceStateStore
{
    Task<ReleaseExperienceState?> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(ReleaseExperienceState state, CancellationToken cancellationToken);
}
