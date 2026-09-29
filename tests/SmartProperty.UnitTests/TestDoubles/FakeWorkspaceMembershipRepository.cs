using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// In-memory workspace memberships. Records every membership a handler adds, so a test can assert both that one
/// was created and that a second one was not.
/// </summary>
internal sealed class FakeWorkspaceMembershipRepository : IWorkspaceMembershipRepository
{
    private readonly Dictionary<Guid, WorkspaceMembership> _membershipsById = [];
    private readonly List<WorkspaceMembership> _added = [];

    public IReadOnlyList<WorkspaceMembership> Added => _added;

    public int GetByUserAndWorkspaceCallCount { get; private set; }

    public void Seed(WorkspaceMembership membership)
    {
        _membershipsById[membership.Id] = membership;
    }

    public void RollBackAdded()
    {
        foreach (var membership in _added)
        {
            _membershipsById.Remove(membership.Id);
        }

        _added.Clear();
    }

    public Task<WorkspaceMembership?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_membershipsById.GetValueOrDefault(id));
    }

    public Task<WorkspaceMembership?> GetByUserAndWorkspaceAsync(
        Guid userId,
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        GetByUserAndWorkspaceCallCount++;

        return Task.FromResult(_membershipsById.Values.FirstOrDefault(
            membership => membership.UserId == userId && membership.WorkspaceId == workspaceId));
    }

    public Task AddAsync(WorkspaceMembership membership, CancellationToken cancellationToken = default)
    {
        _added.Add(membership);
        Seed(membership);

        return Task.CompletedTask;
    }
}
