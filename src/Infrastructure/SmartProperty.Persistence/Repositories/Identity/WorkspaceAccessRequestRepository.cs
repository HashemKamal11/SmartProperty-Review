using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.Identity;

internal sealed class WorkspaceAccessRequestRepository(ApplicationDbContext dbContext)
    : IWorkspaceAccessRequestRepository
{
    public Task<WorkspaceAccessRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(id, nameof(id));

        return dbContext.WorkspaceAccessRequests.FirstOrDefaultAsync(
            accessRequest => accessRequest.Id == id,
            cancellationToken);
    }

    public async Task AddAsync(WorkspaceAccessRequest accessRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessRequest);

        await dbContext.WorkspaceAccessRequests.AddAsync(accessRequest, cancellationToken);
    }

    private static void ValidateRequiredId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", parameterName);
        }
    }
}
