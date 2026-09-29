using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence;

namespace SmartProperty.Persistence.Context;

/// <summary>
/// Serializes Platform Administrator bootstrap across every process connected to the same PostgreSQL database.
/// </summary>
internal sealed class PostgreSqlPlatformAdminBootstrapCoordinator(ApplicationDbContext dbContext)
    : IPlatformAdminBootstrapCoordinator
{
    // ASCII "SPROPADM" encoded as one fixed signed 64-bit advisory-lock key. Unlike GetHashCode, this value is
    // identical on every process, machine, runtime, and restart.
    private const long LockKey = 0x5350524F5041444D;

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // The transaction pins this DbContext to one connection. The xact lock is acquired before the
            // bootstrap delegate performs any reads and is released by PostgreSQL on commit or rollback.
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({LockKey})",
                cancellationToken);

            var result = await operation(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
