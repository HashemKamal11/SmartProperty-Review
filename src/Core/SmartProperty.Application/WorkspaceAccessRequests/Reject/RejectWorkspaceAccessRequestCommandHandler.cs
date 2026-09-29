using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.WorkspaceAccessRequests.Reject;

/// <summary>
/// Rejects a pending workspace access request. Records the decision and nothing else.
/// </summary>
/// <remarks>
/// It does not activate, suspend, or deactivate the account, create a membership, or assign a role. A rejected
/// applicant that registered as Pending therefore stays Pending and still cannot sign in — the account-level
/// rule Login and Refresh already enforce.
///
/// The user row is read only to report the account status back, so the caller can see it was left alone.
/// </remarks>
public sealed class RejectWorkspaceAccessRequestCommandHandler(
    ICurrentUser currentUser,
    IWorkspaceAccessRequestRepository workspaceAccessRequestRepository,
    IUserRepository userRepository,
    IDateTimeProvider dateTimeProvider,
    IUnitOfWork unitOfWork)
    : ICommandHandler<RejectWorkspaceAccessRequestCommand, RejectWorkspaceAccessRequestResult>
{
    public async Task<Result<RejectWorkspaceAccessRequestResult>> Handle(
        RejectWorkspaceAccessRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.UserId is not { } reviewerUserId)
        {
            return Result<RejectWorkspaceAccessRequestResult>.Failure(WorkspaceAccessRequestErrors.Unauthorized);
        }

        if (command.WorkspaceAccessRequestId == Guid.Empty)
        {
            return Result<RejectWorkspaceAccessRequestResult>.Failure(WorkspaceAccessRequestErrors.NotFound);
        }

        var accessRequest = await workspaceAccessRequestRepository.GetByIdAsync(
            command.WorkspaceAccessRequestId,
            cancellationToken);

        if (accessRequest is null)
        {
            return Result<RejectWorkspaceAccessRequestResult>.Failure(WorkspaceAccessRequestErrors.NotFound);
        }

        if (accessRequest.Status != WorkspaceAccessRequestStatus.Pending)
        {
            return Result<RejectWorkspaceAccessRequestResult>.Failure(WorkspaceAccessRequestErrors.AlreadyReviewed);
        }

        var user = await userRepository.GetByIdAsync(accessRequest.UserId, cancellationToken);

        // Unlike approval, no account status blocks a rejection — the decision is about the request. Only a
        // missing row is refused, because there would be nothing to report and the data is inconsistent.
        if (user is null)
        {
            return Result<RejectWorkspaceAccessRequestResult>.Failure(
                WorkspaceAccessRequestErrors.TargetAccountUnavailable);
        }

        accessRequest.Reject(reviewerUserId, dateTimeProvider.UtcNow);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException exception)
            when (exception.Resource == PersistenceResource.WorkspaceAccessRequest)
        {
            return Result<RejectWorkspaceAccessRequestResult>.Failure(
                WorkspaceAccessRequestErrors.AlreadyReviewed);
        }

        return Result<RejectWorkspaceAccessRequestResult>.Success(new RejectWorkspaceAccessRequestResult(
            accessRequest.Id,
            accessRequest.UserId,
            accessRequest.WorkspaceId,
            accessRequest.Status,
            user.Status));
    }
}
