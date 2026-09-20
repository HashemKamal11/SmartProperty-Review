namespace SmartProperty.Application.Abstractions.Persistence;

/// <summary>
/// Thrown by <see cref="IUnitOfWork.SaveChangesAsync"/> when the database rejected the save because another writer
/// had already changed a recognized row. The save is not committed. Provider-specific details stay in Persistence;
/// the provider exception is kept only as the inner exception for diagnostics.
/// </summary>
public sealed class ConcurrencyConflictException(PersistenceResource resource, Exception innerException)
    : Exception($"The save conflicted with a concurrent change to the {resource} row.", innerException)
{
    public PersistenceResource Resource { get; } = resource;
}
