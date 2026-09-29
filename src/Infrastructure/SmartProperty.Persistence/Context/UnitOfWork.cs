using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.Configurations.Identity;

namespace SmartProperty.Persistence.Context;

internal sealed class UnitOfWork(ApplicationDbContext dbContext) : IUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception) when (GetConcurrencyResource(exception) is { } resource)
        {
            throw new ConcurrencyConflictException(resource, exception);
        }
        catch (DbUpdateException exception) when (GetRecognizedUniqueConstraint(exception) is { } constraint)
        {
            throw new UniqueConstraintViolationException(constraint, exception);
        }
    }

    public void DiscardTrackedChanges()
    {
        dbContext.ChangeTracker.Clear();
    }

    // Recognizes conflicts from EF's entry metadata, never from message text. A mixed or unknown set of failed
    // entries remains unexpected and propagates unchanged.
    private static PersistenceResource? GetConcurrencyResource(DbUpdateConcurrencyException exception)
    {
        if (exception.Entries.Count == 0)
        {
            return null;
        }

        var entityType = exception.Entries[0].Metadata.ClrType;

        if (exception.Entries.Any(entry => entry.Metadata.ClrType != entityType))
        {
            return null;
        }

        if (entityType == typeof(RefreshToken))
        {
            return PersistenceResource.RefreshToken;
        }

        return entityType == typeof(WorkspaceAccessRequest)
            ? PersistenceResource.WorkspaceAccessRequest
            : null;
    }

    // Recognizes a violation from the provider's SQLSTATE and constraint name, never from message text.
    // Unrecognized failures do not match the catch filter, so they propagate unchanged.
    private static PersistenceConstraint? GetRecognizedUniqueConstraint(DbUpdateException exception)
    {
        if (exception.InnerException is not PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        } postgresException)
        {
            return null;
        }

        return postgresException.ConstraintName switch
        {
            UserConfiguration.EmailUniqueIndexName => PersistenceConstraint.UserEmail,
            WorkspaceMembershipConfiguration.UserWorkspaceUniqueIndexName =>
                PersistenceConstraint.WorkspaceMembershipUserWorkspace,
            _ => null
        };
    }
}
