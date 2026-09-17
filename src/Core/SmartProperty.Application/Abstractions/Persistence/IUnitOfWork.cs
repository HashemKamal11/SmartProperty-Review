namespace SmartProperty.Application.Abstractions.Persistence;

/// <summary>
/// Single persistence commit boundary. Repositories only track changes; a use case commits them atomically
/// by calling <see cref="SaveChangesAsync"/> once.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
