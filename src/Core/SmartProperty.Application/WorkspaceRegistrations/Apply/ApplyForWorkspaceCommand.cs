using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.WorkspaceRegistrations.Apply;

/// <summary>A signed-in account asks for a workspace for its company.</summary>
public sealed record ApplyForWorkspaceCommand(string? CompanyName, string? CommercialRegistrationNumber)
    : ICommand<WorkspaceRegistrationView>;

/// <summary>
/// Records a Pending workspace registration request for the signed-in account. The account must be Active and may have
/// only one request waiting at a time; the applicant is always the caller, never a value from the request.
/// </summary>
public sealed class ApplyForWorkspaceCommandHandler(
    ICurrentUser currentUser,
    IUserRepository users,
    IWorkspaceRegistrationRequestRepository registrationRequests,
    IDateTimeProvider clock,
    IUnitOfWork unitOfWork)
    : ICommandHandler<ApplyForWorkspaceCommand, WorkspaceRegistrationView>
{
    public async Task<Result<WorkspaceRegistrationView>> Handle(
        ApplyForWorkspaceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.UserId is not { } userId)
        {
            return Result<WorkspaceRegistrationView>.Failure(WorkspaceRegistrationErrors.Unauthorized);
        }

        if (CompanyDetailsValidator.Validate(command.CompanyName, command.CommercialRegistrationNumber) is { } error)
        {
            return Result<WorkspaceRegistrationView>.Failure(error);
        }

        var user = await users.GetByIdAsync(userId, cancellationToken);

        if (user is null || user.Status != UserStatus.Active)
        {
            return Result<WorkspaceRegistrationView>.Failure(WorkspaceRegistrationErrors.AccountNotActive);
        }

        if (await registrationRequests.HasPendingForApplicantAsync(userId, cancellationToken))
        {
            return Result<WorkspaceRegistrationView>.Failure(WorkspaceRegistrationErrors.PendingRequestExists);
        }

        var request = new WorkspaceRegistrationRequest(
            Guid.NewGuid(),
            userId,
            command.CompanyName!,
            command.CommercialRegistrationNumber,
            clock.UtcNow);

        await registrationRequests.AddAsync(request, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException exception)
            when (exception.Constraint == PersistenceConstraint.WorkspaceRegistrationPendingApplicant)
        {
            // A concurrent request from the same account won the one-pending-request index.
            return Result<WorkspaceRegistrationView>.Failure(WorkspaceRegistrationErrors.PendingRequestExists);
        }

        return Result<WorkspaceRegistrationView>.Success(new WorkspaceRegistrationView(
            request.Id,
            request.CompanyName,
            request.CommercialRegistrationNumber,
            request.Status,
            request.RequestedAt,
            request.ReviewedAt,
            request.ReviewedByUserId,
            request.RejectionReason,
            request.WorkspaceId,
            user.Id,
            user.Email,
            user.FirstName,
            user.LastName,
            user.PhoneNumber,
            user.Status,
            user.IsContactVerificationComplete));
    }
}
