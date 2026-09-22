using SmartProperty.Application.Abstractions.Persistence;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// Counts commits and, when asked, fails one the way the real persistence boundary does. It maps no database
/// behaviour of its own: it only lets a test drive the exception contract <see cref="IUnitOfWork"/> documents.
/// </summary>
internal sealed class FakeUnitOfWork : IUnitOfWork
{
    /// <summary>Thrown instead of committing, to exercise a handler's catch. Null commits normally.</summary>
    public Exception? ExceptionToThrow { get; set; }

    public int SaveChangesCallCount { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveChangesCallCount++;

        if (ExceptionToThrow is { } exception)
        {
            throw exception;
        }

        return Task.FromResult(1);
    }

    /// <summary>
    /// The exception <c>UnitOfWork</c> raises when PostgreSQL rejects a refresh-token save because another
    /// writer changed the row first.
    /// </summary>
    public static ConcurrencyConflictException RefreshTokenConcurrencyConflict()
    {
        return new ConcurrencyConflictException(
            PersistenceResource.RefreshToken,
            new InvalidOperationException("Simulated concurrent refresh token update."));
    }
}
