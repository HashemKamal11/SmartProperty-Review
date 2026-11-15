#nullable enable
using SmartProperty.Application.WorkspaceRegistrations;

namespace SmartProperty.Api.Contracts.Workspaces;

/// <summary>
/// Company sign-up: the applicant's account (same fields and rules as <c>POST /api/auth/register</c>) and the company
/// the workspace is for. Members are nullable so a missing value is a 422 from Application validation, not a 400.
/// </summary>
public sealed record RegisterWorkspaceRequest(
    string? Email,
    string? Password,
    string? FirstName,
    string? LastName,
    string? PhoneNumber,
    string? CompanyName,
    string? CommercialRegistrationNumber)
{
    // Keeps the plaintext password and personal data out of logs and debugger displays of the record.
    public override string ToString() => nameof(RegisterWorkspaceRequest);
}

public sealed record RegisterWorkspaceResponse(
    Guid UserId,
    string Status,
    DateTimeOffset? PhoneVerificationExpiresAt,
    DateTimeOffset EmailVerificationExpiresAt,
    Guid RegistrationRequestId,
    string RegistrationStatus);

/// <summary>A signed-in account applies for a workspace for its company.</summary>
public sealed record ApplyForWorkspaceRequest(string? CompanyName, string? CommercialRegistrationNumber);

public sealed record RejectWorkspaceRegistrationRequest(string? Reason);

public sealed record WorkspaceRegistrationApplicantResponse(
    Guid UserId,
    string Email,
    string FirstName,
    string LastName,
    string? PhoneNumber,
    string Status,
    bool ContactVerified);

public sealed record WorkspaceRegistrationResponse(
    Guid Id,
    string CompanyName,
    string? CommercialRegistrationNumber,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? ReviewedAt,
    Guid? ReviewedByUserId,
    string? RejectionReason,
    Guid? WorkspaceId,
    WorkspaceRegistrationApplicantResponse Applicant)
{
    public static WorkspaceRegistrationResponse From(WorkspaceRegistrationView view) => new(
        view.Id,
        view.CompanyName,
        view.CommercialRegistrationNumber,
        view.Status.ToString(),
        view.RequestedAt,
        view.ReviewedAt,
        view.ReviewedByUserId,
        view.RejectionReason,
        view.WorkspaceId,
        new WorkspaceRegistrationApplicantResponse(
            view.ApplicantUserId,
            view.ApplicantEmail,
            view.ApplicantFirstName,
            view.ApplicantLastName,
            view.ApplicantPhoneNumber,
            view.ApplicantStatus.ToString(),
            view.ApplicantContactVerified));
}

public sealed record ApproveWorkspaceRegistrationResponse(
    Guid RequestId,
    string Status,
    Guid WorkspaceId,
    string WorkspaceName,
    Guid ApplicantUserId,
    Guid MembershipId);
