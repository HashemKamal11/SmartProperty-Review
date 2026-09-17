using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartProperty.Application.Abstractions.Persistence;
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
        catch (DbUpdateException exception) when (GetRecognizedUniqueConstraint(exception) is { } constraint)
        {
            throw new UniqueConstraintViolationException(constraint, exception);
        }
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
