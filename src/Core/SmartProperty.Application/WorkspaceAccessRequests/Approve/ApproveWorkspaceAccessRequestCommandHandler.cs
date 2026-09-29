using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.WorkspaceAccessRequests.Approve;

/// <summary>
/// Approves a pending workspace access request: records the decision, activates the account if it is still
/// Pending, and gives it a membership in the requested workspace.
/// </summary>
/// <remarks>
/// It assigns no role. A membership answers "may this account be in this workspace"; what it may then do there
/// is a separate grant, and approval must not smuggle one in — see docs/identity-access-model.md.
///
/// The three writes — the decision, the activation, and the membership — are committed by one
/// <see cref="IUnitOfWork.SaveChangesAsync"/> call, so an approval never half-applies.
///
/// Every precondition is checked before anything is mutated, so a refused approval leaves the loaded entities
/// exactly as they were rather than relying on the commit never happening.
/// </remarks>
public sealed class ApproveWorkspaceAccessRequestCommandHandler(
    ICurrentUser currentUser,
    IWorkspaceAccessRequestRepository workspaceAccessRequestRepository,
    IUserRepository userRepository,
    IWorkspaceMembershipRepository workspaceMembershipRepository,
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

        // No WorkspaceMembershipRole is created. Approval grants membership, never a role.
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
            when (exception.Constraint == PersistenceConstraint.WorkspaceMembershipUserWorkspace)
        {
            // A membership collision does not prove that another reviewer won: an unrelated writer may have
            // created the membership while this approval was in flight. SaveChanges has rolled its implicit
            // transaction back, but the failed Added membership and the in-memory approval are still tracked.
            // Clear that entire graph before consulting authoritative state or attempting one safe recovery.
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

            // The known collision and the authoritative state no longer agree. Do not invent a business result
            // or retry another insert: preserve the provider-neutral persistence failure for normal diagnostics.
            if (persistedMembership is null)
            {
                throw;
            }

            persistedRequest.Approve(reviewerUserId, reviewedAt);

            if (persistedUser.Status == UserStatus.Pending)
            {
                persistedUser.Activate(reviewedAt);
            }

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

    private static bool CanBeGrantedAccess(UserStatus status)
    {
        return status is UserStatus.Pending or UserStatus.Active;
    }
}
