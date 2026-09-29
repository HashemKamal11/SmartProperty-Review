namespace SmartProperty.Application.Abstractions.Persistence;

/// <summary>
/// Executes one Platform Administrator bootstrap attempt under database-wide coordination and one atomic
/// persistence transaction. The supplied operation must use repositories and a unit of work from the same scope.
/// </summary>
public interface IPlatformAdminBootstrapCoordinator
{
    Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);
}
