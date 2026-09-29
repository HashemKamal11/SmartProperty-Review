using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SmartProperty.Persistence.IntegrationTests.Infrastructure;

/// <summary>
/// Pauses the first workspace-membership lookup after PostgreSQL has executed it, allowing a test to commit a
/// membership from an independent context before the original caller acts on its now-stale empty result.
/// </summary>
internal sealed class WorkspaceMembershipLookupBarrier : DbCommandInterceptor
{
    private static readonly TimeSpan DeadlockGuard = TimeSpan.FromSeconds(60);

    private readonly TaskCompletionSource lookupCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int intercepted;

    public Task WaitUntilLookupCompletesAsync(CancellationToken cancellationToken = default)
    {
        return lookupCompleted.Task.WaitAsync(DeadlockGuard, cancellationToken);
    }

    public void Release()
    {
        release.TrySetResult();
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceMembershipLookup(command)
            || Interlocked.CompareExchange(ref intercepted, 1, 0) != 0)
        {
            return result;
        }

        lookupCompleted.TrySetResult();
        await release.Task.WaitAsync(DeadlockGuard, cancellationToken);

        return result;
    }

    private static bool IsWorkspaceMembershipLookup(DbCommand command)
    {
        return command.CommandText.Contains(
            "workspace_memberships",
            StringComparison.OrdinalIgnoreCase)
            && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
    }
}
