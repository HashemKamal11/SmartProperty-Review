namespace SmartProperty.Application.Abstractions.Persistence;

/// <summary>
/// Runs one workspace access approval attempt inside one database transaction that holds every lock the decision
/// depends on, so what the attempt reads is still true when it commits.
/// </summary>
/// <remarks>
/// <para>
/// A check followed by a save is not enough here. The Member role's permissions and assignments, the applicant's
/// account, the request's status and the membership are all read before they are written, and an ordinary
/// transaction or a unique index does not stop another writer from changing any of them in between. The coordinator
/// closes that window: it begins the transaction, takes the locks below in this one fixed order, and only then runs
/// the operation. Every read the operation makes is therefore made after the locks and holds until the commit.
/// </para>
/// <list type="number">
/// <item>the access request row, exclusively, so two reviewers of the same request run one after the other;</item>
/// <item>the applicant's user row, exclusively, so the account cannot be suspended, deactivated or activated by
/// another writer while the decision is made, and no membership for the applicant can be created behind it;</item>
/// <item>the applicant's membership row in the requested workspace, if there is one;</item>
/// <item>the <c>workspace.read</c> permission row, if it exists;</item>
/// <item>the workspace's Member role row, exclusively, if it exists, which also holds back every insert of a role
/// permission or of a membership assignment for that role, because both reach the role row through a foreign key;</item>
/// <item>every existing permission link of that role, so none can be changed or removed;</item>
/// <item>the assignment of that role to the applicant's membership, if there is one, so it cannot be deleted.</item>
/// </list>
/// <para>
/// <b>A row that appears after the locks cannot be relied on.</b> The locks cover the rows that exist when the
/// coordinator takes them. Under Read Committed the operation's own reads see everything committed since, so a role,
/// permission or membership that became visible in between would be read but was never locked. The operation therefore
/// reports what it actually read to <see cref="ConfirmLockedAsync"/> before it relies on any of it; any difference
/// from what was locked throws <see cref="WorkspaceApprovalStateChangedException"/>. Nothing has been written at that
/// point. The caller discards what it tracked and runs the whole attempt again in a new transaction, where the rows
/// that were new are now taken in the same fixed order as every other lock. Rows are never locked out of order inside a
/// transaction, so the retry cannot introduce a deadlock the first attempt could not have had.
/// </para>
/// <para>
/// A row that does not exist cannot be locked; creating it is guarded by its unique index instead, and a lost race
/// there surfaces as a unique-constraint violation from the operation, which the caller handles by running the whole
/// decision again in a new attempt. The operation never relies on a row it did not find.
/// </para>
/// <para>
/// The operation's own <c>SaveChangesAsync</c> joins the transaction, so everything it writes is committed together
/// or not at all. If the operation throws, the transaction is rolled back and the exception propagates unchanged. A
/// lock that cannot be taken within a short bound, or a deadlock, is an exception too: nothing is committed, and
/// nothing is retried here.
/// </para>
/// <para>
/// The operation must read its entities after the coordinator has begun, never before, because anything loaded
/// earlier may predate the locks.
/// </para>
/// </remarks>
public interface IWorkspaceApprovalCoordinator
{
    Task<T> ExecuteAsync<T>(
        Guid accessRequestId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms that the rows the operation actually read are exactly the rows this transaction locked, or throws
    /// <see cref="WorkspaceApprovalStateChangedException"/>. Called only from inside the operation, after it has read
    /// the applicant's membership, the Member role and the <c>workspace.read</c> permission, and before it relies on any
    /// of them or writes anything. A <see langword="null"/> id means the row was not found, and is confirmed only if
    /// none was locked either.
    /// </summary>
    Task ConfirmLockedAsync(
        Guid? membershipId,
        Guid? roleId,
        Guid? permissionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The rows an approval read are not the rows it locked: one became visible, or went away, after the coordinator took
/// its locks. Nothing was written. The approval must be run again from fresh state in a new transaction.
/// </summary>
public sealed class WorkspaceApprovalStateChangedException()
    : Exception("The state an approval depends on changed after it was locked.");
