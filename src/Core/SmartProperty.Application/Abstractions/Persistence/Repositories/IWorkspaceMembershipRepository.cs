using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

public interface IWorkspaceMembershipRepository
{
    Task<WorkspaceMembership?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<WorkspaceMembership?> GetByUserAndWorkspaceAsync(
        Guid userId,
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    Task AddAsync(WorkspaceMembership membership, CancellationToken cancellationToken = default);
}
