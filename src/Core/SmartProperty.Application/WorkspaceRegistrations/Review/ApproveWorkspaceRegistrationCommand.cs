using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Workspaces;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.WorkspaceRegistrations.Review;

public sealed record ApproveWorkspaceRegistrationCommand(Guid Id) : ICommand<ApproveWorkspaceRegistrationResult>;

public sealed record ApproveWorkspaceRegistrationResult(
    Guid RequestId,
    WorkspaceRegistrationRequestStatus Status,
    Guid WorkspaceId,
    string WorkspaceName,
    Guid ApplicantUserId,
    Guid MembershipId);

/// <summary>
/// Approves a pending workspace registration request: creates the workspace named after the company and makes the
/// applicant its admin, with exactly the role a platform administrator gives the admin of a workspace it creates
/// (<see cref="WorkspaceAdminProvisioner"/>).
/// </summary>
/// <remarks>
/// The decision, the workspace, the role, the membership and the assignment are written by one save, so an approval
/// never half-applies. The request's status is a concurrency token: when two reviewers decide at once, the second save
/// matches no row and is rejected as a whole, so no second workspace is left behind.
/// The applicant must have verified the phone number and email; a Suspended or Deactivated account is refused, never
/// revived.
/// </remarks>
public sealed class ApproveWorkspaceRegistrationCommandHandler(
    ICurrentUser currentUser,
    IWorkspaceRegistrationRequestRepository registrationRequests,
    IUserRepository users,
    IWorkspaceRepository workspaces,
    IWorkspaceMembershipRepository memberships,
    IRoleRepository roles,
    IPermissionRepository permissions,
    IDateTimeProvider clock,
    IUnitOfWork unitOfWork)
    : ICommandHandler<ApproveWorkspaceRegistrationCommand, ApproveWorkspaceRegistrationResult>
{
    public async Task<Result<ApproveWorkspaceRegistrationResult>> Handle(
        ApproveWorkspaceRegistrationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.UserId is not { } reviewerUserId)
        {
            return Failure(WorkspaceRegistrationErrors.Unauthorized);
        }

        var request = command.Id == Guid.Empty ? null : await registrationRequests.GetByIdAsync(command.Id, cancellationToken);

        if (request is null)
        {
            return Failure(WorkspaceRegistrationErrors.NotFound);
        }

        if (request.Status != WorkspaceRegistrationRequestStatus.Pending)
        {
            return Failure(WorkspaceRegistrationErrors.AlreadyReviewed);
        }

        var applicant = await users.GetByIdAsync(request.ApplicantUserId, cancellationToken);

        if (applicant is null || applicant.Status is UserStatus.Suspended or UserStatus.Deactivated)
        {
            return Failure(WorkspaceRegistrationErrors.ApplicantUnavailable);
        }

        if (!applicant.IsContactVerificationComplete)
        {
            return Failure(WorkspaceRegistrationErrors.ContactNotVerified);
        }

        var now = clock.UtcNow;

        // Only a Pending account is activated (an older account without a phone number that was never activated).
        if (applicant.Status == UserStatus.Pending)
        {
            applicant.Activate(now);
        }

        var workspace = new Workspace(Guid.NewGuid(), request.CompanyName, now);
        await workspaces.AddAsync(workspace, cancellationToken);

        var membershipId = await new WorkspaceAdminProvisioner(memberships, roles, permissions)
            .ProvisionAsync(workspace.Id, applicant.Id, now, cancellationToken);

        request.Approve(reviewerUserId, workspace.Id, now);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException exception)
            when (exception.Resource == PersistenceResource.WorkspaceRegistrationRequest)
        {
            return Failure(WorkspaceRegistrationErrors.AlreadyReviewed);
        }
        catch (UniqueConstraintViolationException exception)
            when (exception.Constraint == PersistenceConstraint.PermissionCode)
        {
            // Another writer created workspace.read or workspace.members.create at the same moment. Nothing was saved;
            // the next attempt finds the row and reuses it.
            return Failure(WorkspaceRegistrationErrors.ConcurrentChange);
        }

        return Result<ApproveWorkspaceRegistrationResult>.Success(new ApproveWorkspaceRegistrationResult(
            request.Id,
            request.Status,
            workspace.Id,
            workspace.Name,
            applicant.Id,
            membershipId));
    }

    private static Result<ApproveWorkspaceRegistrationResult> Failure(Common.Results.Error error) =>
        Result<ApproveWorkspaceRegistrationResult>.Failure(error);
}
