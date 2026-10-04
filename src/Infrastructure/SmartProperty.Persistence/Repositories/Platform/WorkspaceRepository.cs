using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Workspaces.RegistrationOptions;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.Platform;

internal sealed class WorkspaceRepository(ApplicationDbContext dbContext) : IWorkspaceRepository
{
    public Task<Workspace?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(id, nameof(id));

        return dbContext.Workspaces.FirstOrDefaultAsync(workspace => workspace.Id == id, cancellationToken);
    }

    public async Task AddAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        await dbContext.Workspaces.AddAsync(workspace, cancellationToken);
    }

    /// <remarks>
    /// One command, projected in the database, so no entity is materialized or tracked. Ordered by name for a
    /// person reading the list, tie-broken by id because names are not unique — without it two equally named
    /// workspaces could swap places between calls.
    /// </remarks>
    public async Task<IReadOnlyList<RegistrationWorkspaceOption>> ListRegistrationOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        return await dbContext.Workspaces
            .OrderBy(workspace => workspace.Name)
            .ThenBy(workspace => workspace.Id)
            .Select(workspace => new RegistrationWorkspaceOption(workspace.Id, workspace.Name))
            .ToListAsync(cancellationToken);
    }

    private static void ValidateRequiredId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", parameterName);
        }
    }
}
