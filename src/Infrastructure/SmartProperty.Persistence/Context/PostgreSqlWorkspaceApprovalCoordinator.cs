using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Workspaces;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Persistence.Context;

/// <summary>
/// The PostgreSQL implementation of <see cref="IWorkspaceApprovalCoordinator"/>: one transaction, a bounded lock
/// wait, and row locks taken in one fixed order before the approval's operation reads anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these locks stop the races.</b> <c>FOR UPDATE</c> on the Member role row conflicts with the
/// <c>FOR KEY SHARE</c> lock PostgreSQL takes on a parent row for every insert into a child table with a foreign key
/// to it. An out-of-band assignment of the role (<c>workspace_membership_roles</c>) or a new permission link
/// (<c>role_permissions</c>) therefore waits until this transaction ends, and an assignment already in flight makes
/// this transaction wait until it commits, after which the operation's reads see it. <c>FOR SHARE</c> on the
/// existing permission links conflicts with an update or delete of those rows, which the role lock does not cover.
/// <c>FOR SHARE</c> on the <c>workspace.read</c> permission row conflicts with a rename of it. <c>FOR SHARE</c> on the
/// applicant's existing assignment of the role conflicts with its deletion, which neither the role lock nor the
/// membership lock covers. The applicant's user row, held <c>FOR UPDATE</c>, is what stops a membership for the
/// applicant being created behind the approval, because a new membership references that row.
/// </para>
/// <para>
/// <b>Order.</b> Request, applicant, membership, permission, role, the role's links, the role's assignment to the
/// membership. Every approval uses this order, so two approvals can never wait on each other. An unrelated writer that
/// takes some of the same locks in another order can still deadlock with one; PostgreSQL then aborts one transaction,
/// which propagates here after the rollback.
/// </para>
/// <para>
/// <b>Rows that appear after the locks.</b> A lock covers a row that exists when the query runs. Under Read Committed
/// the operation's later reads see rows committed since, so the operation reports what it read to
/// <see cref="ConfirmLockedAsync"/>, which compares it with the identities this transaction locked and throws
/// <see cref="WorkspaceApprovalStateChangedException"/> on any difference. It never takes a lock late: the caller
/// reruns the attempt in a new transaction, so every lock is still taken in the fixed order above. The locked
/// identities are consulted only there; they are not a cache of the decision.
/// </para>
/// <para>
/// The queries use fixed text and parameters only. Nothing here reads a request value into SQL text.
/// </para>
/// </remarks>
internal sealed class PostgreSqlWorkspaceApprovalCoordinator(ApplicationDbContext dbContext)
    : IWorkspaceApprovalCoordinator
{
    // Long enough for a normal competing approval to finish, short enough that a stuck writer fails the request
    // instead of holding a connection indefinitely. Applied with SET LOCAL, so it ends with the transaction.
    private const int LockTimeoutMilliseconds = 5000;

    // The identities this transaction locked, set once the locks are held and consulted by ConfirmLockedAsync. Null
    // outside an attempt, so a confirmation outside one is an error rather than a vacuous success.
    private LockedState? locked;

    public async Task<T> ExecuteAsync<T>(
        Guid accessRequestId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        locked = null;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                $"SET LOCAL lock_timeout = '{LockTimeoutMilliseconds}ms'",
                cancellationToken);

            locked = await LockAsync(accessRequestId, cancellationToken);

            var result = await operation(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            locked = null;
        }
    }

    public Task ConfirmLockedAsync(
        Guid? membershipId,
        Guid? roleId,
        Guid? permissionId,
        CancellationToken cancellationToken = default)
    {
        if (locked is not { } state)
        {
            throw new InvalidOperationException("A confirmation is only possible inside an approval attempt.");
        }

        if (state.MembershipId != membershipId || state.RoleId != roleId || state.PermissionId != permissionId)
        {
            throw new WorkspaceApprovalStateChangedException();
        }

        return Task.CompletedTask;
    }

    private async Task<LockedState> LockAsync(Guid accessRequestId, CancellationToken cancellationToken)
    {
        if (accessRequestId == Guid.Empty)
        {
            return LockedState.None;
        }

        // The request row first. Its owner and workspace are read from the locked row, so they cannot be a stale
        // copy of another request's.
        var applicants = await ReadGuidsAsync(
            "SELECT user_id FROM identity.workspace_access_requests WHERE id = @id FOR UPDATE",
            cancellationToken,
            ("id", accessRequestId));

        if (applicants.Count == 0)
        {
            // No such request: there is nothing to lock, and the operation reports it as not found.
            return LockedState.None;
        }

        var applicantId = applicants[0];

        var workspaces = await ReadGuidsAsync(
            "SELECT workspace_id FROM identity.workspace_access_requests WHERE id = @id",
            cancellationToken,
            ("id", accessRequestId));
        var workspaceId = workspaces[0];

        await ReadGuidsAsync(
            "SELECT id FROM identity.users WHERE id = @user FOR UPDATE",
            cancellationToken,
            ("user", applicantId));

        var memberships = await ReadGuidsAsync(
            "SELECT id FROM identity.workspace_memberships WHERE user_id = @user AND workspace_id = @workspace FOR SHARE",
            cancellationToken,
            ("user", applicantId),
            ("workspace", workspaceId));

        var permissions = await ReadGuidsAsync(
            "SELECT id FROM identity.permissions WHERE code = @code FOR SHARE",
            cancellationToken,
            ("code", PermissionCodes.WorkspaceRead));

        var roles = await ReadGuidsAsync(
            "SELECT id FROM identity.roles WHERE workspace_id = @workspace AND scope = @scope AND name = @name FOR UPDATE",
            cancellationToken,
            ("workspace", workspaceId),
            ("scope", nameof(RoleScope.Workspace)),
            ("name", WorkspaceRoles.MemberRoleName));

        Guid? membershipId = memberships.Count > 0 ? memberships[0] : null;
        Guid? permissionId = permissions.Count > 0 ? permissions[0] : null;
        Guid? roleId = roles.Count > 0 ? roles[0] : null;

        if (roleId is { } lockedRoleId)
        {
            await ReadGuidsAsync(
                "SELECT role_id FROM identity.role_permissions WHERE role_id = @role FOR SHARE",
                cancellationToken,
                ("role", lockedRoleId));

            // The assignment the decision may rest on. Locked after the role, so the order is the same everywhere.
            if (membershipId is { } lockedMembershipId)
            {
                await ReadGuidsAsync(
                    "SELECT workspace_membership_id FROM identity.workspace_membership_roles "
                    + "WHERE workspace_membership_id = @membership AND role_id = @role FOR SHARE",
                    cancellationToken,
                    ("membership", lockedMembershipId),
                    ("role", lockedRoleId));
            }
        }

        return new LockedState(membershipId, roleId, permissionId);
    }

    // Runs one parameterized query on the context's connection, inside its current transaction, and reads the first
    // column of every row as a Guid. Reading every row is what makes a locking query take every lock.
    private async Task<List<Guid>> ReadGuidsAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        var connection = dbContext.Database.GetDbConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();

        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, value));
        }

        var rows = new List<Guid>();

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(reader.GetGuid(0));
        }

        return rows;
    }

    private readonly record struct LockedState(Guid? MembershipId, Guid? RoleId, Guid? PermissionId)
    {
        public static LockedState None => new(null, null, null);
    }
}
