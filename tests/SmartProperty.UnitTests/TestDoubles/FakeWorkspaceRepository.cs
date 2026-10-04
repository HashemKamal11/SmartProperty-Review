using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Workspaces.RegistrationOptions;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// In-memory workspaces. The registration options it returns are exactly what a test seeded, in the order seeded,
/// so a handler test can tell whether the handler passed the repository's answer through untouched.
/// </summary>
internal sealed class FakeWorkspaceRepository : IWorkspaceRepository
{
    private readonly Dictionary<Guid, Workspace> _workspacesById = [];

    public List<RegistrationWorkspaceOption> RegistrationOptions { get; } = [];

    public int ListRegistrationOptionsCallCount { get; private set; }

    public CancellationToken LastCancellationToken { get; private set; }

    public Task<Workspace?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_workspacesById.GetValueOrDefault(id));
    }

    public Task AddAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        _workspacesById[workspace.Id] = workspace;

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RegistrationWorkspaceOption>> ListRegistrationOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        ListRegistrationOptionsCallCount++;
        LastCancellationToken = cancellationToken;

        return Task.FromResult<IReadOnlyList<RegistrationWorkspaceOption>>(RegistrationOptions.ToArray());
    }
}
