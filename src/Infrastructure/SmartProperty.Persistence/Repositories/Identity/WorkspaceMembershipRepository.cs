using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.Identity;

internal sealed class WorkspaceMembershipRepository(ApplicationDbContext dbContext)
    : IWorkspaceMembershipRepository
{
    public Task<WorkspaceMembership?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(id, nameof(id));

        return dbContext.WorkspaceMemberships.FirstOrDefaultAsync(membership => membership.Id == id, cancellationToken);
    }

    public Task<WorkspaceMembership?> GetByUserAndWorkspaceAsync(
        Guid userId,
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(userId, nameof(userId));
        ValidateRequiredId(workspaceId, nameof(workspaceId));

        return dbContext.WorkspaceMemberships.FirstOrDefaultAsync(
            membership => membership.UserId == userId && membership.WorkspaceId == workspaceId,
            cancellationToken);
    }

    public async Task AddAsync(WorkspaceMembership membership, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(membership);

        await dbContext.WorkspaceMemberships.AddAsync(membership, cancellationToken);
    }

    private static void ValidateRequiredId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", parameterName);
        }
    }
}
