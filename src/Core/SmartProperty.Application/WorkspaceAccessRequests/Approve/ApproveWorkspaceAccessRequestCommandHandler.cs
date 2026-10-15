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
/// Two approvals in one workspace can race to create the same Member role (or the same permission row). The
/// unique indexes make exactly one win; the loser clears its tracked changes, reloads, and applies the approval
/// once more against what the winner committed.
///
/// Every precondition is checked before anything is mutated, so a refused approval leaves the loaded entities
/// exactly as they were rather than relying on the commit never happening.
/// </remarks>
public sealed class ApproveWorkspaceAccessRequestCommandHandler(
    ICurrentUser currentUser,
    IWorkspaceAccessRequestRepository workspaceAccessRequestRepository,
    IUserRepository userRepository,
    IWorkspaceMembershipRepository workspaceMembershipRepository,
    IRoleRepository roleRepository,
    IPermissionRepository permissionRepository,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
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

        accessRequest.Approve(reviewerUserId, reviewedAt);

        // Only a Pending account is activated. An Active one is left exactly as it is, so a second approval for
        // another workspace never rewrites the account's updated timestamp or status.
        if (user.Status == UserStatus.Pending)
        {
            user.Activate(reviewedAt);
        }

        // The unique (user, workspace) index is the final safeguard; this lookup is what makes a repeat approval
        // for the same pair reuse the existing membership instead of racing that index.
        var membership = await workspaceMembershipRepository.GetByUserAndWorkspaceAsync(
            accessRequest.UserId,
            accessRequest.WorkspaceId,
            cancellationToken);

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
        await EnsureMemberRoleAsync(membership.Id, accessRequest.WorkspaceId, reviewedAt, cancellationToken);

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
        catch (UniqueConstraintViolationException exception)
            when (exception.Constraint is PersistenceConstraint.WorkspaceMembershipUserWorkspace
                or PersistenceConstraint.WorkspaceRoleName
                or PersistenceConstraint.PermissionCode)
        {
            // A collision does not prove that another reviewer won: an unrelated writer may have created the
            // membership, the Member role, or the workspace.read permission while this approval was in flight.
            // SaveChanges has rolled its implicit transaction back, but the failed Added rows and the in-memory
            // approval are still tracked. Clear that entire graph before consulting authoritative state or
            // attempting one safe recovery.
            unitOfWork.DiscardTrackedChanges();

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

            if (persistedMembership is null)
            {
                // A membership collision means a membership must exist now. If it does not, the known collision
                // and the authoritative state no longer agree: do not invent a business result or retry another
                // insert, preserve the provider-neutral persistence failure for normal diagnostics.
                if (exception.Constraint == PersistenceConstraint.WorkspaceMembershipUserWorkspace)
                {
                    throw;
                }

                // A role or permission collision rolled back the membership this attempt had added, so the single
                // retry adds it again.
                persistedMembership = new WorkspaceMembership(
                    Guid.NewGuid(),
                    persistedRequest.UserId,
                    persistedRequest.WorkspaceId,
                    reviewedAt);

                await workspaceMembershipRepository.AddAsync(persistedMembership, cancellationToken);
            }

            persistedRequest.Approve(reviewerUserId, reviewedAt);

            if (persistedUser.Status == UserStatus.Pending)
            {
                persistedUser.Activate(reviewedAt);
            }

            // Reads what the winner committed, so the Member role and permission are now reused, not recreated.
            await EnsureMemberRoleAsync(
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

        return Result<ApproveWorkspaceAccessRequestResult>.Success(new ApproveWorkspaceAccessRequestResult(
            accessRequest.Id,
            accessRequest.UserId,
            accessRequest.WorkspaceId,
            accessRequest.Status,
            user.Status,
            membership.Id));
    }

    /// <summary>
    /// Makes sure the workspace has a Member role carrying <see cref="PermissionCodes.WorkspaceRead"/> and that this
    /// membership holds it. Every write is guarded by a lookup, so a repeat adds nothing.
    /// </summary>
    private async Task EnsureMemberRoleAsync(
        Guid membershipId,
        Guid workspaceId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var role = await roleRepository.GetWorkspaceRoleByNameAsync(
            workspaceId,
            WorkspaceRoles.MemberRoleName,
            cancellationToken);

        var roleCreated = role is null;

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

        // The invariants the model requires before assignment. The lookup already constrains both, so this only
        // fails closed if that ever stops being true.
        if (role.Scope != RoleScope.Workspace || role.WorkspaceId != workspaceId)
        {
            throw new InvalidOperationException("The Member role must be a workspace role of the membership's workspace.");
        }

        var permission = await permissionRepository.GetByCodeAsync(PermissionCodes.WorkspaceRead, cancellationToken);

        var permissionCreated = permission is null;

        if (permission is null)
        {
            permission = new Permission(
                Guid.NewGuid(),
                PermissionCodes.WorkspaceRead,
                PermissionCodes.WorkspaceReadDescription);

            await permissionRepository.AddAsync(permission, cancellationToken);
        }

        // A row added in this unit of work is invisible to a by-id query; a new role or permission cannot already
        // be linked, so the lookup is skipped for them rather than relied on.
        var permissionAttached = !roleCreated
            && !permissionCreated
            && await roleRepository.HasPermissionAsync(role.Id, permission.Id, cancellationToken);

        if (!permissionAttached)
        {
            await roleRepository.AddPermissionAsync(new RolePermission(role.Id, permission.Id), cancellationToken);
        }

        var roleAssigned = !roleCreated
            && await roleRepository.IsAssignedToMembershipAsync(membershipId, role.Id, cancellationToken);

        if (!roleAssigned)
        {
            await roleRepository.AddMembershipAssignmentAsync(
                new WorkspaceMembershipRole(membershipId, role.Id),
                cancellationToken);
        }
    }

    private static bool CanBeGrantedAccess(UserStatus status)
    {
        return status is UserStatus.Pending or UserStatus.Active;
    }
}
