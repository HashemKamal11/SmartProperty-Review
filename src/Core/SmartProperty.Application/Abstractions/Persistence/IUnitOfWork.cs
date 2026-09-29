namespace SmartProperty.Application.Abstractions.Persistence;

/// <summary>
/// Single persistence commit boundary. Repositories only track changes; a use case commits them atomically
/// through <see cref="SaveChangesAsync"/>.
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

    /// <summary>
    /// Discards all tracked persistence state in the active unit-of-work scope, abandoning every unsaved tracked
    /// change. Callers must use this only when the current operation owns all pending changes in that scope and
    /// must re-read all required authoritative state before continuing.
    /// </summary>
    void DiscardTrackedChanges();
}
