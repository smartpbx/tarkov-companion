using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.Application.Services.Catalogs;

/// <summary>
/// What a hideout station level asks for besides items: other station levels, trader loyalty and
/// skills. Separate from <see cref="IRequirementCatalog"/> so the many readers of item needs do
/// not have to carry it.
/// </summary>
public interface IHideoutPrerequisiteCatalog
{
    Task<HideoutPrerequisites> GetAsync(CancellationToken cancellationToken);
}
