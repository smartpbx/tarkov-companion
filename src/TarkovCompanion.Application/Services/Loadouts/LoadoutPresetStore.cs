using TarkovCompanion.Core.Domain.Loadouts;

namespace TarkovCompanion.Application.Services.Loadouts;

/// <summary>Where saved kits live between launches.</summary>
public interface ILoadoutPresetStore
{
    Task<IReadOnlyList<LoadoutPreset>> GetAsync(CancellationToken cancellationToken);

    /// <summary>Saves a kit under its name, replacing one already saved under it.</summary>
    Task SaveAsync(LoadoutPreset preset, CancellationToken cancellationToken);

    /// <summary>Removes the kit with this name, if there is one.</summary>
    Task DeleteAsync(string name, CancellationToken cancellationToken);
}
