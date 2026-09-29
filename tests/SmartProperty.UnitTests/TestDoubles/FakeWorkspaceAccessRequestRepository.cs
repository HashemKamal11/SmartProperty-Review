using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.WorkspaceAccessRequests.List;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// In-memory workspace access requests, plus a record of exactly what a handler asked the queue for.
/// </summary>
/// <remarks>
/// The list side stores prepared read-model rows rather than deriving them from the stored entities: the real
/// projection joins users and workspaces in the database, and a fake that reimplemented that join would be
/// asserting its own arithmetic. What the handler's tests care about is the filter, the skip, and the take it
/// passed, which are recorded verbatim.
/// </remarks>
internal sealed class FakeWorkspaceAccessRequestRepository : IWorkspaceAccessRequestRepository
{
    private readonly Dictionary<Guid, WorkspaceAccessRequest> _requestsById = [];
    private readonly List<WorkspaceAccessRequest> _added = [];

    public IReadOnlyList<WorkspaceAccessRequest> Added => _added;

    public int GetByIdCallCount { get; private set; }

    /// <summary>Rows <see cref="ListAsync"/> returns, and the total it reports alongside them.</summary>
    public List<WorkspaceAccessRequestListItem> ListItems { get; } = [];

    public long ListTotalCount { get; set; }

    public ListRequest? LastListRequest { get; private set; }

    public int ListCallCount { get; private set; }

    public void Seed(WorkspaceAccessRequest accessRequest)
    {
        _requestsById[accessRequest.Id] = accessRequest;
    }

    public void Remove(Guid id)
    {
        _requestsById.Remove(id);
    }

    public Task<WorkspaceAccessRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        GetByIdCallCount++;

        return Task.FromResult(_requestsById.GetValueOrDefault(id));
    }

    public Task AddAsync(WorkspaceAccessRequest accessRequest, CancellationToken cancellationToken = default)
    {
        _added.Add(accessRequest);
        Seed(accessRequest);

        return Task.CompletedTask;
    }

    public Task<WorkspaceAccessRequestPage> ListAsync(
        WorkspaceAccessRequestStatus status,
        Guid? workspaceId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ListCallCount++;
        LastListRequest = new ListRequest(status, workspaceId, skip, take);

        return Task.FromResult(new WorkspaceAccessRequestPage(ListItems, ListTotalCount));
    }

    internal sealed record ListRequest(
        WorkspaceAccessRequestStatus Status,
        Guid? WorkspaceId,
        int Skip,
        int Take);
}
