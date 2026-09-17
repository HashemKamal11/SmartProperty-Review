namespace SmartProperty.Application.Abstractions.Persistence;

/// <summary>
/// Thrown by <see cref="IUnitOfWork.SaveChangesAsync"/> when the database rejects the save because it violates a
/// recognized unique constraint. The save is not committed. Provider-specific details stay in Persistence; the
/// provider exception is kept only as the inner exception for diagnostics.
/// </summary>
public sealed class UniqueConstraintViolationException(PersistenceConstraint constraint, Exception innerException)
    : Exception($"The save violated the {constraint} unique constraint.", innerException)
{
    public PersistenceConstraint Constraint { get; } = constraint;
}
