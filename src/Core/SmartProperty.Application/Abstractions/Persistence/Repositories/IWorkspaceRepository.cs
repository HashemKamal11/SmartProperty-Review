using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

public interface IWorkspaceRepository
{
    Task<Workspace?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAsync(Workspace workspace, CancellationToken cancellationToken = default);
}
