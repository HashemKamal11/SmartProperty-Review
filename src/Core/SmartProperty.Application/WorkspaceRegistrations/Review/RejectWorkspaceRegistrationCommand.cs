using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.WorkspaceRegistrations.Review;

public sealed record RejectWorkspaceRegistrationCommand(Guid Id, string? Reason) : ICommand<WorkspaceRegistrationView>;

/// <summary>
/// Rejects a pending workspace registration request with a reason the applicant can read. Records the decision and
/// nothing else: the applicant keeps an ordinary account and may apply again.
/// </summary>
public sealed class RejectWorkspaceRegistrationCommandHandler(
    ICurrentUser currentUser,
    IWorkspaceRegistrationRequestRepository registrationRequests,
    IDateTimeProvider clock,
    IUnitOfWork unitOfWork)
    : ICommandHandler<RejectWorkspaceRegistrationCommand, WorkspaceRegistrationView>
{
    public async Task<Result<WorkspaceRegistrationView>> Handle(
        RejectWorkspaceRegistrationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.UserId is not { } reviewerUserId)
        {
            return Result<WorkspaceRegistrationView>.Failure(WorkspaceRegistrationErrors.Unauthorized);
        }

        if (string.IsNullOrWhiteSpace(command.Reason)
            || command.Reason.Trim().Length > WorkspaceRegistrationRequest.RejectionReasonMaxLength)
        {
            return Result<WorkspaceRegistrationView>.Failure(WorkspaceRegistrationErrors.ValidationFailed(
                $"A rejection reason of 1 to {WorkspaceRegistrationRequest.RejectionReasonMaxLength} characters is required."));
        }

        var request = command.Id == Guid.Empty ? null : await registrationRequests.GetByIdAsync(command.Id, cancellationToken);

        if (request is null)
        {
            return Result<WorkspaceRegistrationView>.Failure(WorkspaceRegistrationErrors.NotFound);
        }

        if (request.Status != WorkspaceRegistrationRequestStatus.Pending)
        {
            return Result<WorkspaceRegistrationView>.Failure(WorkspaceRegistrationErrors.AlreadyReviewed);
        }

        request.Reject(reviewerUserId, command.Reason, clock.UtcNow);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException exception)
            when (exception.Resource == PersistenceResource.WorkspaceRegistrationRequest)
        {
            return Result<WorkspaceRegistrationView>.Failure(WorkspaceRegistrationErrors.AlreadyReviewed);
        }

        return Result<WorkspaceRegistrationView>.Success((await registrationRequests.GetViewAsync(request.Id, cancellationToken))!);
    }
}
