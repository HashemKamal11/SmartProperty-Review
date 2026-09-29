using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Migrator;

/// <summary>
/// Applies the repository's EF migrations and then idempotently establishes the configured initial workspace.
/// </summary>
/// <remarks>
/// This is deployment infrastructure, so it owns the migration call while using the same domain constructor,
/// repository, and unit of work as application workflows. The normal web host never resolves this service and
/// never applies schema changes.
/// </remarks>
public sealed class MigrationProvisioningRunner(
    ApplicationDbContext dbContext,
    IWorkspaceRepository workspaceRepository,
    IUnitOfWork unitOfWork,
    IOptions<InitialWorkspaceOptions> options,
    TimeProvider timeProvider,
    ILogger<MigrationProvisioningRunner> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var configuredWorkspace = CreateConfiguredWorkspace(options.Value);

        logger.LogInformation("Applying pending Entity Framework Core migrations.");
        await dbContext.Database.MigrateAsync(cancellationToken);

        var pendingMigrations = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
        if (pendingMigrations.Length != 0)
        {
            throw new MigrationProvisioningException(
                $"Migration verification failed. Pending migrations remain: {string.Join(", ", pendingMigrations)}.");
        }

        logger.LogInformation("Database migrations are current.");

        if (configuredWorkspace is null)
        {
            logger.LogInformation("Initial workspace provisioning is disabled. No workspace was changed.");
            return;
        }

        var existingWorkspace = await workspaceRepository.GetByIdAsync(
            configuredWorkspace.Id,
            cancellationToken);

        if (existingWorkspace is null)
        {
            await workspaceRepository.AddAsync(configuredWorkspace, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Created initial workspace {WorkspaceId} named {WorkspaceName}.",
                configuredWorkspace.Id,
                configuredWorkspace.Name);
            return;
        }

        if (!string.Equals(existingWorkspace.Name, configuredWorkspace.Name, StringComparison.Ordinal))
        {
            throw new MigrationProvisioningException(
                $"Initial workspace conflict for id '{configuredWorkspace.Id}'. "
                + $"The database contains name '{existingWorkspace.Name}', while configuration requires "
                + $"'{configuredWorkspace.Name}'. No workspace was changed; correct the configuration or resolve "
                + "the existing data explicitly.");
        }

        logger.LogInformation(
            "Initial workspace {WorkspaceId} already exists with matching name {WorkspaceName}. No change was made.",
            existingWorkspace.Id,
            existingWorkspace.Name);
    }

    private Workspace? CreateConfiguredWorkspace(InitialWorkspaceOptions initialWorkspace)
    {
        if (!initialWorkspace.Enabled)
        {
            return null;
        }

        if (!Guid.TryParse(initialWorkspace.Id, out var workspaceId) || workspaceId == Guid.Empty)
        {
            throw new MigrationProvisioningException(
                "Provisioning:InitialWorkspace:Id must be a valid, non-empty GUID when initial workspace "
                + "provisioning is enabled.");
        }

        try
        {
            return new Workspace(workspaceId, initialWorkspace.Name, timeProvider.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            throw new MigrationProvisioningException(
                "Provisioning:InitialWorkspace:Name is invalid according to the Workspace domain rules.",
                exception);
        }
    }
}
