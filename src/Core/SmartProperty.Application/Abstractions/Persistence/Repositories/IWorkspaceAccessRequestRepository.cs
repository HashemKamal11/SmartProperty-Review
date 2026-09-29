using SmartProperty.Application.WorkspaceAccessRequests.List;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

public interface IWorkspaceAccessRequestRepository
{
    Task<WorkspaceAccessRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAsync(WorkspaceAccessRequest accessRequest, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of the review queue, plus how many rows the filter matches in total.
    /// </summary>
    /// <remarks>
    /// Returns a read model rather than entities on purpose: the queue needs the applicant's name and the
    /// workspace's name alongside each request, and projecting them in one query is what keeps that from becoming
    /// a lookup per row. Nothing is returned as <c>IQueryable</c> — the filter is these parameters and no more.
    /// </remarks>
    /// <param name="status">The only status returned.</param>
    /// <param name="workspaceId">One workspace, or null for every workspace.</param>
    /// <param name="skip">Rows to skip, from an ordering the implementation keeps stable across pages.</param>
    /// <param name="take">Maximum rows to return.</param>
    Task<WorkspaceAccessRequestPage> ListAsync(
        WorkspaceAccessRequestStatus status,
        Guid? workspaceId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);
}
