using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SmartProperty.Persistence.IntegrationTests.Infrastructure;

/// <summary>
/// Holds every competing writer at the moment it is about to save, until all of them have arrived.
/// </summary>
/// <remarks>
/// This is how the race tests are made deterministic without a sleep or a delay. A handler reads the refresh
/// token before it writes, so a writer that has reached <c>SavingChangesAsync</c> has already loaded the row.
/// Releasing none of them until the last one arrives therefore guarantees the state the race needs: every
/// participant loaded the same active token, and no participant's UPDATE had yet been sent.
///
/// The barrier only delays the call; it does not replace it. Once released, every participant runs the real
/// <c>SaveChangesAsync</c> against real PostgreSQL, and which of them wins is decided by the database's row lock
/// and by the concurrency predicate EF puts in the UPDATE — never by this class.
///
/// <see cref="DeadlockGuard"/> is a failsafe, not synchronisation: it exists so a test that wires up the wrong
/// number of participants fails with a message instead of hanging the run. It is never reached on a passing run,
/// and no assertion depends on its value.
/// </remarks>
internal sealed class SaveChangesBarrier(int participants) : SaveChangesInterceptor
{
    private static readonly TimeSpan DeadlockGuard = TimeSpan.FromSeconds(60);

    private readonly TaskCompletionSource allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int arrived;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref arrived) >= participants)
        {
            allArrived.TrySetResult();
        }

        await allArrived.Task.WaitAsync(DeadlockGuard, cancellationToken);

        return result;
    }
}
