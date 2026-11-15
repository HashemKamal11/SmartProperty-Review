using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.WorkspaceRegistrations.Register;

/// <summary>
/// A company signs up: the applicant's account (the same fields and rules as individual registration) and the
/// company the workspace is for.
/// </summary>
public sealed record RegisterWorkspaceCommand(
    string Email,
    string Password,
    string FirstName,
    string LastName,
    string PhoneNumber,
    string CompanyName,
    string? CommercialRegistrationNumber) : ICommand<RegisterWorkspaceResult>
{
    // Keeps the plaintext password and personal data out of logs and debugger displays of the record.
    public override string ToString() => nameof(RegisterWorkspaceCommand);
}

/// <summary>
/// The Pending account (it signs in once the phone number and email are verified, like any registration) and the
/// Pending request a platform administrator now reviews.
/// </summary>
public sealed record RegisterWorkspaceResult(
    Guid UserId,
    UserStatus UserStatus,
    DateTimeOffset? PhoneVerificationExpiresAt,
    DateTimeOffset EmailVerificationExpiresAt,
    Guid RegistrationRequestId,
    WorkspaceRegistrationRequestStatus RegistrationStatus);
