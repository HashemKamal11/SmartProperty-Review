using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.WorkspaceRegistrations;

/// <summary>A workspace registration request with the applicant's account details.</summary>
public sealed record WorkspaceRegistrationView(
    Guid Id,
    string CompanyName,
    string? CommercialRegistrationNumber,
    WorkspaceRegistrationRequestStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? ReviewedAt,
    Guid? ReviewedByUserId,
    string? RejectionReason,
    Guid? WorkspaceId,
    Guid ApplicantUserId,
    string ApplicantEmail,
    string ApplicantFirstName,
    string ApplicantLastName,
    string? ApplicantPhoneNumber,
    UserStatus ApplicantStatus,
    bool ApplicantContactVerified);

public sealed record WorkspaceRegistrationPage(IReadOnlyList<WorkspaceRegistrationView> Items, long TotalCount);
