using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SmartProperty.Persistence.IntegrationTests.Infrastructure;

/// <summary>
/// Holds the first bootstrap after PostgreSQL grants its advisory lock and observes the second attempt before
/// allowing the first bootstrap to proceed. The lock itself remains real and is never short-circuited.
/// </summary>
internal sealed class PlatformAdminBootstrapLockBarrier : DbCommandInterceptor
{
    private static readonly TimeSpan DeadlockGuard = TimeSpan.FromSeconds(60);

    private readonly TaskCompletionSource firstLockAcquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource secondLockAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int attempts;
    private int completedLocks;

    public Task WaitUntilFirstLockIsAcquiredAsync(CancellationToken cancellationToken = default)
    {
        return firstLockAcquired.Task.WaitAsync(DeadlockGuard, cancellationToken);
    }

    public Task WaitUntilSecondLockIsAttemptedAsync(CancellationToken cancellationToken = default)
    {
        return secondLockAttempted.Task.WaitAsync(DeadlockGuard, cancellationToken);
    }

    public void ReleaseFirst()
    {
        releaseFirst.TrySetResult();
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (IsBootstrapLock(command)
            && Interlocked.Increment(ref attempts) == 2)
        {
            secondLockAttempted.TrySetResult();
        }

        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (!IsBootstrapLock(command)
            || Interlocked.Increment(ref completedLocks) != 1)
        {
            return result;
        }

        firstLockAcquired.TrySetResult();
        await releaseFirst.Task.WaitAsync(DeadlockGuard, cancellationToken);

        return result;
    }

    private static bool IsBootstrapLock(DbCommand command)
    {
        return command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.OrdinalIgnoreCase);
    }
}
