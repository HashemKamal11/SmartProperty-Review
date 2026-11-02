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

    public async Task<SmartProperty.Application.Workspaces.Administration.AdminWorkspacePage> ListAdministrativeAsync(
        int skip, int take, CancellationToken cancellationToken = default)
    {
        if (skip < 0 || skip > SmartProperty.Common.Pagination.PageParameters.MaxOffset)
            throw new ArgumentOutOfRangeException(nameof(skip));
        if (take < 1 || take > SmartProperty.Common.Pagination.PageParameters.MaxPageSize)
            throw new ArgumentOutOfRangeException(nameof(take));

        var query = dbContext.Workspaces.AsNoTracking();
        var count = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderBy(x => x.Name).ThenBy(x => x.Id).Skip(skip).Take(take)
            .Select(x => new SmartProperty.Application.Workspaces.Administration.AdminWorkspaceDetails(
                x.Id, x.Name, x.CreatedAt, x.UpdatedAt))
            .ToArrayAsync(cancellationToken);
        return new(items, count);
    }

    private static void ValidateRequiredId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", parameterName);
        }
    }
}
