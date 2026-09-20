using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Domain.Identity;
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
        catch (DbUpdateConcurrencyException exception) when (IsRefreshTokenConflict(exception))
        {
            throw new ConcurrencyConflictException(PersistenceResource.RefreshToken, exception);
        }
        catch (DbUpdateException exception) when (GetRecognizedUniqueConstraint(exception) is { } constraint)
        {
            throw new UniqueConstraintViolationException(constraint, exception);
        }
    }

    // Recognizes the conflict from EF's own metadata for the rows that failed, never from message text. Only a
    // conflict whose failed entries are all refresh tokens is translated; a conflict involving any other entity
    // type does not match the catch filter and propagates unchanged as an unexpected failure.
    private static bool IsRefreshTokenConflict(DbUpdateConcurrencyException exception)
    {
        return exception.Entries.Count > 0
            && exception.Entries.All(entry => entry.Metadata.ClrType == typeof(RefreshToken));
    }

    // Recognizes a violation from the provider's SQLSTATE and constraint name, never from message text.
    // Unrecognized failures do not match the catch filter, so they propagate unchanged.
    private static PersistenceConstraint? GetRecognizedUniqueConstraint(DbUpdateException exception)
    {
        var isUserEmailViolation = exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: UserConfiguration.EmailUniqueIndexName
        };

        return isUserEmailViolation ? PersistenceConstraint.UserEmail : null;
    }
}
