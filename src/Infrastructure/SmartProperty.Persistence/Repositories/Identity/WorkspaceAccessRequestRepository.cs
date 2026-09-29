using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.WorkspaceAccessRequests.List;
using SmartProperty.Common.Pagination;
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

    /// <remarks>
    /// Two commands: one count over the filter, one page of projected rows. The projection is built in the
    /// database, so no entity is materialized and no row triggers a follow-up query for the applicant or the
    /// workspace.
    ///
    /// The ordering is oldest request first, tie-broken by id. The tie-break is what makes paging stable —
    /// <c>requested_at</c> alone is not unique, and without it a row could appear on two pages or on none.
    /// </remarks>
    public async Task<WorkspaceAccessRequestPage> ListAsync(
        WorkspaceAccessRequestStatus status,
        Guid? workspaceId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip must not be negative.");
        }

        if (skip > PageParameters.MaxOffset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(skip),
                skip,
                $"Skip must not exceed {PageParameters.MaxOffset}.");
        }

        if (take is < 1 or > PageParameters.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(take),
                take,
                $"Take must be between 1 and {PageParameters.MaxPageSize}.");
        }

        var filtered = dbContext.WorkspaceAccessRequests
            .Where(accessRequest => accessRequest.Status == status);

        if (workspaceId is { } targetWorkspaceId)
        {
            filtered = filtered.Where(accessRequest => accessRequest.WorkspaceId == targetWorkspaceId);
        }

        var totalCount = await filtered.LongCountAsync(cancellationToken);

        var items = await filtered
            .OrderBy(accessRequest => accessRequest.RequestedAt)
            .ThenBy(accessRequest => accessRequest.Id)
            .Skip(skip)
            .Take(take)
            .Join(
                dbContext.Users,
                accessRequest => accessRequest.UserId,
                user => user.Id,
                (accessRequest, user) => new { accessRequest, user })
            .Join(
                dbContext.Workspaces,
                row => row.accessRequest.WorkspaceId,
                workspace => workspace.Id,
                (row, workspace) => new WorkspaceAccessRequestListItem(
                    row.accessRequest.Id,
                    row.user.Id,
                    row.user.Email,
                    row.user.FirstName,
                    row.user.LastName,
                    workspace.Id,
                    workspace.Name,
                    row.accessRequest.Status,
                    row.accessRequest.RequestedAt,
                    row.accessRequest.ReviewedAt,
                    row.accessRequest.ReviewedByUserId))
            .ToListAsync(cancellationToken);

        return new WorkspaceAccessRequestPage(items, totalCount);
    }

    private static void ValidateRequiredId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", parameterName);
        }
    }
}
