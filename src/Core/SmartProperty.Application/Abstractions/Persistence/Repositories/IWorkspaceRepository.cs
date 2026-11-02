using SmartProperty.Application.Workspaces.RegistrationOptions;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

public interface IWorkspaceRepository
{
    /// <summary>Platform directory, ordered by name and id; count and page are separate database reads.</summary>
    Task<SmartProperty.Application.Workspaces.Administration.AdminWorkspacePage> ListAdministrativeAsync(
        int skip, int take, CancellationToken cancellationToken = default);

    Task<Workspace?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAsync(Workspace workspace, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every workspace as a registration option, ordered by name and then id.
    /// </summary>
    Task<IReadOnlyList<RegistrationWorkspaceOption>> ListRegistrationOptionsAsync(
        CancellationToken cancellationToken = default);
}
