using SmartProperty.Application.Abstractions.Persistence;

namespace SmartProperty.UnitTests.TestDoubles;

internal sealed class FakePlatformAdminBootstrapCoordinator : IPlatformAdminBootstrapCoordinator
{
    public int ExecuteCallCount { get; private set; }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        ExecuteCallCount++;

        return await operation(cancellationToken);
    }
}
