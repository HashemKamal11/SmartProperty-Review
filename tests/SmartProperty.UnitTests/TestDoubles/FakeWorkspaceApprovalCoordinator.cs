using SmartProperty.Application.Abstractions.Persistence;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// Stands in for the transaction-and-locks coordinator: it records each attempt and runs the operation exactly
/// once per call, passing every exception through unchanged, as the real coordinator does after its rollback.
/// </summary>
internal sealed class FakeWorkspaceApprovalCoordinator : IWorkspaceApprovalCoordinator
{
    public List<Guid> ExecutedFor { get; } = [];

    public int ExecuteCount => ExecutedFor.Count;

    /// <summary>When set, thrown before the operation runs, as a lock timeout or a deadlock would be.</summary>
    public Exception? ExceptionBeforeOperation { get; set; }

    /// <summary>Called when an attempt begins, so a test can see what has happened before it.</summary>
    public Action<int>? OnAttemptBegins { get; set; }

    /// <summary>Every confirmation the operation made: the membership, role and permission ids it had read.</summary>
    public List<(Guid? MembershipId, Guid? RoleId, Guid? PermissionId)> Confirmations { get; } = [];

    /// <summary>Called when the operation confirms what it read, so a test can see what has happened before it.</summary>
    public Action<int>? OnConfirm { get; set; }

    /// <summary>
    /// The next this-many confirmations report that a row became visible after the locks, as the real coordinator does
    /// when the operation reads something it did not lock.
    /// </summary>
    public int ReportStateChangeOnNextConfirmations { get; set; }

    public async Task<T> ExecuteAsync<T>(
        Guid accessRequestId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        ExecutedFor.Add(accessRequestId);
        OnAttemptBegins?.Invoke(ExecutedFor.Count);

        if (ExceptionBeforeOperation is { } exception)
        {
            throw exception;
        }

        return await operation(cancellationToken);
    }

    public Task ConfirmLockedAsync(
        Guid? membershipId,
        Guid? roleId,
        Guid? permissionId,
        CancellationToken cancellationToken = default)
    {
        Confirmations.Add((membershipId, roleId, permissionId));
        OnConfirm?.Invoke(Confirmations.Count);

        if (ReportStateChangeOnNextConfirmations > 0)
        {
            ReportStateChangeOnNextConfirmations--;

            throw new WorkspaceApprovalStateChangedException();
        }

        return Task.CompletedTask;
    }
}
