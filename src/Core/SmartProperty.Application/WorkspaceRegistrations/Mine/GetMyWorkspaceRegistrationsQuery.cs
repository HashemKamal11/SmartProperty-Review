using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.WorkspaceRegistrations.Mine;

/// <summary>The signed-in account's own workspace registration requests, newest first.</summary>
public sealed record GetMyWorkspaceRegistrationsQuery : IQuery<IReadOnlyList<WorkspaceRegistrationView>>;

public sealed class GetMyWorkspaceRegistrationsQueryHandler(
    ICurrentUser currentUser,
    IWorkspaceRegistrationRequestRepository registrationRequests)
    : IQueryHandler<GetMyWorkspaceRegistrationsQuery, IReadOnlyList<WorkspaceRegistrationView>>
{
    public async Task<Result<IReadOnlyList<WorkspaceRegistrationView>>> Handle(
        GetMyWorkspaceRegistrationsQuery query,
        CancellationToken cancellationToken = default)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Result<IReadOnlyList<WorkspaceRegistrationView>>.Failure(WorkspaceRegistrationErrors.Unauthorized);
        }

        return Result<IReadOnlyList<WorkspaceRegistrationView>>.Success(
            await registrationRequests.ListForApplicantAsync(userId, cancellationToken));
    }
}
