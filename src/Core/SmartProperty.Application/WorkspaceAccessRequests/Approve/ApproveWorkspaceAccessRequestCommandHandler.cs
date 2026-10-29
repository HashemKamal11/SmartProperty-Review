using System.Runtime.ExceptionServices;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Workspaces;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.WorkspaceAccessRequests.Approve;

/// <summary>
/// Approves a pending workspace access request: records the decision, activates the account if it is still
/// Pending, gives it a membership in the requested workspace, and gives that membership the workspace's default
/// Member role.
/// </summary>
/// <remarks>
/// Approval grants the membership and then the default <see cref="WorkspaceRoles.MemberRoleName"/> role, which
/// carries only <see cref="PermissionCodes.WorkspaceRead"/>. The role is found or created in the request's own
/// workspace, so the invariants in docs/identity-access-model.md hold: it is workspace-scoped and belongs to the
/// same workspace as the membership. Nothing broader is ever assigned, and rejection assigns nothing.
///
/// The writes — the decision, the activation, the membership, and the Member role with its permission and
/// assignment — are committed by one <see cref="IUnitOfWork.SaveChangesAsync"/> call, so an approval never
/// half-applies.
///
/// The Member role must carry exactly workspace.read, and its complete persisted permission set is read before
/// anything is mutated. A missing role is created; exactly { workspace.read } is reused; a role with no permissions
/// and no assignment to any membership gets workspace.read; a role with no permissions that is already assigned,
/// or with any other permission set, fails closed with <see cref="WorkspaceAccessRequestErrors.MemberRoleMisconfigured"/>
/// and writes nothing. Misconfigured roles are never repaired, and roles the membership holds independently are
/// never read or touched.
///
/// Reading the role and then writing is a check followed by a save, which no unique index makes safe on its own:
/// another writer could assign the empty role, add a permission to it, or change the applicant's account between
/// the read and the commit. So every attempt runs inside <see cref="IWorkspaceApprovalCoordinator"/>, which takes
/// the transaction and the row locks the decision depends on (the request, the applicant, the membership, the
/// workspace.read permission, the Member role and its permission links) before the attempt reads anything. What
/// the attempt reads is then still true at the commit, and a writer that wants to change any of it waits until the
/// attempt ends. Nothing is read before the coordinator begins.
///
/// The locks cover the rows that existed when the coordinator took them, and the attempt's own reads can see rows
/// committed since. So the Member role, the workspace.read permission and the membership the attempt actually read are
/// confirmed against the locked ones before any of them is relied on and before anything is written. A difference
/// aborts the attempt, discards what it tracked, and runs it again in a new transaction, up to a small bound, so a row
/// that appeared late is always locked in the coordinator's fixed order before it is used.
///
/// Two approvals can still race to create the Member role, the workspace.read permission, or a link or assignment
/// that does not exist yet, because a row that does not exist cannot be locked; the unique indexes and composite
/// keys make exactly one win. The loser's transaction is rolled back, the tracked changes are cleared, and one
/// recovery attempt runs the whole decision again, in a new transaction with new locks, from what the winner
/// committed. It first confirms that the exact row it collided on now exists, and then repeats the Member-role
/// decision from scratch. A second collision propagates.
///
/// Every precondition is checked before anything is mutated, so a refused approval leaves the loaded entities
/// exactly as they were rather than relying on the commit never happening. A lock timeout or a deadlock aborts the
/// attempt, commits nothing and propagates.
/// </remarks>
public sealed class ApproveWorkspaceAccessRequestCommandHandler(
    ICurrentUser currentUser,
    IWorkspaceAccessRequestRepository workspaceAccessRequestRepository,
    IUserRepository userRepository,
    IWorkspaceMembershipRepository workspaceMembershipRepository,
    IRoleRepository roleRepository,
    IPermissionRepository permissionRepository,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork,
    IWorkspaceApprovalCoordinator approvalCoordinator)
    : ICommandHandler<ApproveWorkspaceAccessRequestCommand, ApproveWorkspaceAccessRequestResult>
{
    public async Task<Result<ApproveWorkspaceAccessRequestResult>> Handle(
        ApproveWorkspaceAccessRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The endpoint requires a permission and therefore authentication, so this is normally already
        // satisfied. It is still checked rather than assumed: an unusable subject fails closed, and no reviewer
        // id is ever taken from the request.
        if (currentUser.UserId is not { } reviewerUserId)
        {
            return Result<ApproveWorkspaceAccessRequestResult>.Failure(WorkspaceAccessRequestErrors.Unauthorized);
        }

        if (command.WorkspaceAccessRequestId == Guid.Empty)
        {
            return Result<ApproveWorkspaceAccessRequestResult>.Failure(WorkspaceAccessRequestErrors.NotFound);
        }

        var attempt = new ApprovalAttempt();
        UniqueConstraintViolationException collision;

        try
        {
            return await RunAttemptAsync(
                command.WorkspaceAccessRequestId,
                token => ApproveAsync(command, reviewerUserId, attempt, token),
                cancellationToken);
        }
        catch (UniqueConstraintViolationException exception)
            when (IsRecoverableCollision(exception.Constraint))
        {
            // A collision does not prove that another reviewer won: an unrelated writer may have created the
            // membership, the Member role, the workspace.read permission, or either link while this approval was
            // in flight. The coordinator has rolled the transaction back, but the failed Added rows and the
            // in-memory approval are still tracked. Clear that entire graph before the recovery attempt reads
            // anything.
            collision = exception;
        }

        unitOfWork.DiscardTrackedChanges();

        return await RunAttemptAsync(
            command.WorkspaceAccessRequestId,
            token => RecoverAsync(command, reviewerUserId, attempt, collision, token),
            cancellationToken);
    }

    /// <summary>
    /// Runs one attempt in the coordinator's transaction, and runs it again from fresh state when the coordinator
    /// reports that a row it depends on became visible after the locks were taken.
    /// </summary>
    /// <remarks>
    /// The report is made before the attempt writes anything, so the rerun starts from a rolled-back transaction. The
    /// tracked entities are discarded first, so nothing the failed run loaded is reused: the rerun reads the request,
    /// the account, the membership, the role, the permission and every link and assignment again, under a new
    /// transaction whose locks now include the rows that were new. The runs are bounded; a state that keeps changing
    /// propagates as an error and commits nothing.
    /// </remarks>
    private async Task<Result<ApproveWorkspaceAccessRequestResult>> RunAttemptAsync(
        Guid accessRequestId,
        Func<CancellationToken, Task<Result<ApproveWorkspaceAccessRequestResult>>> attemptOperation,
        CancellationToken cancellationToken)
    {
        for (var run = 1; ; run++)
        {
            try
            {
                return await approvalCoordinator.ExecuteAsync(accessRequestId, attemptOperation, cancellationToken);
            }
            catch (WorkspaceApprovalStateChangedException) when (run < MaxRunsPerAttempt)
            {
                unitOfWork.DiscardTrackedChanges();
            }
        }
    }

    // The first run plus two reruns. A row only becomes visible after a lock when another writer committed in the
    // few statements between the coordinator's locks and the operation's reads, so one rerun is the normal case.
    private const int MaxRunsPerAttempt = 3;

    /// <summary>
    /// The first attempt: every read happens after the coordinator's locks, and everything it writes is committed
    /// by one save inside the coordinator's transaction.
    /// </summary>
    private async Task<Result<ApproveWorkspaceAccessRequestResult>> ApproveAsync(
        ApproveWorkspaceAccessRequestCommand command,
        Guid reviewerUserId,
        ApprovalAttempt attempt,
        CancellationToken cancellationToken)
    {
        var accessRequest = await workspaceAccessRequestRepository.GetByIdAsync(
            command.WorkspaceAccessRequestId,
            cancellationToken);

        if (accessRequest is null)
        {
            return Result<ApproveWorkspaceAccessRequestResult>.Failure(WorkspaceAccessRequestErrors.NotFound);
        }

        // Asked here so an already-reviewed request returns a stable business error. The domain enforces the
        // same rule and would throw; this is the translation, not a second state machine.
        if (accessRequest.Status != WorkspaceAccessRequestStatus.Pending)
        {
            return Result<ApproveWorkspaceAccessRequestResult>.Failure(WorkspaceAccessRequestErrors.AlreadyReviewed);
        }

        var user = await userRepository.GetByIdAsync(accessRequest.UserId, cancellationToken);

        // A Suspended or Deactivated account is refused rather than reactivated: an approval decides workspace
        // access, and using it to lift an account-level block would be a privilege escalation path. A missing
        // row is the same answer, so the response does not distinguish the two.
        if (user is null || !CanBeGrantedAccess(user.Status))
        {
            return Result<ApproveWorkspaceAccessRequestResult>.Failure(
                WorkspaceAccessRequestErrors.TargetAccountUnavailable);
        }

        var reviewedAt = dateTimeProvider.UtcNow;
        attempt.ReviewedAt = reviewedAt;

        // The unique (user, workspace) index is the final safeguard; this lookup is what makes a repeat approval
        // for the same pair reuse the existing membership instead of racing that index.
        var membership = await workspaceMembershipRepository.GetByUserAndWorkspaceAsync(
            accessRequest.UserId,
            accessRequest.WorkspaceId,
            cancellationToken);

        // Decided from the complete persisted Member-role state before any entity is touched, so a policy failure
        // leaves the loaded request, user and membership exactly as they were and saves nothing.
        var plan = await PlanMemberRoleAsync(accessRequest.WorkspaceId, membership?.Id, cancellationToken);

        if (plan.IsFailure)
        {
            return Result<ApproveWorkspaceAccessRequestResult>.Failure(plan.Error!);
        }

        accessRequest.Approve(reviewerUserId, reviewedAt);

        // Only a Pending account is activated. An Active one is left exactly as it is, so a second approval for
        // another workspace never rewrites the account's updated timestamp or status.
        if (user.Status == UserStatus.Pending)
        {
            user.Activate(reviewedAt);
        }

        if (membership is null)
        {
            membership = new WorkspaceMembership(
                Guid.NewGuid(),
                accessRequest.UserId,
                accessRequest.WorkspaceId,
                reviewedAt);

            await workspaceMembershipRepository.AddAsync(membership, cancellationToken);
        }

        // Approval grants the membership and the workspace's default Member role.
        await ApplyMemberRolePlanAsync(
            plan.Value,
            membership.Id,
            accessRequest.WorkspaceId,
            reviewedAt,
            cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException exception)
            when (exception.Resource == PersistenceResource.WorkspaceAccessRequest)
        {
            return Result<ApproveWorkspaceAccessRequestResult>.Failure(
                WorkspaceAccessRequestErrors.AlreadyReviewed);
        }

        return Result<ApproveWorkspaceAccessRequestResult>.Success(new ApproveWorkspaceAccessRequestResult(
            accessRequest.Id,
            accessRequest.UserId,
            accessRequest.WorkspaceId,
            accessRequest.Status,
            user.Status,
            membership.Id));
    }

    /// <summary>
    /// The one recovery attempt after a recognized collision: a new transaction and new locks, every entity read
    /// again, the collided row confirmed, and the whole Member-role decision repeated from what the winner
    /// committed.
    /// </summary>
    private async Task<Result<ApproveWorkspaceAccessRequestResult>> RecoverAsync(
        ApproveWorkspaceAccessRequestCommand command,
        Guid reviewerUserId,
        ApprovalAttempt attempt,
        UniqueConstraintViolationException exception,
        CancellationToken cancellationToken)
    {
        var persistedRequest = await workspaceAccessRequestRepository.GetByIdAsync(
            command.WorkspaceAccessRequestId,
            cancellationToken);

        if (persistedRequest is null)
        {
            return Result<ApproveWorkspaceAccessRequestResult>.Failure(
                WorkspaceAccessRequestErrors.NotFound);
        }

        if (persistedRequest.Status != WorkspaceAccessRequestStatus.Pending)
        {
            return Result<ApproveWorkspaceAccessRequestResult>.Failure(
                WorkspaceAccessRequestErrors.AlreadyReviewed);
        }

        var persistedUser = await userRepository.GetByIdAsync(persistedRequest.UserId, cancellationToken);

        if (persistedUser is null || !CanBeGrantedAccess(persistedUser.Status))
        {
            return Result<ApproveWorkspaceAccessRequestResult>.Failure(
                WorkspaceAccessRequestErrors.TargetAccountUnavailable);
        }

        var persistedMembership = await workspaceMembershipRepository.GetByUserAndWorkspaceAsync(
            persistedRequest.UserId,
            persistedRequest.WorkspaceId,
            cancellationToken);

        // The row the save collided on must exist now. If it does not, the known collision and the
        // authoritative state no longer agree: do not invent a business result or retry another insert,
        // preserve the provider-neutral persistence failure for normal diagnostics.
        if (!await CollidedRowExistsAsync(
                exception.Constraint,
                persistedRequest.WorkspaceId,
                persistedMembership?.Id,
                cancellationToken))
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        // The complete decision runs again against what the winner committed, before anything is mutated.
        var retryPlan = await PlanMemberRoleAsync(
            persistedRequest.WorkspaceId,
            persistedMembership?.Id,
            cancellationToken);

        if (retryPlan.IsFailure)
        {
            return Result<ApproveWorkspaceAccessRequestResult>.Failure(retryPlan.Error!);
        }

        var reviewedAt = attempt.ReviewedAt ?? dateTimeProvider.UtcNow;

        persistedRequest.Approve(reviewerUserId, reviewedAt);

        if (persistedUser.Status == UserStatus.Pending)
        {
            persistedUser.Activate(reviewedAt);
        }

        if (persistedMembership is null)
        {
            // A role or permission collision rolled back the membership the first attempt had added, so the single
            // recovery adds it again.
            persistedMembership = new WorkspaceMembership(
                Guid.NewGuid(),
                persistedRequest.UserId,
                persistedRequest.WorkspaceId,
                reviewedAt);

            await workspaceMembershipRepository.AddAsync(persistedMembership, cancellationToken);
        }

        await ApplyMemberRolePlanAsync(
            retryPlan.Value,
            persistedMembership.Id,
            persistedRequest.WorkspaceId,
            reviewedAt,
            cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException retryException)
            when (retryException.Resource == PersistenceResource.WorkspaceAccessRequest)
        {
            return Result<ApproveWorkspaceAccessRequestResult>.Failure(
                WorkspaceAccessRequestErrors.AlreadyReviewed);
        }

        return Result<ApproveWorkspaceAccessRequestResult>.Success(new ApproveWorkspaceAccessRequestResult(
            persistedRequest.Id,
            persistedRequest.UserId,
            persistedRequest.WorkspaceId,
            persistedRequest.Status,
            persistedUser.Status,
            persistedMembership.Id));
    }

    private static bool IsRecoverableCollision(PersistenceConstraint constraint)
    {
        return constraint is PersistenceConstraint.WorkspaceMembershipUserWorkspace
            or PersistenceConstraint.WorkspaceRoleName
            or PersistenceConstraint.PermissionCode
            or PersistenceConstraint.RolePermissionKey
            or PersistenceConstraint.WorkspaceMembershipRoleKey;
    }

    /// <summary>The review time the first attempt chose, so the recovery attempt records the same moment.</summary>
    private sealed class ApprovalAttempt
    {
        public DateTimeOffset? ReviewedAt { get; set; }
    }

    /// <summary>
    /// Confirms, by a direct query, that the exact row the failed save collided on is now persisted. A valid
    /// Member permission set is not accepted as proof of a specific link or assignment.
    /// </summary>
    private async Task<bool> CollidedRowExistsAsync(
        PersistenceConstraint constraint,
        Guid workspaceId,
        Guid? membershipId,
        CancellationToken cancellationToken)
    {
        switch (constraint)
        {
            case PersistenceConstraint.WorkspaceMembershipUserWorkspace:
                return membershipId is not null;

            case PersistenceConstraint.WorkspaceRoleName:
                return await roleRepository.GetWorkspaceRoleByNameAsync(
                    workspaceId,
                    WorkspaceRoles.MemberRoleName,
                    cancellationToken) is not null;

            case PersistenceConstraint.PermissionCode:
                return await permissionRepository.GetByCodeAsync(
                    PermissionCodes.WorkspaceRead,
                    cancellationToken) is not null;

            case PersistenceConstraint.RolePermissionKey:
            {
                var role = await roleRepository.GetWorkspaceRoleByNameAsync(
                    workspaceId,
                    WorkspaceRoles.MemberRoleName,
                    cancellationToken);
                var permission = await permissionRepository.GetByCodeAsync(
                    PermissionCodes.WorkspaceRead,
                    cancellationToken);

                return role is not null
                    && permission is not null
                    && await roleRepository.HasPermissionAsync(role.Id, permission.Id, cancellationToken);
            }

            case PersistenceConstraint.WorkspaceMembershipRoleKey:
            {
                if (membershipId is not { } id)
                {
                    return false;
                }

                var role = await roleRepository.GetWorkspaceRoleByNameAsync(
                    workspaceId,
                    WorkspaceRoles.MemberRoleName,
                    cancellationToken);

                return role is not null
                    && await roleRepository.IsAssignedToMembershipAsync(id, role.Id, cancellationToken);
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Reads the workspace's Member role and decides, without writing anything, what approval must do to give the
    /// membership exactly <see cref="PermissionCodes.WorkspaceRead"/>, or whether the role is misconfigured and
    /// the approval must fail closed.
    /// </summary>
    private async Task<Result<MemberRolePlan>> PlanMemberRoleAsync(
        Guid workspaceId,
        Guid? membershipId,
        CancellationToken cancellationToken)
    {
        var role = await roleRepository.GetWorkspaceRoleByNameAsync(
            workspaceId,
            WorkspaceRoles.MemberRoleName,
            cancellationToken);

        var permission = await permissionRepository.GetByCodeAsync(PermissionCodes.WorkspaceRead, cancellationToken);

        // The locks cover the rows that existed when the coordinator took them. Under Read Committed these reads can
        // see a role, permission or membership committed since, which nothing holds. Nothing is relied on, and nothing
        // is written, until what was read is confirmed to be exactly what was locked; otherwise the attempt is aborted
        // here and run again from fresh state.
        await approvalCoordinator.ConfirmLockedAsync(membershipId, role?.Id, permission?.Id, cancellationToken);

        if (role is null)
        {
            return Result<MemberRolePlan>.Success(new MemberRolePlan(null, permission, true, true));
        }

        // The invariants the model requires before assignment. The lookup already constrains both, so this only
        // fails closed if that ever stops being true.
        if (role.Scope != RoleScope.Workspace || role.WorkspaceId != workspaceId)
        {
            throw new InvalidOperationException("The Member role must be a workspace role of the membership's workspace.");
        }

        var permissionIds = await roleRepository.GetPermissionIdsAsync(role.Id, cancellationToken);

        if (permissionIds.Count == 0)
        {
            // An empty role that is already held would silently grant its holders access they did not have, so it
            // is only completed when nobody holds it yet.
            if (!await roleRepository.HasAnyMembershipAssignmentAsync(role.Id, cancellationToken))
            {
                return Result<MemberRolePlan>.Success(new MemberRolePlan(role, permission, true, true));
            }

            // The two reads are separate statements. A concurrent approval commits the role's link and its
            // assignment in one transaction, so "empty, then assigned" can mean that commit landed between them
            // rather than that the role is misconfigured. Re-read the complete set once, after the assignment was
            // observed: its snapshot is at least as new as that commit. Still empty means genuinely held while
            // empty; anything else is judged by the exact check below, so this can only ever refuse or accept
            // the one permitted set.
            permissionIds = await roleRepository.GetPermissionIdsAsync(role.Id, cancellationToken);

            if (permissionIds.Count == 0)
            {
                return Result<MemberRolePlan>.Failure(WorkspaceAccessRequestErrors.MemberRoleMisconfigured);
            }
        }

        // workspace.read may likewise have been created after the first lookup. A role that carries permissions
        // while that row was absent is otherwise always refused, so the single refetch costs the happy path nothing.
        // A permission found only now was not locked, so it is confirmed like the others before it is relied on.
        if (permission is null)
        {
            permission = await permissionRepository.GetByCodeAsync(PermissionCodes.WorkspaceRead, cancellationToken);

            if (permission is not null)
            {
                await approvalCoordinator.ConfirmLockedAsync(membershipId, role.Id, permission.Id, cancellationToken);
            }
        }

        if (permission is null || permissionIds.Count != 1 || permissionIds[0] != permission.Id)
        {
            return Result<MemberRolePlan>.Failure(WorkspaceAccessRequestErrors.MemberRoleMisconfigured);
        }

        var assigned = membershipId is { } id
            && await roleRepository.IsAssignedToMembershipAsync(id, role.Id, cancellationToken);

        return Result<MemberRolePlan>.Success(new MemberRolePlan(role, permission, false, !assigned));
    }

    /// <summary>
    /// Performs exactly the writes <see cref="PlanMemberRoleAsync"/> decided on. It reads nothing and repairs
    /// nothing.
    /// </summary>
    private async Task ApplyMemberRolePlanAsync(
        MemberRolePlan plan,
        Guid membershipId,
        Guid workspaceId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var role = plan.Role;

        if (role is null)
        {
            role = new Role(
                Guid.NewGuid(),
                WorkspaceRoles.MemberRoleName,
                RoleScope.Workspace,
                workspaceId,
                at);

            await roleRepository.AddAsync(role, cancellationToken);
        }

        var permission = plan.Permission;

        if (permission is null)
        {
            permission = new Permission(
                Guid.NewGuid(),
                PermissionCodes.WorkspaceRead,
                PermissionCodes.WorkspaceReadDescription);

            await permissionRepository.AddAsync(permission, cancellationToken);
        }

        if (plan.AttachPermission)
        {
            await roleRepository.AddPermissionAsync(new RolePermission(role.Id, permission.Id), cancellationToken);
        }

        if (plan.AssignToMembership)
        {
            await roleRepository.AddMembershipAssignmentAsync(
                new WorkspaceMembershipRole(membershipId, role.Id),
                cancellationToken);
        }
    }

    /// <summary>
    /// What approval must write for the Member role. A null <see cref="Role"/> or <see cref="Permission"/> means
    /// it does not exist yet and is created.
    /// </summary>
    private sealed record MemberRolePlan(
        Role? Role,
        Permission? Permission,
        bool AttachPermission,
        bool AssignToMembership);

    private static bool CanBeGrantedAccess(UserStatus status)
    {
        return status is UserStatus.Pending or UserStatus.Active;
    }
}
