namespace SmartProperty.Application.Abstractions.Persistence;

/// <summary>
/// Single persistence commit boundary. Repositories only track changes; a use case commits them atomically
/// by calling <see cref="SaveChangesAsync"/> once.
/// </summary>
public interface IUnitOfWork
{
    /// <exception cref="UniqueConstraintViolationException">
    /// The save violates a recognized unique constraint.
    /// </exception>
    /// <exception cref="ConcurrencyConflictException">
    /// The save lost a race for a recognized row. Every other failure propagates unchanged.
    /// </exception>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
