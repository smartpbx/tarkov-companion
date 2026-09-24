using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.Application.Services.Profiles;

/// <summary>
/// [#269] The raid context from the published profile snapshot: the active profile and every
/// profile a recorded raid could belong to. Read per call, so a profile switch is seen at once.
/// </summary>
public sealed class ProfileRaidContextSource(IProfileRuntimeContextService runtime) : IRaidContextSource
{
    public RaidContextView Current()
    {
        var snapshot = runtime.Current;
        return snapshot.IsInitialized
            ? new(snapshot.ActiveProfile, snapshot.Workspace.Profiles)
            : RaidContextView.None;
    }
}
