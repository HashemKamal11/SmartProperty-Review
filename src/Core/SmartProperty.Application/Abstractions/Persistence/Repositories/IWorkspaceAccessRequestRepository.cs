using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

public interface IWorkspaceAccessRequestRepository
{
    Task<WorkspaceAccessRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAsync(WorkspaceAccessRequest accessRequest, CancellationToken cancellationToken = default);
}
