using SmartProperty.Application.WorkspaceRegistrations;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

public interface IWorkspaceRegistrationRequestRepository
{
    /// <summary>The tracked request, for a decision.</summary>
    Task<WorkspaceRegistrationRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAsync(WorkspaceRegistrationRequest request, CancellationToken cancellationToken = default);

    Task<bool> HasPendingForApplicantAsync(Guid applicantUserId, CancellationToken cancellationToken = default);

    /// <summary>The request with its applicant's details, for reading; null when it does not exist.</summary>
    Task<WorkspaceRegistrationView?> GetViewAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>One page of requests in a status, oldest first.</summary>
    Task<WorkspaceRegistrationPage> ListAsync(
        WorkspaceRegistrationRequestStatus status,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>Every request the applicant made, newest first.</summary>
    Task<IReadOnlyList<WorkspaceRegistrationView>> ListForApplicantAsync(
        Guid applicantUserId,
        CancellationToken cancellationToken = default);
}
